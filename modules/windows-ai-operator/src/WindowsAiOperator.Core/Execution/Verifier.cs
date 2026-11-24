using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;

namespace WindowsAiOperator.Core.Execution;

public sealed record VerificationResult(VerificationState State, string Message, ElementInfo? After = null);

/// <summary>
/// Checks the live UI after a step: the step's own expectation if it has one, otherwise the natural result of the
/// action (text present, check state, selection). Polls briefly because UIs update asynchronously.
/// </summary>
public sealed class Verifier(IDesktopAutomation desktop, ElementResolver resolver, ExecutorOptions options)
{
    public async Task<VerificationResult> VerifyAsync(PlanStep step, WindowInfo? window, ElementInfo? before, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + options.VerifyTimeout;
        VerificationResult last;
        do
        {
            last = CheckOnce(step, window, before, cancellationToken);
            if (last.State != VerificationState.Failed)
            {
                return last;
            }

            await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return last;
    }

    private VerificationResult CheckOnce(PlanStep step, WindowInfo? window, ElementInfo? before, CancellationToken cancellationToken)
    {
        var expect = step.Expect;
        var after = before is null ? null : resolver.Refresh(before, cancellationToken);

        if (expect is { Gone: true })
        {
            return after is null
                ? new VerificationResult(VerificationState.Passed, "요소가 사라진 것을 확인했습니다.")
                : new VerificationResult(VerificationState.Failed, "요소가 아직 남아 있습니다.", after);
        }

        if (expect?.WindowTitleContains is { } title)
        {
            var current = window is null ? null : desktop.GetWindow(window.Handle);
            if (current is null || !current.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                return new VerificationResult(VerificationState.Failed, $"창 제목에 '{title}'이(가) 없습니다.");
            }
        }

        var needsElement = expect is not null && (expect.ValueEquals ?? expect.ValueContains ?? expect.NameContains) is not null
                           || expect?.Checked is not null || expect?.Selected is not null || expect?.Exists == true;
        if (expect is not null && !expect.IsEmpty)
        {
            if (needsElement && after is null)
            {
                return new VerificationResult(VerificationState.Failed, "확인할 요소를 다시 찾지 못했습니다.");
            }

            if (after is not null)
            {
                var failure = CheckExpectation(expect, after);
                if (failure is not null)
                {
                    return new VerificationResult(VerificationState.Failed, failure, after);
                }
            }

            return new VerificationResult(VerificationState.Passed, "기대한 상태를 확인했습니다.", after);
        }

        return DefaultCheck(step, window, before, after);
    }

    private VerificationResult DefaultCheck(PlanStep step, WindowInfo? window, ElementInfo? before, ElementInfo? after)
    {
        switch (step.Action)
        {
            case ActionType.SetText when before is not null:
                if (after is null)
                {
                    return new VerificationResult(VerificationState.Failed, "입력 후 요소를 다시 찾지 못했습니다.");
                }

                if (after.Value is null)
                {
                    return new VerificationResult(VerificationState.NotChecked, "값을 읽을 수 없는 요소라 입력 결과를 확인하지 못했습니다.", after);
                }

                var actual = NormalizeText(after.Value);
                var expected = NormalizeText(step.Text ?? string.Empty);
                var ok = step.Mode == SetTextMode.Append ? actual.EndsWith(expected, StringComparison.Ordinal) || actual.Contains(expected, StringComparison.Ordinal)
                    : actual == expected || actual.TrimEnd() == expected.TrimEnd();
                return ok
                    ? new VerificationResult(VerificationState.Passed, "입력한 텍스트를 확인했습니다.", after)
                    : new VerificationResult(VerificationState.Failed, "요소의 값이 입력한 텍스트와 다릅니다.", after);

            case ActionType.Toggle when before is not null:
                if (after?.Toggle is null)
                {
                    return new VerificationResult(VerificationState.Failed, "확인란 상태를 다시 읽지 못했습니다.");
                }

                var wanted = step.Checked == true ? ToggleValue.On : ToggleValue.Off;
                return after.Toggle == wanted
                    ? new VerificationResult(VerificationState.Passed, $"상태 {(wanted == ToggleValue.On ? "선택" : "해제")} 확인", after)
                    : new VerificationResult(VerificationState.Failed, $"상태가 {after.Toggle}입니다.", after);

            case ActionType.Select when before is not null:
                if (after is null)
                {
                    return new VerificationResult(VerificationState.NotChecked, "선택 후 항목이 목록에서 사라져 확인하지 못했습니다(드롭다운이 닫혔을 수 있음).");
                }

                return after.IsSelected switch
                {
                    true => new VerificationResult(VerificationState.Passed, "선택됨을 확인했습니다.", after),
                    false => new VerificationResult(VerificationState.Failed, "항목이 선택되지 않았습니다.", after),
                    null => new VerificationResult(VerificationState.NotChecked, "선택 상태를 제공하지 않는 요소입니다.", after),
                };

            case ActionType.Expand or ActionType.Collapse when before is not null:
                if (after?.Expand is null)
                {
                    return new VerificationResult(VerificationState.NotChecked, "펼침 상태를 제공하지 않는 요소입니다.", after);
                }

                var expanded = after.Expand is ExpandValue.Expanded or ExpandValue.PartiallyExpanded;
                return expanded == (step.Action == ActionType.Expand)
                    ? new VerificationResult(VerificationState.Passed, "펼침 상태를 확인했습니다.", after)
                    : new VerificationResult(VerificationState.Failed, "펼침 상태가 바뀌지 않았습니다.", after);

            case ActionType.FocusWindow when window is not null:
                var foreground = desktop.GetForegroundWindow();
                return foreground?.Handle == window.Handle
                    ? new VerificationResult(VerificationState.Passed, "창이 앞에 있습니다.")
                    : new VerificationResult(VerificationState.Failed, "창을 앞으로 가져오지 못했습니다.");

            default:
                return new VerificationResult(VerificationState.NotChecked, "이 동작은 별도 기대 상태가 없어 실행 여부만 확인했습니다.", after);
        }
    }

    private static string? CheckExpectation(StepExpectation expect, ElementInfo after)
    {
        if (expect.ValueEquals is not null)
        {
            if (after.Value is null || NormalizeText(after.Value).TrimEnd() != NormalizeText(expect.ValueEquals).TrimEnd())
            {
                return "값이 기대와 다릅니다.";
            }
        }

        if (expect.ValueContains is not null)
        {
            if (after.Value is null || !NormalizeText(after.Value).Contains(NormalizeText(expect.ValueContains), StringComparison.Ordinal))
            {
                return "값에 기대한 텍스트가 없습니다.";
            }
        }

        if (expect.NameContains is not null && !after.Name.Contains(expect.NameContains, StringComparison.OrdinalIgnoreCase))
        {
            return $"이름에 '{expect.NameContains}'이(가) 없습니다.";
        }

        if (expect.Checked is { } isChecked && after.Toggle != (isChecked ? ToggleValue.On : ToggleValue.Off))
        {
            return "확인란 상태가 기대와 다릅니다.";
        }

        if (expect.Selected is { } selected && after.IsSelected != selected)
        {
            return "선택 상태가 기대와 다릅니다.";
        }

        return null;
    }

    public static string NormalizeText(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
