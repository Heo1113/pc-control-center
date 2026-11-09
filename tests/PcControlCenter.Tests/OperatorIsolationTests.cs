using System.IO.Pipes;
using PcControlCenter.Operator;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Automation.Mock;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Core.Settings;

namespace PcControlCenter.Tests;

/// <summary>The host's server and the shell's client connected by in-process pipes (the same streams a host process uses).</summary>
internal sealed class InProcessHost : IHostConnectionSource, IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _toHost = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _toShell = new(PipeDirection.Out);
    private readonly Task _server;
    private int _inFlight;

    private InProcessHost(MockDesktop desktop, HostGuard guard)
    {
        Desktop = desktop;
        Guard = guard;
        var hostIn = new AnonymousPipeClientStream(PipeDirection.In, _toHost.ClientSafePipeHandle);
        var hostOut = new AnonymousPipeClientStream(PipeDirection.In, _toShell.ClientSafePipeHandle);
        HostReader = hostOut;
        _server = Task.Run(() => new DesktopServer(desktop, hostIn, _toShell, guard).RunAsync());
    }

    public MockDesktop Desktop { get; }
    public HostGuard Guard { get; }
    public HostConnection? Current { get; private set; }
    public string UnavailableReason => "test host not connected";
    public bool EmergencyLatched { get; set; }
    public bool ExpectMock => true;
    public int MaxInFlight { get; private set; }
    private Stream HostReader { get; }

    public static async Task<InProcessHost> StartAsync(MockDesktop? desktop = null, params int[] protectedPids)
    {
        var host = new InProcessHost(desktop ?? MockDesktop.CreateDemo(), new HostGuard(protectedPids));
        host.Current = await HostConnection.OpenAsync(host.HostReader, host._toHost, TimeSpan.FromSeconds(10));
        return host;
    }

    public void BeginAction() => MaxInFlight = Math.Max(MaxInFlight, Interlocked.Increment(ref _inFlight));

    public void EndAction() => Interlocked.Decrement(ref _inFlight);

    public async ValueTask DisposeAsync()
    {
        if (Current is not null)
        {
            await Current.TrySendControlAsync(HostOp.Shutdown, TimeSpan.FromSeconds(2));
            await Current.DisposeAsync();
        }

        await Task.WhenAny(_server, Task.Delay(TimeSpan.FromSeconds(5)));
        _toHost.Dispose();
        _toShell.Dispose();
    }
}

public class OperatorIsolationTests
{
    private static readonly ExecutorOptions Fast = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        VerifyTimeout = TimeSpan.FromMilliseconds(300),
        LaunchTimeout = TimeSpan.FromSeconds(1),
    };

    private static SafetySettings Settings()
    {
        var settings = new SafetySettings { StepTimeoutSeconds = 5, MinActionIntervalMs = 0, RequirePlanReview = false, ApprovalTimeoutSeconds = 5 };
        settings.AllowedApps.AddRange(AllowedApp.Defaults());
        return settings;
    }

    private static async Task<TaskRun> Run(IDesktopAutomation desktop, string planJson, IApprovalHandler approvals)
    {
        var parsed = PlanParser.Parse(planJson, new PlanParserOptions());
        Assert.True(parsed.IsValid, string.Join("\n", parsed.Errors));
        var executor = new TaskExecutor(desktop, new PolicyEngine(), approvals, options: Fast);
        var run = new TaskRun { Request = "테스트", IsMockDesktop = true, Source = TaskSource.Workflow };
        using var control = new ExecutionControl();
        return await executor.RunAsync(run, new StaticPlanSource(parsed.Plan!, "test"), Settings(), control, TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 60_000)]
    public async Task E_executor_runs_plans_through_the_process_boundary()
    {
        await using var host = await InProcessHost.StartAsync();
        var remote = new RemoteDesktop(host, ownProcessId: Environment.ProcessId);

        var run = await Run(remote, """
            {"steps":[
              {"id":1,"action":"click","window":{"app":"test-target"},"element":{"automation_id":"IncrementButton"}},
              {"id":2,"action":"read_element","element":{"automation_id":"CounterText"}},
              {"id":3,"action":"finish","message":"결과: {{s2.value}}"}]}
            """, ScriptedApprovalHandler.DenyAll());

        Assert.Equal(TaskRunStatus.Completed, run.Status);
        Assert.Equal("결과: 카운트: 1", run.Report);
        Assert.Contains(host.Desktop.ActionLog, e => e.StartsWith("Invoke 카운트 증가", StringComparison.Ordinal));
        Assert.True(remote.IsMock);
        Assert.Equal(1, host.MaxInFlight);
    }

    [Fact(Timeout = 60_000)]
    public async Task E_policy_and_approval_still_decide_first()
    {
        await using var host = await InProcessHost.StartAsync();
        var remote = new RemoteDesktop(host, Environment.ProcessId);
        var approvals = ScriptedApprovalHandler.DenyAll();

        var run = await Run(remote, """
            {"steps":[{"action":"click","window":{"app":"test-target"},"element":{"automation_id":"DeleteItemButton"}}]}
            """, approvals);

        Assert.Equal(TaskRunStatus.Denied, run.Status);
        Assert.Empty(host.Desktop.ActionLog);
        Assert.Single(approvals.Requests);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_host_refuses_protected_blocked_and_elevated_windows_even_if_the_shell_asks()
    {
        var desktop = MockDesktop.CreateDemo(openTestTarget: false);
        var shellWindow = desktop.AddWindow(new MockWindow { ProcessId = 777, ProcessName = "PcControlCenter", Title = "PC Control Center", Root = MockApps.CreateTestTarget(desktop).Root });
        var cmd = desktop.AddWindow(new MockWindow { ProcessId = 900, ProcessName = "cmd", Title = "명령 프롬프트", Root = MockApps.CreateTestTarget(desktop).Root });
        var admin = desktop.AddWindow(new MockWindow { ProcessId = 901, ProcessName = "OperatorTestTarget", Title = "관리자", IsElevated = true, Root = MockApps.CreateTestTarget(desktop).Root });
        await using var host = await InProcessHost.StartAsync(desktop, 777);
        var remote = new RemoteDesktop(host, Environment.ProcessId);

        foreach (var window in new[] { shellWindow, cmd, admin })
        {
            // Bypasses E's policy on purpose (a buggy or compromised shell): the host's own guard must still refuse.
            var outcome = await remote.PerformAsync(new ActionRequest { Action = PrimitiveAction.Focus, WindowHandle = window.Handle }, TestContext.Current.CancellationToken);
            Assert.False(outcome.Performed);
            Assert.Equal(ExecutionErrorKind.PolicyDenied, outcome.Error);
            Assert.False(remote.FocusWindow(window.Handle));
        }

        var launch = await remote.LaunchAsync(@"C:\Windows\System32\cmd.exe", ["cmd"], TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(launch.Success);
        Assert.Empty(desktop.ActionLog);
    }

    [Fact(Timeout = 60_000)]
    public async Task Emergency_latch_refuses_every_change_until_resume()
    {
        await using var host = await InProcessHost.StartAsync();
        var remote = new RemoteDesktop(host, Environment.ProcessId);
        var target = host.Desktop.Windows.First(w => w.ProcessName == "OperatorTestTarget");
        var increment = remote.FindElements(target.Handle, new ElementQuery { AutomationId = "IncrementButton" }, TestContext.Current.CancellationToken).Single();
        var press = new ActionRequest { Action = PrimitiveAction.Invoke, WindowHandle = target.Handle, RuntimeId = increment.RuntimeId };

        Assert.True(await host.Current!.TrySendControlAsync(HostOp.EmergencyStop, TimeSpan.FromSeconds(2)));
        var refused = await remote.PerformAsync(press, TestContext.Current.CancellationToken);
        Assert.False(refused.Performed);
        Assert.Equal(ExecutionErrorKind.Cancelled, refused.Error);
        Assert.Empty(host.Desktop.ActionLog);

        // The shell-side latch (kept across host restarts) refuses before anything is sent.
        host.EmergencyLatched = true;
        Assert.Equal(ExecutionErrorKind.Cancelled, (await remote.PerformAsync(press, TestContext.Current.CancellationToken)).Error);
        Assert.False(remote.Status.Available);

        host.EmergencyLatched = false;
        Assert.True(await host.Current.TrySendControlAsync(HostOp.Resume, TimeSpan.FromSeconds(2)));
        var done = await remote.PerformAsync(press, TestContext.Current.CancellationToken);
        Assert.True(done.Success, done.Message);
        Assert.Single(host.Desktop.ActionLog);
    }

    [Fact(Timeout = 60_000)]
    public async Task UI_trees_survive_the_wire_unchanged()
    {
        await using var host = await InProcessHost.StartAsync();
        var remote = new RemoteDesktop(host, Environment.ProcessId);
        var target = host.Desktop.Windows.First(w => w.ProcessName == "OperatorTestTarget");

        var local = host.Desktop.CaptureTree(target.Handle, new TreeCaptureOptions(), TestContext.Current.CancellationToken);
        var viaHost = remote.CaptureTree(target.Handle, new TreeCaptureOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(local.NodeCount, viaHost.NodeCount);
        Assert.Equal(local.Window, viaHost.Window);
        Assert.Equal(local.Flatten().ToList(), viaHost.Flatten().ToList());
        Assert.Equal(host.Desktop.ListWindows(), remote.ListWindows());
    }

    [Fact(Timeout = 60_000)]
    public async Task A_lost_host_is_reported_not_hidden()
    {
        var host = await InProcessHost.StartAsync();
        var remote = new RemoteDesktop(host, Environment.ProcessId);
        await host.DisposeAsync();

        Assert.Throws<HostUnavailableException>(() => remote.ListWindows());
        Assert.Throws<HostUnavailableException>(() => remote.GetWindow(1));
        Assert.False(remote.Status.Available);
        var outcome = await remote.PerformAsync(new ActionRequest { Action = PrimitiveAction.Focus, WindowHandle = 1 }, TestContext.Current.CancellationToken);
        Assert.False(outcome.Success);
    }
}

/// <summary>The real separate process (test host with E's simulated desktop), as the shell starts it.</summary>
public class OperatorSupervisorTests
{
    private static string MockHostPath()
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PcControlCenter.MockOperatorHost", "bin", configuration, "net10.0",
            "PcControlCenter.MockOperatorHost.dll"));
        Assert.True(File.Exists(path), path);
        return path;
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

    private static OperatorHostSupervisor New(TempDir temp, int maxRestarts = 3) => new(new OperatorHostOptions
    {
        HostPath = MockHostPath(),
        LogDirectory = temp.Sub("logs"),
        MockDesktop = true,
        MaxAutomaticRestarts = maxRestarts,
        RestartDelay = TimeSpan.FromMilliseconds(200),
    });

    [Fact(Timeout = 120_000)]
    public async Task The_host_runs_as_a_separate_process_and_is_restarted_when_it_dies()
    {
        using var temp = new TempDir();
        await using var supervisor = New(temp);
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HostState.Connected, supervisor.State);
        var remote = new RemoteDesktop(supervisor, Environment.ProcessId);
        Assert.True(remote.IsMock);
        Assert.NotEmpty(remote.ListWindows());
        var first = supervisor.ProcessId!.Value;
        Assert.NotEqual(Environment.ProcessId, first);

        using (var process = System.Diagnostics.Process.GetProcessById(first))
        {
            process.Kill();
        }

        Assert.True(await Eventually(() => supervisor.State == HostState.Connected && supervisor.ProcessId is { } p && p != first, TimeSpan.FromSeconds(30)), supervisor.UnavailableReason);
        Assert.Equal(1, supervisor.Restarts);
        Assert.NotEmpty(remote.ListWindows());
    }

    [Fact(Timeout = 120_000)]
    public async Task Emergency_stop_with_input_in_flight_kills_the_host_and_stays_latched()
    {
        using var temp = new TempDir();
        await using var supervisor = New(temp);
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var remote = new RemoteDesktop(supervisor, Environment.ProcessId);
        var first = supervisor.ProcessId!.Value;

        supervisor.BeginAction();
        await supervisor.EmergencyStopAsync();
        supervisor.EndAction();

        Assert.True(await Eventually(() => supervisor.ProcessId is { } p && p != first && supervisor.State == HostState.Connected, TimeSpan.FromSeconds(30)));
        Assert.True(supervisor.EmergencyLatched);
        var window = remote.ListWindows().First(w => w.ProcessName == "OperatorTestTarget");
        var increment = remote.FindElements(window.Handle, new ElementQuery { AutomationId = "IncrementButton" }, TestContext.Current.CancellationToken).Single();
        var press = new ActionRequest { Action = PrimitiveAction.Invoke, WindowHandle = window.Handle, RuntimeId = increment.RuntimeId };
        var refused = await remote.PerformAsync(press, TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionErrorKind.Cancelled, refused.Error);
        Assert.False(refused.Performed);

        await supervisor.ResumeAsync();
        Assert.False(supervisor.EmergencyLatched);
        var done = await remote.PerformAsync(press, TestContext.Current.CancellationToken);
        Assert.True(done.Success, done.Message);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_host_that_keeps_dying_is_not_restarted_forever()
    {
        using var temp = new TempDir();
        await using var supervisor = New(temp, maxRestarts: 1);
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 2; i++)
        {
            var pid = supervisor.ProcessId!.Value;
            using (var process = System.Diagnostics.Process.GetProcessById(pid))
            {
                process.Kill();
            }

            if (i == 0)
            {
                Assert.True(await Eventually(() => supervisor.State == HostState.Connected && supervisor.ProcessId != pid, TimeSpan.FromSeconds(30)));
            }
        }

        Assert.True(await Eventually(() => supervisor.State == HostState.Failed, TimeSpan.FromSeconds(30)));
        Assert.Throws<HostUnavailableException>(() => new RemoteDesktop(supervisor, Environment.ProcessId).ListWindows());
        await supervisor.RestartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HostState.Connected, supervisor.State);
    }

    [Fact(Timeout = 120_000)]
    public async Task Stop_ends_the_host_process()
    {
        using var temp = new TempDir();
        var supervisor = New(temp);
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var pid = supervisor.ProcessId!.Value;
        await supervisor.DisposeAsync();

        Assert.Equal(HostState.Stopped, supervisor.State);
        Assert.True(await Eventually(() =>
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                return p.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }, TimeSpan.FromSeconds(10)));
    }
}
