using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Automation.Mock;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.Tests;

internal static class TestSupport
{
    public static readonly ExecutorOptions FastOptions = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        VerifyTimeout = TimeSpan.FromMilliseconds(150),
        LaunchTimeout = TimeSpan.FromSeconds(1),
    };

    public static SafetySettings Settings(Action<SafetySettings>? configure = null)
    {
        var settings = new SafetySettings
        {
            StepTimeoutSeconds = 2,
            MinActionIntervalMs = 0,
            RequirePlanReview = false,
            ApprovalTimeoutSeconds = 5,
        };
        settings.AllowedApps.AddRange(AllowedApp.Defaults());
        configure?.Invoke(settings);
        return settings;
    }

    public static TaskPlan Plan(string json, bool allowClickPoint = false)
    {
        var result = PlanParser.Parse(json, new PlanParserOptions { AllowClickPoint = allowClickPoint });
        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        return result.Plan!;
    }

    public static async Task<TaskRun> RunStatic(
        MockDesktop desktop,
        string planJson,
        IApprovalHandler? approvals = null,
        SafetySettings? settings = null,
        ExecutionControl? control = null,
        ITaskObserver? observer = null)
    {
        var executor = new TaskExecutor(desktop, new PolicyEngine(), approvals ?? ScriptedApprovalHandler.DenyAll(), observer, options: FastOptions);
        var run = new TaskRun { Request = "테스트", IsMockDesktop = true, Source = TaskSource.Workflow };
        using var ownedControl = control is null ? new ExecutionControl() : null;
        return await executor.RunAsync(run, new StaticPlanSource(Plan(planJson), "test"), settings ?? Settings(), control ?? ownedControl!, TestContext.Current.CancellationToken);
    }

    public static MockElement Element(this MockDesktop desktop, string automationId) =>
        desktop.Windows.Select(w => w.Find(automationId)).First(e => e is not null)!;
}

/// <summary>Records what the executor reports.</summary>
internal sealed class RecordingObserver : ITaskObserver
{
    private readonly Lock _gate = new();

    public List<TaskRunStatus> Statuses { get; } = [];
    public List<string> Events { get; } = [];
    public Action<TaskRun>? OnTaskChanged { get; set; }

    public void TaskChanged(TaskRun run)
    {
        lock (_gate)
        {
            Statuses.Add(run.Status);
        }

        OnTaskChanged?.Invoke(run);
    }

    public void StepsChanged(TaskRun run)
    {
    }

    public void StepChanged(TaskRun run, StepRecord step)
    {
    }

    public void Event(TaskRun run, TaskEventLevel level, string message)
    {
        lock (_gate)
        {
            Events.Add($"{level}: {message}");
        }
    }
}

/// <summary>A model-like plan source that returns scripted plans and records the contexts it was given.</summary>
internal sealed class ScriptedPlanSource : IPlanSource
{
    private readonly Func<PlanningContext, string>[] _plans;
    private int _next;

    public ScriptedPlanSource(params string[] plans) => _plans = plans.Select(p => (Func<PlanningContext, string>)(_ => p)).ToArray();

    public ScriptedPlanSource(params Func<PlanningContext, string>[] plans) => _plans = plans;

    /// <summary>The ref the formatted snapshot gave to the element with this automation id (like the real planner does).</summary>
    public static string RefOf(PlanningContext context, string automationId)
    {
        var text = SnapshotFormatter.Format(context.Snapshot!, context.Registry).Text;
        var line = text.Split('\n').First(l => l.Contains("id=" + automationId, StringComparison.Ordinal));
        return line[(line.IndexOf('[', StringComparison.Ordinal) + 1)..line.IndexOf(']', StringComparison.Ordinal)];
    }

    public List<PlanningContext> Contexts { get; } = [];
    public string Label => "scripted";
    public bool UsesModel => true;
    public bool CanReplan => true;
    public bool VisionAvailable { get; init; }

    public Task<PlanOutcome> CreatePlanAsync(PlanningContext context, CancellationToken cancellationToken) => Next(context);

    public Task<PlanOutcome> ReplanAsync(PlanningContext context, CancellationToken cancellationToken) => Next(context);

    private Task<PlanOutcome> Next(PlanningContext context)
    {
        Contexts.Add(context);
        if (_next >= _plans.Length)
        {
            return Task.FromResult(PlanOutcome.Failed("스크립트된 계획이 더 없습니다.", ExecutionErrorKind.ModelError, 1, 1));
        }

        var result = PlanParser.Parse(_plans[_next++](context), new PlanParserOptions { AllowClickPoint = VisionAvailable });
        return Task.FromResult(result.IsValid
            ? new PlanOutcome(result.Plan, null, null, 1)
            : PlanOutcome.Failed(string.Join("; ", result.Errors), ExecutionErrorKind.ModelOutputRejected, 1, 1));
    }
}
