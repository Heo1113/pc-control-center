using GpuProfit.Core;
using GpuProfit.Core.Domain;
using GpuProfit.Core.Settings;
using GpuProfit.Storage;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Telemetry;

namespace GpuProfit.Recording;

public sealed class EngineOptions
{
    public required AppPaths Paths { get; init; }
    public AppSettings Settings { get; init; } = new();
    public bool UseMockSensors { get; init; }
    public IAppLogger Log { get; init; } = NullLogger.Instance;
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Tests: a monitor with scripted providers (for example several mock GPUs). Overrides <see cref="UseMockSensors"/>.</summary>
    public HardwareMonitor? Monitor { get; init; }

    /// <summary>The command line reads the database without sampling the sensors or touching open sessions.</summary>
    public bool ReadOnlyTools { get; init; }

    /// <summary>Tests with a manual clock turn the sampling loop off and call <see cref="TelemetryRecorder.SampleOnce"/> themselves.</summary>
    public bool AutoSample { get; init; } = true;
}

/// <summary>
/// The running application core: database, sensors, recorder and sessions. The GUI and the command line both use it.
/// Structure follows project C's DiagnosticsEngine (start: recover, open run, start collection; stop: close cleanly).
/// </summary>
public sealed class ProfitEngine : IAsyncDisposable
{
    private readonly IAppLogger _log;
    private readonly TimeProvider _time;
    private Timer? _heartbeat;
    private AccountingProfile _profile;
    private int _stopped;

    private ProfitEngine(EngineOptions options, ProfitStore store, HardwareMonitor? hardware, long? runId, IReadOnlyList<AppRun> recoveredRuns,
        IReadOnlyList<WorkSession> recoveredSessions)
    {
        Options = options;
        _log = options.Log;
        _time = options.Time;
        Store = store;
        Hardware = hardware;
        RunId = runId;
        RecoveredRuns = recoveredRuns;
        RecoveredSessions = recoveredSessions;
        _profile = store.GetProfile();
        IsMock = hardware?.IsMock ?? options.UseMockSensors;
        if (hardware is not null && runId is long id)
        {
            Recorder = new TelemetryRecorder(hardware, store, id, TimeSpan.FromSeconds(options.Settings.Sampling.IntervalSeconds),
                () => _profile.MaxSampleGapSeconds, _time, _log);
            Recorder.Sampled += s => SensorSampled?.Invoke(s);
        }

        Sessions = new SessionService(store, _time, () => RunId, () => IsMock, Recorder is null ? null : () => Recorder.SampleOnce(), _log);
    }

    public EngineOptions Options { get; }
    public ProfitStore Store { get; }
    public HardwareMonitor? Hardware { get; }
    public TelemetryRecorder? Recorder { get; }
    public SessionService Sessions { get; }
    public long? RunId { get; }
    public bool IsMock { get; }
    public AppSettings Settings => Options.Settings;
    public IReadOnlyList<AppRun> RecoveredRuns { get; }
    public IReadOnlyList<WorkSession> RecoveredSessions { get; }
    public RetentionResult? LastRetention { get; private set; }

    public AccountingProfile Profile => _profile;

    /// <summary>Raised on a background thread after each sensor sample.</summary>
    public event Action<HardwareSnapshot>? SensorSampled;

    /// <summary>Raised after the accounting profile was saved.</summary>
    public event Action<AccountingProfile>? ProfileChanged;

    public static Task<ProfitEngine> StartAsync(EngineOptions options) => Task.Run(() => Start(options));

    public static ProfitEngine Start(EngineOptions options)
    {
        var log = options.Log;
        options.Paths.EnsureDirectories();
        var store = ProfitStore.Open(options.Paths.DatabasePath, options.Time, log);
        try
        {
            if (options.ReadOnlyTools)
            {
                return new ProfitEngine(options, store, null, null, [], []);
            }

            var recoveredRuns = store.RecoverUnfinishedRuns();
            var monitor = options.Monitor ?? HardwareMonitor.Create(
                new SamplingSettings { GpuEnabled = options.Settings.Sampling.GpuEnabled, SystemEnabled = options.Settings.Sampling.SystemEnabled },
                options.UseMockSensors, options.Time, log);
            var runId = store.StartRun(options.Time.GetUtcNow(), AppInfo.Version, monitor.IsMock, options.Settings.Sampling.IntervalSeconds * 1000);
            var recoveredSessions = store.RecoverInterruptedSessions(runId);
            foreach (var s in recoveredSessions)
            {
                log.Warn("session", $"세션 #{s.Id}은(는) 앱이 정상 종료되지 않아 마지막 기록 시점에서 닫았습니다 (재개 가능).");
            }

            var engine = new ProfitEngine(options, store, monitor, runId, recoveredRuns, recoveredSessions);
            foreach (var s in recoveredSessions)
            {
                try
                {
                    engine.Sessions.Recalculate(s.Id, "비정상 종료 후 복구");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    log.Error("session", $"복구된 세션 #{s.Id} 계산 실패", ex);
                }
            }

            if (options.AutoSample)
            {
                engine.Recorder?.Start();
                engine._heartbeat = new Timer(_ => engine.Beat(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            }

            _ = Task.Run(engine.RunRetention);
            return engine;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public void SaveProfile(AccountingProfile profile)
    {
        Store.SaveProfile(profile);
        _profile = profile;
        ProfileChanged?.Invoke(profile);
    }

    private void Beat()
    {
        if (RunId is long id)
        {
            try
            {
                Store.Heartbeat(id, _time.GetUtcNow());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("engine", "하트비트 기록 실패", ex);
            }
        }
    }

    private void RunRetention()
    {
        try
        {
            LastRetention = Store.ApplyRetention(Settings.Storage.SampleRetentionDays);
            if (LastRetention.GpuSamplesRemoved + LastRetention.SystemSamplesRemoved > 0)
            {
                _log.Info("storage", $"보존 기간이 지난 센서 기록 정리: GPU {LastRetention.GpuSamplesRemoved}행, 시스템 {LastRetention.SystemSamplesRemoved}행 (세션 구간은 보존)");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
        {
            _log.Warn("storage", "보존 기간 정리 실패", ex);
        }
    }

    public void NotifySuspend()
    {
        Recorder?.NotifySuspend();
        Beat();
    }

    public void NotifyResume() => Recorder?.NotifyResume();

    public async Task StopAsync(string reason = RunEndReasons.Normal)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        if (_heartbeat is not null)
        {
            await _heartbeat.DisposeAsync().ConfigureAwait(false);
            _heartbeat = null;
        }

        if (Recorder is not null)
        {
            await Recorder.StopAsync().ConfigureAwait(false);
        }

        if (RunId is long id)
        {
            Sessions.PauseAllForExit(reason == RunEndReasons.WindowsSessionEnd ? SegmentEndReasons.WindowsSessionEnd : SegmentEndReasons.AppExit);
            try
            {
                Store.EndRun(id, _time.GetUtcNow(), reason);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
            {
                _log.Error("engine", "실행 종료 기록 실패 (다음 실행에서 복구됩니다)", ex);
            }
        }

        Hardware?.Dispose();
        Store.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
