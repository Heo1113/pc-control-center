using GpuProfit.Accounting.Profitability;
using GpuProfit.Core;
using GpuProfit.Core.Domain;
using GpuProfit.Recording;
using GpuProfit.Storage;
using LocalAiPerf.Telemetry;

namespace GpuProfit.Tests.Recording;

/// <summary>
/// Engine, recorder and session lifecycle against a real SQLite file with scripted mock GPUs and a manual clock.
/// These are mock-based checks of the logic; they are not measurements on real hardware.
/// </summary>
public class EngineAndSessionTests
{
    private static (ProfitEngine Engine, ManualTime Time, ScriptedGpuProvider Gpus) Start(TempDir dir, ManualTime? time = null, params Func<DateTimeOffset, double?>[] power)
    {
        time ??= new ManualTime(T.T0);
        var gpus = new ScriptedGpuProvider(time, power.Length == 0 ? [_ => 300.0] : power);
        var monitor = new HardwareMonitor(gpus, new FixedSystemProvider(), time, isMock: true);
        var engine = ProfitEngine.Start(new EngineOptions
        {
            Paths = new AppPaths(dir.Path, false),
            Monitor = monitor,
            Time = time,
            AutoSample = false,
        });
        return (engine, time, gpus);
    }

    // 5 s steps (under the 10 s max gap): every stored sample is an fsync with synchronous=FULL, slow on CI disks.
    private static void Run(ProfitEngine engine, ManualTime time, int seconds, int step = 5)
    {
        for (var i = 0; i < seconds; i += step)
        {
            time.Advance(TimeSpan.FromSeconds(step));
            engine.Recorder!.SampleOnce();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task Session_lifecycle_with_pause_measures_only_recorded_time()
    {
        using var dir = new TempDir();
        var (engine, time, _) = Start(dir);
        await using var _ = engine;
        var w = engine.Store.AddWorkload(new Workload { Name = "AI 추론", Type = WorkloadType.AiInference });
        engine.Store.SaveProfile(new AccountingProfile { Tariff = new ElectricityTariff { FlatRateKrwPerKwh = 200m } });
        engine.Recorder!.SampleOnce();
        var s = engine.Sessions.Start(w, "테스트", null);
        Assert.True(s.IsMock);
        Run(engine, time, 1800);
        engine.Sessions.Pause(s.Id);
        Run(engine, time, 600); // paused: not counted
        engine.Sessions.Resume(s.Id);
        Run(engine, time, 1800);
        engine.Store.AddRevenue(new RevenueEntry { SessionId = s.Id, Amount = 1000, Occurred = time.GetUtcNow() });
        var result = engine.Sessions.Stop(s.Id);

        Assert.Equal(3600, result.RecordedSeconds, 3);
        Assert.Equal(600, result.PausedSeconds, 3);
        Assert.Equal(0.3, result.Energy.TotalKwh.Value!.Value, 6); // 300 W × 1 h
        Assert.Equal(1.0, result.Energy.Coverage!.Value, 6);
        Assert.Equal(940m, Math.Round(result.View(RevenueView.Realized).NetProfit.Value!.Value, 4)); // 1000 − 60
        Assert.Equal(SessionState.Ended, engine.Store.GetSession(s.Id)!.State);
        Assert.Equal(2, engine.Store.ListSegments(s.Id).Count);
        Assert.Single(engine.Store.ListResults(s.Id)); // saved on stop
    }

    [Fact(Timeout = 120_000)]
    public async Task Only_one_running_session_per_gpu()
    {
        using var dir = new TempDir();
        var (engine, _, _) = Start(dir, null, _ => 100.0, _ => 200.0);
        await using var _e = engine;
        var w = engine.Store.AddWorkload(new Workload { Name = "w" });
        var a = engine.Sessions.Start(w, "GPU0", null, [0]);
        Assert.Throws<InvalidOperationException>(() => engine.Sessions.Start(w, "all", null));
        var b = engine.Sessions.Start(w, "GPU1", null, [1]); // different GPU: allowed
        Assert.NotEqual(a.Id, b.Id);
        Assert.Throws<InvalidOperationException>(() => engine.Sessions.Start(w, "GPU1 again", null, [1]));
    }

    [Fact(Timeout = 120_000)]
    public async Task Multiple_gpus_are_accounted_separately_and_summed()
    {
        using var dir = new TempDir();
        var (engine, time, _) = Start(dir, null, _ => 300.0, _ => 150.0, _ => 75.0);
        await using var _e = engine;
        var w = engine.Store.AddWorkload(new Workload { Name = "w" });
        engine.Recorder!.SampleOnce();
        var all = engine.Sessions.Start(w, "all", null);
        Run(engine, time, 3600);
        var r = engine.Sessions.Stop(all.Id);
        Assert.Equal(3, r.Energy.Devices.Count);
        Assert.Equal(525, r.Energy.GpuMeasuredWh, 3);
        Assert.Equal(300, r.Energy.Devices[0].MeasuredWh, 3);
        Assert.Equal(75, r.Energy.Devices[2].MeasuredWh, 3);
        Assert.Equal(3, engine.Store.ListDevices().Count);
    }

    [Fact(Timeout = 120_000)]
    public async Task Selected_gpus_only()
    {
        using var dir = new TempDir();
        var (engine, time, _) = Start(dir, null, _ => 300.0, _ => 150.0);
        await using var _e = engine;
        var w = engine.Store.AddWorkload(new Workload { Name = "w" });
        engine.Recorder!.SampleOnce();
        var s = engine.Sessions.Start(w, "GPU1", null, [1]);
        Run(engine, time, 3600);
        var r = engine.Sessions.Stop(s.Id);
        Assert.Equal(150, r.Energy.GpuMeasuredWh, 3);
        Assert.Equal([1], r.Energy.Devices.Select(d => d.DeviceIndex));
    }

    [Fact(Timeout = 120_000)]
    public async Task Sensor_failures_and_lost_gpu_become_unobserved_time_and_gaps()
    {
        using var dir = new TempDir();
        var time = new ManualTime(T.T0);
        double? Power(DateTimeOffset t) => (t - T.T0).TotalSeconds is > 1000 and < 1200 ? null : 300.0; // 200 s without power values
        var (engine, _, gpus) = Start(dir, time, Power);
        await using var _e = engine;
        var w = engine.Store.AddWorkload(new Workload { Name = "w" });
        engine.Recorder!.SampleOnce();
        var s = engine.Sessions.Start(w, null, null);
        Run(engine, time, 1800);
        gpus.Lost = true;
        Run(engine, time, 300); // GPU connection lost for 5 minutes
        gpus.Lost = false;
        gpus.Fail = true;
        Run(engine, time, 60); // provider throws: snapshot without GPUs, app keeps running
        gpus.Fail = false;
        Run(engine, time, 1440);
        var r = engine.Sessions.Stop(s.Id);
        var device = Assert.Single(r.Energy.Devices);
        Assert.InRange(device.UnobservedSeconds, 200 + 300 + 60, 200 + 300 + 60 + 6);
        Assert.Equal(300 * device.ObservedSeconds / 3600, device.MeasuredWh, 3);
        Assert.Contains(engine.Store.ListGaps(), g => g.Kind == GapKinds.SensorUnavailable);
        Assert.Contains(r.Warnings, w2 => w2.Contains("MOCK"));
    }

    [Fact(Timeout = 120_000)]
    public async Task No_gpu_at_all_keeps_the_app_running_and_energy_undefined()
    {
        using var dir = new TempDir();
        var time = new ManualTime(T.T0);
        var monitor = new HardwareMonitor(new LocalAiPerf.Telemetry.Platform.DisabledGpuProvider(), new FixedSystemProvider(), time);
        await using var engine = ProfitEngine.Start(new EngineOptions { Paths = new AppPaths(dir.Path, false), Monitor = monitor, Time = time, AutoSample = false });
        var w = engine.Store.AddWorkload(new Workload { Name = "w" });
        engine.Recorder!.SampleOnce();
        var s = engine.Sessions.Start(w, null, null);
        for (var i = 0; i < 60; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            engine.Recorder.SampleOnce();
        }

        var r = engine.Sessions.Stop(s.Id);
        Assert.False(r.Energy.TotalWh.HasValue);
        Assert.False(r.Electricity.CostKrw.HasValue);
        Assert.Empty(r.Energy.Devices);
        Assert.Equal(61, engine.Store.GetStats().SystemSamples);
    }

    [Fact(Timeout = 120_000)]
    public async Task Sleep_and_long_sample_gaps_are_not_interpolated()
    {
        using var dir = new TempDir();
        var (engine, time, _) = Start(dir);
        await using var _e = engine;
        var w = engine.Store.AddWorkload(new Workload { Name = "w" });
        engine.Recorder!.SampleOnce();
        var s = engine.Sessions.Start(w, null, null);
        Run(engine, time, 600);
        engine.NotifySuspend();
        time.Advance(TimeSpan.FromMinutes(30));
        engine.NotifyResume();
        engine.Recorder.SampleOnce();
        Run(engine, time, 600);
        time.Advance(TimeSpan.FromSeconds(120)); // app hung for 2 minutes
        engine.Recorder.SampleOnce();
        Run(engine, time, 600);
        var r = engine.Sessions.Stop(s.Id);
        Assert.Equal(1800 + 1800 + 120, r.RecordedSeconds, 3);
        Assert.Equal(1800, r.Energy.Devices[0].ObservedSeconds, 3);
        Assert.Equal(150, r.Energy.GpuMeasuredWh, 3);
        var kinds = engine.Store.ListGaps().Select(g => g.Kind).ToList();
        Assert.Contains(GapKinds.Suspend, kinds);
        Assert.Contains(GapKinds.SampleGap, kinds);
    }

    [Fact(Timeout = 120_000)]
    public async Task Wall_clock_jump_is_recorded()
    {
        using var dir = new TempDir();
        var (engine, time, _) = Start(dir);
        await using var _e = engine;
        engine.Recorder!.SampleOnce();
        time.Advance(TimeSpan.FromSeconds(1));
        time.JumpWall(TimeSpan.FromHours(-1));
        engine.Recorder.SampleOnce();
        Assert.Contains(engine.Store.ListGaps(), g => g.Kind == GapKinds.ClockJump);
    }

    [Fact(Timeout = 120_000)]
    public async Task Unclean_end_is_recovered_on_the_next_start_and_the_session_can_be_resumed()
    {
        using var dir = new TempDir();
        var time = new ManualTime(T.T0);
        long sessionId;
        {
            var (engine, _, _) = Start(dir, time);
            var w = engine.Store.AddWorkload(new Workload { Name = "w" });
            engine.Recorder!.SampleOnce();
            sessionId = engine.Sessions.Start(w, null, null).Id;
            Run(engine, time, 900);
            // Crash: no StopAsync, no EndRun, no segment closed. Only the connection goes away (as when the process dies).
            engine.Store.Dispose();
        }

        time.Advance(TimeSpan.FromMinutes(20)); // app not running
        var (again, _, _) = Start(dir, time);
        await using var _a = again;
        Assert.Single(again.RecoveredRuns);
        var recovered = Assert.Single(again.RecoveredSessions);
        Assert.Equal(sessionId, recovered.Id);
        Assert.Equal(SessionState.Interrupted, recovered.State);
        var segment = Assert.Single(again.Store.ListSegments(sessionId));
        Assert.Equal(T.At(900), segment.End); // closed at the last sample, not at "now"
        Assert.Equal(SegmentEndReasons.RecoveredAfterCrash, segment.EndReason);
        Assert.Contains(again.Store.ListGaps(), g => g.Kind == GapKinds.AppEndedUncleanly);
        Assert.NotEmpty(again.Store.ListResults(sessionId)); // recalculated on recovery

        again.Sessions.Resume(sessionId);
        Run(again, time, 900);
        var r = again.Sessions.Stop(sessionId);
        Assert.Equal(1800, r.RecordedSeconds, 3); // the 20 minutes the app was not running are not counted
        Assert.Equal(150, r.Energy.GpuMeasuredWh, 2);
    }

    [Fact(Timeout = 120_000)]
    public async Task Normal_exit_pauses_running_sessions_and_ends_the_run()
    {
        using var dir = new TempDir();
        var time = new ManualTime(T.T0);
        long id;
        {
            var (engine, _, _) = Start(dir, time);
            var w = engine.Store.AddWorkload(new Workload { Name = "w" });
            id = engine.Sessions.Start(w, null, null).Id;
            Run(engine, time, 60);
            await engine.StopAsync();
            await engine.StopAsync(); // idempotent
        }

        var (again, _, _) = Start(dir, time);
        await using var _a = again;
        Assert.Empty(again.RecoveredRuns);
        Assert.Empty(again.RecoveredSessions);
        Assert.Equal(SessionState.Paused, again.Store.GetSession(id)!.State);
        Assert.Equal(SegmentEndReasons.AppExit, Assert.Single(again.Store.ListSegments(id)).EndReason);
    }

    [Fact(Timeout = 120_000)]
    public async Task Mock_session_cannot_be_resumed_with_real_sensors()
    {
        using var dir = new TempDir();
        var time = new ManualTime(T.T0);
        long id;
        {
            var (engine, _, _) = Start(dir, time);
            var w = engine.Store.AddWorkload(new Workload { Name = "w" });
            id = engine.Sessions.Start(w, null, null).Id;
            engine.Sessions.Pause(id);
            await engine.StopAsync();
        }

        var real = new HardwareMonitor(new ScriptedGpuProvider(time, _ => 100.0), new FixedSystemProvider(), time, isMock: false);
        await using var engine2 = ProfitEngine.Start(new EngineOptions { Paths = new AppPaths(dir.Path, false), Monitor = real, Time = time, AutoSample = false });
        Assert.Throws<InvalidOperationException>(() => engine2.Sessions.Resume(id));
    }

    [Fact(Timeout = 120_000)]
    public async Task Recalculation_after_a_tariff_change_adds_a_new_result_and_keeps_the_old_one()
    {
        using var dir = new TempDir();
        var (engine, time, _) = Start(dir);
        await using var _e = engine;
        var w = engine.Store.AddWorkload(new Workload { Name = "w" });
        engine.Store.SaveProfile(new AccountingProfile { Tariff = new ElectricityTariff { FlatRateKrwPerKwh = 100m } });
        engine.Recorder!.SampleOnce();
        var s = engine.Sessions.Start(w, null, null);
        Run(engine, time, 3600);
        engine.Sessions.Stop(s.Id);
        Assert.Equal(0, engine.Sessions.RecalculateAll("변경 없음", TestContext.Current.CancellationToken));

        engine.SaveProfile(new AccountingProfile { Tariff = new ElectricityTariff { FlatRateKrwPerKwh = 300m } });
        Assert.Equal(1, engine.Sessions.RecalculateAll("요금 변경", TestContext.Current.CancellationToken));
        var history = engine.Store.ListResults(s.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal(90m, Math.Round(history[0].Summary.ElectricityCost!.Value, 4)); // newest first: 0.3 kWh × 300
        Assert.Equal(30m, Math.Round(history[1].Summary.ElectricityCost!.Value, 4));
        Assert.Contains(history[0].Conditions, c => c.Value.Contains("300"));
        var latest = engine.Store.ListResults(latestOnly: true);
        Assert.Equal(90m, Math.Round(Assert.Single(latest).Summary.ElectricityCost!.Value, 4));
    }

    [Fact(Timeout = 120_000)]
    public async Task Manual_session_with_wall_meter_and_editing_rules()
    {
        using var dir = new TempDir();
        var time = new ManualTime(T.T0.AddDays(1));
        var (engine, _, _) = Start(dir, time);
        await using var _e = engine;
        var w = engine.Store.AddWorkload(new Workload { Name = "w" });
        var s = engine.Sessions.CreateManual(w, "어제 작업", T.At(0), T.At(7200), 1.2);
        Assert.Equal(SessionSource.Manual, s.Source);
        var r = engine.Sessions.Compute(s.Id);
        Assert.Equal(1.2, r.Energy.TotalKwh.Value!.Value, 9);
        Assert.Equal(2, r.BasisHours, 9);
        Assert.Throws<InvalidOperationException>(() => engine.Sessions.Resume(s.Id));
        Assert.Throws<ArgumentException>(() => engine.Sessions.CreateManual(w, null, T.At(100), T.At(0), null));
        Assert.Throws<ArgumentException>(() => engine.Sessions.CreateManual(w, null, T.At(0), T.At(10 * 86400), null));

        engine.Sessions.UpdateDetails(s with { Title = "고친 제목", WallMeterKwh = 1.5, State = SessionState.Running });
        var edited = engine.Store.GetSession(s.Id)!;
        Assert.Equal("고친 제목", edited.Title);
        Assert.Equal(SessionState.Ended, edited.State); // state is not editable this way
        Assert.Equal(1.5, engine.Sessions.Compute(s.Id).Energy.TotalKwh.Value!.Value, 9);

        engine.Sessions.Delete(s.Id);
        Assert.Null(engine.Store.GetSession(s.Id));
    }

    [Fact(Timeout = 120_000)]
    public async Task Data_folder_lock_allows_one_process()
    {
        using var dir = new TempDir();
        using var first = DataFolderLock.TryAcquire(dir.Path);
        Assert.NotNull(first);
        Assert.Null(DataFolderLock.TryAcquire(dir.Path));
        first!.Dispose();
        using var second = DataFolderLock.TryAcquire(dir.Path);
        Assert.NotNull(second);
        await Task.CompletedTask;
    }
}
