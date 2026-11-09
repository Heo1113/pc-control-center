using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.Core.Execution;

public sealed record WindowResolution(WindowInfo? Window, AllowedApp? App, ExecutionErrorKind? Error, string Message)
{
    public bool Found => Window is not null;
}

public sealed record ElementResolution(ElementInfo? Element, ExecutionErrorKind? Error, string Message)
{
    public bool Found => Element is not null;
}

/// <summary>Finds the live window for a step from its selector and the task's current window.</summary>
public sealed class WindowResolver(IDesktopAutomation desktop)
{
    public WindowResolution Resolve(WindowSelector? selector, string? stepApp, long? currentHandle, IReadOnlyCollection<long> launchedHandles, SafetySettings settings)
    {
        if (selector?.Handle is { } handle)
        {
            var byHandle = desktop.GetWindow(handle);
            return byHandle is null
                ? new WindowResolution(null, null, ExecutionErrorKind.WindowClosed, $"창 {handle}이(가) 더 이상 없습니다.")
                : new WindowResolution(byHandle, settings.FindAppForProcess(byHandle.ProcessName), null, "창 핸들로 찾음");
        }

        var appId = selector?.App ?? stepApp;
        if (appId is null && selector?.TitleContains is null)
        {
            if (currentHandle is null)
            {
                return new WindowResolution(null, null, ExecutionErrorKind.WindowNotFound, "대상 창이 지정되지 않았고 이전 단계의 창도 없습니다.");
            }

            var current = desktop.GetWindow(currentHandle.Value);
            return current is null
                ? new WindowResolution(null, null, ExecutionErrorKind.WindowClosed, "이전 단계의 대상 창이 닫혔습니다.")
                : new WindowResolution(current, settings.FindAppForProcess(current.ProcessName), null, "이전 단계의 창");
        }

        AllowedApp? app = null;
        if (appId is not null)
        {
            app = settings.FindApp(appId);
            if (app is null || !app.Enabled)
            {
                return new WindowResolution(null, null, ExecutionErrorKind.PolicyDenied, $"허용 앱 목록에 없거나 꺼져 있는 앱입니다: {appId}");
            }
        }

        var windows = desktop.ListWindows();
        var candidates = windows
            .Where(w => app is null || app.MatchesProcess(w.ProcessName))
            .Where(w => selector?.TitleContains is null || w.Title.Contains(selector.TitleContains, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
        {
            return new WindowResolution(null, app, ExecutionErrorKind.WindowNotFound,
                $"조건에 맞는 창이 없습니다({selector?.ToString() ?? appId}).");
        }

        var chosen = candidates.FirstOrDefault(w => w.IsForeground)
                     ?? candidates.FirstOrDefault(w => w.Handle == currentHandle)
                     ?? candidates.LastOrDefault(w => launchedHandles.Contains(w.Handle))
                     ?? candidates[0];
        return new WindowResolution(chosen, app ?? settings.FindAppForProcess(chosen.ProcessName), null,
            candidates.Count > 1 ? $"후보 창 {candidates.Count}개 중 선택" : "창을 찾음");
    }
}

/// <summary>
/// Finds the live element for a selector. A ref is resolved through its fingerprint (runtime id first, then automation
/// id or name); ambiguous matches are refused rather than guessed.
/// </summary>
public sealed class ElementResolver(IDesktopAutomation desktop)
{
    /// <param name="preferredPatterns">Patterns the action needs; used only to choose between copies of the same on-screen element.</param>
    public ElementResolution Resolve(long windowHandle, ElementSelector selector, ElementRegistry registry, CancellationToken cancellationToken, UiPatterns preferredPatterns = UiPatterns.None)
    {
        var query = selector.ToQuery();
        if (selector.Ref is not null)
        {
            var fingerprint = registry.Get(selector.Ref);
            if (fingerprint is null)
            {
                return new ElementResolution(null, ExecutionErrorKind.ElementNotFound, $"알 수 없는 요소 참조 {selector.Ref}입니다(이전 화면 정보에 없음).");
            }

            var byRuntime = desktop.FindElements(windowHandle, new ElementQuery { RuntimeId = fingerprint.RuntimeId, MaxResults = 2 }, cancellationToken)
                .Where(fingerprint.IsCompatible)
                .ToList();
            if (byRuntime.Count == 1 && (query.IsEmpty || query.Matches(byRuntime[0])))
            {
                return new ElementResolution(byRuntime[0], null, $"{selector.Ref} 확인(동일 요소)");
            }

            query = new ElementQuery
            {
                AutomationId = query.AutomationId ?? NullIfEmpty(fingerprint.AutomationId),
                Name = query.Name ?? (string.IsNullOrEmpty(fingerprint.AutomationId) ? NullIfEmpty(fingerprint.Name) : null),
                NameContains = query.NameContains,
                ControlType = query.ControlType ?? fingerprint.ControlType,
                ClassName = query.ClassName,
                MaxResults = 20,
            };
        }

        if (query.IsEmpty)
        {
            return new ElementResolution(null, ExecutionErrorKind.ElementNotFound, "요소 선택 조건이 없습니다.");
        }

        var matches = desktop.FindElements(windowHandle, query, cancellationToken);
        if (matches.Count == 0)
        {
            return new ElementResolution(null, ExecutionErrorKind.ElementNotFound, $"조건에 맞는 요소가 없습니다({selector}).");
        }

        if (selector.Index is { } index)
        {
            return index < matches.Count
                ? new ElementResolution(matches[index], null, $"{matches.Count}개 중 {index}번")
                : new ElementResolution(null, ExecutionErrorKind.ElementNotFound, $"요소가 {matches.Count}개뿐이라 {index}번을 찾을 수 없습니다.");
        }

        if (matches.Count == 1)
        {
            return new ElementResolution(matches[0], null, "요소를 찾음");
        }

        var usable = matches.Where(m => m.IsEnabled && !m.IsOffscreen).ToList();
        if (usable.Count == 1)
        {
            return new ElementResolution(usable[0], null, $"{matches.Count}개 중 보이고 활성화된 1개");
        }

        // Some frameworks expose one on-screen item twice (e.g. a WPF combo box item under the combo box and its popup).
        // Identical type, name, id and rectangle means the same thing on screen; anything else stays ambiguous.
        var candidates = usable.Count > 0 ? usable : matches.ToList();
        if (candidates.Select(m => (m.ControlType, m.Name, m.AutomationId, m.Bounds)).Distinct().Count() == 1 && !candidates[0].Bounds.IsEmpty)
        {
            var best = candidates.FirstOrDefault(c => preferredPatterns == UiPatterns.None || (c.Patterns & preferredPatterns) != 0) ?? candidates[0];
            return new ElementResolution(best, null, $"같은 화면 요소가 UIA에 {candidates.Count}번 노출되어 필요한 조작을 지원하는 것을 사용");
        }

        var sample = string.Join(", ", matches.Take(4).Select(m => m.Describe()));
        return new ElementResolution(null, ExecutionErrorKind.ElementAmbiguous,
            $"조건에 맞는 요소가 {matches.Count}개라 하나로 정할 수 없습니다(index나 automation_id 필요): {Security.Redactor.OneLine(sample, 200)}");
    }

    /// <summary>Re-reads one element by runtime id (fallback: its fingerprint). Null when it is gone.</summary>
    public ElementInfo? Refresh(ElementInfo element, CancellationToken cancellationToken)
    {
        var fingerprint = ElementFingerprint.From(element);
        var byRuntime = desktop.FindElements(element.WindowHandle, new ElementQuery { RuntimeId = element.RuntimeId, MaxResults = 2 }, cancellationToken)
            .Where(fingerprint.IsCompatible)
            .ToList();
        if (byRuntime.Count == 1)
        {
            return byRuntime[0];
        }

        if (string.IsNullOrEmpty(element.AutomationId))
        {
            return null;
        }

        var byId = desktop.FindElements(element.WindowHandle, new ElementQuery { AutomationId = element.AutomationId, ControlType = element.ControlType, MaxResults = 2 }, cancellationToken);
        return byId.Count == 1 ? byId[0] : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
