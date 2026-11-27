using Iron.App.Presentation;
using Iron.Stage2;

namespace Iron.App.Tests;

/// <summary>
/// A process using both GPUs (A1-A4): the GPU filter lists it with that GPU's values, "전체" keeps one row per process and
/// names the other GPU in the tooltip, and load histories are kept per process instance (PID + creation time) and GPU (LUID).
/// </summary>
public class DualGpuTests
{
    private const long Luid5080 = 0x1A2B;
    private const long Luid5060 = 0x3C4D;
    private const long T1 = 638_000_000_000_000_000;
    private const long T2 = 638_000_000_100_000_000;

    private static AdapterUsage On5080(double ded, double util, double shared = 0) => new("RTX 5080", Luid5080, ded, shared, util, "Compute");
    private static AdapterUsage On5060(double ded, double util, double shared = 0) => new("RTX 5060 Ti", Luid5060, ded, shared, util, "Compute");

    /// <summary>A process as Stage 2 gives it: the representative GPU (most dedicated memory) in the row, the others aside.</summary>
    private static ProcessReading Proc(int pid, string name, long start, params AdapterUsage[] uses)
    {
        var main = uses.OrderByDescending(u => u.DedicatedGb).First();
        return new ProcessReading(pid, name, main.Adapter, main.DedicatedGb, main.SharedGb, main.UtilizationPercent, main.Engine, "", false,
            main.Luid, start, [.. uses.Where(u => u != main)]);
    }

    [Fact]
    public void GpuFilterListsEveryProcessOnThatGpuWithThatGpusValues()
    {
        var list = new ProcessListViewModel();
        list.Apply([Proc(100, "python.exe", T1, On5080(6, 40), On5060(1, 12, shared: 0.2)), Proc(200, "game.exe", T1, On5060(3, 80))], SensorPhase.Ready);

        // 전체: one row per process, the representative GPU's values, the other GPU only in the tooltip
        Assert.Equal(["python.exe", "game.exe"], list.Rows.Select(r => r.Name));
        var py = list.Rows[0];
        Assert.Equal("RTX 5080", py.Adapter);
        Assert.Equal("6.00 GB", py.DedicatedText);
        Assert.Contains("표시 값은 RTX 5080 한 GPU의 사용량입니다 (모든 GPU의 합계가 아님)", py.UsageNote, StringComparison.Ordinal);
        Assert.Contains("· RTX 5060 Ti: 전용 1.00 GB · 공유 0.20 GB · Compute 12%", py.UsageNote, StringComparison.Ordinal);
        Assert.StartsWith("RTX 5080\n표시 값은", py.AdapterToolTip, StringComparison.Ordinal);
        Assert.Null(list.Rows[1].UsageNote); // one GPU: no tooltip box
        Assert.Equal("RTX 5060 Ti", list.Rows[1].AdapterToolTip);

        // RTX 5060 Ti: python is listed (it was missing before) with its 5060 Ti memory and load, never the 5080's or a sum
        list.Filter = "RTX 5060 Ti";
        Assert.Equal(["game.exe", "python.exe"], list.Rows.Select(r => r.Name)); // sorted by this GPU's dedicated memory
        py = list.Rows.Single(r => r.Pid == 100);
        Assert.Equal("RTX 5060 Ti", py.Adapter);
        Assert.Equal("1.00 GB", py.DedicatedText);
        Assert.Equal("0.20 GB", py.SharedText);
        Assert.Equal("12%", py.UtilizationText);
        Assert.Equal("Compute", py.Engine);
        Assert.Contains("표시 값은 RTX 5060 Ti 한 GPU의 사용량입니다", py.UsageNote, StringComparison.Ordinal);
        Assert.Contains("· RTX 5080: 전용 6.00 GB", py.UsageNote, StringComparison.Ordinal);

        list.Filter = "RTX 5080";
        Assert.Equal("6.00 GB", Assert.Single(list.Rows).DedicatedText);

        list.Filter = ProcessListViewModel.All;
        Assert.Equal(2, list.Rows.Count);
        Assert.Equal("6.00 GB", list.Rows.Single(r => r.Pid == 100).DedicatedText);
    }

    [Fact]
    public void FilterSwitchKeepsTheSelectedProcessAndShowsThatGpusHistory()
    {
        var list = new ProcessListViewModel();
        for (var i = 0; i < 3; i++)
        {
            list.Apply([Proc(100, "python.exe", T1, On5080(6, 40 + i), On5060(1, 10 + i))], SensorPhase.Ready);
        }

        list.Selected = list.Rows.Single();
        Assert.Equal([40.0, 41.0, 42.0], list.SelectedHistory);
        list.Filter = "RTX 5060 Ti";
        Assert.Equal(100, list.Selected!.Pid);
        Assert.Equal([10.0, 11.0, 12.0], list.SelectedHistory); // the 5060 Ti's own load, not the 5080's
        list.Filter = ProcessListViewModel.All;
        Assert.Equal([40.0, 41.0, 42.0], list.SelectedHistory);
    }

    [Fact]
    public void RepresentativeGpuChangeDoesNotMixHistories()
    {
        var list = new ProcessListViewModel();
        list.Apply([Proc(100, "llama-server.exe", T1, On5080(6, 40), On5060(2, 10))], SensorPhase.Ready);
        list.Apply([Proc(100, "llama-server.exe", T1, On5080(6, 41), On5060(2, 11))], SensorPhase.Ready);
        // the model moves: the 5060 Ti now holds more dedicated memory and becomes the representative GPU
        list.Apply([Proc(100, "llama-server.exe", T1, On5080(1, 42), On5060(9, 12))], SensorPhase.Ready);
        var row = list.Rows.Single();
        Assert.Equal("RTX 5060 Ti", row.Adapter);
        list.Selected = row;
        Assert.Equal([10.0, 11.0, 12.0], list.SelectedHistory); // the 5060 Ti's whole history, no 5080 value in it
        list.Filter = "RTX 5080";
        Assert.Equal([40.0, 41.0, 42.0], list.SelectedHistory);
        Assert.Same(row, list.Rows.Single()); // same process instance: same row object
    }

    [Fact]
    public void ReusedPidIsAnotherProcess()
    {
        var list = new ProcessListViewModel();
        list.Apply([Proc(100, "python.exe", T1, On5080(6, 40))], SensorPhase.Ready);
        list.Apply([Proc(100, "python.exe", T1, On5080(6, 41))], SensorPhase.Ready);
        var old = list.Rows.Single();
        list.Selected = old;
        Assert.Equal(2, list.SelectedHistory.Length);

        // python ended and Windows gave PID 100 to a new process (another creation time)
        list.Apply([Proc(100, "node.exe", T2, On5080(0.5, 5))], SensorPhase.Ready);
        var row = list.Rows.Single();
        Assert.NotSame(old, row);
        Assert.Equal("node.exe", row.Name);
        Assert.Equal((100, T2), row.Instance);
        list.Selected = row;
        Assert.Equal([5.0], list.SelectedHistory); // no value of the old python process
    }

    [Fact]
    public void NoCurrentReadingEndsTheHistoriesInsteadOfBridgingTheGap()
    {
        var list = new ProcessListViewModel();
        list.Apply([Proc(100, "python.exe", T1, On5080(6, 40), On5060(1, 10))], SensorPhase.Ready);
        list.Apply([Proc(100, "python.exe", T1, On5080(6, 41), On5060(1, 11))], SensorPhase.Ready);

        list.Apply([], SensorPhase.Ready, "WDDM 카운터 값이 7초째 들어오지 않아 프로세스 목록을 표시하지 않습니다.");
        Assert.Empty(list.Rows);
        Assert.Equal("WDDM 카운터 값이 7초째 들어오지 않아 프로세스 목록을 표시하지 않습니다.", list.EmptyText);

        list.Apply([Proc(100, "python.exe", T1, On5080(6, 50), On5060(1, 20))], SensorPhase.Ready);
        list.Selected = list.Rows.Single();
        Assert.Equal([50.0], list.SelectedHistory);
        list.Filter = "RTX 5060 Ti";
        Assert.Equal([20.0], list.SelectedHistory);
    }

    [Fact]
    public void ProcessThatStopsUsingAGpuLosesOnlyThatHistory()
    {
        var list = new ProcessListViewModel();
        list.Apply([Proc(100, "python.exe", T1, On5080(6, 40), On5060(1, 10))], SensorPhase.Ready);
        list.Apply([Proc(100, "python.exe", T1, On5080(6, 41))], SensorPhase.Ready); // freed its 5060 Ti memory
        list.Selected = list.Rows.Single();
        Assert.Equal([40.0, 41.0], list.SelectedHistory);
        Assert.Null(list.Selected.UsageNote);
        list.Filter = "RTX 5060 Ti";
        Assert.Empty(list.Rows); // nothing on that GPU any more
    }

    [Fact]
    public void TwoCardsOfOneModelAreTwoGpusInside()
    {
        // A4: separate labels are deferred, but the two cards are told apart by LUID (histories, tooltips), never merged by name
        var a = new AdapterUsage("RTX 5080", 0x10, 6, 0, 40, "Compute");
        var b = new AdapterUsage("RTX 5080", 0x20, 4, 0, 70, "Compute");
        var list = new ProcessListViewModel();
        list.Apply([Proc(100, "trainer.exe", T1, a, b)], SensorPhase.Ready);
        list.Apply([Proc(100, "trainer.exe", T1, a with { UtilizationPercent = 41 }, b with { UtilizationPercent = 71 })], SensorPhase.Ready);
        var row = list.Rows.Single();
        Assert.Equal("6.00 GB", row.DedicatedText); // one card's value, not 10 GB
        Assert.Contains("· RTX 5080: 전용 4.00 GB", row.UsageNote, StringComparison.Ordinal);
        list.Selected = row;
        Assert.Equal([40.0, 41.0], list.SelectedHistory); // card 0x10 only
        list.Filter = "RTX 5080";
        Assert.Equal("6.00 GB", list.Rows.Single().DedicatedText); // the larger of its uses on the two cards
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Stage2FramesCarryEveryGpuOfAProcess()
    {
        var frame = new Stage2Frame
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            Phase = Stage2Phase.Ready,
            Processes = [new Stage2Process(100, "python.exe", "RTX 5080", 6, 0, 40, "Compute", "", false, null,
                [new AdapterShare("RTX 5060 Ti", 1, 0.2, Luid5060, 12, "Compute")], Luid5080, T1)],
        };
        var p = Assert.Single(RealTelemetrySource.Map(frame).Processes);
        Assert.Equal(Luid5080, p.AdapterLuid);
        Assert.Equal(T1, p.StartTicks);
        Assert.Equal(new AdapterUsage("RTX 5060 Ti", Luid5060, 1, 0.2, 12, "Compute"), Assert.Single(p.OtherAdapters!));
        var vm = new ShellViewModel(new MockTelemetryProvider(jitter: false), new TestScheduler(Rig.T0), new RealTelemetrySource(_ => frame));
        vm.Tick();
        vm.Processes.Filter = "RTX 5060 Ti";
        Assert.Equal("1.00 GB", Assert.Single(vm.Processes.Rows).DedicatedText);
    }

    [Fact]
    public void MockRowsFilterAsBefore()
    {
        var vm = new ShellViewModel(new MockTelemetryProvider(jitter: false), new TestScheduler(Rig.T0));
        vm.Tick();
        var all = vm.Processes.Rows.Select(r => (r.Pid, r.Adapter, r.DedicatedText)).ToList();
        vm.Processes.Filter = "RTX 5060 Ti";
        var only = vm.Processes.Rows.Select(r => (r.Pid, r.Adapter, r.DedicatedText)).ToList();
        Assert.NotEmpty(only);
        Assert.All(only, r => Assert.Equal("RTX 5060 Ti", r.Adapter));
        Assert.Equal(all.Where(r => r.Adapter == "RTX 5060 Ti"), only); // same rows, same values as without A1
        Assert.All(vm.Processes.Rows, r => Assert.Null(r.UsageNote));
    }
}
