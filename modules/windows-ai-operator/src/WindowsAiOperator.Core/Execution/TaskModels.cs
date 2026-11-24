using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Policy;

namespace WindowsAiOperator.Core.Execution;

public enum TaskRunStatus
{
    Created,
    Planning,
    AwaitingPlanReview,
    Running,
    Paused,
    AwaitingApproval,
    Completed,
    PartiallyCompleted,
    Failed,
    Cancelled,
    Denied,
    NeedsUserInput,

    /// <summary>The app ended while the task was running (found at the next start). Never resumed automatically.</summary>
    Interrupted,
}

public enum StepRunStatus
{
    Pending,
    Running,
    AwaitingApproval,
    Succeeded,
    Failed,
    Denied,
    Skipped,
    Cancelled,

    /// <summary>The app ended during this step: it may or may not have been performed.</summary>
    Unknown,
}

public enum ApprovalState
{
    NotNeeded,
    Pending,
    Approved,
    Denied,
    TimedOut,
}

public enum VerificationState
{
    NotChecked,
    Passed,
    Failed,
}

public enum TaskSource
{
    User,
    Workflow,
    Evaluation,
    Manual,
}

public static class TaskStatusText
{
    public static string Korean(TaskRunStatus status) => status switch
    {
        TaskRunStatus.Created => "준비",
        TaskRunStatus.Planning => "계획 중",
        TaskRunStatus.AwaitingPlanReview => "계획 확인 대기",
        TaskRunStatus.Running => "실행 중",
        TaskRunStatus.Paused => "일시정지",
        TaskRunStatus.AwaitingApproval => "승인 대기",
        TaskRunStatus.Completed => "완료",
        TaskRunStatus.PartiallyCompleted => "부분 완료",
        TaskRunStatus.Failed => "실패",
        TaskRunStatus.Cancelled => "중단됨",
        TaskRunStatus.Denied => "거부·차단됨",
        TaskRunStatus.NeedsUserInput => "사용자 입력 필요",
        TaskRunStatus.Interrupted => "비정상 종료로 중단",
        _ => status.ToString(),
    };

    public static string Korean(StepRunStatus status) => status switch
    {
        StepRunStatus.Pending => "대기",
        StepRunStatus.Running => "실행 중",
        StepRunStatus.AwaitingApproval => "승인 대기",
        StepRunStatus.Succeeded => "성공",
        StepRunStatus.Failed => "실패",
        StepRunStatus.Denied => "차단·거부",
        StepRunStatus.Skipped => "건너뜀",
        StepRunStatus.Cancelled => "취소",
        StepRunStatus.Unknown => "실행 여부 불확실",
        _ => status.ToString(),
    };

    public static bool IsFinal(TaskRunStatus status) => status is TaskRunStatus.Completed or TaskRunStatus.PartiallyCompleted
        or TaskRunStatus.Failed or TaskRunStatus.Cancelled or TaskRunStatus.Denied or TaskRunStatus.NeedsUserInput or TaskRunStatus.Interrupted;
}

/// <summary>One executed (or planned) step. Mutated only by the executor; observers read it.</summary>
public sealed class StepRecord
{
    public required int Sequence { get; init; }
    public required int Round { get; init; }
    public required PlanStep Step { get; init; }
    public StepRunStatus Status { get; set; } = StepRunStatus.Pending;
    public string? TargetApp { get; set; }
    public string? TargetWindow { get; set; }
    public string? TargetElement { get; set; }
    public PolicyDecision? Decision { get; set; }
    public ApprovalState Approval { get; set; } = ApprovalState.NotNeeded;
    public int Attempts { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Message { get; set; }
    public ExecutionErrorKind? Error { get; set; }

    /// <summary>The action was applied to the target (a change may have happened).</summary>
    public bool Performed { get; set; }
    public VerificationState Verification { get; set; } = VerificationState.NotChecked;
    public string? Method { get; set; }

    /// <summary>Value read by read_element (shown to the user, redacted when stored).</summary>
    public string? ReadValue { get; set; }

    /// <summary>The allow-listed app and element the step actually used (stable selectors for saving as a workflow).</summary>
    public string? ResolvedAppId { get; set; }
    public Planning.ElementFingerprint? ResolvedElement { get; set; }

    public bool ChangesState => Step.Action.ChangesState();
}

public sealed class TaskRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Request { get; init; }
    public TaskSource Source { get; init; } = TaskSource.User;
    public string? WorkflowId { get; init; }
    public string? ModelLabel { get; set; }
    public bool IsMockDesktop { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public TaskRunStatus Status { get; set; } = TaskRunStatus.Created;
    public string? StatusMessage { get; set; }
    public ExecutionErrorKind? Error { get; set; }
    public string? TargetApp { get; set; }
    public string? PlanSummary { get; set; }
    public string? Report { get; set; }
    public int Rounds { get; set; }
    public int ModelCalls { get; set; }
    public int ModelErrors { get; set; }
    public int ApprovalsRequested { get; set; }
    public int ApprovalsGranted { get; set; }
    public int UserInterventions { get; set; }
    public bool InjectionSuspected { get; set; }
    public string? LastKnownState { get; set; }

    /// <summary>Folders granted for this task only (in addition to the safety settings).</summary>
    public List<string> GrantedFolders { get; init; } = [];

    /// <summary>Window the user picked as the starting target (optional).</summary>
    public long? InitialWindowHandle { get; init; }

    public List<StepRecord> Steps { get; } = [];

    public int StepsSucceeded => Steps.Count(s => s.Status == StepRunStatus.Succeeded);
    public bool AnyChangePerformed => Steps.Any(s => s.Performed && s.ChangesState);
}

public enum TaskEventLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>Receives progress. The UI and the database journal are both observers. Called on the executor's thread.</summary>
public interface ITaskObserver
{
    void TaskChanged(TaskRun run);

    void StepsChanged(TaskRun run);

    void StepChanged(TaskRun run, StepRecord step);

    void Event(TaskRun run, TaskEventLevel level, string message);
}

public sealed class CompositeTaskObserver(params ITaskObserver[] observers) : ITaskObserver
{
    private readonly ITaskObserver[] _observers = observers;

    public void TaskChanged(TaskRun run) => ForEach(o => o.TaskChanged(run));

    public void StepsChanged(TaskRun run) => ForEach(o => o.StepsChanged(run));

    public void StepChanged(TaskRun run, StepRecord step) => ForEach(o => o.StepChanged(run, step));

    public void Event(TaskRun run, TaskEventLevel level, string message) => ForEach(o => o.Event(run, level, message));

    private void ForEach(Action<ITaskObserver> action)
    {
        foreach (var observer in _observers)
        {
            action(observer);
        }
    }
}

public sealed class NullTaskObserver : ITaskObserver
{
    public static readonly NullTaskObserver Instance = new();

    public void TaskChanged(TaskRun run)
    {
    }

    public void StepsChanged(TaskRun run)
    {
    }

    public void StepChanged(TaskRun run, StepRecord step)
    {
    }

    public void Event(TaskRun run, TaskEventLevel level, string message)
    {
    }
}

/// <summary>Pause, resume and stop for one running task. Stop is final; pause blocks before the next action.</summary>
public sealed class ExecutionControl : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _resumed = NewCompleted();

    public event Action? StateChanged;

    public bool IsPaused { get; private set; }
    public string? PauseReason { get; private set; }
    public bool IsStopRequested => _stop.IsCancellationRequested;
    public string? StopReason { get; private set; }
    public CancellationToken StopToken => _stop.Token;

    public void Pause(string reason = "사용자 일시정지")
    {
        lock (_gate)
        {
            if (IsPaused || IsStopRequested)
            {
                return;
            }

            IsPaused = true;
            PauseReason = reason;
            _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        StateChanged?.Invoke();
    }

    public void Resume()
    {
        TaskCompletionSource toComplete;
        lock (_gate)
        {
            if (!IsPaused)
            {
                return;
            }

            IsPaused = false;
            PauseReason = null;
            toComplete = _resumed;
        }

        toComplete.TrySetResult();
        StateChanged?.Invoke();
    }

    public void Stop(string reason = "사용자 중단")
    {
        lock (_gate)
        {
            if (IsStopRequested)
            {
                return;
            }

            StopReason = reason;
        }

        _stop.Cancel();
        Resume();
        StateChanged?.Invoke();
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (_gate)
        {
            wait = _resumed.Task;
        }

        await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _stop.Dispose();

    private static TaskCompletionSource NewCompleted()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

public sealed record ExecutorOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public int MaxResolveRetries { get; init; } = 1;

    /// <summary>Pause the task when a window of a program outside the allow list comes to the front.</summary>
    public bool PauseOnUnexpectedWindow { get; init; } = true;

    /// <summary>Receives screenshots taken for the vision fallback (only set when the user turned saving on).</summary>
    public Action<TaskRun, ScreenImage>? ScreenshotSink { get; init; }
}
