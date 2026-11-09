using WindowsAiOperator.Core.Actions;

namespace WindowsAiOperator.Core.Automation;

/// <summary>
/// The only way the operator touches the desktop. The Windows implementation uses FlaUI (UIA3) and Win32;
/// <see cref="Mock.MockDesktop"/> is a simulated desktop for tests and demo mode. Implementations perform exactly the
/// primitive they are given: every safety decision (allow list, approval, re-validation) is made by the executor first.
/// </summary>
public interface IDesktopAutomation : IDisposable
{
    string Name { get; }

    /// <summary>True for the simulated desktop. Results from it are never reported as real GUI automation.</summary>
    bool IsMock { get; }

    DesktopStatus Status { get; }

    /// <summary>This program's own process id. The executor never automates its own windows.</summary>
    int OwnProcessId { get; }

    IReadOnlyList<WindowInfo> ListWindows();

    /// <summary>Fresh information about one window, or null when it no longer exists.</summary>
    WindowInfo? GetWindow(long handle);

    WindowInfo? GetForegroundWindow();

    UiSnapshot CaptureTree(long windowHandle, TreeCaptureOptions options, CancellationToken cancellationToken = default);

    /// <summary>Live elements in the window (and its popups/menus of the same process) that match the query.</summary>
    IReadOnlyList<ElementInfo> FindElements(long windowHandle, ElementQuery query, CancellationToken cancellationToken = default);

    ElementInfo? ElementFromPoint(PixelPoint point);

    bool FocusWindow(long windowHandle);

    Task<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken);

    /// <summary>Starts an allow-listed program and waits for a new window of one of <paramref name="expectedProcessNames"/>
    /// (the started process may hand over to another one, e.g. the packaged Notepad on Windows 11).</summary>
    Task<LaunchResult> LaunchAsync(string executable, IReadOnlyCollection<string> expectedProcessNames, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>PNG of the window as currently shown on screen, downscaled to <paramref name="maxWidth"/>; null when not possible.</summary>
    ScreenImage? CaptureWindowImage(long windowHandle, int maxWidth);
}

public enum PrimitiveAction
{
    Invoke,
    SetValue,
    AppendText,
    Toggle,
    Select,
    Expand,
    Collapse,
    Scroll,
    Focus,
    SendHotkey,
    ClickPoint,
    SelectMenuPath,
}

public enum ScrollDirection
{
    Up,
    Down,
    Left,
    Right,
}

public sealed record ActionRequest
{
    public required PrimitiveAction Action { get; init; }
    public required long WindowHandle { get; init; }
    public string? RuntimeId { get; init; }
    public string? Text { get; init; }
    public bool? DesiredToggle { get; init; }
    public ScrollDirection Direction { get; init; } = ScrollDirection.Down;
    public int Amount { get; init; } = 1;
    public Hotkey? Hotkey { get; init; }
    public PixelPoint? Point { get; init; }
    public IReadOnlyList<string>? MenuPath { get; init; }

    public override string ToString()
    {
        var target = RuntimeId is null ? $"window {WindowHandle}" : $"element {RuntimeId} in window {WindowHandle}";
        return $"{Action} on {target}";
    }
}

/// <param name="Performed">Whether input or a UIA pattern was actually applied. A step that was performed is never
/// repeated blindly (the executor re-inspects the screen first).</param>
public sealed record ActionOutcome(bool Success, bool Performed, string Method, string Message, ExecutionErrorKind? Error = null)
{
    public static ActionOutcome Ok(string method, string message = "완료") => new(true, true, method, message);

    public static ActionOutcome NotPerformed(ExecutionErrorKind error, string message) => new(false, false, "-", message, error);

    public static ActionOutcome PerformedButFailed(string method, ExecutionErrorKind error, string message) => new(false, true, method, message, error);
}

public enum ExecutionErrorKind
{
    WindowNotFound,
    WindowClosed,
    ElementNotFound,
    ElementAmbiguous,
    ElementDisabled,
    PatternNotSupported,
    FocusFailed,
    ActionFailed,
    VerificationFailed,
    Timeout,
    PolicyDenied,
    ApprovalDenied,
    ApprovalTimeout,
    TargetChangedAfterApproval,
    UnexpectedWindow,
    ModelUnavailable,
    ModelOutputRejected,
    ModelError,
    Cancelled,
    LimitReached,
    InvalidPlan,
    AccessDenied,
    NotSupported,
    Interrupted,
    Internal,
}

public static class ExecutionErrorText
{
    public static string Korean(ExecutionErrorKind kind) => kind switch
    {
        ExecutionErrorKind.WindowNotFound => "대상 창 없음",
        ExecutionErrorKind.WindowClosed => "대상 창이 닫힘",
        ExecutionErrorKind.ElementNotFound => "UI 요소 없음",
        ExecutionErrorKind.ElementAmbiguous => "대상 요소가 여러 개",
        ExecutionErrorKind.ElementDisabled => "요소 비활성",
        ExecutionErrorKind.PatternNotSupported => "지원하지 않는 조작",
        ExecutionErrorKind.FocusFailed => "창 활성화 실패",
        ExecutionErrorKind.ActionFailed => "조작 실패",
        ExecutionErrorKind.VerificationFailed => "결과 검증 실패",
        ExecutionErrorKind.Timeout => "시간 초과",
        ExecutionErrorKind.PolicyDenied => "안전 정책 차단",
        ExecutionErrorKind.ApprovalDenied => "사용자 거부",
        ExecutionErrorKind.ApprovalTimeout => "승인 시간 초과",
        ExecutionErrorKind.TargetChangedAfterApproval => "승인 후 대상 변경",
        ExecutionErrorKind.UnexpectedWindow => "예상하지 못한 창",
        ExecutionErrorKind.ModelUnavailable => "모델 연결 불가",
        ExecutionErrorKind.ModelOutputRejected => "모델 출력 거부",
        ExecutionErrorKind.ModelError => "모델 오류",
        ExecutionErrorKind.Cancelled => "사용자 중단",
        ExecutionErrorKind.LimitReached => "실행 한도 도달",
        ExecutionErrorKind.InvalidPlan => "잘못된 계획",
        ExecutionErrorKind.AccessDenied => "접근 거부(권한)",
        ExecutionErrorKind.NotSupported => "지원하지 않음",
        ExecutionErrorKind.Interrupted => "비정상 종료로 중단",
        _ => "내부 오류",
    };
}
