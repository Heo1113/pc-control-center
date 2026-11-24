using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Core.Settings;
using WindowsAiOperator.Core.Workflows;

namespace WindowsAiOperator.Presentation.ViewModels;

/// <summary>One approval request. The executor enforces the timeout; this view model only shows the countdown.</summary>
public sealed partial class ApprovalViewModel : ObservableObject, IDisposable
{
    private readonly TaskCompletionSource<ApprovalResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IUiDispatcher _ui;
    private readonly Timer? _timer;
    private readonly DateTimeOffset _deadline;

    public ApprovalViewModel(ApprovalRequest request, IUiDispatcher ui, bool startTimer = true)
    {
        Request = request;
        _ui = ui;
        _deadline = DateTimeOffset.Now + request.Timeout;
        RemainingSeconds = (int)request.Timeout.TotalSeconds;
        Reasons = new ObservableCollection<string>(request.Reasons);
        RiskTone = request.Risk switch
        {
            RiskLevel.Critical or RiskLevel.High => Tone.Bad,
            RiskLevel.Medium => Tone.Warning,
            _ => Tone.Neutral,
        };
        if (startTimer)
        {
            _timer = new Timer(_ => _ui.Post(Tick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    public ApprovalRequest Request { get; }
    public string Title => Request.Title;
    public string Target => Request.Target;
    public string Change => Request.Change;
    public string Impact => Request.Impact;
    public string Recovery => Request.Recovery;
    public ObservableCollection<string> Reasons { get; }
    public Tone RiskTone { get; }

    public string RiskText => Request.Risk switch
    {
        RiskLevel.Critical => "매우 높음",
        RiskLevel.High => "높음",
        RiskLevel.Medium => "보통",
        _ => "낮음",
    };

    public byte[]? PreviewPng => Request.Preview?.Png;
    /// <summary>Marker position in image pixels (0 when there is no preview; the preview is hidden then).</summary>
    public double PreviewX => Request.PreviewPoint?.X ?? 0;
    public double PreviewY => Request.PreviewPoint?.Y ?? 0;
    public bool HasPreview => Request.Preview is not null;

    [ObservableProperty]
    public partial int RemainingSeconds { get; set; }

    [ObservableProperty]
    public partial bool IsAnswered { get; set; }

    public Task<ApprovalResult> Result => _result.Task;

    [RelayCommand]
    private void Approve() => Answer(ApprovalResult.Approved);

    [RelayCommand]
    private void Deny() => Answer(ApprovalResult.Denied);

    /// <summary>The executor gave up waiting (timeout or stop).</summary>
    public void Expire() => Answer(ApprovalResult.TimedOut);

    public void Dispose() => _timer?.Dispose();

    private void Answer(ApprovalResult result)
    {
        if (_result.TrySetResult(result))
        {
            IsAnswered = true;
            _timer?.Dispose();
        }
    }

    private void Tick()
    {
        RemainingSeconds = Math.Max(0, (int)Math.Ceiling((_deadline - DateTimeOffset.Now).TotalSeconds));
        if (RemainingSeconds == 0)
        {
            Expire();
        }
    }
}

/// <summary>The whole plan, shown before the first action (and again after re-planning).</summary>
public sealed partial class PlanReviewViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PlanReviewViewModel(PlanReviewRequest request, SafetySettings settings)
    {
        Request = request.Request;
        PlannerLabel = request.PlannerLabel;
        Summary = request.Plan.Summary ?? "(요약 없음)";
        Steps = new ObservableCollection<string>(request.Plan.Steps.Select((s, i) => $"{i + 1}. {s.Summary()}"));
        Warnings = new ObservableCollection<string>(request.Warnings);
        Hints = new ObservableCollection<string>(WorkflowSerializer.ApprovalHints(request.Plan, settings).Select(h => $"{h.StepNumber}단계({h.Action}): {h.Reason}"));
    }

    public string Request { get; }
    public string PlannerLabel { get; }
    public string Summary { get; }
    public ObservableCollection<string> Steps { get; }
    public ObservableCollection<string> Warnings { get; }

    /// <summary>Steps that will very likely ask for approval when they are reached.</summary>
    public ObservableCollection<string> Hints { get; }

    [ObservableProperty]
    public partial bool IsAnswered { get; set; }

    public Task<bool> Result => _result.Task;

    [RelayCommand]
    private void Start() => Answer(true);

    [RelayCommand]
    private void Cancel() => Answer(false);

    public void Expire() => Answer(false);

    private void Answer(bool value)
    {
        if (_result.TrySetResult(value))
        {
            IsAnswered = true;
        }
    }
}

/// <summary>A step in the live run list.</summary>
public sealed partial class StepItemViewModel(int sequence, int round) : ObservableObject
{
    public int Sequence { get; } = sequence;
    public int Round { get; } = round;

    [ObservableProperty]
    public partial string Action { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "대기";

    [ObservableProperty]
    public partial Tone StatusTone { get; set; }

    [ObservableProperty]
    public partial string? Target { get; set; }

    [ObservableProperty]
    public partial string? Decision { get; set; }

    [ObservableProperty]
    public partial string? Detail { get; set; }

    [ObservableProperty]
    public partial string? ReadValue { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool ChangedSomething { get; set; }

    public void Update(StepSnapshot snapshot)
    {
        Action = snapshot.Action;
        Summary = snapshot.Summary;
        StatusText = snapshot.StatusText;
        StatusTone = snapshot.Tone;
        Target = snapshot.Target;
        Decision = snapshot.Decision;
        Detail = snapshot.Detail;
        ReadValue = snapshot.ReadValue;
        ChangedSomething = snapshot.ChangedSomething;
    }
}

/// <summary>Immutable copy of a <see cref="StepRecord"/> taken on the executor thread.</summary>
public sealed record StepSnapshot(int Sequence, int Round, string Action, string Summary, string StatusText, Tone Tone, string? Target, string? Decision, string? Detail, string? ReadValue, bool ChangedSomething, bool IsActive)
{
    public static StepSnapshot From(StepRecord step)
    {
        var target = string.Join(" › ", new[] { step.TargetApp, step.TargetWindow is null ? null : $"'{step.TargetWindow}'", step.TargetElement }.Where(s => !string.IsNullOrEmpty(s)));
        var decision = step.Decision is null
            ? null
            : step.Decision.Verdict switch
            {
                PolicyVerdict.Allow => "허용",
                PolicyVerdict.RequireApproval => "승인 필요",
                _ => "차단",
            } + (step.Decision.Categories.Count > 0 ? $" ({string.Join(", ", step.Decision.Categories.Select(c => RiskCatalog.Get(c).Label))})" : string.Empty)
              + step.Approval switch
              {
                  ApprovalState.Approved => " → 승인됨",
                  ApprovalState.Denied => " → 거부됨",
                  ApprovalState.TimedOut => " → 시간 초과",
                  ApprovalState.Pending => " → 승인 대기",
                  _ => string.Empty,
              };
        var detail = string.Join(" · ", new[]
        {
            step.Method is null or "실행 중" ? null : $"방법: {step.Method}",
            step.Verification switch
            {
                VerificationState.Passed => "결과 확인됨",
                VerificationState.Failed => "결과 불일치",
                _ => null,
            },
            step.Attempts > 1 ? $"시도 {step.Attempts}회" : null,
            step.Message,
        }.Where(s => !string.IsNullOrEmpty(s)));
        var tone = step.Status switch
        {
            StepRunStatus.Succeeded => Tone.Good,
            StepRunStatus.Failed or StepRunStatus.Denied or StepRunStatus.Unknown => Tone.Bad,
            StepRunStatus.AwaitingApproval or StepRunStatus.Running => Tone.Info,
            StepRunStatus.Skipped or StepRunStatus.Cancelled => Tone.Warning,
            _ => Tone.Neutral,
        };
        return new StepSnapshot(step.Sequence, step.Round, step.Step.Action.ToString(), step.Step.Summary(), TaskStatusText.Korean(step.Status), tone,
            string.IsNullOrEmpty(target) ? null : target, decision, string.IsNullOrEmpty(detail) ? null : detail, step.ReadValue,
            step.Performed && step.ChangesState, step.Status is StepRunStatus.Running or StepRunStatus.AwaitingApproval);
    }
}
