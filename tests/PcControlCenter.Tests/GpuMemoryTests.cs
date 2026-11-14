using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Telemetry;
using PcControlCenter.Core;
using PcControlCenter.GpuMemory;
using PcControlCenter.GpuMemory.Mock;
using PcControlCenter.Hardware;
using PcControlCenter.Modules;
using PcControlCenter.Presentation;

namespace PcControlCenter.Tests;

/// <summary>A provider whose readings the test decides (or that throws), counting how often it is read.</summary>
internal sealed class ScriptedGpuMemoryProvider(Func<GpuMemoryReadRequest, GpuMemorySnapshot> read, string? unavailable = null) : IGpuMemoryProvider
{
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);
    public GpuMemoryReadRequest? LastRequest { get; private set; }
    public bool Disposed { get; private set; }
    public string Name => "scripted";
    public bool IsMock => false;
    public string? UnavailableReason { get; } = unavailable;

    public GpuMemorySnapshot Read(GpuMemoryReadRequest request)
    {
        Interlocked.Increment(ref _reads);
        LastRequest = request;
        return read(request);
    }

    public void Dispose() => Disposed = true;
}

internal static class GpuMemoryFixtures
{
    public const long MiB = 1L << 20;
    public const long GiB = 1L << 30;
    public static readonly DateTimeOffset Start = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    public static GpuMemorySnapshot Snapshot(DateTimeOffset at, IReadOnlyList<GpuProcessMemory>? processes = null,
        IReadOnlyDictionary<int, ProcessEntry>? table = null, IReadOnlyList<TcpListenerEntry>? listeners = null) => new()
    {
        Timestamp = at,
        Source = "test",
        Variant = ProcessCounterVariant.LocalNonLocal,
        CommittedAvailable = true,
        AdapterCountersAvailable = true,
        Adapters = [new GpuAdapterMemory { Luid = 1, Info = new GpuAdapterInfo(1, "Test GPU", GpuVendors.Nvidia, 16 * GiB, 32 * GiB, false), DedicatedUsageBytes = 5 * GiB, SharedUsageBytes = GiB }],
        Processes = processes ?? [new GpuProcessMemory { ProcessId = 100, Luid = 1, DedicatedBytes = 4 * GiB, SharedBytes = 100 * MiB, CommittedBytes = 5 * GiB }],
        ProcessTable = table ?? new Dictionary<int, ProcessEntry> { [100] = new(100, 10, "app.exe", at.AddMinutes(-5), ProcessIdentityState.Verified) },
        Listeners = listeners ?? [],
    };

    public static RuntimeSnapshot Strata(DateTimeOffset at, RuntimeConnectionState state = RuntimeConnectionState.Connected, string endpoint = "http://127.0.0.1:18500") => new()
    {
        RuntimeKind = "strata",
        Timestamp = at,
        State = state,
        Endpoint = endpoint,
        Model = new RuntimeModelInfo { Id = "test-model-30b", Loaded = true, ContextTokens = 32768 },
        Slots = [new RuntimeSlot(0, "idle", 4096, null, null, null, null), new RuntimeSlot(1, "decoding", 1024, null, null, null, null)],
    };

    public static SharedHardwareHub Hub(TimeProvider time)
    {
        var hub = new SharedHardwareHub(new HardwareMonitor(new CountingGpuProvider(time), new CountingSystemProvider(), time), TimeSpan.FromSeconds(1), time);
        hub.ReadNow();
        return hub;
    }
}

public class GpuCounterInstanceTests
{
    [Theory]
    [InlineData("pid_7412_luid_0x00000000_0x0000C8D1_phys_0", 7412, 0x0000C8D1L)]
    [InlineData("pid_4_luid_0x00000001_0x0000c8d1_phys_1", 4, 0x1_0000C8D1L)]
    [InlineData("pid_0_luid_0xFFFFFFFF_0x00000000_phys_0", 0, unchecked((long)0xFFFF_FFFF_0000_0000UL))]
    public void Process_instance_names_give_the_pid_and_the_adapter_luid(string instance, int pid, long luid)
    {
        Assert.True(CounterInstances.TryParseProcess(instance, out var parsedPid, out var parsedLuid));
        Assert.Equal(pid, parsedPid);
        Assert.Equal(luid, parsedLuid);
        Assert.Equal(instance[(instance.IndexOf("luid_", StringComparison.Ordinal) + 5)..instance.IndexOf("_phys", StringComparison.Ordinal)].ToUpperInvariant().Replace("0X", "0x"),
            GpuLuid.Format(parsedLuid));
    }

    [Theory]
    [InlineData("pid_7412_luid_0x00000000_0x0000C8D1_phys_0_eng_0_engtype_3D")] // a GPU Engine instance, not memory
    [InlineData("_Total")]
    [InlineData("pid_x_luid_0x0_0x1_phys_0")]
    [InlineData("pid_99999999999_luid_0x0_0x1_phys_0")] // larger than any PID
    [InlineData("luid_0x00000000_0x0000C8D1_phys_0")] // an adapter instance in a process array
    public void Unknown_instance_names_are_counted_not_guessed(string instance)
    {
        Assert.False(CounterInstances.TryParseProcess(instance, out _, out _));
        CounterInstances.Merge([new CounterItem(instance, 123)], null, null, null, null, out var unrecognized);
        Assert.Equal(1, unrecognized);
    }

    [Fact]
    public void Physical_parts_of_one_process_on_one_adapter_are_added()
    {
        var (processes, _) = CounterInstances.Merge(
            [new("pid_10_luid_0x00000000_0x00000001_phys_0", 100), new("pid_10_luid_0x00000000_0x00000001_phys_1", 50), new("pid_10_luid_0x00000000_0x00000002_phys_0", 7)],
            [new("pid_10_luid_0x00000000_0x00000001_phys_0", 3)], null, null, null, out var unrecognized);
        Assert.Equal(0, unrecognized);
        Assert.Equal(2, processes.Count);
        var first = processes.Single(p => p.Luid == 1);
        Assert.Equal(150, first.DedicatedBytes);
        Assert.Equal(3, first.SharedBytes);
        var second = processes.Single(p => p.Luid == 2);
        Assert.Equal(7, second.DedicatedBytes);
        Assert.Null(second.SharedBytes); // not in the shared array this sample: unknown, not zero
    }

    [Fact]
    public void A_missing_counter_is_null_for_everyone_and_a_measured_zero_stays_zero()
    {
        var (processes, adapters) = CounterInstances.Merge(
            [new("pid_10_luid_0x00000000_0x00000001_phys_0", 0)],
            [new("pid_10_luid_0x00000000_0x00000001_phys_0", 0)],
            null, // Total Committed could not be added
            [new("luid_0x00000000_0x00000001_phys_0", 0)],
            null,
            out _);
        var row = Assert.Single(processes);
        Assert.Equal(0, row.DedicatedBytes);
        Assert.Equal(0, row.SharedBytes);
        Assert.Null(row.CommittedBytes);
        Assert.Equal((0L, (long?)null), (adapters[1].Dedicated!.Value, adapters[1].Shared));
    }

    [Fact]
    public void Adapter_arrays_merge_by_luid()
    {
        var (_, adapters) = CounterInstances.Merge(null, null, null,
            [new("luid_0x00000000_0x00000001_phys_0", 10 * GpuMemoryFixtures.GiB), new("luid_0x00000000_0x00000002_phys_0", 0)],
            [new("luid_0x00000000_0x00000001_phys_0", GpuMemoryFixtures.GiB)], out _);
        Assert.Equal(10 * GpuMemoryFixtures.GiB, adapters[1].Dedicated);
        Assert.Equal(GpuMemoryFixtures.GiB, adapters[1].Shared);
        Assert.Equal(0, adapters[2].Dedicated);
        Assert.Null(adapters[2].Shared);
    }
}

public class GpuMemoryMonitorTests
{
    [Fact]
    public void A_disabled_reader_never_reads_and_says_why()
    {
        var monitor = new GpuMemoryMonitor(null, disabledReason: "꺼짐 (테스트)");
        monitor.Start();
        Assert.Null(monitor.ReadNow());
        Assert.Equal(GpuMemoryMonitorState.Disabled, monitor.Health.State);
        Assert.Equal("꺼짐 (테스트)", monitor.Health.UnavailableReason);
        Assert.Null(monitor.Latest);
    }

    [Fact]
    public void A_reading_keeps_its_original_timestamp_and_goes_into_the_history()
    {
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var provider = new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(time.GetUtcNow()));
        var monitor = new GpuMemoryMonitor(() => provider, time);
        var first = monitor.ReadNow()!;
        time.Advance(TimeSpan.FromSeconds(2));
        monitor.ReadNow();

        Assert.Equal(GpuMemoryFixtures.Start, first.Timestamp);
        Assert.Equal(GpuMemoryMonitor.DefaultBackgroundInterval, first.ExpectedInterval);
        Assert.Equal(GpuMemoryFixtures.Start.AddSeconds(2), monitor.Latest!.Timestamp);
        var history = monitor.History(TimeSpan.FromMinutes(5));
        Assert.Equal([GpuMemoryFixtures.Start, GpuMemoryFixtures.Start.AddSeconds(2)], history.Select(h => h.Timestamp));
        Assert.Equal(4 * GpuMemoryFixtures.GiB, history[0].Processes.Single().DedicatedBytes);
        Assert.Equal("app.exe", history[0].Processes.Single().Name);
    }

    [Fact]
    public void The_history_keeps_gpu_utilization_only_where_the_engines_were_read()
    {
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var engines = false;
        var provider = new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(time.GetUtcNow(),
            processes:
            [
                new GpuProcessMemory { ProcessId = 100, Luid = 1, DedicatedBytes = 4 * GpuMemoryFixtures.GiB, SharedBytes = 0, CommittedBytes = 4 * GpuMemoryFixtures.GiB,
                    Engines = engines ? new EngineUsage(0, 42, 0, 0, 0) : null },
                new GpuProcessMemory { ProcessId = 200, Luid = 1, DedicatedBytes = 512 * GpuMemoryFixtures.MiB, SharedBytes = 0, CommittedBytes = 512 * GpuMemoryFixtures.MiB },
            ],
            table: new Dictionary<int, ProcessEntry>
            {
                [100] = new(100, 10, "app.exe", time.GetUtcNow().AddMinutes(-5), ProcessIdentityState.Verified),
                [200] = new(200, 10, "idle.exe", time.GetUtcNow().AddMinutes(-5), ProcessIdentityState.Verified),
            }) with
        {
            EnginesRead = engines,
            Adapters = [new GpuAdapterMemory { Luid = 1, DedicatedUsageBytes = 5 * GpuMemoryFixtures.GiB, Engines = engines ? new EngineUsage(3, 42, 0, 0, 0) : null }],
        });
        var monitor = new GpuMemoryMonitor(() => provider, time);
        monitor.ReadNow(); // the page was not open: no engine counters read
        engines = true;
        time.Advance(TimeSpan.FromSeconds(2));
        monitor.ReadNow();

        var history = monitor.History(TimeSpan.FromMinutes(5));
        Assert.Null(history[0].Processes.Single(p => p.Name == "app.exe").Busiest); // not read: a gap, never 0 %
        Assert.Null(history[0].Adapters.Single().Busiest);
        Assert.Equal(42, history[1].Processes.Single(p => p.Name == "app.exe").Busiest);
        Assert.Equal(0, history[1].Processes.Single(p => p.Name == "idle.exe").Busiest); // read and not listed: idle
        Assert.Equal(42, history[1].Adapters.Single().Busiest);
    }

    [Fact]
    public void The_history_keeps_a_bounded_window()
    {
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var monitor = new GpuMemoryMonitor(() => new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(time.GetUtcNow())), time,
            historyWindow: TimeSpan.FromMinutes(1));
        for (var i = 0; i < 40; i++)
        {
            monitor.ReadNow();
            time.Advance(TimeSpan.FromSeconds(10));
        }

        var history = monitor.History(TimeSpan.FromHours(1));
        Assert.True(history[^1].Timestamp - history[0].Timestamp <= TimeSpan.FromMinutes(1));
        Assert.InRange(history.Count, 6, 7);
    }

    [Fact]
    public void A_throwing_provider_costs_only_this_page_and_is_counted()
    {
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var fail = false;
        var provider = new ScriptedGpuMemoryProvider(_ => fail ? throw new InvalidOperationException("driver reset") : GpuMemoryFixtures.Snapshot(time.GetUtcNow()));
        var monitor = new GpuMemoryMonitor(() => provider, time);
        var good = monitor.ReadNow();
        fail = true;
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Null(monitor.ReadNow()); // no exception escapes
        Assert.Same(good, monitor.Latest); // the last good reading keeps its own (old) timestamp; the page judges its age
        Assert.Equal(1, monitor.Health.Failures);
        Assert.Equal("driver reset", monitor.Health.LastError);
    }

    [Fact]
    public void A_provider_that_cannot_be_created_is_reported_as_unavailable()
    {
        var monitor = new GpuMemoryMonitor(() => throw new DllNotFoundException("pdh.dll"));
        Assert.Null(monitor.ReadNow());
        Assert.Equal(GpuMemoryMonitorState.Unavailable, monitor.Health.State);
        Assert.Contains("pdh.dll", monitor.Health.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Watched_ports_reach_the_provider_and_invalid_ports_are_dropped()
    {
        var provider = new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(GpuMemoryFixtures.Start));
        var monitor = new GpuMemoryMonitor(() => provider);
        monitor.SetWatchedPorts([18500, 0, 70000]);
        monitor.ReadNow();
        Assert.Equal([18500], provider.LastRequest!.WatchedPorts);
        monitor.SetWatchedPorts([]);
        monitor.ReadNow();
        Assert.Empty(provider.LastRequest!.WatchedPorts);
    }

    [Fact(Timeout = 30_000)]
    public async Task The_reader_runs_on_its_own_thread_reads_at_once_when_the_page_opens_and_stops_cleanly()
    {
        var provider = new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(DateTimeOffset.UtcNow));
        var callerThread = Environment.CurrentManagedThreadId;
        var readerThread = 0;
        await using var monitor = new GpuMemoryMonitor(() => provider, activeInterval: TimeSpan.FromMilliseconds(100), backgroundInterval: TimeSpan.FromMinutes(10));
        monitor.Sampled += _ => readerThread = Environment.CurrentManagedThreadId;
        monitor.Start();
        Assert.True(await Until(() => provider.Reads >= 1)); // the first reading comes right away
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.Reads); // background: one reading per 10 minutes

        monitor.SetActive(true); // the page opened: read now, then every 100 ms
        Assert.True(await Until(() => provider.Reads >= 4));
        Assert.Equal(TimeSpan.FromMilliseconds(100), monitor.Latest!.ExpectedInterval);
        Assert.NotEqual(callerThread, readerThread);
        Assert.Equal(GpuMemoryMonitorState.Running, monitor.Health.State);

        await monitor.StopAsync();
        var reads = provider.Reads;
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(reads, provider.Reads);
        Assert.True(provider.Disposed);
        Assert.Equal(GpuMemoryMonitorState.Stopped, monitor.Health.State);
    }

    [Fact]
    public void A_reused_pid_with_another_start_time_is_another_process_in_the_history()
    {
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var monitor = new GpuMemoryMonitor(() => new MockGpuMemoryProvider(time), time);
        monitor.ReadNow(); // PID 6000 = game.exe, started now
        time.Advance(TimeSpan.FromSeconds(120));
        monitor.ReadNow(); // PID 6000 = renderer.exe, started 120 s later

        var keys = monitor.History(TimeSpan.FromHours(1))
            .SelectMany(h => h.Processes.Where(p => p.Key.ProcessId == MockGpuMemoryProvider.CyclingPid).Select(p => (p.Key, p.Name)))
            .ToList();
        Assert.Equal(2, keys.Count);
        Assert.NotEqual(keys[0].Key, keys[1].Key);
        Assert.Equal(["game.exe", "renderer.exe"], keys.Select(k => k.Name));
    }

    private static async Task<bool> Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return condition();
    }
}

public class ServerProcessLinkerTests
{
    private static readonly DateTimeOffset T = GpuMemoryFixtures.Start;

    private static GpuMemorySnapshot Tree(IEnumerable<ProcessEntry> entries, IReadOnlyList<TcpListenerEntry> listeners, params GpuProcessMemory[] memory)
        => GpuMemoryFixtures.Snapshot(T, memory, entries.ToDictionary(e => e.ProcessId), listeners);

    [Fact]
    public void The_engine_child_of_the_server_is_linked_through_the_port_owner_and_start_times()
    {
        var time = new ManualTime(T);
        var snapshot = new MockGpuMemoryProvider(time).Read(new GpuMemoryReadRequest(new HashSet<int> { MockGpuMemoryProvider.ServerPort }));

        var link = ServerProcessLinker.Link("http://127.0.0.1:18500", snapshot);

        Assert.True(link.IsLinked, link.Reason);
        Assert.Equal(MockGpuMemoryProvider.ServerPid, link.Listener!.ProcessId);
        Assert.Equal([MockGpuMemoryProvider.ServerPid, MockGpuMemoryProvider.EnginePid], link.Processes.Select(p => p.Process.ProcessId));
        Assert.Equal([0, 1], link.Processes.Select(p => p.Depth));
        var engine = link.Processes[1].Memory.Single();
        Assert.True(engine.DedicatedBytes > 9 * GpuMemoryFixtures.GiB);
        Assert.Contains(link.Evidence, e => e.Contains("GetExtendedTcpTable", StringComparison.Ordinal));
        Assert.Contains(link.Evidence, e => e.Contains("하위 프로세스", StringComparison.Ordinal));
        // The desktop app above the server and unrelated processes are not part of the server.
        Assert.DoesNotContain(link.Processes, p => p.Process.Name is "Strata.exe" or "chrome.exe");
    }

    [Fact]
    public void A_reused_parent_pid_does_not_adopt_an_older_process()
    {
        var link = ServerProcessLinker.Link("http://127.0.0.1:9000", Tree(
            [new(50, 1, "server.exe", T.AddMinutes(-1), ProcessIdentityState.Verified),
             new(60, 50, "old-engine.exe", T.AddMinutes(-30), ProcessIdentityState.Verified)], // started before PID 50's current process
            [new TcpListenerEntry(9000, 50, false, true)],
            new GpuProcessMemory { ProcessId = 60, Luid = 1, DedicatedBytes = 8 * GpuMemoryFixtures.GiB }));

        Assert.True(link.IsLinked);
        Assert.Equal([50], link.Processes.Select(p => p.Process.ProcessId));
    }

    [Fact]
    public void A_child_without_a_start_time_is_listed_as_unverified_not_linked()
    {
        var link = ServerProcessLinker.Link("http://localhost:9000", Tree(
            [new(50, 1, "server.exe", T.AddMinutes(-1), ProcessIdentityState.Verified),
             new(60, 50, "engine.exe", null, ProcessIdentityState.StartTimeUnavailable)],
            [new TcpListenerEntry(9000, 50, true, true)],
            new GpuProcessMemory { ProcessId = 60, Luid = 1, DedicatedBytes = GpuMemoryFixtures.GiB }));

        Assert.True(link.IsLinked);
        Assert.Single(link.Processes);
        Assert.Contains(link.Unverified, u => u.Contains("engine.exe", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://192.168.0.10:18500", "이 PC가 아니라서")]
    [InlineData("http://127.0.0.1:18501", "찾지 못했습니다")]
    [InlineData("not a url", "주소가 없습니다")]
    public void Nothing_is_linked_without_a_unique_local_listener(string endpoint, string reason)
    {
        var link = ServerProcessLinker.Link(endpoint, Tree(
            [new(50, 1, "server.exe", T.AddMinutes(-1), ProcessIdentityState.Verified)], [new TcpListenerEntry(18500, 50, false, true)]));
        Assert.False(link.IsLinked);
        Assert.Contains(reason, link.Reason, StringComparison.Ordinal);
        Assert.Empty(link.Processes);
    }

    [Fact]
    public void Two_processes_on_one_port_or_a_kernel_owned_port_are_not_guessed()
    {
        var entries = new ProcessEntry[] { new(50, 1, "a.exe", T.AddMinutes(-1), ProcessIdentityState.Verified), new(51, 1, "b.exe", T.AddMinutes(-1), ProcessIdentityState.Verified) };
        var ambiguous = ServerProcessLinker.Link("http://127.0.0.1:9000", Tree(entries, [new(9000, 50, false, true), new(9000, 51, true, true)]));
        Assert.False(ambiguous.IsLinked);
        Assert.Contains("여러 프로세스", ambiguous.Reason, StringComparison.Ordinal);

        var kernel = ServerProcessLinker.Link("http://127.0.0.1:80", Tree(entries, [new(80, 4, false, false)]));
        Assert.False(kernel.IsLinked);
        Assert.Contains("커널", kernel.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_listener_that_ended_or_was_replaced_after_the_sample_is_not_linked()
    {
        var link = ServerProcessLinker.Link("http://127.0.0.1:9000", Tree(
            [new(50, 1, "server.exe", T.AddSeconds(1), ProcessIdentityState.ReusedAfterSample)], [new(9000, 50, false, true)]));
        Assert.False(link.IsLinked);
    }
}

public class GpuMemoryViewModelTests
{
    private const long MiB = GpuMemoryFixtures.MiB;
    private const long GiB = GpuMemoryFixtures.GiB;

    private static (GpuMemoryViewModel Vm, GpuMemoryMonitor Monitor, ManualTime Time) Create(Func<StrataContext>? strata = null,
        Func<ManualTime, IGpuMemoryProvider>? provider = null, IRuntimeHttp? http = null)
    {
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var monitor = new GpuMemoryMonitor(() => provider?.Invoke(time) ?? new MockGpuMemoryProvider(time), time);
        var vm = new GpuMemoryViewModel(monitor, GpuMemoryFixtures.Hub(time), strata ?? (() => new StrataContext(null, "로컬 AI 모듈 꺼짐 (테스트)")), time,
            runtimeHttp: http);
        return (vm, monitor, time);
    }

    /// <summary>The page is open (details and engines are read), Strata is polled now and serves the MOCK port.</summary>
    private static (GpuMemoryViewModel Vm, GpuMemoryMonitor Monitor, ManualTime Time) OpenWithStrata(IRuntimeHttp? http = null, string? keyVariable = null)
    {
        ManualTime? clock = null;
        var created = Create(() => new StrataContext(GpuMemoryFixtures.Strata(clock!.GetUtcNow()), null, keyVariable), http: http);
        clock = created.Time;
        created.Vm.IsVisible = true;
        created.Monitor.ReadNow();
        created.Vm.Refresh();
        return created;
    }

    private static GpuProcessRow Row(GpuMemoryViewModel vm, string name) => vm.Processes.Single(r => r.Name == name);

    [Fact]
    public void The_device_strip_splits_the_measured_memory_by_process_kind_and_keeps_the_rest_unattributed()
    {
        var (vm, _, _) = OpenWithStrata();

        Assert.True(vm.HasValues);
        Assert.True(vm.IsMock);
        Assert.Equal(Level.Warning, vm.StatusLevel); // MOCK is never "good"
        Assert.Contains("MOCK", vm.StatusToolTip, StringComparison.Ordinal);
        var nvidia = vm.Adapters[0].View;
        Assert.Contains("RTX 5080", nvidia.Name, StringComparison.Ordinal);
        Assert.EndsWith("/ 15.71 GiB", nvidia.DedicatedText, StringComparison.Ordinal);
        // The kinds come from evidence; what the processes do not account for stays "나뉘지 않음" (never spread over them).
        Assert.Equal("LLM", nvidia.Legend[0].Label);
        Assert.Equal("260 MiB", nvidia.Legend.Single(l => l.Label == "나뉘지 않음").Value);
        Assert.All(nvidia.Legend, l => Assert.False(l.IsEstimate));
        Assert.True(nvidia.DedicatedSegments.Sum(x => x.Fraction) <= 1.0);
        Assert.Null(nvidia.LegendNote);
        Assert.Equal("NVML 4.00 GiB / 16.00 GiB", nvidia.Nvml); // one NVIDIA adapter, one NVML device: paired
        Assert.EndsWith("%", nvidia.Utilization, StringComparison.Ordinal);
        Assert.StartsWith("GPU", nvidia.UtilizationLabel, StringComparison.Ordinal);
        Assert.Single(nvidia.SharedSegments);

        Assert.False(nvidia.IsCompact);

        var integrated = vm.Adapters[1].View;
        Assert.True(integrated.IsCompact); // further adapters on one line, the legend in a tooltip
        Assert.Equal("화면 합성 22 MiB · 나뉘지 않음 8 MiB", integrated.LegendText);
        Assert.Contains("통합 GPU일 수 있음", integrated.Meta, StringComparison.Ordinal);
        Assert.Null(integrated.Nvml);
    }

    [Fact]
    public void Counter_timing_noise_is_no_double_counting_warning_and_a_display_only_adapter_is_not_called_integrated()
    {
        var (vm, monitor, _) = Create(provider: t => new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(t.GetUtcNow(),
            processes: [new GpuProcessMemory { ProcessId = 100, Luid = 1, DedicatedBytes = 5 * GiB + 3 * MiB, SharedBytes = 0, CommittedBytes = 5 * GiB }]) with
        {
            Adapters =
            [
                new GpuAdapterMemory { Luid = 1, Info = new GpuAdapterInfo(1, "Test GPU", GpuVendors.Nvidia, 16 * GiB, 32 * GiB, false), DedicatedUsageBytes = 5 * GiB, SharedUsageBytes = GiB },
                new GpuAdapterMemory { Luid = 2, Info = new GpuAdapterInfo(2, "Display Adapter", GpuVendors.Microsoft, 0, 8 * GiB, false) },
            ],
        }));
        monitor.ReadNow();
        vm.Refresh();

        Assert.Null(vm.Adapters[0].View.LegendNote); // 3 MiB over the adapter: counter timing, not shared resources counted twice
        var display = vm.Adapters[1].View;
        Assert.True(display.IsCompact);
        Assert.Contains("전용 메모리 없음", display.Meta, StringComparison.Ordinal);
        Assert.DoesNotContain("통합 GPU", display.Meta, StringComparison.Ordinal);
        Assert.Equal(Text.NA, display.DedicatedText);
    }

    [Fact]
    public void Rows_show_kind_runtime_and_contents_and_the_largest_process_is_selected()
    {
        var (vm, _, _) = OpenWithStrata();

        var engine = Row(vm, "strata.exe");
        Assert.Same(engine, vm.SelectedProcess); // largest local memory
        Assert.True(engine.IsStrata);
        Assert.Equal("LLM", engine.KindLabel);
        Assert.Contains("Strata 서버 포트", engine.KindBasis, StringComparison.Ordinal);
        Assert.Equal("ggml · CUDA", engine.Runtimes);
        Assert.Equal("MOCK Qwen3 14B · 14.8B · Q4_K_M", engine.Inside);
        Assert.False(engine.InsideIsGeneric); // read from the mapped model file
        Assert.True(Row(vm, "chrome.exe").InsideIsGeneric); // what browsers usually hold, not found in this one
        Assert.Equal("페이지·영상 텍스처", Row(vm, "chrome.exe").Inside);
        Assert.Null(engine.Identity);
        Assert.False(engine.IsUncertain);
        Assert.InRange(engine.Gpu, 30, 60);
        Assert.Equal("권한 없음", vm.Processes.First(r => r.Name == "dwm.exe").Runtimes);
        Assert.Equal("화면 합성", vm.Processes.First(r => r.Name == "dwm.exe").KindLabel);
        Assert.Equal("브라우저", Row(vm, "chrome.exe").KindLabel);
        Assert.Equal("커널", Row(vm, "System").KindLabel);
        Assert.True(Row(vm, "System").IsUncertain); // no start time: identity not verified
        Assert.NotNull(Row(vm, "System").Identity);
        Assert.DoesNotContain(vm.Processes, r => r.Name == "notepad.exe"); // 300 KiB: below the listing threshold
        Assert.Contains("숨김", vm.CountText, StringComparison.Ordinal);
        Assert.True(vm.ShowGpuColumn); // dwm.exe has memory on both adapters
        Assert.Equal(2, vm.Processes.Count(r => r.Name == "dwm.exe"));
    }

    [Fact]
    public void The_detail_keeps_measured_values_strata_reports_and_estimates_apart()
    {
        var (vm, _, _) = OpenWithStrata();
        var detail = vm.Detail!;

        Assert.Equal("strata.exe", detail.Title);
        Assert.Equal(MockGpuMemoryProvider.EnginePid.ToString(System.Globalization.CultureInfo.InvariantCulture), detail.Pid);
        Assert.True(detail.IsStrata);
        Assert.StartsWith("9.", detail.Dedicated, StringComparison.Ordinal);
        Assert.EndsWith(" GiB", detail.Dedicated, StringComparison.Ordinal);
        Assert.EndsWith("%", detail.Gpu, StringComparison.Ordinal);
        Assert.Contains("연산", detail.Engines, StringComparison.Ordinal);
        Assert.Equal("ggml · CUDA", detail.Runtimes);

        // Strata's own report: model, context, tokens held by the slots (not bytes).
        Assert.Equal("test-model-30b", detail.StrataModel);
        Assert.Equal("32,768 토큰", detail.StrataWindow);
        Assert.Equal("5,120 (슬롯 2)", detail.StrataSlots);
        Assert.False(string.IsNullOrEmpty(detail.StrataEvidence));

        // The model file header is shown, but no split: Strata's engine places experts, KV (int8 by default) and session
        // buffers its own way, so a split from the GGUF header would be wrong however it is labelled.
        var model = Assert.Single(detail.Models);
        Assert.Equal("MOCK Qwen3 14B", model.Name);
        Assert.Equal("qwen3 · 14.8B · Q4_K_M · 40층 · 학습 컨텍스트 40,960 · KV 160 KiB/토큰", model.Summary);
        Assert.Equal("8.38 GiB", model.Size);
        Assert.Empty(detail.Breakdown);
        Assert.Empty(detail.BreakdownLegend);
        Assert.StartsWith("Strata 엔진은", detail.BreakdownNote, StringComparison.Ordinal);

        Assert.NotNull(detail.Chart);
        Assert.Equal("GiB", detail.Chart!.Unit);
        Assert.Equal(["GPU 전체", "strata.exe"], detail.Chart.Series.Select(x => x.Label));
        Assert.Equal("Strata · strata.exe", vm.StrataText);
        Assert.Equal(Level.Good, vm.StrataLevel);
    }

    /// <summary>One llama.cpp process holding <paramref name="model"/>; with <paramref name="strata"/> it serves Strata's port.</summary>
    private static (GpuMemoryViewModel Vm, GpuMemoryMonitor Monitor) LlmProcess(GgufSummary model, bool strata)
    {
        var start = GpuMemoryFixtures.Start.AddMinutes(-10);
        ManualTime? clock = null;
        var created = Create(
            strata ? () => new StrataContext(GpuMemoryFixtures.Strata(clock!.GetUtcNow()), null) : null,
            t => new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(t.GetUtcNow(),
                processes: [new GpuProcessMemory { ProcessId = 60, Luid = 1, DedicatedBytes = 2 * GiB, SharedBytes = 50 * MiB, CommittedBytes = 2 * GiB + 100 * MiB, Engines = new EngineUsage(0, 35, 1, 0, 0) }],
                table: new Dictionary<int, ProcessEntry>
                {
                    [50] = new(50, 10, "server.exe", start, ProcessIdentityState.Verified),
                    [60] = new(60, 50, "llama-server.exe", start.AddSeconds(1), ProcessIdentityState.Verified),
                },
                listeners: [new TcpListenerEntry(18500, 50, false, true)]) with
            {
                EnginesRead = true,
                Details = new Dictionary<int, ProcessDetail>
                {
                    [60] = new() { Runtimes = ["llama.cpp", "ggml", "CUDA"], Models = [new ModelFile("model.gguf", 639_446_688, "GGUF", model)], ScannedAt = t.GetUtcNow() },
                },
            }));
        clock = created.Time;
        created.Vm.IsVisible = true;
        created.Monitor.ReadNow();
        created.Vm.Refresh();
        return (created.Vm, created.Monitor);
    }

    [Fact]
    public void A_header_based_split_is_drawn_only_where_the_header_can_say_it()
    {
        var dense = new GgufSummary
        {
            Name = "Qwen3 0.6B",
            Architecture = "qwen3",
            Kind = "LLM",
            Parameters = 596_049_920,
            Quantization = "Q8_0",
            Layers = 28,
            TrainedContext = 40_960,
            TensorBytes = 610 * MiB,
            GpuWeightBytesAllLayers = 600 * MiB,
            KvBytesPerTokenF16 = GgufHeader.KvBytesPerToken(28, 16L, 8L, 1024L, 128L, 128L),
        };

        // A plain llama.cpp process: the weights from the tensor table; KV is not split without a context from the runtime.
        var (plain, _) = LlmProcess(dense, strata: false);
        var row = Row(plain, "llama-server.exe");
        Assert.False(row.IsStrata);
        Assert.Equal("Qwen3 0.6B · 596M · Q8_0", row.Inside);
        var detail = plain.Detail!;
        Assert.Equal("qwen3 · 596M · Q8_0 · 28층 · 학습 컨텍스트 40,960 · KV 112 KiB/토큰", detail.Models.Single().Summary);
        Assert.Equal([("가중치", "600 MiB"), ("KV·버퍼·런타임", "1.41 GiB")], detail.BreakdownLegend.Select(l => (l.Label, l.Value)));
        Assert.All(detail.Breakdown, b => Assert.True(b.IsEstimate));
        Assert.All(detail.BreakdownLegend, l => Assert.True(l.IsEstimate));
        Assert.StartsWith("모든 층이 GPU에 있다고 가정한 추정입니다.", detail.BreakdownNote, StringComparison.Ordinal);
        Assert.Contains("런타임이 컨텍스트를 알려 주지 않아", detail.BreakdownNote, StringComparison.Ordinal);
        Assert.Equal("2.00 GiB", detail.Dedicated); // the only measured part
        Assert.Equal("35%", detail.Gpu);

        // The same file served by Strata: its engine's own placement, so no split from the header.
        var (served, _) = LlmProcess(dense, strata: true);
        Assert.True(Row(served, "llama-server.exe").IsStrata);
        Assert.Empty(served.Detail!.Breakdown);
        Assert.StartsWith("Strata 엔진은", served.Detail.BreakdownNote, StringComparison.Ordinal);
        Assert.Equal("32,768 토큰", served.Detail.StrataWindow);

        // A mixture of experts: which experts are on the GPU is not in the header.
        var (moe, _) = LlmProcess(dense with { Experts = 128 }, strata: false);
        Assert.Empty(moe.Detail!.Breakdown);
        Assert.StartsWith("전문가(MoE) 모델은", moe.Detail.BreakdownNote, StringComparison.Ordinal);

        // Recurrent or linear layers: the weights still come from the header, the KV size does not.
        var (hybrid, _) = LlmProcess(dense with { KvBytesPerTokenF16 = null, KvUnknownReason = "순환·선형 층이 섞인 구조" }, strata: false);
        Assert.Equal(["가중치", "KV·버퍼·런타임"], hybrid.Detail!.BreakdownLegend.Select(l => l.Label));
        Assert.Contains("순환·선형 층이 섞인 구조라 KV를 헤더로 계산할 수 없어", hybrid.Detail.BreakdownNote, StringComparison.Ordinal);
        Assert.DoesNotContain("KV", hybrid.Detail.Models.Single().Summary.Split(" · ").Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void Weights_larger_than_the_process_are_shown_beside_the_measured_total_without_a_split()
    {
        var model = new GgufSummary { Name = "Big", Architecture = "llama", Kind = "LLM", Parameters = 8_000_000_000, Layers = 32, GpuWeightBytesAllLayers = 3 * GiB };
        var (vm, _) = LlmProcess(model, strata: false); // the process holds 2 GiB

        var detail = vm.Detail!;
        Assert.Empty(detail.Breakdown);
        Assert.Equal([("가중치 (모든 층 GPU 가정, 이론값)", "3.00 GiB", true), ("이 프로세스 전용 (측정)", "2.00 GiB", false)],
            detail.BreakdownLegend.Select(l => (l.Label, l.Value, l.IsEstimate)));
        Assert.Contains("일부 층만 GPU에 있는 것으로 보입니다", detail.BreakdownNote, StringComparison.Ordinal);
        Assert.Contains("비교: 가중치 (모든 층 GPU 가정, 이론값) 3.00 GiB", vm.DetailText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_and_slots_are_updated_in_place_so_selection_and_open_tooltips_survive_readings()
    {
        var (vm, monitor, time) = OpenWithStrata();
        var chrome = Row(vm, "chrome.exe");
        vm.SelectedProcess = chrome;
        Assert.Equal("chrome.exe", vm.Detail!.Title);
        Assert.Empty(vm.Detail.Breakdown); // no split for a process without an LLM model file
        Assert.Equal("D3D11", vm.Detail.Runtimes);
        var slot = vm.Adapters[0];
        var view = slot.View;

        time.Advance(TimeSpan.FromSeconds(2));
        monitor.ReadNow();
        vm.Refresh();

        Assert.Same(chrome, Row(vm, "chrome.exe"));
        Assert.Same(chrome, vm.SelectedProcess);
        Assert.Equal("chrome.exe", vm.Detail!.Title);
        Assert.Same(slot, vm.Adapters[0]);
        Assert.NotSame(view, slot.View);
    }

    [Fact]
    public void Filter_matches_names_and_pids_and_small_processes_can_be_listed()
    {
        var (vm, _, _) = OpenWithStrata();

        Assert.Equal("", vm.EmptyText);
        vm.Filter = "no-such-process";
        Assert.Empty(vm.Processes);
        Assert.Null(vm.Detail);
        Assert.Equal("'no-such-process'와 맞는 프로세스가 없습니다", vm.EmptyText);
        vm.Filter = "CHROME";
        Assert.Equal(["chrome.exe"], vm.Processes.Select(r => r.Name));
        Assert.Same(vm.Processes[0], vm.SelectedProcess); // the selection follows the filter
        vm.Filter = "1520";
        Assert.All(vm.Processes, r => Assert.Equal(1520, r.Pid));
        Assert.Equal(2, vm.Processes.Count);
        vm.Filter = "";
        Assert.DoesNotContain(vm.Processes, r => r.Name == "notepad.exe");
        vm.ShowAll = true;
        Assert.Contains(vm.Processes, r => r.Name == "notepad.exe");
        Assert.DoesNotContain("숨김", vm.CountText, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_table_says_why()
    {
        var (vm, monitor, _) = Create(provider: t => new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(t.GetUtcNow(),
            processes: [new GpuProcessMemory { ProcessId = 100, Luid = 1, DedicatedBytes = 0, SharedBytes = 24 * 1024, CommittedBytes = 24 * 1024 }])));
        monitor.ReadNow();
        vm.Refresh();

        Assert.True(vm.HasValues); // a reading, just nothing worth listing (a PC whose GPU is idle, the CI runner)
        Assert.Empty(vm.Processes);
        Assert.Contains("'작은 프로세스 포함'으로 1개", vm.EmptyText, StringComparison.Ordinal);
        vm.ShowAll = true;
        Assert.Single(vm.Processes);
        Assert.Equal("", vm.EmptyText);
    }

    [Fact]
    public void Pausing_holds_the_values_until_resumed()
    {
        var (vm, monitor, time) = OpenWithStrata();
        var engine = Row(vm, "strata.exe");
        var held = engine.Dedicated;

        vm.TogglePauseCommand.Execute(null);
        Assert.StartsWith("일시정지", vm.StatusText, StringComparison.Ordinal);
        time.Advance(TimeSpan.FromSeconds(30)); // the MOCK engine's memory moves with time
        monitor.ReadNow();
        vm.Refresh();
        Assert.Equal(held, engine.Dedicated);

        vm.TogglePauseCommand.Execute(null);
        Assert.NotEqual(held, engine.Dedicated);
        Assert.DoesNotContain("일시정지", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void An_old_reading_is_not_shown_as_the_current_value()
    {
        var (vm, monitor, time) = Create();
        monitor.ReadNow();
        vm.Refresh();
        Assert.NotEmpty(vm.Processes);

        time.Advance(GpuMemoryViewModel.StaleAfter(monitor.Latest!) + TimeSpan.FromSeconds(1));
        vm.Refresh();

        Assert.False(vm.HasValues);
        Assert.Empty(vm.Processes);
        Assert.Empty(vm.Adapters);
        Assert.Null(vm.Detail);
        Assert.Equal(Level.Error, vm.StatusLevel);
        Assert.StartsWith("새 측정 없음", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal("새 측정값이 없습니다", vm.Message);
        Assert.Contains("표시하지 않습니다", vm.MessageDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_reachable_recent_local_strata_nothing_is_linked()
    {
        ManualTime? clock = null;
        var state = RuntimeConnectionState.Unreachable;
        var age = TimeSpan.Zero;
        var (vm, monitor, time) = Create(() => new StrataContext(GpuMemoryFixtures.Strata(clock!.GetUtcNow() - age, state), null));
        clock = time;
        vm.IsVisible = true;
        monitor.ReadNow();
        vm.Refresh();
        Assert.DoesNotContain(vm.Processes, p => p.IsStrata);
        Assert.Equal(Level.Muted, vm.StrataLevel);
        Assert.Null(vm.Detail!.StrataModel);
        // Without the link the engine process is still an LLM process, now on the evidence of its mapped model file.
        Assert.Equal("LLM", Row(vm, "strata.exe").KindLabel);
        Assert.Contains("LLM 모델 파일 매핑", Row(vm, "strata.exe").KindBasis, StringComparison.Ordinal);

        state = RuntimeConnectionState.Connected;
        age = GpuMemoryViewModel.StrataMaxAge + TimeSpan.FromSeconds(1); // the last poll is too old: the port may belong to someone else
        vm.Refresh();
        Assert.Null(GpuMemoryViewModel.StrataPort(new StrataContext(GpuMemoryFixtures.Strata(time.GetUtcNow() - age), null), time.GetUtcNow()));
        Assert.DoesNotContain(vm.Processes, p => p.IsStrata);
        Assert.Equal(Level.Warning, vm.StrataLevel);
        Assert.Contains("연결하지 않습니다", vm.StrataToolTip, StringComparison.Ordinal);
        Assert.Empty(monitor.WatchedPorts);
    }

    [Fact]
    public void An_unavailable_reader_says_why_and_shows_no_values()
    {
        var (vm, monitor, _) = Create(provider: t => new UnavailableGpuMemoryProvider("카운터 없음 (테스트)", t));
        monitor.ReadNow();
        vm.Refresh();
        Assert.Equal(Level.Warning, vm.StatusLevel);
        Assert.Equal("사용 불가", vm.StatusText);
        Assert.Equal("Windows GPU 카운터를 읽을 수 없습니다", vm.Message);
        Assert.Contains("카운터 없음 (테스트)", vm.MessageDetail, StringComparison.Ordinal);
        Assert.False(vm.HasValues);
        Assert.Empty(vm.Adapters);
        Assert.Equal("Strata 꺼짐", vm.StrataText);
        Assert.Equal("로컬 AI 모듈 꺼짐 (테스트)", vm.StrataToolTip);
    }

    [Fact]
    public void Fallback_counters_are_flagged_and_a_larger_process_sum_is_a_warning()
    {
        var (vm, monitor, _) = Create(provider: t => new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(t.GetUtcNow()) with
        {
            Variant = ProcessCounterVariant.DedicatedShared,
            Processes = [new GpuProcessMemory { ProcessId = 100, Luid = 1, DedicatedBytes = 7 * GiB, SharedBytes = 0, CommittedBytes = null }],
            CommittedAvailable = false,
        }));
        monitor.ReadNow();
        vm.Refresh();

        Assert.Equal(Level.Warning, vm.StatusLevel);
        Assert.Contains("중복 집계", vm.StatusToolTip, StringComparison.Ordinal);
        var adapter = vm.Adapters.Single().View;
        Assert.Contains("2.00 GiB 큽니다", adapter.LegendNote, StringComparison.Ordinal);
        Assert.DoesNotContain(adapter.Legend, l => l.Label == "나뉘지 않음");
        Assert.Equal("7.00 GiB", adapter.Legend.Single().Value); // the legend keeps the measured number
        Assert.Equal(5.0 / 16, adapter.DedicatedSegments.Single().Fraction, 6); // the bar stays within the adapter's usage
        Assert.Equal(Text.NA, vm.Processes.Single().CommittedText);
        Assert.Equal("N/A", vm.Detail!.Committed);
        Assert.Equal("N/A", vm.Detail.Gpu); // engines were not read (the page is not open)
        Assert.Equal("", vm.Detail.Engines);
        Assert.Equal("아직 확인하지 않음", vm.Detail.Runtimes);
    }

    [Fact]
    public void Showing_the_page_switches_the_reader_to_its_short_interval_and_asks_for_details()
    {
        ScriptedGpuMemoryProvider? provider = null;
        var (vm, monitor, _) = Create(provider: t => provider = new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(t.GetUtcNow())));
        Assert.Equal(GpuMemoryMonitor.DefaultBackgroundInterval, monitor.Interval);
        monitor.ReadNow();
        Assert.False(provider!.LastRequest!.IncludeDetails);

        vm.IsVisible = true;
        Assert.Equal(GpuMemoryMonitor.DefaultActiveInterval, monitor.Interval);
        monitor.ReadNow();
        Assert.True(provider.LastRequest!.IncludeDetails);

        vm.IsVisible = false;
        Assert.Equal(GpuMemoryMonitor.DefaultBackgroundInterval, monitor.Interval);
        monitor.ReadNow();
        Assert.False(provider.LastRequest!.IncludeDetails);
    }

    [Fact]
    public void A_legend_entry_filters_the_table_by_kind_and_the_same_entry_shows_all_again()
    {
        var (vm, _, _) = OpenWithStrata();
        var all = vm.Processes.Count;
        var browser = vm.Adapters[0].View.Legend.Single(l => l.Label == "브라우저");
        Assert.Equal(ProcessKind.Browser, browser.Kind);
        Assert.Null(vm.Adapters[0].View.Legend.Single(l => l.Label == "나뉘지 않음").Kind); // computed, not a kind: not a filter

        vm.FilterKindCommand.Execute(browser.Kind);
        Assert.Equal(["chrome.exe"], vm.Processes.Select(r => r.Name));
        Assert.Equal("종류: 브라우저", vm.KindFilterText);
        Assert.Equal($"1/{all}개 표시", vm.CountText);
        Assert.True(vm.Adapters[0].View.Legend.Single(l => l.Label == "브라우저").IsActive);
        Assert.Equal("chrome.exe", vm.SelectedProcess!.Name); // the selection follows the filter

        vm.FilterKindCommand.Execute(browser.Kind);
        Assert.Null(vm.KindFilter);
        Assert.Equal("", vm.KindFilterText);
        Assert.Equal(all, vm.Processes.Count);

        vm.FilterKindCommand.Execute(ProcessKind.Video);
        Assert.Empty(vm.Processes);
        Assert.Equal("영상 종류의 프로세스가 없습니다", vm.EmptyText);
        vm.ClearKindFilterCommand.Execute(null);
        Assert.Equal(all, vm.Processes.Count);
    }

    [Fact]
    public void Find_looks_at_kind_runtimes_and_found_contents_but_not_at_generic_descriptions()
    {
        var (vm, _, _) = OpenWithStrata();
        vm.Filter = "ggml";
        Assert.Equal(["strata.exe"], vm.Processes.Select(r => r.Name));
        vm.Filter = "qwen";
        Assert.Equal(["strata.exe"], vm.Processes.Select(r => r.Name));
        vm.Filter = "브라우저";
        Assert.Equal(["chrome.exe"], vm.Processes.Select(r => r.Name));
        vm.Filter = "텍스처"; // only in the italic "what this kind usually holds" text
        Assert.Empty(vm.Processes);
        Assert.Equal("'텍스처'와 맞는 프로세스가 없습니다", vm.EmptyText);
        vm.ClearFilterCommand.Execute(null);
        Assert.Equal("", vm.Filter);
        Assert.NotEmpty(vm.Processes);
    }

    [Fact]
    public void A_restarted_process_keeps_the_selection_and_a_vanished_one_is_reported_not_replaced()
    {
        var start = GpuMemoryFixtures.Start.AddMinutes(-30);
        var processes = new List<(int Pid, string Name, long Dedicated)> { (100, "llama-server.exe", 6 * GiB), (200, "app.exe", 512 * MiB) };
        var (vm, monitor, time) = Create(provider: t => new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(t.GetUtcNow(),
            processes: [.. processes.Select(p => new GpuProcessMemory { ProcessId = p.Pid, Luid = 1, DedicatedBytes = p.Dedicated, SharedBytes = 0, CommittedBytes = p.Dedicated })],
            table: processes.ToDictionary(p => p.Pid, p => new ProcessEntry(p.Pid, 10, p.Name, start.AddSeconds(p.Pid), ProcessIdentityState.Verified)))));
        void Read()
        {
            time.Advance(TimeSpan.FromSeconds(2));
            monitor.ReadNow();
            vm.Refresh();
        }

        Read();
        Assert.Equal(100, vm.SelectedProcess!.Pid);

        processes[0] = (300, "llama-server.exe", 6 * GiB); // restarted: a new PID with the same name
        Read();
        Assert.Equal(300, vm.SelectedProcess!.Pid);

        processes.RemoveAt(0); // gone, and no process of that name
        Read();
        Assert.Null(vm.SelectedProcess); // not silently replaced by app.exe
        Assert.Null(vm.Detail);
        Assert.Contains("llama-server.exe (PID 300)", vm.DetailPlaceholder, StringComparison.Ordinal);
        Read();
        Assert.Null(vm.SelectedProcess);

        processes.Add((400, "llama-server.exe", 5 * GiB)); // back later
        Read();
        Assert.Equal(400, vm.SelectedProcess!.Pid);
        Assert.DoesNotContain("사라졌습니다", vm.DetailPlaceholder, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_and_details_copy_as_text_with_their_kind_of_value()
    {
        var (vm, _, _) = OpenWithStrata();
        var engine = Row(vm, "strata.exe");
        var fields = GpuMemoryViewModel.RowText(engine).Split('\t');
        Assert.Equal(9, fields.Length);
        Assert.Equal(["strata.exe", "7412", "LLM"], fields[..3]);
        Assert.EndsWith(" GiB", fields[3], StringComparison.Ordinal);
        Assert.EndsWith("%", fields[5], StringComparison.Ordinal);
        Assert.Equal("ggml · CUDA", fields[6]);
        Assert.Equal("MOCK Qwen3 14B · 14.8B · Q4_K_M", fields[7]);
        Assert.Equal("MOCK NVIDIA GeForce RTX 5080", fields[8]);
        Assert.Equal("", GpuMemoryViewModel.RowText(Row(vm, "chrome.exe")).Split('\t')[7]); // generic descriptions are not copied as facts

        var detail = vm.DetailText()!;
        Assert.StartsWith("strata.exe (PID 7412) · LLM — 분류 근거:", detail, StringComparison.Ordinal);
        Assert.Contains("구성: Strata 엔진은", detail, StringComparison.Ordinal); // why no split is drawn, not a split
        Assert.Contains("Strata 보고: 모델 test-model-30b", detail, StringComparison.Ordinal);
        Assert.Contains("출처 MOCK 가상 GPU 메모리 (측정 아님)", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_device_strip_shows_free_memory_and_rows_short_adapter_names()
    {
        var (vm, _, _) = OpenWithStrata();
        Assert.StartsWith("여유 ", vm.Adapters[0].View.FreeText, StringComparison.Ordinal);
        Assert.Equal("여유 98 MiB", vm.Adapters[1].View.FreeText); // 128 MiB − 30 MiB
        var engine = Row(vm, "strata.exe");
        Assert.Equal("MOCK RTX 5080", engine.Adapter);
        Assert.Equal("MOCK NVIDIA GeForce RTX 5080", engine.AdapterFull);
        Assert.StartsWith("카드 VRAM의 ", engine.DedicatedShareText, StringComparison.Ordinal);
        Assert.Equal("카드 VRAM의 1% 미만", Row(vm, "System").DedicatedShareText); // 12 MiB of 15.7 GiB
        Assert.StartsWith("VRAM의 ", vm.Detail!.DedicatedShare, StringComparison.Ordinal);
        Assert.StartsWith("분류 근거:", vm.Detail.KindBasis, StringComparison.Ordinal);
        Assert.DoesNotContain("분류 근거", vm.Detail.Subtitle, StringComparison.Ordinal);

        Assert.Equal("RTX 5080", GpuMemoryViewModel.ShortAdapterName("NVIDIA GeForce RTX 5080"));
        Assert.Equal("Radeon RX 7900 XTX", GpuMemoryViewModel.ShortAdapterName("AMD Radeon(TM) RX 7900 XTX"));
        Assert.Equal("Arc A770 Graphics", GpuMemoryViewModel.ShortAdapterName("Intel(R) Arc(TM) A770 Graphics"));
        Assert.Equal("Basic Render Driver", GpuMemoryViewModel.ShortAdapterName("Microsoft Basic Render Driver"));
        Assert.Equal("NVIDIA", GpuMemoryViewModel.ShortAdapterName("NVIDIA")); // nothing left: the original
    }

    [Fact]
    public void The_strata_status_selects_the_strata_process_even_when_a_filter_hides_it()
    {
        var (vm, _, _) = OpenWithStrata();
        vm.Filter = "chrome";
        Assert.True(vm.ShowStrataCommand.CanExecute(null));

        vm.ShowStrataCommand.Execute(null);

        Assert.Equal("", vm.Filter);
        Assert.Equal("strata.exe", vm.SelectedProcess!.Name);
        Assert.Contains(vm.SelectedProcess, vm.Processes);
    }

    [Fact]
    public void The_layout_is_remembered_in_the_shell_settings()
    {
        var page = new GpuMemoryPageSettings();
        var saves = 0;
        var time = new ManualTime(GpuMemoryFixtures.Start);
        GpuMemoryViewModel New() => new(new GpuMemoryMonitor(() => new MockGpuMemoryProvider(time), time), GpuMemoryFixtures.Hub(time),
            () => new StrataContext(null, "꺼짐"), time, page, () => saves++);
        var vm = New();
        Assert.Equal(("Dedicated", true), (vm.SortColumn, vm.SortDescending));

        vm.SetSort("Shared", false);
        vm.SetSort("Shared", false); // unchanged: not written again
        vm.SetSort("Bogus", true); // unknown column: ignored
        vm.SetColumnHidden("Runtimes", true);
        vm.SetColumnHidden("Name", true); // the name column stays
        vm.SetDetailHeight(260.4);
        vm.ShowAll = true;

        Assert.Equal(("Shared", false), (page.SortColumn, page.SortDescending));
        Assert.Equal(["Runtimes"], page.HiddenColumns);
        Assert.Equal(260, page.DetailHeight);
        Assert.True(page.ShowAll);
        Assert.Equal(4, saves);

        var again = New(); // a later visit or start
        Assert.True(again.ShowAll);
        Assert.Equal(("Shared", false), (again.SortColumn, again.SortDescending));
        Assert.Contains("Runtimes", again.HiddenColumns);
        again.ResetColumns();
        again.SetDetailHeight(-5);
        Assert.Empty(page.HiddenColumns);
        Assert.Equal(0, page.DetailHeight);

        var failing = new GpuMemoryViewModel(new GpuMemoryMonitor(() => new MockGpuMemoryProvider(time), time), GpuMemoryFixtures.Hub(time),
            () => new StrataContext(null, "꺼짐"), time, new GpuMemoryPageSettings(), () => throw new IOException("disk full (test)"));
        failing.SetSort("Pid", false); // the page keeps working; only the remembered layout is lost
        Assert.Equal("Pid", failing.SortColumn);
    }

    [Fact]
    public void The_baseline_shows_changes_and_tells_new_restarted_and_gone_processes_apart()
    {
        var start = GpuMemoryFixtures.Start.AddMinutes(-30);
        var processes = new List<(int Pid, string Name, long Dedicated, DateTimeOffset Started)>
        {
            (100, "app.exe", 4 * GiB, start), (200, "game.exe", 2 * GiB, start), (300, "llama-server.exe", GiB, start),
        };
        var (vm, monitor, time) = Create(provider: t => new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(t.GetUtcNow(),
            processes: [.. processes.Select(p => new GpuProcessMemory { ProcessId = p.Pid, Luid = 1, DedicatedBytes = p.Dedicated, SharedBytes = 0, CommittedBytes = p.Dedicated })],
            table: processes.ToDictionary(p => p.Pid, p => new ProcessEntry(p.Pid, 10, p.Name, p.Started, ProcessIdentityState.Verified)))));
        void Read()
        {
            time.Advance(TimeSpan.FromSeconds(2));
            monitor.ReadNow();
            vm.Refresh();
        }

        vm.HasBaseline = true; // nothing measured yet: no baseline
        Assert.False(vm.HasBaseline);
        Read();
        vm.HasBaseline = true;
        Assert.True(vm.HasBaseline);
        Assert.Equal("0", Row(vm, "app.exe").DeltaText);

        var baselineTime = time.GetUtcNow();
        processes[0] = (100, "app.exe", 5 * GiB, start);                                   // grew
        processes.RemoveAt(1);                                                              // game.exe gone
        processes[1] = (310, "llama-server.exe", GiB + 512 * MiB, baselineTime.AddSeconds(1)); // restarted: new PID
        processes.Add((400, "new.exe", 512 * MiB, baselineTime.AddSeconds(1)));             // started after the baseline
        processes.Add((500, "old.exe", 64 * MiB, start));                                   // ran before, began using the GPU after
        Read();

        Assert.Equal(("+1.00 GiB", (string?)null), (Row(vm, "app.exe").DeltaText, Row(vm, "app.exe").DeltaNote));
        Assert.Equal(GiB, Row(vm, "app.exe").Delta);
        Assert.Equal(("+512 MiB", "재시작 (이전 PID 300)"), (Row(vm, "llama-server.exe").DeltaText, Row(vm, "llama-server.exe").DeltaNote));
        Assert.Equal(("+512 MiB", "기준점 뒤 시작"), (Row(vm, "new.exe").DeltaText, Row(vm, "new.exe").DeltaNote));
        Assert.Equal("기준점 뒤 GPU 사용 시작", Row(vm, "old.exe").DeltaNote);
        Assert.StartsWith("사라짐 1개: game.exe (PID 200) −2.00 GiB", vm.BaselineGone, StringComparison.Ordinal);
        Assert.Contains("Test GPU 전용 0", vm.BaselineText, StringComparison.Ordinal); // the fixture's adapter total did not move
        Assert.Equal("기준점 대비 0", vm.Adapters.Single().View.DeltaText);
        var report = vm.BaselineReport()!;
        Assert.Contains("app.exe\t100\t+1.00 GiB\t\t5.00 GiB", report, StringComparison.Ordinal);
        Assert.Contains("game.exe (PID 200)", report, StringComparison.Ordinal);

        vm.ToggleBaselineCommand.Execute(null);
        Assert.False(vm.HasBaseline);
        Assert.Equal("", Row(vm, "app.exe").DeltaText);
        Assert.Equal(long.MinValue, Row(vm, "app.exe").Delta);
        Assert.Equal("", vm.BaselineText);
        Assert.Null(vm.Adapters.Single().View.DeltaText);
        Assert.Null(vm.BaselineReport());
    }

    [Fact]
    public void The_chart_shows_the_chosen_value_and_window_with_gaps_where_nothing_was_read()
    {
        var page = new GpuMemoryPageSettings();
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var monitor = new GpuMemoryMonitor(() => new MockGpuMemoryProvider(time), time);
        var vm = new GpuMemoryViewModel(monitor, GpuMemoryFixtures.Hub(time), () => new StrataContext(null, "꺼짐"), time, page, () => { });
        monitor.ReadNow(); // page not open: memory only, no engine counters
        time.Advance(TimeSpan.FromSeconds(10));
        vm.IsVisible = true;
        for (var i = 0; i < 3; i++)
        {
            monitor.ReadNow();
            time.Advance(TimeSpan.FromSeconds(2));
        }

        vm.Refresh();
        Assert.Equal("전용 · 15분", vm.Detail!.Chart!.Title);
        Assert.Equal(4, vm.Detail.Chart.Series.Single(x => x.Label == "strata.exe").Y.Length);

        vm.ChartMetric = "Gpu";
        var gpu = vm.Detail!.Chart!;
        Assert.Equal(("GPU % · 15분", "%", (double?)100), (gpu.Title, gpu.Unit, gpu.YMax));
        Assert.Equal(3, gpu.Series.Single(x => x.Label == "strata.exe").Y.Length); // the reading without engines is a gap, not 0 %
        Assert.All(gpu.Series.Single(x => x.Label == "strata.exe").Y, y => Assert.InRange(y, 30, 60));

        vm.ChartMetric = "Shared";
        Assert.Equal("GiB", vm.Detail!.Chart!.Unit);
        vm.ChartMinutes = 5;
        Assert.Equal("공유 · 5분", vm.Detail!.Chart!.Title);
        Assert.Equal(("Shared", 5), (page.ChartMetric, page.ChartMinutes)); // remembered

        vm.ChartMetric = "Bogus";
        Assert.Equal("Dedicated", vm.ChartMetric);
        vm.ChartMinutes = 7;
        Assert.Equal(15, vm.ChartMinutes);
    }

    [Fact]
    public void Help_lists_where_each_value_comes_from()
    {
        var (vm, _, _) = Create();
        Assert.False(vm.HelpOpen);
        vm.ToggleHelpCommand.Execute(null);
        Assert.True(vm.HelpOpen);
        Assert.Contains(vm.ValueKinds, k => k.Label == "추정" && k.IsEstimate);
        Assert.Contains(vm.Knowable, k => k.Label.StartsWith("가중치", StringComparison.Ordinal) && k.Value.Contains("추정", StringComparison.Ordinal));
        Assert.NotEmpty(vm.Checks);
    }

    [Fact]
    public void A_kind_given_to_an_executable_name_replaces_the_classification_everywhere_and_is_remembered()
    {
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var page = new GpuMemoryPageSettings();
        var saves = 0;
        var monitor = new GpuMemoryMonitor(() => new MockGpuMemoryProvider(time), time);
        var vm = new GpuMemoryViewModel(monitor, GpuMemoryFixtures.Hub(time), () => new StrataContext(null, "꺼짐"), time, page, () => saves++);
        vm.IsVisible = true;
        monitor.ReadNow();
        vm.Refresh();
        var chrome = Row(vm, "chrome.exe");
        vm.SelectedProcess = chrome;
        Assert.Equal(ProcessKind.Browser, chrome.Kind);
        Assert.DoesNotContain(vm.Adapters[0].View.Legend, l => l.Kind == ProcessKind.Video);

        vm.SetKindOverride(chrome, ProcessKind.Video);

        Assert.Same(chrome, Row(vm, "chrome.exe")); // updated in place
        Assert.Equal(ProcessKind.Video, chrome.Kind);
        Assert.Equal("영상", chrome.KindLabel);
        Assert.True(chrome.KindOverridden);
        Assert.StartsWith("분류 근거: 사용자가 지정함 (자동 분류: 브라우저 — ", chrome.KindBasis, StringComparison.Ordinal);
        Assert.Contains(vm.Adapters[0].View.Legend, l => l.Kind == ProcessKind.Video); // the device legend follows
        Assert.Equal("영상", vm.Detail!.Kind);
        Assert.Equal(ProcessKind.Video, vm.KindOverrideOf(chrome));
        Assert.Equal(new Dictionary<string, string> { ["chrome.exe"] = "Video" }, page.KindOverrides);
        Assert.Equal(1, saves);
        vm.SetKindOverride(chrome, ProcessKind.Video); // the same kind again: nothing to save
        Assert.Equal(1, saves);

        monitor.ReadNow(); // the next reading keeps it
        vm.Refresh();
        Assert.Equal(ProcessKind.Video, Row(vm, "chrome.exe").Kind);

        vm.SetKindOverride(chrome, null);
        Assert.Equal(ProcessKind.Browser, chrome.Kind);
        Assert.False(chrome.KindOverridden);
        Assert.Empty(page.KindOverrides);
        Assert.Equal(2, saves);

        // The kernel's row has no executable name to remember.
        vm.ShowAll = true;
        var kernel = vm.Processes.Single(r => r.Pid == 4);
        Assert.False(vm.CanOverrideKind(kernel));
        vm.SetKindOverride(kernel, ProcessKind.App);
        Assert.Empty(page.KindOverrides);
        Assert.DoesNotContain(ProcessKind.Kernel, vm.AssignableKinds);
    }

    [Fact]
    public void Strata_engine_by_name_gets_no_header_split_even_when_strata_is_not_connected()
    {
        // Without the local AI module's link, strata.exe is still Strata's engine (its Windows executable name): the
        // llama.cpp-style split from the GGUF header would be wrong for it, so none is drawn.
        var (vm, monitor, _) = Create();
        vm.IsVisible = true;
        monitor.ReadNow();
        vm.Refresh();
        vm.SelectedProcess = Row(vm, "strata.exe");

        var detail = vm.Detail!;
        Assert.False(detail.IsStrata); // not linked
        Assert.Empty(detail.Breakdown);
        Assert.Empty(detail.BreakdownLegend);
        Assert.Contains("strata.exe", detail.BreakdownNote, StringComparison.Ordinal);
        Assert.NotEmpty(detail.Models); // the model file is still shown
        Assert.True(ProcessKinds.IsStrataEngineName("STRATA.EXE"));
        Assert.False(ProcessKinds.IsStrataEngineName("strata-gguf.exe"));
    }

    private static Dictionary<string, string> Facts(ReportGroup group) => group.Facts.ToDictionary(f => f.Label, f => f.Value);

    [Fact]
    public async Task Ollama_is_asked_about_the_selected_runner_only_and_its_sizes_are_not_turned_into_ram_use()
    {
        var http = new FakeRuntimeHttp();
        http.Answers[":11434/api/ps"] = FakeRuntimeHttp.Ok("""
            {"models":[{"name":"gemma3:4b","size":3500000000,"size_vram":1600000000,"context_length":8192,"details":{"parameter_size":"4.3B","quantization_level":"Q4_K_M"}}]}
            """);
        var (vm, monitor, _) = Create(http: http);
        vm.IsVisible = true;
        monitor.ReadNow();
        vm.Refresh();
        vm.SelectedProcess = Row(vm, "chrome.exe");
        await vm.ReportsIdle();
        Assert.Empty(http.Calls); // not a runtime: nothing is asked
        Assert.Empty(vm.Detail!.Reports);

        // The runner holds the GPU memory; the server (its parent) listens on 11434 and is the one asked.
        vm.SelectedProcess = Row(vm, "ollama.exe");
        await vm.ReportsIdle();
        vm.Refresh();

        Assert.Equal("http://127.0.0.1:11434/api/ps", Assert.Single(http.Calls).Url);
        var group = Assert.Single(vm.Detail!.Reports);
        Assert.Equal("Ollama · gemma3:4b", group.Title);
        var facts = Facts(group);
        Assert.Equal("3.26 GiB", facts["전체 (Ollama 계산)"]);
        Assert.Equal("1.49 GiB", facts["VRAM (Ollama 계산)"]);
        Assert.Equal("8,192 토큰", facts["컨텍스트"]);
        Assert.DoesNotContain(facts.Keys, k => k.Contains("RAM", StringComparison.Ordinal) && !k.Contains("VRAM", StringComparison.Ordinal));
        Assert.Contains("RAM 사용량으로 보면 안 됩니다", group.Note, StringComparison.Ordinal);
        Assert.Contains("모델 파일 크기", group.Note, StringComparison.Ordinal);
        Assert.Contains("Ollama · gemma3:4b: 전체 (Ollama 계산) 3.26 GiB", vm.DetailText(), StringComparison.Ordinal);
        // The runner's model does not fit on the GPU whole: the two numbers are shown apart, no split.
        Assert.Empty(vm.Detail.Breakdown);
        Assert.Equal(["가중치 (모든 층 GPU 가정, 이론값)", "이 프로세스 전용 (측정)"], vm.Detail.BreakdownLegend.Select(l => l.Label));

        vm.IsVisible = false; // closing the page forgets the answers and asks nothing more
        vm.IsVisible = true;
        monitor.ReadNow();
        vm.Refresh();
        await vm.ReportsIdle();
        Assert.Equal(2, http.Calls.Count); // asked again after reopening, not in between
    }

    [Fact]
    public async Task Strata_engine_report_keeps_start_settings_apart_from_its_last_report_and_from_the_measurement()
    {
        var (vm, monitor, _) = OpenWithStrata(new MockRuntimeHttp());
        vm.SelectedProcess = Row(vm, "strata.exe");
        await vm.ReportsIdle();
        vm.Refresh();

        var reports = vm.Detail!.Reports;
        Assert.Equal(["Strata 엔진 · 시작 때 정한 값", "Strata 엔진 · 마지막 보고 (VRAM 조정 시)"], reports.Select(g => g.Title));
        var start = Facts(reports[0]);
        Assert.Equal("int8", start["KV 형식"]);
        Assert.Equal("32,768 토큰", start["컨텍스트"]);
        Assert.Equal("4.00 GiB", start["대화 캐시 상한 (RAM)"]);
        var last = Facts(reports[1]);
        Assert.Equal("1,200 (최대 3,700)", last["전문가 슬롯"]);
        Assert.Equal("1.50 GiB (최대 4.79 GiB)", last["전문가 캐시"]); // the adjusted size, not the start's 4.79 GiB
        Assert.Equal("5.96 GiB", last["여유 VRAM (그때 장치 전체)"]);
        Assert.Equal("5.86 GiB", last["요청한 예약"]);
        Assert.Contains("Windows 측정", reports[1].Note, StringComparison.Ordinal);
        Assert.Equal(Row(vm, "strata.exe").DedicatedText, vm.Detail.Dedicated); // the measurement stays the measurement
        Assert.Empty(vm.Detail.Breakdown);
        Assert.Contains("Strata 엔진 · 마지막 보고 (VRAM 조정 시): 전문가 슬롯 1,200 (최대 3,700)", vm.DetailText(), StringComparison.Ordinal);
        Assert.NotNull(monitor.Latest);
    }

    [Fact]
    public async Task A_refused_strata_request_is_a_reason_and_a_paused_page_asks_nothing()
    {
        var http = new FakeRuntimeHttp();
        http.Answers["/metrics"] = new RuntimeHttpResponse(401, null, null);
        var (vm, monitor, time) = OpenWithStrata(http, keyVariable: "PCC_TEST_UNSET_" + Guid.NewGuid().ToString("N"));
        Assert.Same(Row(vm, "strata.exe"), vm.SelectedProcess); // the largest process is selected when the page opens
        await vm.ReportsIdle();
        var call = Assert.Single(http.Calls);
        Assert.Equal("http://127.0.0.1:18500/metrics", call.Url);
        Assert.Null(call.Bearer); // the named variable is not set: no key

        vm.IsPaused = true;
        time.Advance(RuntimeReportPoller.Interval);
        monitor.ReadNow(); // the reader keeps reading while the page is paused
        vm.SelectedProcess = Row(vm, "chrome.exe");
        vm.SelectedProcess = Row(vm, "strata.exe");
        vm.Refresh();
        await vm.ReportsIdle();
        Assert.Single(http.Calls); // paused: nothing new is asked

        vm.IsPaused = false;
        await vm.ReportsIdle();
        vm.Refresh();
        Assert.Equal(2, http.Calls.Count);
        var group = Assert.Single(vm.Detail!.Reports);
        Assert.True(group.IsError);
        Assert.Equal("Strata 엔진 보고 · 읽지 못함", group.Title);
        Assert.Contains("API 키", group.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_llama_router_child_shows_its_own_settings_and_only_the_instance_whose_file_it_maps()
    {
        var http = new FakeRuntimeHttp();
        http.Answers[":49152/props"] = FakeRuntimeHttp.Ok("""{"default_generation_settings":{"n_ctx":131072},"total_slots":1,"model_path":"D:\\m\\gemma-3-4b-it-Q4_K_M.gguf","build_info":"b7000"}""");
        http.Answers[":8080/props"] = new RuntimeHttpResponse(400, """{"error":"model name is missing from the request"}""", null);
        http.Answers[":8080/models"] = FakeRuntimeHttp.Ok("""
            {"data":[
              {"id":"gemma","path":"/m/gemma-3-4b-it-Q4_K_M.gguf","memory":{"status":"measured","min":{"n_ctx":4096,"devices":[]},
                "max":{"n_ctx":262144,"devices":[{"kind":"device","name":"CUDA0","model":1,"context":99999999999,"compute":1}]},
                "effective":{"n_ctx":131072,"devices":[{"kind":"device","name":"CUDA0","model":4986635264,"context":536870912,"compute":301989888}]}}},
              {"id":"other","path":"/m/other.gguf","memory":{"status":"measured","effective":{"n_ctx":4096,"devices":[{"kind":"device","name":"CUDA0","model":1,"context":1,"compute":1}]}}}
            ]}
            """);
        var at = GpuMemoryFixtures.Start;
        var provider = (ManualTime time) => new ScriptedGpuMemoryProvider(request => GpuMemoryFixtures.Snapshot(time.GetUtcNow(),
            [new GpuProcessMemory { ProcessId = 300, Luid = 1, DedicatedBytes = 5 * GiB, SharedBytes = 0, CommittedBytes = 5 * GiB }],
            new Dictionary<int, ProcessEntry>
            {
                [300] = new(300, 200, "llama-server.exe", at.AddMinutes(-2), ProcessIdentityState.Verified),
                [200] = new(200, 10, "llama-server.exe", at.AddMinutes(-9), ProcessIdentityState.Verified),
            }) with
        {
            ServiceListeners = [new(8080, 200, false, true), new(49152, 300, false, true)],
            Details = new Dictionary<int, ProcessDetail>
            {
                [300] = new() { Runtimes = ["llama.cpp", "CUDA"], ScannedAt = at, Models = [new ModelFile("gemma-3-4b-it-Q4_K_M.gguf", 3_000_000_000, "GGUF", null)] },
            },
        });
        var (vm, monitor, _) = Create(provider: provider, http: http);
        vm.IsVisible = true;
        monitor.ReadNow();
        vm.Refresh();
        vm.SelectedProcess = Row(vm, "llama-server.exe");
        await vm.ReportsIdle();
        vm.Refresh();

        Assert.Equal(4, http.Calls.Count); // the child's /props and /models, the router's /props and /models
        var reports = vm.Detail!.Reports;
        Assert.Equal(["llama.cpp 서버 · 설정", "llama.cpp 라우터 · 이 인스턴스"], reports.Select(g => g.Title));
        Assert.Equal("131,072 토큰", Facts(reports[0])["컨텍스트 (n_ctx)"]);
        Assert.Equal("gemma-3-4b-it-Q4_K_M.gguf", Facts(reports[0])["모델 파일"]); // the file name, never the path
        Assert.Contains("KV 형식은 알려 주지 않아", reports[0].Note, StringComparison.Ordinal);
        var instance = Facts(reports[1]);
        Assert.Equal("가중치 4.64 GiB · 컨텍스트 512 MiB · 계산 288 MiB", instance["CUDA0"]); // effective, not max
        Assert.DoesNotContain(reports[1].Facts, f => f.Value.Contains("93.13 GiB", StringComparison.Ordinal));
        Assert.Contains("min·max", reports[1].Note, StringComparison.Ordinal);
        Assert.DoesNotContain("D:", vm.DetailText(), StringComparison.Ordinal);
    }
}

public class GpuMemoryShellTests
{
    [Fact(Timeout = 180_000)]
    public async Task A_broken_gpu_memory_reader_does_not_affect_the_shared_sensors_or_the_modules()
    {
        using var temp = new TempDir();
        await using var runtime = ShellRuntime.Create(new ShellOptions
        {
            DataDirectory = temp.Sub("data"),
            ExecutableDirectory = temp.Path,
            MockSensors = true,
            Unattended = true,
            LegacyLocations = [],
            GpuMemoryProvider = () => throw new InvalidOperationException("PDH broken (test)"),
        });
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        var shell = new ShellViewModel(runtime, new LockedDispatcher(), new RecordingShellDialogs());

        Assert.True(await Eventually(() => runtime.Hub.Health.Readings >= 3, TimeSpan.FromSeconds(15)));
        Assert.True(await Eventually(() => runtime.GpuMemory.Health.State == GpuMemoryMonitorState.Unavailable, TimeSpan.FromSeconds(10)));
        Assert.Contains("PDH broken", runtime.GpuMemory.Health.UnavailableReason, StringComparison.Ordinal);
        Assert.All(runtime.Supervisor.Modules, m => Assert.NotEqual(ModuleState.Failed, m.Status.State));

        // The page exists in the hardware menu, right after the shared sensors, and explains the problem.
        var hardware = shell.Section(SectionKeys.Hardware);
        Assert.Equal(["hardware/shared", "hardware/gpu-memory"], hardware.Tabs.Take(2).Select(t => t.Key));
        Assert.Same(shell.GpuMemory, hardware.Find("hardware/gpu-memory")!.Content);
        shell.Navigate(SectionKeys.Hardware, "hardware/gpu-memory");
        shell.Tick();
        Assert.True(shell.GpuMemory.IsVisible);
        Assert.Equal("사용 불가", shell.GpuMemory.StatusText);
        Assert.Contains("PDH broken", shell.GpuMemory.MessageDetail, StringComparison.Ordinal);
        shell.Navigate(SectionKeys.Dashboard);
        shell.Tick();
        Assert.False(shell.GpuMemory.IsVisible);
    }

    [Fact(Timeout = 180_000)]
    public async Task With_mock_sensors_the_page_shows_mock_rows_and_never_opens_the_windows_counters()
    {
        using var temp = new TempDir();
        await using var runtime = ShellRuntime.Create(new ShellOptions
        {
            DataDirectory = temp.Sub("data"),
            ExecutableDirectory = temp.Path,
            MockSensors = true,
            Unattended = true,
            LegacyLocations = [],
        });
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        var shell = new ShellViewModel(runtime, new LockedDispatcher(), new RecordingShellDialogs());
        shell.Navigate(SectionKeys.Hardware, "hardware/gpu-memory");

        Assert.True(await Eventually(() =>
        {
            shell.Tick();
            return shell.GpuMemory.Processes.Count > 0;
        }, TimeSpan.FromSeconds(15)));
        Assert.True(runtime.GpuMemory.Health.IsMock);
        Assert.True(shell.GpuMemory.IsMock);
        Assert.Contains("MOCK", runtime.GpuMemory.Latest!.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_setting_switches_the_reader_off_and_older_files_keep_it_on()
    {
        using var temp = new TempDir();
        var path = temp.Sub("shell-settings.json");
        File.WriteAllText(path, """{ "SchemaVersion": 1, "Hardware": { "MaxIntervalMs": 1000, "GpuEnabled": true, "SystemEnabled": true } }""");
        Assert.True(new ShellSettingsStore(path).Load().Settings.Hardware.ProcessMemoryEnabled);

        var monitor = GpuMemoryMonitor.Create(enabled: false, useMock: false);
        Assert.Equal(GpuMemoryMonitorState.Disabled, monitor.Health.State);
        Assert.Contains("꺼져", monitor.Health.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_layout_survives_a_save_and_bad_values_are_dropped()
    {
        using var temp = new TempDir();
        var path = temp.Sub("shell-settings.json");
        var store = new ShellSettingsStore(path);
        File.WriteAllText(path, """{ "SchemaVersion": 1 }""");
        var older = store.Load().Settings.GpuMemoryPage; // files written before the page remembered its layout
        Assert.Equal((null, true, false, 0.0), (older.SortColumn, older.SortDescending, older.ShowAll, older.DetailHeight));
        Assert.Empty(older.HiddenColumns);

        File.WriteAllText(path, """
            { "SchemaVersion": 1, "GpuMemoryPage": { "SortColumn": "Bogus", "SortDescending": false, "HiddenColumns": ["Name", "Bogus", "Pid", "Pid"],
              "DetailHeight": -1, "ShowAll": true } }
            """);
        var damaged = store.Load().Settings.GpuMemoryPage;
        Assert.Null(damaged.SortColumn);
        Assert.True(damaged.SortDescending);
        Assert.Equal(["Pid"], damaged.HiddenColumns);
        Assert.Equal(0, damaged.DetailHeight);
        Assert.True(damaged.ShowAll);

        var settings = store.Load().Settings;
        settings.GpuMemoryPage.SortColumn = "Gpu";
        settings.GpuMemoryPage.DetailHeight = 300;
        settings.GpuMemoryPage.KindOverrides["myrenderer.exe"] = "Graphics3D";
        store.Save(settings);
        var reloaded = store.Load().Settings.GpuMemoryPage;
        Assert.Equal(("Gpu", 300.0), (reloaded.SortColumn, reloaded.DetailHeight));
        Assert.Equal("Graphics3D", reloaded.KindOverrides["MyRenderer.EXE"]); // names compare without case
    }

    [Fact]
    public void Kind_overrides_keep_bare_executable_names_and_known_kinds_only()
    {
        var page = new GpuMemoryPageSettings
        {
            KindOverrides = new()
            {
                [" MyApp.EXE "] = "Llm", [@"C:\tools\a.exe"] = "Llm", ["b.exe"] = "Spaceship", [""] = "App", ["tab\t.exe"] = "App",
                ["c.exe"] = null!, [new string('x', 300)] = "App",
            },
        };
        page.Normalize();
        Assert.Equal(new Dictionary<string, string> { ["myapp.exe"] = "Llm" }, page.KindOverrides);

        var many = new GpuMemoryPageSettings { KindOverrides = Enumerable.Range(0, 250).ToDictionary(i => $"app{i}.exe", _ => "App") };
        many.Normalize();
        Assert.Equal(GpuMemoryPageSettings.MaxKindOverrides, many.KindOverrides.Count);

        // The names kept in the settings are the classifier's kinds (the settings library cannot see the enum).
        Assert.Equal(Enum.GetNames<ProcessKind>().Order(StringComparer.Ordinal), GpuMemoryPageSettings.KindNames.Order(StringComparer.Ordinal));
    }

    [Fact(Timeout = 180_000)]
    public async Task The_dashboard_card_lists_the_largest_processes_and_opens_the_page()
    {
        using var temp = new TempDir();
        await using var runtime = ShellRuntime.Create(new ShellOptions
        {
            DataDirectory = temp.Sub("data"),
            ExecutableDirectory = temp.Path,
            MockSensors = true,
            Unattended = true,
            LegacyLocations = [],
        });
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        var shell = new ShellViewModel(runtime, new LockedDispatcher(), new RecordingShellDialogs());
        var dashboard = shell.Dashboard;

        Assert.True(await Eventually(() =>
        {
            dashboard.Tick();
            return runtime.GpuMemory.Latest is not null && dashboard.GpuMemoryLargest != Text.NA;
        }, TimeSpan.FromSeconds(20)));
        var latest = runtime.GpuMemory.Latest!;
        var largest = latest.Processes.OrderByDescending(p => p.DedicatedBytes).First();
        Assert.StartsWith(latest.Process(largest.ProcessId)!.Name, dashboard.GpuMemoryState, StringComparison.Ordinal);
        Assert.Equal(Text.Bytes(largest.DedicatedBytes), dashboard.GpuMemoryLargest);
        Assert.InRange(dashboard.GpuMemoryTop.Count, 1, 3);
        Assert.Equal(Level.Warning, dashboard.GpuMemoryLevel); // MOCK is never "good"
        Assert.Contains("MOCK", dashboard.GpuMemoryNote, StringComparison.Ordinal);
        Assert.DoesNotContain("합계", dashboard.GpuMemoryNote, StringComparison.Ordinal);

        dashboard.OpenCommand.Execute("hardware/gpu-memory");
        shell.Tick();
        Assert.Same(shell.GpuMemory, shell.CurrentSection!.SelectedTab!.Content);
        Assert.True(shell.GpuMemory.IsVisible);
    }

    private static async Task<bool> Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
    }
}
