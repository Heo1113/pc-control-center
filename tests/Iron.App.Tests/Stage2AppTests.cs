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
}
