using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.Core.Planning;

public sealed record PlanningContext
{
    public required string Request { get; init; }
    public required TaskRun Run { get; init; }
    public required SafetySettings Settings { get; init; }

    /// <summary>Open windows of enabled allow-listed apps (titles are untrusted text).</summary>
    public required IReadOnlyList<WindowInfo> AllowedWindows { get; init; }
    public UiSnapshot? Snapshot { get; init; }
    public required ElementRegistry Registry { get; init; }
    public IReadOnlyList<StepRecord> History { get; init; } = [];
    public string? FailureReason { get; init; }
    public int NextStepId { get; init; } = 1;
    public bool VisionAvailable { get; init; }
    public ScreenImage? Screenshot { get; init; }
}

public sealed record PlanOutcome(TaskPlan? Plan, string? Error, ExecutionErrorKind? ErrorKind = null, int ModelCalls = 0, int ModelErrors = 0, IReadOnlyList<string>? Notes = null)
{
    public static PlanOutcome Failed(string error, ExecutionErrorKind kind, int calls = 0, int errors = 0) => new(null, error, kind, calls, errors);
}

/// <summary>Where plans come from: the AI planner, or a stored workflow (no model).</summary>
public interface IPlanSource
{
    string Label { get; }
    bool UsesModel { get; }
    bool CanReplan { get; }

    /// <summary>Whether this source can send screenshots (a model that really accepts images, with vision allowed).</summary>
    bool VisionAvailable { get; }

    Task<PlanOutcome> CreatePlanAsync(PlanningContext context, CancellationToken cancellationToken);

    Task<PlanOutcome> ReplanAsync(PlanningContext context, CancellationToken cancellationToken);
}

/// <summary>A fixed plan (workflow or test script). It never changes during the run.</summary>
public sealed class StaticPlanSource(TaskPlan plan, string label) : IPlanSource
{
    public string Label { get; } = label;
    public bool UsesModel => false;
    public bool CanReplan => false;
    public bool VisionAvailable => false;

    public Task<PlanOutcome> CreatePlanAsync(PlanningContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new PlanOutcome(plan, null));

    public Task<PlanOutcome> ReplanAsync(PlanningContext context, CancellationToken cancellationToken) =>
        Task.FromResult(PlanOutcome.Failed("고정 계획(워크플로)은 실행 중에 다시 계획하지 않습니다.", ExecutionErrorKind.InvalidPlan));
}
