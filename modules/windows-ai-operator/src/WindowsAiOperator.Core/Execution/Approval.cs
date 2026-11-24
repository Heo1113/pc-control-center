using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Core.Security;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.Core.Execution;

public sealed record ApprovalRequest
{
    public required Guid TaskId { get; init; }
    public required int StepSequence { get; init; }
    public required string Title { get; init; }
    public required string Target { get; init; }
    public required string Change { get; init; }
    public required string Impact { get; init; }
    public required string Recovery { get; init; }
    public required RiskLevel Risk { get; init; }
    public IReadOnlyList<RiskCategory> Categories { get; init; } = [];
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>For coordinate clicks: the screenshot the point was chosen on and the point in image pixels.</summary>
    public ScreenImage? Preview { get; init; }
    public PixelPoint? PreviewPoint { get; init; }
}

public enum ApprovalResult
{
    Approved,
    Denied,
    TimedOut,
}

public sealed record PlanReviewRequest(Guid TaskId, string Request, TaskPlan Plan, string PlannerLabel, IReadOnlyList<string> Warnings);

public interface IApprovalHandler
{
    /// <summary>Asks the user. The executor enforces the timeout itself; anything but Approved means "do not run".</summary>
    Task<ApprovalResult> RequestStepApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);

    /// <summary>Shows the whole plan before the first action; false means the user did not start it.</summary>
    Task<bool> ReviewPlanAsync(PlanReviewRequest request, CancellationToken cancellationToken);
}

/// <summary>Scripted answers for tests, CI smoke runs and model evaluation (never used for a user's own tasks).</summary>
public sealed class ScriptedApprovalHandler : IApprovalHandler
{
    private readonly Func<ApprovalRequest, ApprovalResult> _decide;
    private readonly bool _approvePlans;
    private readonly List<ApprovalRequest> _requests = [];
    private readonly Lock _gate = new();

    public ScriptedApprovalHandler(Func<ApprovalRequest, ApprovalResult> decide, bool approvePlans = true)
    {
        _decide = decide;
        _approvePlans = approvePlans;
    }

    public static ScriptedApprovalHandler DenyAll(bool approvePlans = true) => new(_ => ApprovalResult.Denied, approvePlans);

    public static ScriptedApprovalHandler ApproveAll() => new(_ => ApprovalResult.Approved);

    /// <summary>Never answers: the executor's timeout must decide.</summary>
    public static ScriptedApprovalHandler NeverAnswer() => new(_ => (ApprovalResult)(-1));

    public IReadOnlyList<ApprovalRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public int PlanReviews { get; private set; }

    public Action<ApprovalRequest>? OnRequest { get; set; }

    public async Task<ApprovalResult> RequestStepApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        OnRequest?.Invoke(request);
        var result = _decide(request);
        if ((int)result == -1)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public Task<bool> ReviewPlanAsync(PlanReviewRequest request, CancellationToken cancellationToken)
    {
        PlanReviews++;
        return Task.FromResult(_approvePlans);
    }
}

public static class ApprovalTextBuilder
{
    public static ApprovalRequest Build(TaskRun run, StepRecord record, PolicyDecision decision, AllowedApp? app, WindowInfo? window, ElementInfo? element, TimeSpan timeout, ScreenImage? preview = null, PixelPoint? previewPoint = null)
    {
        var step = record.Step;
        var infos = decision.Categories.Select(RiskCatalog.Get).ToList();
        var target = window is null
            ? (app?.DisplayName ?? step.App ?? "-")
            : $"{app?.DisplayName ?? window.ProcessName} — '{Redactor.OneLine(window.Title, 80)}' ({window.ProcessName}.exe, PID {window.ProcessId})";
        if (element is not null)
        {
            target += $" › {Redactor.OneLine(element.Describe(), 100)}";
        }

        return new ApprovalRequest
        {
            TaskId = run.Id,
            StepSequence = record.Sequence,
            Title = $"승인 필요: {step.Action.DisplayName()}" + (infos.Count > 0 ? $" ({string.Join(", ", infos.Select(i => i.Label))})" : string.Empty),
            Target = target,
            Change = DescribeChange(step, app, element),
            Impact = infos.Count == 0 ? "화면의 상태가 바뀝니다." : string.Join(" ", infos.Select(i => i.Impact).Distinct()),
            Recovery = infos.Count == 0 ? "대부분 Ctrl+Z나 반대 조작으로 되돌릴 수 있습니다." : string.Join(" ", infos.Select(i => i.Recovery).Where(r => r != "-").Distinct()),
            Risk = decision.Risk,
            Categories = decision.Categories,
            Reasons = decision.Reasons,
            Timeout = timeout,
            Preview = preview,
            PreviewPoint = previewPoint,
        };
    }

    public static string DescribeChange(PlanStep step, AllowedApp? app, ElementInfo? element)
    {
        var name = element is null ? step.Element?.ToString() ?? "대상" : Redactor.OneLine(element.Describe(), 80);
        return step.Action switch
        {
            ActionType.LaunchApp => $"'{app?.DisplayName ?? step.App}'을(를) 실행합니다: {app?.LaunchPath}",
            ActionType.Click => $"{name}을(를) 누릅니다.",
            ActionType.SetText => $"{name}에 {(step.Mode == SetTextMode.Append ? "다음 텍스트를 덧붙입니다" : "다음 텍스트를 입력합니다(기존 내용 대체)")} ({step.Text?.Length ?? 0}자): \"{Redactor.OneLine(step.Text, 200)}\"",
            ActionType.Toggle => $"{name}을(를) {(step.Checked == true ? "선택(켜기)" : "해제(끄기)")} 상태로 바꿉니다.",
            ActionType.Select => $"{name}을(를) 선택합니다.",
            ActionType.Expand => $"{name}을(를) 펼칩니다.",
            ActionType.Collapse => $"{name}을(를) 접습니다.",
            ActionType.SelectMenu => $"메뉴 '{string.Join(" › ", step.MenuPath ?? [])}'을(를) 선택합니다.",
            ActionType.Scroll => $"{name} 영역을 {step.Direction} 방향으로 {step.Amount}번 스크롤합니다.",
            ActionType.Hotkey => $"{step.Keys} 키를 누릅니다.",
            ActionType.ClickPoint => step.Point is { } p ? $"창 스크린샷 기준 ({p.X}/1000, {p.Y}/1000) 위치를 클릭합니다." : "좌표를 클릭합니다.",
            _ => step.Summary(),
        };
    }
}
