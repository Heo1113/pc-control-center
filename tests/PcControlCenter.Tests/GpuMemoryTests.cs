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
    private const long MiB = GpuMemoryFixtures.MiB;
    private const long GiB = GpuMemoryFixtures.GiB;

    private static (GpuMemoryViewModel Vm, GpuMemoryMonitor Monitor, ManualTime Time) Create(Func<StrataContext>? strata = null,
        Func<ManualTime, IGpuMemoryProvider>? provider = null)
    {
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var monitor = new GpuMemoryMonitor(() => provider?.Invoke(time) ?? new MockGpuMemoryProvider(time), time);
        var vm = new GpuMemoryViewModel(monitor, GpuMemoryFixtures.Hub(time), strata ?? (() => new StrataContext(null, "로컬 AI 모듈 꺼짐 (테스트)")), time);
        return (vm, monitor, time);
    }

    /// <summary>The page is open (details and engines are read), Strata is polled now and serves the MOCK port.</summary>
    private static (GpuMemoryViewModel Vm, GpuMemoryMonitor Monitor, ManualTime Time) OpenWithStrata()
    {
        ManualTime? clock = null;
        var created = Create(() => new StrataContext(GpuMemoryFixtures.Strata(clock!.GetUtcNow()), null));
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

        var integrated = vm.Adapters[1].View;
        Assert.Contains("통합 GPU일 수 있음", integrated.Meta, StringComparison.Ordinal);
        Assert.Null(integrated.Nvml);
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

        // The model file header: weights from the tensor table. 32,768 tokens of KV (5 GiB at f16) do not fit in what is
        // left after the weights, so no KV split is shown rather than a made-up one.
        var model = Assert.Single(detail.Models);
        Assert.Equal("MOCK Qwen3 14B", model.Name);
        Assert.Equal("qwen3 · 14.8B · Q4_K_M · 40층 · 학습 컨텍스트 40,960 · KV 160 KiB/토큰", model.Summary);
        Assert.Equal("8.38 GiB", model.Size);
        Assert.All(detail.Breakdown, b => Assert.True(b.IsEstimate));
        Assert.All(detail.BreakdownLegend, l => Assert.True(l.IsEstimate));
        Assert.Equal(["가중치", "KV·버퍼·런타임"], detail.BreakdownLegend.Select(l => l.Label));
        Assert.Equal("8.00 GiB", detail.BreakdownLegend[0].Value);
        Assert.Contains("KV 추정값이 남은 양보다 커서", detail.BreakdownNote, StringComparison.Ordinal);

        Assert.NotNull(detail.Chart);
        Assert.Equal("GiB", detail.Chart!.Unit);
        Assert.Equal(["GPU 전체", "strata.exe"], detail.Chart.Series.Select(x => x.Label));
        Assert.Equal("Strata · strata.exe", vm.StrataText);
        Assert.Equal(Level.Good, vm.StrataLevel);
    }

    [Fact]
    public void A_kv_cache_that_fits_is_shown_as_an_estimate_next_to_the_weights()
    {
        var start = GpuMemoryFixtures.Start.AddMinutes(-10);
        var qwen = new GgufSummary
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
        ManualTime? clock = null;
        var (vm, monitor, time) = Create(
            () => new StrataContext(GpuMemoryFixtures.Strata(clock!.GetUtcNow()) with { Model = new RuntimeModelInfo { Id = "qwen3-0.6b", Loaded = true, ContextTokens = 8192 } }, null),
            t => new ScriptedGpuMemoryProvider(request => GpuMemoryFixtures.Snapshot(t.GetUtcNow(),
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
                    [60] = new() { Runtimes = ["llama.cpp", "ggml", "CUDA"], Models = [new ModelFile("Qwen3-0.6B-Q8_0.gguf", 639_446_688, "GGUF", qwen)], ScannedAt = t.GetUtcNow() },
                },
            }));
        clock = time;
        vm.IsVisible = true;
        monitor.ReadNow();
        vm.Refresh();

        var row = Row(vm, "llama-server.exe");
        Assert.True(row.IsStrata);
        Assert.Equal("llama.cpp · ggml · CUDA", row.Runtimes);
        Assert.Equal("Qwen3 0.6B · 596M · Q8_0", row.Inside);
        var detail = vm.Detail!;
        Assert.Equal("qwen3 · 596M · Q8_0 · 28층 · 학습 컨텍스트 40,960 · KV 112 KiB/토큰", detail.Models.Single().Summary);
        // 8,192 tokens × 114,688 B (28 layers × 8 KV heads × (128 + 128) × 2 B) = 896 MiB, within the 1,448 MiB left after the weights.
        Assert.Equal([("가중치", "600 MiB"), ("KV 캐시", "896 MiB"), ("버퍼·런타임", "552 MiB")], detail.BreakdownLegend.Select(l => (l.Label, l.Value)));
        Assert.Equal([600.0 / 2048, 896.0 / 2048, 552.0 / 2048], detail.Breakdown.Select(b => b.Fraction));
        Assert.All(detail.Breakdown, b => Assert.True(b.IsEstimate));
        Assert.StartsWith("모든 층이 GPU에 있고 KV가 f16이라고 가정한 추정입니다.", detail.BreakdownNote, StringComparison.Ordinal);
        Assert.Equal("2.00 GiB", detail.Dedicated); // the only measured part
        Assert.Equal("35%", detail.Gpu);
        Assert.Equal("3D 0 · 연산 35 · 복사 1 · 영상 0", detail.Engines);
        Assert.Equal("3.00 GiB", vm.Adapters.Single().View.Legend.Single(l => l.Label == "나뉘지 않음").Value);

        // Without Strata's context the KV cache is not split off.
        var noStrata = Create(provider: t => new ScriptedGpuMemoryProvider(_ => GpuMemoryFixtures.Snapshot(t.GetUtcNow(),
            processes: [new GpuProcessMemory { ProcessId = 60, Luid = 1, DedicatedBytes = 2 * GiB, SharedBytes = 0, CommittedBytes = 2 * GiB }],
            table: new Dictionary<int, ProcessEntry> { [60] = new(60, 50, "llama-server.exe", start, ProcessIdentityState.Verified) }) with
        {
            Details = new Dictionary<int, ProcessDetail> { [60] = new() { Runtimes = ["llama.cpp"], Models = [new ModelFile("q.gguf", 1, "GGUF", qwen)], ScannedAt = t.GetUtcNow() } },
        }));
        noStrata.Monitor.ReadNow();
        noStrata.Vm.Refresh();
        var plain = noStrata.Vm.Detail!;
        Assert.Equal("LLM", plain.Kind);
        Assert.Equal(["가중치", "KV·버퍼·런타임"], plain.BreakdownLegend.Select(l => l.Label));
        Assert.Contains("컨텍스트를 알 수 없어", plain.BreakdownNote, StringComparison.Ordinal);
        Assert.Null(plain.StrataModel);
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
