using PcControlCenter.GpuMemory;
using PcControlCenter.Presentation;
using static PcControlCenter.Tests.GpuMemoryFixtures;

namespace PcControlCenter.Tests;

/// <summary>
/// The GPU memory page and Basalt: basalt.exe (wrapper) → python.exe (listening on 8080) → engine, and an engine started by
/// the wrapper beside the server. Linked by the port owner, its descendants and its basalt* wrapper, start times verified.
/// </summary>
public class BasaltGpuMemoryTests
{
    private static readonly DateTimeOffset T = Start;

    private static Dictionary<int, ProcessEntry> Table() => new()
    {
        [5] = new(5, 1, "powershell.exe", T.AddMinutes(-10), ProcessIdentityState.Verified),
        [10] = new(10, 5, "basalt.exe", T.AddMinutes(-9), ProcessIdentityState.Verified),
        [20] = new(20, 10, "python.exe", T.AddMinutes(-8), ProcessIdentityState.Verified),
        [30] = new(30, 20, "basalt-engine.exe", T.AddMinutes(-7), ProcessIdentityState.Verified),
        [31] = new(31, 10, "helper.exe", T.AddMinutes(-7), ProcessIdentityState.Verified),
        [40] = new(40, 1, "chrome.exe", T.AddMinutes(-30), ProcessIdentityState.Verified),
    };

    private static readonly GpuProcessMemory[] Memory =
    [
        new() { ProcessId = 30, Luid = 1, DedicatedBytes = 12 * GiB, SharedBytes = 200 * MiB },
        new() { ProcessId = 20, Luid = 1, DedicatedBytes = 300 * MiB, SharedBytes = 10 * MiB },
        new() { ProcessId = 40, Luid = 1, DedicatedBytes = 500 * MiB, SharedBytes = 50 * MiB },
    ];

    private static GpuMemorySnapshot Snapshot(Dictionary<int, ProcessEntry>? table = null)
        => GpuMemoryFixtures.Snapshot(T, Memory, table ?? Table(), [new TcpListenerEntry(8080, 20, false, true)]);

    [Fact]
    public void The_wrapper_above_the_listener_and_its_other_children_are_linked_with_basalt_names()
    {
        var link = ServerProcessLinker.Link("http://127.0.0.1:8080", Snapshot(), ProcessKinds.IsBasaltName);

        Assert.True(link.IsLinked, link.Reason);
        Assert.Equal([20, 10, 30, 31], link.Processes.Select(p => p.Process.ProcessId));
        Assert.Equal(-1, link.Processes.Single(p => p.Process.ProcessId == 10).Depth);
        Assert.Contains(link.Evidence, e => e.Contains("PID 10 basalt.exe", StringComparison.Ordinal) && e.Contains("래퍼", StringComparison.Ordinal));
        Assert.DoesNotContain(link.Processes, p => p.Process.Name is "powershell.exe" or "chrome.exe");

        // Without the wrapper rule (as for Strata) only the listener and its own children are linked.
        var plain = ServerProcessLinker.Link("http://127.0.0.1:8080", Snapshot());
        Assert.Equal([20, 30], plain.Processes.Select(p => p.Process.ProcessId));
    }

    [Fact]
    public void A_wrapper_that_started_after_the_listener_is_not_its_parent()
    {
        var table = Table();
        table[10] = table[10] with { StartTime = T.AddMinutes(-1) }; // the PID 10 now is newer than python.exe: reused

        var link = ServerProcessLinker.Link("http://127.0.0.1:8080", Snapshot(table), ProcessKinds.IsBasaltName);

        Assert.Equal([20, 30], link.Processes.Select(p => p.Process.ProcessId));
        Assert.Contains(link.Unverified, u => u.Contains("PID 10", StringComparison.Ordinal));
    }

    [Fact]
    public void Basalt_executables_are_llm_runtimes_by_name_and_linked_processes_by_the_port()
    {
        Assert.Equal(ProcessKind.Llm, ProcessKinds.Classify(10, "basalt.exe", null, null, false).Kind);
        Assert.Equal(ProcessKind.Llm, ProcessKinds.Classify(30, "basalt-engine.exe", null, null, false).Kind);
        var python = ProcessKinds.Classify(20, "python.exe", null, null, true, "Basalt");
        Assert.Equal(ProcessKind.Llm, python.Kind);
        Assert.Equal("Basalt 서버 포트와 프로세스 트리", python.Basis);
        Assert.Equal(ProcessKind.App, ProcessKinds.Classify(20, "python.exe", null, null, false).Kind);
    }

    [Fact]
    public void The_page_marks_basalts_processes_shows_basalt_in_the_toolbar_and_watches_its_port()
    {
        var time = new ManualTime(T);
        var provider = new ScriptedGpuMemoryProvider(_ => Snapshot());
        var monitor = new GpuMemoryMonitor(() => provider, time);
        var basalt = new BasaltContext(8080, "Basalt 실행 중", 20, Table()[20].StartTime, T);
        var vm = new GpuMemoryViewModel(monitor, Hub(time), () => new StrataContext(null, "꺼짐"), time, basalt: () => basalt);
        vm.IsVisible = true;
        vm.Refresh();
        monitor.ReadNow();
        vm.Refresh();

        Assert.Contains(8080, monitor.WatchedPorts);
        Assert.True(vm.ShowBasaltStatus);
        Assert.Equal("Basalt · basalt-engine.exe", vm.BasaltText);
        Assert.Contains("basalt.exe", vm.BasaltToolTip, StringComparison.Ordinal);
        Assert.False(vm.ShowStrataStatus);
        var python = vm.Processes.Single(r => r.Name == "python.exe");
        Assert.Equal("Basalt", python.ServerLabel);
        Assert.Equal(ProcessKind.Llm, python.Kind);
        Assert.Equal("Basalt", vm.Processes.Single(r => r.Name == "basalt-engine.exe").ServerLabel);
        Assert.Equal("", vm.Processes.Single(r => r.Name == "chrome.exe").ServerLabel);

        vm.ShowBasaltCommand.Execute(null);
        Assert.Equal("basalt-engine.exe", vm.SelectedProcess?.Name);
        Assert.Equal("Basalt", vm.Detail?.ServerLabel);
        Assert.Contains("PID 20 python.exe", vm.Detail?.ServerEvidence, StringComparison.Ordinal);

        basalt = new BasaltContext(null, "Basalt 꺼짐");
        monitor.ReadNow();
        vm.Refresh();
        Assert.False(vm.ShowBasaltStatus);
        Assert.Equal("", vm.Processes.Single(r => r.Name == "python.exe").ServerLabel);
    }
}
