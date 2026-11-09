namespace WindowsAiOperator.Storage;

public sealed record TaskSummary(
    string Id,
    string Request,
    string Source,
    string Status,
    string? StatusMessage,
    string? ErrorKind,
    string? TargetApp,
    string? ModelLabel,
    bool IsMock,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt,
    int StepCount,
    int StepsSucceeded,
    int ChangesPerformed,
    int ApprovalsRequested,
    int ModelCalls);

public sealed record StepRow(
    int Sequence,
    int Round,
    int StepId,
    string Action,
    string Summary,
    string? TargetApp,
    string? TargetWindow,
    string? TargetElement,
    string? Risk,
    string? Decision,
    string? DecisionReasons,
    string Approval,
    string Status,
    int Attempts,
    bool Performed,
    bool ChangesState,
    string Verification,
    string? Method,
    string? Message,
    string? ErrorKind,
    string? ReadValue,
    string? ParamsJson,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt);

public sealed record EventRow(DateTimeOffset At, string Level, string Message);

public sealed record TaskDetail(
    TaskSummary Summary,
    string? PlanSummary,
    string? Report,
    int Rounds,
    int ModelErrors,
    int ApprovalsGranted,
    int Interventions,
    bool InjectionSuspected,
    string? LastKnownState,
    DateTimeOffset? StartedAt,
    IReadOnlyList<StepRow> Steps,
    IReadOnlyList<EventRow> Events);

public sealed record WorkflowRecord(
    string Id,
    string Name,
    string? Description,
    string? TargetApp,
    string DefinitionJson,
    bool BuiltIn,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EvalRunRecord(
    string BatchId,
    DateTimeOffset At,
    string ProfileName,
    string ModelId,
    string Scenario,
    bool IsMock,
    bool PlanValid,
    bool GuiSuccess,
    int Steps,
    long DurationMs,
    int ModelCalls,
    int ModelErrors,
    int WrongTargets,
    int Interventions,
    string Status,
    string? Note);

public sealed record RecoveredTask(string Id, string Request, string NewStatus, int ChangesPerformed, int Uncertain, int NotExecuted);

public sealed record StoreStatus(
    string DatabasePath,
    long SizeBytes,
    int SchemaVersion,
    string Integrity,
    int Tasks,
    int Steps,
    int Events,
    int Workflows,
    int EvalRuns,
    IReadOnlyList<string> UnfinishedTaskIds);

public sealed record BackupInfo(string Path, DateTimeOffset CreatedAt, long SizeBytes);

public sealed record HistoryQuery(string? Text = null, string? Status = null, DateTimeOffset? Since = null, int Limit = 200);
