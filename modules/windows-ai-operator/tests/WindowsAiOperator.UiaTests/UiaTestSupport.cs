using System.Diagnostics;
using WindowsAiOperator.Automation.Windows;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Core.Settings;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace WindowsAiOperator.UiaTests;

/// <summary>Starts a fresh instance of the real test app with a unique title and closes it afterwards.</summary>
internal sealed class TestTargetApp : IDisposable
{
    private readonly Process _process;

    private TestTargetApp(Process process, string suffix, WindowInfo window)
    {
        _process = process;
        Suffix = suffix;
        Window = window;
    }

    public string Suffix { get; }
    public WindowInfo Window { get; }
    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;

    /// <summary>JSON window selector for plans.</summary>
    public string Selector => "{\"app\":\"test-target\",\"title_contains\":\"[" + Suffix + "]\"}";

    public static string ExecutablePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("WAO_TEST_TARGET_EXE");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        // PC Control Center: E now lives in modules/windows-ai-operator (no WindowsAiOperator.slnx); find its src folder instead.
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "WindowsAiOperator.TestTarget")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new FileNotFoundException("프로젝트 E 폴더(src/WindowsAiOperator.TestTarget)를 찾지 못했습니다.");
        }

        var path = Path.Combine(directory.FullName, "src", "WindowsAiOperator.TestTarget", "bin", configuration, "net10.0-windows", "OperatorTestTarget.exe");
        return File.Exists(path) ? path : throw new FileNotFoundException("테스트 앱이 빌드되지 않았습니다: " + path);
    }

    public static TestTargetApp Start(FlaUiDesktop desktop, params string[] extraArgs)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var info = new ProcessStartInfo(ExecutablePath()) { UseShellExecute = false };
        info.ArgumentList.Add("--title-suffix");
        info.ArgumentList.Add(suffix);
        info.ArgumentList.Add("--exit-after");
        info.ArgumentList.Add("300");
        foreach (var arg in extraArgs)
        {
            info.ArgumentList.Add(arg);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("테스트 앱을 시작하지 못했습니다.");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var window = desktop.ListWindows().FirstOrDefault(w => w.ProcessId == process.Id && w.Title.Contains(suffix, StringComparison.Ordinal));
            if (window is not null)
            {
                Thread.Sleep(300);
                return new TestTargetApp(process, suffix, window);
            }

            Thread.Sleep(200);
        }

        process.Kill(entireProcessTree: true);
        throw new TimeoutException("테스트 앱 창이 30초 안에 나타나지 않았습니다.");
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }
}

internal static class Uia
{
    public static readonly ExecutorOptions Options = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(100),
        VerifyTimeout = TimeSpan.FromSeconds(3),
        LaunchTimeout = TimeSpan.FromSeconds(20),
        PauseOnUnexpectedWindow = false,
    };

    public static SafetySettings Settings(Action<SafetySettings>? configure = null)
    {
        var settings = new SafetySettings { StepTimeoutSeconds = 10, MinActionIntervalMs = 50, RequirePlanReview = false, ApprovalTimeoutSeconds = 10 };
        settings.AllowedApps.AddRange(AllowedApp.Defaults());
        configure?.Invoke(settings);
        return settings;
    }

    public static async Task<TaskRun> Run(FlaUiDesktop desktop, string planJson, IApprovalHandler? approvals = null, SafetySettings? settings = null, IPlanSource? source = null, long? initialWindow = null)
    {
        var parsed = PlanParser.Parse(planJson);
        Assert.True(parsed.IsValid, string.Join("\n", parsed.Errors));
        var executor = new TaskExecutor(desktop, new PolicyEngine(), approvals ?? ScriptedApprovalHandler.DenyAll(), options: Options);
        using var control = new ExecutionControl();
        var run = new TaskRun { Request = "UIA 테스트", InitialWindowHandle = initialWindow };
        return await executor.RunAsync(run, source ?? new StaticPlanSource(parsed.Plan!, "uia-test"), settings ?? Settings(), control, TestContext.Current.CancellationToken);
    }

    public static ElementInfo Find(FlaUiDesktop desktop, TestTargetApp app, string automationId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            var found = desktop.FindElements(app.Window.Handle, new ElementQuery { AutomationId = automationId });
            if (found.Count > 0)
            {
                return found[0];
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"요소 {automationId}를 찾지 못했습니다.");
            }

            Thread.Sleep(100);
        }
    }

    public static string Describe(TaskRun run) =>
        $"{run.Status} ({run.Error}): {run.StatusMessage}\n" + string.Join("\n", run.Steps.Select(s => $"  {s.Sequence}. {s.Step.Action} {s.Status} {s.Error} {s.Method} | {s.Message}"));
}

/// <summary>Plan source that behaves like the AI planner (snapshot → plan) without a model.</summary>
internal sealed class FixedModelLikeSource(string json) : IPlanSource
{
    public string Label => "fixed";
    public bool UsesModel => true;
    public bool CanReplan => false;
    public bool VisionAvailable => false;
    public PlanningContext? Context { get; private set; }

    public Task<PlanOutcome> CreatePlanAsync(PlanningContext context, CancellationToken cancellationToken)
    {
        Context = context;
        return Task.FromResult(new PlanOutcome(PlanParser.Parse(json).Plan, null, null, 1));
    }

    public Task<PlanOutcome> ReplanAsync(PlanningContext context, CancellationToken cancellationToken) =>
        Task.FromResult(PlanOutcome.Failed("no replans", ExecutionErrorKind.ModelError));
}
