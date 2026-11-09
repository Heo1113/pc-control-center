using System.Buffers.Binary;
using System.Text;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Telemetry;
using LocalAiPerf.Telemetry.Mock;
using LocalAiPerf.Telemetry.Platform;
using WinDiag.Collection;
using WinDiag.Collection.Crash;
using WinDiag.Collection.Notifications;
using WinDiag.Collection.Sensors;
using WinDiag.Collection.Sources;
using WinDiag.Core;
using WinDiag.Core.Crash;
using WinDiag.Core.Events;
using WinDiag.Core.Privacy;
using WinDiag.Core.Settings;
using WinDiag.Storage;

namespace WinDiag.Tests.Collection;

public class SensorRecorderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class ThrowingGpu : IGpuSensorProvider
    {
        public string Name => "throwing";
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public string? DriverVersion => null;
        public string? LibraryVersion => null;
        public IReadOnlyList<GpuInfo> Devices => [];
        public IReadOnlyList<SensorCapability> Capabilities => [];
        public IReadOnlyList<GpuSample> ReadGpus(ICollection<SensorIssue> issues) => throw new InvalidOperationException("driver lost");
        public void Dispose()
        {
        }
    }

    [Fact]
    public void Samples_from_project_A_mock_providers_are_stored_and_flagged_mock()
    {
        using var dir = new TempDir();
        var time = new ManualTime(T0);
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"), time);
        var run = store.StartRun(T0, "t", true, 5000, false);
        using var monitor = new HardwareMonitor(new MockGpuProvider(time), new MockSystemProvider(time), time, isMock: true);
        var recorder = new SensorRecorder(monitor, store, run, TimeSpan.FromSeconds(5), time);
        for (var i = 0; i < 3; i++)
        {
            recorder.SampleOnce();
            time.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(3, recorder.SamplesStored);
        var series = store.QuerySensors(T0, T0.AddMinutes(1));
        Assert.Equal(3, series.Gpu.Count);
        Assert.All(series.Gpu, g => Assert.NotNull(g.TempC));
        Assert.Equal(3, recorder.Live.Snapshot().Count);
    }

    [Fact]
    public void A_failing_gpu_provider_never_stops_system_sampling()
    {
        using var dir = new TempDir();
        var time = new ManualTime(T0);
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"), time);
        var run = store.StartRun(T0, "t", false, 5000, false);
        using var monitor = new HardwareMonitor(new ThrowingGpu(), new MockSystemProvider(time), time);
        var recorder = new SensorRecorder(monitor, store, run, TimeSpan.FromSeconds(5), time);

        var snapshot = recorder.SampleOnce();

        Assert.Empty(snapshot.Gpus);
        Assert.Contains(snapshot.Issues, i => i.Status == SensorStatus.Error);
        var stored = Assert.Single(store.QuerySensors(T0.AddMinutes(-1), T0.AddMinutes(1)).System);
        Assert.NotNull(stored.CpuUtil);
    }

    [Fact]
    public async Task Missing_gpu_is_recorded_as_unavailable_not_estimated()
    {
        using var dir = new TempDir();
        var time = new ManualTime(T0);
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"), time);
        var run = store.StartRun(T0, "t", false, 5000, false);
        using var monitor = new HardwareMonitor(new DisabledGpuProvider(), new UnsupportedSystemProvider(SensorStatus.NotSupported, "테스트"), time);
        var recorder = new SensorRecorder(monitor, store, run, TimeSpan.FromSeconds(5), time);
        recorder.Start();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await recorder.StopAsync();
        Assert.Contains(store.ListGaps(), g => g.Kind == GapKinds.SensorUnavailable);
        var row = Assert.Single(store.QuerySensors(T0.AddMinutes(-1), T0.AddMinutes(1)).System);
        Assert.Null(row.CpuUtil);
        Assert.Null(row.RamTotal);
    }

    [Fact]
    public void Wall_clock_jumps_are_recorded()
    {
        using var dir = new TempDir();
        var time = new ManualTime(T0);
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"), time);
        var run = store.StartRun(T0, "t", true, 5000, false);
        using var monitor = new HardwareMonitor(new MockGpuProvider(time), new MockSystemProvider(time), time, isMock: true);
        var recorder = new SensorRecorder(monitor, store, run, TimeSpan.FromSeconds(5), time);
        recorder.SampleOnce();
        time.Advance(TimeSpan.FromSeconds(5));
        recorder.SampleOnce();
        Assert.Empty(store.ListGaps());

        time.Advance(TimeSpan.FromSeconds(5));
        time.JumpWallClock(TimeSpan.FromHours(-1));
        recorder.SampleOnce();

        var gap = Assert.Single(store.ListGaps(), g => g.Kind == GapKinds.ClockJump);
        Assert.Contains("뒤로", gap.Detail);
    }
}

public class DumpHeaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    internal static byte[] KernelHeader(uint code, ulong[] parameters, DateTimeOffset crash, uint dumpType = 4)
    {
        var header = new byte[DumpHeaderParser.HeaderSize];
        Encoding.ASCII.GetBytes("PAGEDU64").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x008), 0xF);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x00C), 26100);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x030), 0x8664);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x034), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x038), code);
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x040 + 8 * i), parameters[i]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xF98), dumpType);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0xFA8), crash.ToFileTime());
        return header;
    }

    [Fact]
    public void Reads_bugcheck_code_parameters_time_and_type_from_a_kernel_dump_header()
    {
        var crash = new DateTimeOffset(2026, 10, 30, 13, 41, 10, TimeSpan.Zero);
        var header = KernelHeader(0x124, [0, 0xffffb08c2a6e9028, 0xbe000000, 0x800400], crash);
        var info = DumpHeaderParser.Parse(@"C:\Windows\Minidump\103026-1-01.dmp", header, header.Length, crash, Now);
        Assert.Equal(DumpStatus.Ok, info.Status);
        Assert.Equal(0x124u, info.BugCheckCode);
        Assert.Equal(0xffffb08c2a6e9028ul, info.BugCheckParameters[1]);
        Assert.Equal(crash, info.DumpTime);
        Assert.Equal(4, info.DumpType);
        Assert.Equal(16, info.ProcessorCount);
        Assert.Equal(26100, info.BuildNumber);
    }

    [Fact]
    public void Implausible_header_values_are_dropped_not_shown()
    {
        var header = KernelHeader(0x116, [1, 2, 3, 4], Now.AddYears(5), dumpType: 999);
        var info = DumpHeaderParser.Parse("x.dmp", header, header.Length, null, Now);
        Assert.Equal(0x116u, info.BugCheckCode);
        Assert.Null(info.DumpTime);
        Assert.Null(info.DumpType);
    }

    [Theory]
    [InlineData("MDMP\u0093\u00a7\0\0", "MDMP")]
    [InlineData("PAGEDUMP", "PAGEDUMP")]
    [InlineData("ZZZZZZZZ", "ZZZZZZZZ")]
    public void Other_formats_are_identified_without_guessing(string signature, string expected)
    {
        var bytes = new byte[64];
        Encoding.Latin1.GetBytes(signature).CopyTo(bytes, 0);
        var info = DumpHeaderParser.Parse("x.dmp", bytes, 64, null, Now);
        Assert.Equal(DumpStatus.UnknownFormat, info.Status);
        Assert.Equal(expected, info.Signature);
        Assert.Null(info.BugCheckCode);
    }

    [Fact]
    public void Scanner_reads_files_read_only_and_keeps_them()
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(Path.Combine(dir.Path, "Minidump")).FullName;
        var file = Path.Combine(folder, "110126-1-01.dmp");
        var bytes = KernelHeader(0x9F, [3, 0, 0, 0], Now.AddDays(-1));
        File.WriteAllBytes(file, bytes);
        var result = new DumpScanner(new ManualTime(Now)).Scan([folder, Path.Combine(dir.Path, "MEMORY.DMP")]);
        var dump = Assert.Single(result.Files);
        Assert.Equal(0x9Fu, dump.BugCheckCode);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }
}

public class NotificationServiceTests
{
    private static StoredEvent Stored(long id, DiagSeverity severity, string group, string source = EventSources.Live) => new()
    {
        Id = id, Channel = "System", Provider = "p", EventId = 1, RecordId = id, TimeCreated = DateTimeOffset.UnixEpoch.AddSeconds(id),
        Category = EventCategories.GpuDriver, Severity = severity, GroupKey = group, Signature = "s", Title = "t" + id, Source = source,
    };

    [Fact]
    public void Notifies_at_or_above_threshold_and_merges_repeats()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var settings = new NotificationSettings { MinimumSeverity = DiagSeverity.Error, RepeatQuietMinutes = 10 };
        var service = new NotificationService(() => settings, time);

        var first = service.Evaluate([Stored(1, DiagSeverity.Error, "g"), Stored(2, DiagSeverity.Error, "g"), Stored(3, DiagSeverity.Warning, "w"),
            Stored(4, DiagSeverity.Critical, "v", EventSources.Virtual)]);
        var notice = Assert.Single(first);
        Assert.Equal(2, notice.Count);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(service.Evaluate([Stored(5, DiagSeverity.Error, "g")]));
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Single(service.Evaluate([Stored(6, DiagSeverity.Error, "g")]));

        settings.Enabled = false;
        Assert.Empty(service.Evaluate([Stored(7, DiagSeverity.Critical, "x")]));
    }
}

public class DiagnosticsEngineTests
{
    [Fact]
    public async Task Engine_collects_events_and_sensors_and_closes_its_run()
    {
        using var dir = new TempDir();
        var paths = AppPaths.Resolve(dir.Path, dir.Path);
        var settings = new AppSettings();
        settings.Collection.Channels = [new ChannelSetting { Name = "System" }];
        settings.Normalize();
        var source = new MemoryEventSource("test", false);
        source.Add(Ev.Make("disk", 7, 2, DateTimeOffset.UtcNow.AddHours(-1), record: 0));
        var engine = await DiagnosticsEngine.StartAsync(new EngineOptions
        {
            Paths = paths, Settings = settings, UseMockSensors = true, EventSource = source, StartBackgroundLoops = false, ScanDumps = false,
            Masker = PrivacyMasker.Generic,
        });
        var report = await engine.PollNowAsync(TestContext.Current.CancellationToken);
        var snapshot = await engine.TakeSnapshotAsync("test", TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await engine.StopAsync();

        Assert.Equal(1, report!.Inserted);
        Assert.True(snapshot.IsNew);
        using var store = DiagnosticsStore.Open(paths.DatabasePath);
        var run = Assert.Single(store.ListRuns());
        Assert.Equal(RunEndReasons.Normal, run.EndReason);
        Assert.True(run.IsMock);
        Assert.True(store.GetStats().SensorSamples >= 1);
        Assert.True(store.ListSnapshots().Single().IsMock);
    }

    [Fact]
    public async Task Engine_that_was_not_stopped_is_recovered_on_the_next_start()
    {
        using var dir = new TempDir();
        var paths = AppPaths.Resolve(dir.Path, dir.Path);
        var settings = new AppSettings();
        settings.Normalize();
        var options = new EngineOptions
        {
            Paths = paths, Settings = settings, UseMockSensors = true, EventSource = new MemoryEventSource("t", false), StartBackgroundLoops = false,
            ScanDumps = false,
        };
        var crashed = await DiagnosticsEngine.StartAsync(options);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await crashed.Recorder!.StopAsync();
        crashed.Hardware.Dispose();
        crashed.Store.Dispose(); // the process "dies": the run is never ended

        var next = await DiagnosticsEngine.StartAsync(options);
        try
        {
            Assert.Single(next.RecoveredRuns);
            Assert.Contains(next.Store.ListGaps(), g => g.Kind == GapKinds.AppEndedUncleanly);
        }
        finally
        {
            await next.StopAsync();
        }
    }

    [Fact]
    public async Task Imports_and_the_demo_never_move_the_live_log_positions()
    {
        using var dir = new TempDir();
        var paths = AppPaths.Resolve(dir.Path, dir.Path);
        var settings = new AppSettings();
        settings.Collection.Channels = [new ChannelSetting { Name = "System" }];
        settings.Normalize();
        var live = new MemoryEventSource("live", false);
        live.Add(Ev.Make("disk", 7, 2, DateTimeOffset.UtcNow.AddHours(-1), record: 90_000));
        var engine = await DiagnosticsEngine.StartAsync(new EngineOptions
        {
            Paths = paths, Settings = settings, UseMockSensors = true, EventSource = live, StartBackgroundLoops = false, ScanDumps = false,
            Masker = PrivacyMasker.Generic,
        });
        try
        {
            await engine.PollNowAsync(TestContext.Current.CancellationToken);
            var before = engine.Store.GetChannelState("System");
            var gapsBefore = engine.Store.ListGaps().Count;

            await engine.SeedVirtualScenarioAsync();
            var import = await engine.ImportXmlFilesAsync([Fixtures.Path("events", "wevtutil-multi.xml")], TestContext.Current.CancellationToken);

            Assert.Equal(3, import.Inserted);
            Assert.Equal(before, engine.Store.GetChannelState("System"));
            Assert.Equal(gapsBefore, engine.Store.ListGaps().Count);
            var imported = engine.Store.QueryEvents(new EventQuery { Limit = 100_000 }).Where(e => e.Source == EventSources.Import).ToList();
            Assert.Equal(3, imported.Count);
            live.Add(Ev.Make("disk", 11, 2, DateTimeOffset.UtcNow, record: 90_001));
            var next = await engine.PollNowAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, next!.Read);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Demo_scenario_seeds_virtual_events_snapshots_records_and_mock_sensors()
    {
        using var dir = new TempDir();
        var paths = AppPaths.Resolve(dir.Path, dir.Path);
        var settings = new AppSettings();
        settings.Normalize();
        var engine = await DiagnosticsEngine.StartAsync(new EngineOptions
        {
            Paths = paths, Settings = settings, UseMockSensors = true, EventSource = new MemoryEventSource("t", false), StartBackgroundLoops = false,
            ScanDumps = false,
        });
        try
        {
            var report = await engine.SeedVirtualScenarioAsync();
            Assert.True(report.Inserted > 100);
            var stats = engine.Store.GetStats();
            Assert.Equal(stats.Events, stats.VirtualEvents);
            Assert.Equal(2, engine.Store.ListSnapshots().Count);
            Assert.Equal(2, engine.Store.ListBaselines().Count);
            Assert.Single(engine.Store.ListUserRecords());
            Assert.True(stats.SensorSamples > 1000);
        }
        finally
        {
            await engine.StopAsync();
        }
    }
}
