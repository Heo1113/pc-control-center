using Iron.App.Presentation;
using Iron.ProcessIdentity;
using Iron.Stage2;

namespace Iron.App.Tests;

/// <summary>Stage 2 in the window logic: real frames mapped to the Stage 1 frame, real identities, the source switch.</summary>
public class Stage2AppTests
{
    private static Stage2Frame Frame(params Stage2Process[] processes) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        Phase = Stage2Phase.Ready,
        Gpus = [new Stage2Gpu(0, "RTX 5080", 61, 210, 55, 9, 16, null), new Stage2Gpu(1, "RTX 5060 Ti", 44, null, 3, 1.2, 16, 210)],
        Cpu = new Stage2Cpu(12.5, 64.8, null, null),
        RamUsedGb = 40,
        RamTotalGb = 128,
        SsdTemperatureC = 48,
        SsdReadMBps = null,
        WireView = new Stage2WireView(true, 334.8, 12.07, null, null, [4.62, 4.71, 4.55, 4.8, 4.49, 4.66], "COMPLETE"),
        Processes = processes,
        Sources = [new SourceStatus("NVML (GPU)", SourceState.Live), new SourceStatus("HWiNFO REST", SourceState.Offline, "connection refused")],
    };

    [Fact]
    public void RealFrameMapsWithoutInventingValues()
    {
        var t = RealTelemetrySource.Map(Frame(new Stage2Process(100, "python.exe", "RTX 5080", 6, 0.5, 40, "Compute", "python.exe train.py --api-key=***", false, null)));
        Assert.False(t.IsMock);
        Assert.Equal(SensorPhase.Ready, t.Phase);
        Assert.Equal(2, t.Gpus.Count);
        Assert.Null(t.Gpus[1].PowerW); // not reported: N/A, never 0
        Assert.Null(t.Cpu.PackageC);
        Assert.Equal(64.8, t.Cpu.TctlC);
        Assert.Null(t.Ssd.ReadMBps);
        Assert.True(t.WireView.Connected);
        Assert.Equal(6, t.WireView.PinCurrentsA.Count);
        Assert.False(t.Flux.Connected); // FLUX PRO output is not part of the data connection
        var p = Assert.Single(t.Processes);
        Assert.Equal("python.exe train.py --api-key=***", p.CommandLine);
    }

    [Fact]
    public void ShellRunsOnTheRealSourceAndSaysSo()
    {
        var scheduler = new TestScheduler(Rig.T0);
        var python = new ProcessIdentityInfo
        {
            Key = new ProcessKey(100, 7),
            ImageName = "python.exe",
            Source = IdentitySource.Native,
            Title = new IdentityText("Python", IdentityConfidence.Confirmed, "버전 정보"),
            Executor = new IdentityText("train.py · 프로젝트 ml-lab", IdentityConfidence.Inferred, "명령줄"),
            RedactedCommandLine = "python.exe train.py --api-key=***",
        };
        var known = new Dictionary<int, ProcessIdentityInfo>();
        var frame = Frame(
            new Stage2Process(100, "python.exe", "RTX 5080", 6, 0.5, 40, "Compute", "python.exe train.py --api-key=***", false, python),
            new Stage2Process(200, "game.exe", "RTX 5080", 3, 0.1, 80, "3D", "", false, null));
        var source = new RealTelemetrySource(_ => frame);
        var vm = new ShellViewModel(new MockTelemetryProvider(jitter: false), scheduler, source,
            new RealProcessIdentityProvider(pid => known.GetValueOrDefault(pid)));
        vm.Tick();
        Assert.False(vm.IsMockSource);
        Assert.Equal("실측 데이터", vm.MockButtonLabel);
        Assert.Contains("HWiNFO REST: 미연결 — connection refused", vm.SourceToolTip, StringComparison.Ordinal);
        var rows = vm.Processes.Rows;
        Assert.Equal(2, rows.Count);
        var pending = rows.Single(r => r.Pid == 200).Identity!;
        Assert.Equal("game.exe", pending.Name); // not resolved yet: its executable name, confirmed (it is the real name)
        Assert.False(pending.IsInferred);
        Assert.Contains("식별 중", pending.Text, StringComparison.Ordinal);
        known[100] = python;
        vm.Tick();
        var py = rows.Single(r => r.Pid == 100).Identity!;
        Assert.Equal("Python · train.py", py.Name);
        Assert.Equal(IdentitySource.Native, py.Source);
        Assert.DoesNotContain("모의", py.Text, StringComparison.Ordinal);
        vm.Processes.Search = "Python"; // the identified name is searchable
        Assert.Equal([100], vm.Processes.Rows.Select(r => r.Pid));
    }

    [Fact]
    public void MockShellIsUnchanged()
    {
        var vm = new ShellViewModel(new MockTelemetryProvider(jitter: false), new TestScheduler(Rig.T0));
        vm.Tick();
        Assert.True(vm.IsMockSource);
        Assert.Equal("모의 데이터", vm.MockButtonLabel);
        Assert.StartsWith("모의 데이터 (Stage 1) — 상태 전환: F5 정상", vm.SourceToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCapacitiesStayNotAvailable()
    {
        // NVML gave VRAM used but no total, Windows gave no installed RAM: no 16 GB / 128 GB stand-in
        var frame = Frame() with
        {
            Gpus = [new Stage2Gpu(0, "RTX 5080", 61, 210, 55, 9, null, null), new Stage2Gpu(1, "RTX 5060 Ti", 44, 30, 3, 1.2, 16, 210)],
            RamTotalGb = null,
        };
        var t = RealTelemetrySource.Map(frame);
        Assert.Null(t.Gpus[0].VramTotalGb);
        Assert.Null(t.Ram.TotalGb);
        var vm = new ShellViewModel(new MockTelemetryProvider(jitter: false), new TestScheduler(Rig.T0), new RealTelemetrySource(_ => frame));
        vm.Tick();
        Assert.Equal("/ N/A", vm.Gpus[0].VramTotalText);
        Assert.Null(vm.Gpus[0].VramRatio); // no meter against a guessed size
        Assert.Equal("9.0", vm.Gpus[0].VramText); // the measured part is still shown
        Assert.Equal("/ 16 GB", vm.Gpus[1].VramTotalText);
        Assert.Equal("40 GB / N/A", vm.Ram.Value);
        Assert.Null(vm.Ram.Ratio);
    }

    [Fact]
    public void MockCapacitiesAreUnchanged()
    {
        var vm = new ShellViewModel(new MockTelemetryProvider(jitter: false), new TestScheduler(Rig.T0));
        vm.Tick();
        Assert.Equal("/ 16 GB", vm.Gpus[0].VramTotalText);
        Assert.Equal("/ 16 GB", vm.Gpus[1].VramTotalText);
        Assert.EndsWith(" / 128 GB", vm.Ram.Value, StringComparison.Ordinal);
        Assert.EndsWith(" · 모의 데이터", vm.TrayToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuCardsDoNotKeepValuesTheReaderNoLongerGives()
    {
        var frame = Frame();
        var vm = new ShellViewModel(new MockTelemetryProvider(jitter: false), new TestScheduler(Rig.T0), new RealTelemetrySource(_ => frame));
        vm.Tick();
        Assert.Equal("61°C", vm.Gpus[0].TemperatureText);
        Assert.Equal("44°C", vm.Gpus[1].TemperatureText);

        // NVML lost the GPUs (or the hardware reader stopped and its reading went stale): the frame has no GPU readings
        frame = Frame() with { Phase = Stage2Phase.NoDevice, Gpus = [] };
        vm.Tick();
        foreach (var card in vm.Gpus)
        {
            Assert.Equal(Fmt.Missing, card.TemperatureText);
            Assert.Equal(Fmt.Missing, card.VramText);
            Assert.Equal("/ N/A", card.VramTotalText);
            Assert.Null(card.LoadRatio);
            Assert.False(card.IsHot);
        }

        Assert.Equal("RTX 5080", vm.Gpus[0].Name); // the card keeps its label, not its values
        Assert.EndsWith(" · 실측 데이터", vm.TrayToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessListSaysWhyItIsEmptyWhenWddmGaveNothingCurrent()
    {
        var frame = Frame(new Stage2Process(100, "python.exe", "RTX 5080", 6, 0.5, 40, "Compute", "", false, null));
        var vm = new ShellViewModel(new MockTelemetryProvider(jitter: false), new TestScheduler(Rig.T0), new RealTelemetrySource(_ => frame));
        vm.Tick();
        Assert.Single(vm.Processes.Rows);

        frame = Frame() with { ProcessesUnavailable = "WDDM 카운터 값이 9초째 들어오지 않아 프로세스 목록을 표시하지 않습니다." };
        vm.Tick();
        Assert.Empty(vm.Processes.Rows); // the previous process is not kept
        Assert.Equal("WDDM 카운터 값이 9초째 들어오지 않아 프로세스 목록을 표시하지 않습니다.", vm.Processes.EmptyText);

        frame = Frame();
        vm.Tick();
        Assert.Equal("GPU 메모리를 쓰는 프로세스가 없습니다.", vm.Processes.EmptyText); // current and empty
    }

    [Fact]
    public void DualGpuProcessIsOneRowInAllAndListedUnderEachGpuItUses()
    {
        // A1/A2 (docs/IRON_STAGE2.md, "듀얼 GPU"): one row per process in 전체 (the GPU with more dedicated memory, the other in
        // the tooltip); the RTX 5060 Ti filter lists it with its 5060 Ti memory (before A1 it was missing there).
        var frame = Frame(new Stage2Process(100, "python.exe", "RTX 5080", 6, 0, 40, "Compute", "", false, null, [new AdapterShare("RTX 5060 Ti", 1, 0, 2, 12, "Compute")], 1, 7));
        var vm = new ShellViewModel(new MockTelemetryProvider(jitter: false), new TestScheduler(Rig.T0), new RealTelemetrySource(_ => frame));
        vm.Tick();
        var row = Assert.Single(vm.Processes.Rows);
        Assert.Equal("RTX 5080", row.Adapter);
        Assert.NotNull(row.UsageNote);
        vm.Processes.Filter = "RTX 5060 Ti";
        Assert.Equal("1.00 GB", Assert.Single(vm.Processes.Rows).DedicatedText);
        vm.Processes.Filter = "RTX 5080";
        Assert.Equal("6.00 GB", Assert.Single(vm.Processes.Rows).DedicatedText);
    }
}
