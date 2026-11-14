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
    private static (GpuMemoryViewModel Vm, GpuMemoryMonitor Monitor, ManualTime Time) Create(Func<StrataContext>? strata = null,
        Func<ManualTime, IGpuMemoryProvider>? provider = null)
    {
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var monitor = new GpuMemoryMonitor(() => provider?.Invoke(time) ?? new MockGpuMemoryProvider(time), time);
        var vm = new GpuMemoryViewModel(monitor, GpuMemoryFixtures.Hub(time), strata ?? (() => new StrataContext(null, "로컬 AI 모듈 꺼짐 (테스트)")), time);
        return (vm, monitor, time);
    }

    [Fact]
    public void Measured_rows_computed_sums_and_runtime_reported_strata_facts_are_labelled_apart()
    {
        ManualTime? clock = null;
        var (vm, monitor, time) = Create(() => new StrataContext(GpuMemoryFixtures.Strata(clock!.GetUtcNow()), null));
        clock = time;
        monitor.SetWatchedPorts([MockGpuMemoryProvider.ServerPort]);
        monitor.ReadNow();
        vm.Refresh();

        Assert.True(vm.HasValues);
        Assert.True(vm.IsMock);
        Assert.Equal(Level.Warning, vm.StatusLevel); // MOCK is never "good"
        // Processes: largest local memory first, the Strata engine tagged by evidence (port owner + process tree).
        Assert.Equal("strata.exe", vm.Processes[0].Name);
        Assert.Equal("Strata", vm.Processes[0].Tag);
        Assert.Equal("확인됨", vm.Processes[0].Identity);
        Assert.DoesNotContain(vm.Processes, p => p.Name == "notepad.exe"); // 300 KiB: below the listing threshold
        Assert.Contains("숨김", vm.ProcessSummary, StringComparison.Ordinal);

        // Adapter card: Windows usage (measured), NVML paired (one NVIDIA adapter, one NVML device), sum and difference (computed).
        var card = vm.Adapters.First(a => a.Title.Contains("RTX 5080", StringComparison.Ordinal));
        Assert.StartsWith("측정", card.Rows.Single(r => r.Label == "전용 GPU 메모리 사용").Origin, StringComparison.Ordinal);
        Assert.Equal("4.00 GiB / 16.00 GiB", card.Rows.Single(r => r.Label.StartsWith("NVML", StringComparison.Ordinal)).Value);
        Assert.Equal("계산", card.Rows.Single(r => r.Label == "프로세스별 전용 합계").Origin);
        Assert.Equal("+260 MiB", card.Rows.Single(r => r.Label == "어댑터 − 프로세스 합계").Value);
        Assert.Contains(vm.Adapters, a => a.Note is not null && a.Note.Contains("통합 GPU", StringComparison.Ordinal));

        // Strata: runtime-reported facts, measured process memory, and no weights/KV split.
        Assert.Equal("test-model-30b", vm.StrataRows.Single(r => r.Label == "모델 ID").Value);
        Assert.Equal("런타임 보고", vm.StrataRows.Single(r => r.Label == "컨텍스트 창").Origin);
        Assert.Contains("합계 5,120", vm.StrataRows.Single(r => r.Label == "슬롯이 가진 토큰").Value, StringComparison.Ordinal);
        Assert.Equal(Text.NA, vm.StrataRows.Single(r => r.Label == "양자화").Value);
        var memory = vm.StrataRows.Single(r => r.Label == "Strata 프로세스의 GPU 메모리");
        Assert.Contains("전용 9.", memory.Value, StringComparison.Ordinal);
        Assert.Equal("알 수 없음", vm.StrataRows.Single(r => r.Label.StartsWith("가중치", StringComparison.Ordinal)).Value);
        Assert.Equal(["python.exe", "strata.exe"], vm.StrataProcesses.Select(p => p.Name));
        Assert.NotEmpty(vm.StrataEvidence);

        // History chart: measured series in GiB.
        Assert.NotNull(vm.MemoryChart);
        Assert.Equal("GiB", vm.MemoryChart!.Unit);
        Assert.Contains(vm.MemoryChart.Series, s => s.Label.StartsWith("strata.exe", StringComparison.Ordinal));
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
        Assert.Equal(Level.Error, vm.StatusLevel);
        Assert.Contains("새 측정값이 없습니다", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("표시하지 않습니다", vm.ProcessEmptyText, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_reachable_recent_local_strata_nothing_is_linked()
    {
        ManualTime? clock = null;
        var state = RuntimeConnectionState.Unreachable;
        var age = TimeSpan.Zero;
        var (vm, monitor, time) = Create(() => new StrataContext(GpuMemoryFixtures.Strata(clock!.GetUtcNow() - age, state), null));
        clock = time;
        monitor.SetWatchedPorts([MockGpuMemoryProvider.ServerPort]);
        monitor.ReadNow();
        vm.Refresh();
        Assert.DoesNotContain(vm.Processes, p => p.Tag == "Strata");
        Assert.Equal(Text.NA, vm.StrataRows.Single(r => r.Label == "Strata 프로세스의 GPU 메모리").Value);

        state = RuntimeConnectionState.Connected;
        age = GpuMemoryViewModel.StrataMaxAge + TimeSpan.FromSeconds(1); // the last poll is too old: the port may belong to someone else
        vm.Refresh();
        Assert.Null(GpuMemoryViewModel.StrataPort(new StrataContext(GpuMemoryFixtures.Strata(time.GetUtcNow() - age), null), time.GetUtcNow()));
        Assert.DoesNotContain(vm.Processes, p => p.Tag == "Strata");
        Assert.Empty(monitor.WatchedPorts);
    }

    [Fact]
    public void An_unavailable_reader_says_why_and_shows_no_values()
    {
        var (vm, monitor, _) = Create(provider: t => new UnavailableGpuMemoryProvider("카운터 없음 (테스트)", t));
        monitor.ReadNow();
        vm.Refresh();
        Assert.Equal(Level.Warning, vm.StatusLevel);
        Assert.Contains("카운터 없음 (테스트)", vm.StatusText, StringComparison.Ordinal);
        Assert.Empty(vm.Adapters);
        Assert.Equal("로컬 AI 모듈 꺼짐 (테스트)", vm.StrataStatus);
    }

    [Fact]
    public void Fallback_counters_are_flagged_and_a_larger_process_sum_is_a_warning()
    {
        var (vm, monitor, _) = Create(provider: t => new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(t.GetUtcNow()) with
        {
            Variant = ProcessCounterVariant.DedicatedShared,
            Processes = [new GpuProcessMemory { ProcessId = 100, Luid = 1, DedicatedBytes = 7 * GpuMemoryFixtures.GiB, SharedBytes = 0, CommittedBytes = null }],
            CommittedAvailable = false,
        }));
        monitor.ReadNow();
        vm.Refresh();

        Assert.Equal(Level.Warning, vm.StatusLevel);
        Assert.Contains("중복 집계", vm.VariantText, StringComparison.Ordinal);
        var difference = vm.Adapters.Single().Rows.Single(r => r.Label == "어댑터 − 프로세스 합계");
        Assert.Equal("−2.00 GiB", difference.Value);
        Assert.Equal(Level.Warning, difference.Level);
        Assert.Equal(Text.NA, vm.Processes.Single().Committed);
    }

    [Fact]
    public void Showing_the_page_switches_the_reader_to_its_short_interval()
    {
        var (vm, monitor, _) = Create();
        Assert.Equal(GpuMemoryMonitor.DefaultBackgroundInterval, monitor.Interval);
        vm.IsVisible = true;
        Assert.Equal(GpuMemoryMonitor.DefaultActiveInterval, monitor.Interval);
        vm.IsVisible = false;
        Assert.Equal(GpuMemoryMonitor.DefaultBackgroundInterval, monitor.Interval);
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
        Assert.Contains("PDH broken", shell.GpuMemory.StatusText, StringComparison.Ordinal);
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
