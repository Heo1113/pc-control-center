using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Files;
using WindowsAiOperator.Core.Security;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.Core.Policy;

public enum PolicyVerdict
{
    Allow,
    RequireApproval,
    Deny,
}

public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical,
}

public sealed record PolicyDecision(PolicyVerdict Verdict, RiskLevel Risk, IReadOnlyList<RiskCategory> Categories, IReadOnlyList<string> Reasons)
{
    public static PolicyDecision Deny(string reason, params RiskCategory[] categories) =>
        new(PolicyVerdict.Deny, RiskLevel.Critical, categories, [reason]);

    public string ReasonText => string.Join(" / ", Reasons);
}

/// <summary>How a window relates to the allow list (shown in the inspector and used before every step).</summary>
public sealed record WindowAccess(bool CanRead, bool CanInteract, AllowedApp? App, string Reason);

public sealed record PolicyInput
{
    public required PlanStep Step { get; init; }
    public required SafetySettings Settings { get; init; }
    public AllowedApp? App { get; init; }
    public WindowInfo? Window { get; init; }
    public ElementInfo? Element { get; init; }
    public int OwnProcessId { get; init; }
    public bool InjectionSuspected { get; init; }
    public IReadOnlyCollection<string> TaskGrantedFolders { get; init; } = [];
    public bool VisionActive { get; init; }

    /// <summary>The value currently in the target field (for replace warnings). Not shown anywhere.</summary>
    public string? CurrentValue { get; init; }
}

/// <summary>
/// Decides, for one resolved step, whether it may run, needs the user's approval, or is refused. Rules are evaluated
/// against the live window and element (not just the model's description), and the strictest verdict wins.
/// </summary>
public sealed class PolicyEngine
{
    public WindowAccess CheckWindow(WindowInfo window, SafetySettings settings, int ownProcessId)
    {
        if (window.ProcessId == ownProcessId)
        {
            return new WindowAccess(false, false, null, "이 프로그램 자신의 창은 자동화하지 않습니다.");
        }

        if (BuiltInBlockList.IsBlocked(window.ProcessName, settings.ExtraBlockedProcesses))
        {
            return new WindowAccess(false, false, null, $"'{window.ProcessName}'은(는) 차단 목록의 프로그램입니다(명령 실행·시스템 관리·보안·인증·원격 제어).");
        }

        if (window.IsElevatedOrProtected)
        {
            return new WindowAccess(false, false, null, "관리자 권한(또는 보호된) 프로세스라 조작하지 않습니다.");
        }

        var app = settings.FindAppForProcess(window.ProcessName);
        if (app is null)
        {
            return new WindowAccess(false, false, null, $"허용 앱 목록에 없는 프로그램입니다({window.ProcessName}).");
        }

        return app.Permission == AppPermission.ReadOnly
            ? new WindowAccess(true, false, app, $"허용 앱 '{app.DisplayName}' (읽기 전용)")
            : new WindowAccess(true, true, app, $"허용 앱 '{app.DisplayName}'");
    }

    public PolicyDecision Evaluate(PolicyInput input)
    {
        var step = input.Step;
        var settings = input.Settings;
        var descriptor = ActionCatalog.Get(step.Action);
        var reasons = new List<string>();
        var categories = new List<RiskCategory>();
        var verdict = PolicyVerdict.Allow;
        var risk = descriptor.ChangesState ? RiskLevel.Medium : RiskLevel.Low;

        void Require(RiskCategory category, string? reason = null)
        {
            var info = RiskCatalog.Get(category);
            if (info.Denied)
            {
                verdict = PolicyVerdict.Deny;
                risk = RiskLevel.Critical;
            }
            else if (verdict != PolicyVerdict.Deny)
            {
                verdict = PolicyVerdict.RequireApproval;
                risk = (RiskLevel)Math.Max((int)risk, info.Mandatory ? (int)RiskLevel.High : (int)RiskLevel.Medium);
            }

            if (!categories.Contains(category))
            {
                categories.Add(category);
            }

            reasons.Add(reason ?? $"{info.Label}: {info.Impact}");
        }

        // 1. Flow-control steps carry no desktop effect.
        if (step.Action is ActionType.Finish or ActionType.AskUser or ActionType.Wait)
        {
            return new PolicyDecision(PolicyVerdict.Allow, RiskLevel.Low, [], ["흐름 제어 단계"]);
        }

        // 2. File system: read-only listing inside granted folders.
        if (step.Action == ActionType.ListDirectory)
        {
            var problem = FolderAccess.CheckAccess(step.Path, settings.GrantedFolders.Concat(input.TaskGrantedFolders), out _);
            return problem is null
                ? new PolicyDecision(PolicyVerdict.Allow, RiskLevel.Low, [], ["허용된 폴더의 목록만 읽습니다(파일 내용은 읽지 않음)."])
                : PolicyDecision.Deny(problem);
        }

        // 3. Launching: only allow-listed apps with a launch command.
        if (step.Action == ActionType.LaunchApp)
        {
            var app = input.App ?? settings.FindApp(step.App);
            if (app is null || !app.Enabled)
            {
                return PolicyDecision.Deny($"허용 앱 목록에 없거나 꺼져 있는 앱입니다: {step.App}");
            }

            if (string.IsNullOrWhiteSpace(app.LaunchPath))
            {
                return PolicyDecision.Deny($"'{app.DisplayName}'은(는) 실행 명령이 설정되지 않아 시작할 수 없습니다.");
            }

            if (app.ProcessNames.Any(p => BuiltInBlockList.IsBlocked(p, settings.ExtraBlockedProcesses)))
            {
                return PolicyDecision.Deny($"'{app.DisplayName}'은(는) 차단 목록의 프로그램입니다.");
            }

            if (app.RequireApprovalForLaunch)
            {
                Require(RiskCategory.LaunchApp);
            }

            reasons.Add($"허용 앱 '{app.DisplayName}' 실행");
            return new PolicyDecision(verdict, verdict == PolicyVerdict.Allow ? RiskLevel.Low : risk, categories, reasons);
        }

        // 4. Every other action targets a resolved window that must pass the allow list.
        if (input.Window is null)
        {
            return PolicyDecision.Deny("대상 창을 확인하지 못해 실행하지 않습니다.");
        }

        var access = CheckWindow(input.Window, settings, input.OwnProcessId);
        if (!access.CanRead)
        {
            return PolicyDecision.Deny(access.Reason);
        }

        var targetApp = access.App!;
        if (descriptor.Category == ActionCategory.Observe)
        {
            return new PolicyDecision(PolicyVerdict.Allow, RiskLevel.Low, [], [access.Reason + " — 읽기 동작"]);
        }

        if (step.Action == ActionType.FocusWindow)
        {
            return new PolicyDecision(PolicyVerdict.Allow, RiskLevel.Low, [], [access.Reason + " — 창 활성화"]);
        }

        if (!access.CanInteract)
        {
            return PolicyDecision.Deny($"'{targetApp.DisplayName}'은(는) 읽기 전용으로 허용된 앱이라 {step.Action.DisplayName()}을(를) 할 수 없습니다.");
        }

        reasons.Add(access.Reason);

        // 5. Element-level facts.
        var element = input.Element;
        if (element is not null)
        {
            if (element.ProcessId != 0 && element.ProcessId != input.Window.ProcessId && element.ProcessId == input.OwnProcessId)
            {
                return PolicyDecision.Deny("대상 요소가 이 프로그램 자신의 창에 있습니다.");
            }

            if (element.IsPassword && step.Action == ActionType.SetText)
            {
                return PolicyDecision.Deny("비밀번호 입력란에는 자동으로 입력하지 않습니다. 비밀번호는 직접 입력하세요.", RiskCategory.Credentials);
            }

            var dialogTitle = input.Window.Title;
            foreach (var category in RiskKeywords.Classify(element.Name, element.AutomationId, element.HelpText,
                         step.Action is ActionType.Click or ActionType.Select or ActionType.Toggle ? dialogTitle : null))
            {
                if (category == RiskCategory.ConfirmButton && !settings.RequireApprovalForConfirmButtons)
                {
                    continue;
                }

                if (category == RiskCategory.CloseWindow && !settings.RequireApprovalForCloseButtons)
                {
                    continue;
                }

                if (category == RiskCategory.Credentials && step.Action == ActionType.Select)
                {
                    continue;
                }

                Require(category, $"{RiskCatalog.Get(category).Label}: 요소 {Redactor.OneLine(element.Describe(), 80)}");
            }
        }

        // 6. Action-specific rules.
        switch (step.Action)
        {
            case ActionType.SetText:
                var text = step.Text ?? string.Empty;
                if (text.Length > settings.MaxTextLength)
                {
                    return PolicyDecision.Deny($"입력 텍스트가 상한({settings.MaxTextLength}자)을 넘습니다.");
                }

                if (Redactor.ContainsSecret(text))
                {
                    Require(RiskCategory.SecretInText);
                }

                if (element is not null && RiskKeywords.LooksLikeCredentialField(element.Name, element.AutomationId))
                {
                    Require(RiskCategory.Credentials, "인증정보 입력란으로 보이는 요소에 입력합니다.");
                }

                // A writable ValuePattern inserts line breaks as text; only keyboard typing turns them into Enter key presses.
                var typedByKeyboard = element is not null && !(element.Supports(UiPatterns.Value) && element.IsReadOnly != true);
                if (text.Contains('\n') && typedByKeyboard && !string.Equals(element!.ControlType, "Document", StringComparison.OrdinalIgnoreCase))
                {
                    Require(RiskCategory.NewlineMaySubmit);
                }

                if (step.Mode == SetTextMode.Replace && !string.IsNullOrEmpty(input.CurrentValue) && input.CurrentValue.Length > 200)
                {
                    Require(RiskCategory.ReplaceExistingContent, $"기존 내용 {input.CurrentValue.Length}자를 새 내용으로 바꿉니다.");
                }
                else if (step.Mode == SetTextMode.Replace && typedByKeyboard && input.CurrentValue is null
                         && string.Equals(element!.ControlType, "Document", StringComparison.OrdinalIgnoreCase))
                {
                    // Without a readable value the keyboard path selects everything (Ctrl+A) before typing: the whole document could be replaced.
                    Require(RiskCategory.ReplaceExistingContent, "문서의 기존 내용을 읽을 수 없어, 전체를 새 내용으로 바꾸기 전에 확인합니다(덧붙이려면 append).");
                }

                break;

            case ActionType.SelectMenu:
                var menuText = string.Join(" ", step.MenuPath ?? []);
                var last = step.MenuPath is { Count: > 0 } path ? path[^1] : string.Empty;
                foreach (var category in RiskKeywords.ClassifyText(last).Concat(RiskKeywords.ClassifyText(menuText).Where(c => RiskCatalog.Get(c).Mandatory)).Distinct())
                {
                    if (category == RiskCategory.CloseWindow && !settings.RequireApprovalForCloseButtons)
                    {
                        continue;
                    }

                    Require(category, $"{RiskCatalog.Get(category).Label}: 메뉴 '{Redactor.OneLine(string.Join(" › ", step.MenuPath ?? []), 80)}'");
                }

                break;

            case ActionType.Hotkey:
                if (step.Keys is null)
                {
                    return PolicyDecision.Deny("단축키가 지정되지 않았습니다.");
                }

                var (safety, reason) = HotkeyCatalog.Classify(step.Keys);
                if (safety == HotkeySafety.Blocked)
                {
                    return PolicyDecision.Deny(reason);
                }

                if (safety == HotkeySafety.NeedsApproval)
                {
                    var keyText = step.Keys.ToString();
                    var category = keyText switch
                    {
                        "Delete" or "Shift+Delete" or "Ctrl+S" or "Ctrl+Shift+S" => RiskCategory.FileDeleteOrOverwrite,
                        "Alt+F4" or "Ctrl+W" or "Ctrl+F4" => RiskCategory.CloseWindow,
                        "Ctrl+X" => RiskCategory.BulkFileMove,
                        "Ctrl+Enter" => RiskCategory.ExternalSend,
                        _ => RiskCategory.UnlistedHotkey,
                    };
                    Require(category, reason);
                }

                break;

            case ActionType.ClickPoint:
                if (!input.VisionActive || !settings.AllowVision)
                {
                    return PolicyDecision.Deny("화면 인식(비전)이 켜져 있지 않아 좌표 클릭을 하지 않습니다.");
                }

                // Even with the setting off, a low or missing confidence means the image analysis is not good enough to click unseen.
                if (settings.RequireApprovalForCoordinateClicks || step.Confidence is not >= 0.8)
                {
                    Require(RiskCategory.CoordinateClick, settings.RequireApprovalForCoordinateClicks
                        ? null
                        : step.Confidence is { } c ? $"모델의 위치 확신도가 낮습니다({c:0.00})." : "모델이 위치 확신도를 주지 않았습니다.");
                }

                break;
        }

        // 7. App- and task-level rules.
        if (targetApp.IsSystemSettings)
        {
            Require(RiskCategory.SystemSettingChange);
        }

        if (targetApp.RequireApprovalForAllActions || settings.RequireApprovalForAllInteractions)
        {
            Require(RiskCategory.UserRequiresApproval);
        }

        if (input.InjectionSuspected && settings.InjectionGuard)
        {
            Require(RiskCategory.InjectionSuspected);
        }

        if (verdict == PolicyVerdict.Allow)
        {
            reasons.Add("일반 UI 조작");
        }

        return new PolicyDecision(verdict, risk, categories, reasons);
    }
}
