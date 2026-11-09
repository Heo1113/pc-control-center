using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Diagnostics;

namespace PcControlCenter.Operator;

/// <summary>What the remote desktop needs from whoever manages the host process.</summary>
public interface IHostConnectionSource
{
    HostConnection? Current { get; }
    string UnavailableReason { get; }
    bool EmergencyLatched { get; }
    bool ExpectMock { get; }

    /// <summary>Counts changing requests in flight, so an emergency stop knows whether input may be running right now.</summary>
    void BeginAction();

    void EndAction();
}

/// <summary>
/// Project E's <see cref="IDesktopAutomation"/> for the shell process: every call goes to the separate Operator host
/// process, where E's real desktop implementation runs. E's executor, policy engine, approvals and journal use it
/// unchanged. When the host is gone, reads throw <see cref="HostUnavailableException"/> (an InvalidOperationException,
/// which E already handles) and an action whose answer never came back is reported as "performed, result unknown" so
/// E never repeats it blindly.
/// </summary>
public sealed class RemoteDesktop(IHostConnectionSource source, int ownProcessId, IOperatorLog? log = null) : IDesktopAutomation
{
    public static TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public static TimeSpan TreeTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private readonly IOperatorLog _log = log ?? NullOperatorLog.Instance;

    public string Name => source.Current?.Hello is { } hello ? $"{hello.DesktopName} (별도 프로세스)" : "Operator 실행 프로세스 (연결 안 됨)";

    public bool IsMock => source.Current?.Hello.IsMock ?? source.ExpectMock;

    public DesktopStatus Status
    {
        get
        {
            if (source.Current is not { IsOpen: true } connection)
            {
                return new DesktopStatus(false, source.UnavailableReason);
            }

            if (source.EmergencyLatched)
            {
                return new DesktopStatus(false, "긴급 중단 상태입니다. [재개]를 누르기 전에는 화면을 조작하지 않습니다.");
            }

            try
            {
                return connection.CallAsync<DesktopStatus>(HostOp.Status, null, ReadTimeout).GetAwaiter().GetResult() ?? connection.Hello.Status;
            }
            catch (Exception ex) when (ex is HostUnavailableException or TimeoutException or HostCallException or OperationCanceledException)
            {
                return new DesktopStatus(false, "Operator 실행 프로세스가 응답하지 않습니다: " + ex.Message);
            }
        }
    }

    /// <summary>The shell's process id: E's policy never automates the window that shows its own approval buttons.</summary>
    public int OwnProcessId => ownProcessId;

    public IReadOnlyList<WindowInfo> ListWindows() => Read<List<WindowInfo>>(HostOp.ListWindows, null, ReadTimeout, default) ?? [];

    public WindowInfo? GetWindow(long handle) => Read<WindowInfo>(HostOp.GetWindow, new HandleArgs(handle), ReadTimeout, default);

    public WindowInfo? GetForegroundWindow() => Read<WindowInfo>(HostOp.GetForegroundWindow, null, ReadTimeout, default);

    public UiSnapshot CaptureTree(long windowHandle, TreeCaptureOptions options, CancellationToken cancellationToken = default)
        => Read<UiSnapshot>(HostOp.CaptureTree, new CaptureTreeArgs(windowHandle, options), TreeTimeout, cancellationToken)
           ?? throw new HostUnavailableException("Operator 실행 프로세스가 UI 구조를 보내지 않았습니다.");

    public IReadOnlyList<ElementInfo> FindElements(long windowHandle, ElementQuery query, CancellationToken cancellationToken = default)
        => Read<List<ElementInfo>>(HostOp.FindElements, new FindElementsArgs(windowHandle, query), TreeTimeout, cancellationToken) ?? [];

    public ElementInfo? ElementFromPoint(PixelPoint point)
    {
        try
        {
            return Read<ElementInfo>(HostOp.ElementFromPoint, new PointArgs(point), ReadTimeout, default);
        }
        catch (InvalidOperationException ex)
        {
            _log.Warn("remote", "element from point unavailable", ex);
            return null;
        }
    }

    public bool FocusWindow(long windowHandle)
    {
        if (source.EmergencyLatched)
        {
            return false;
        }

        try
        {
            return Read<bool>(HostOp.FocusWindow, new HandleArgs(windowHandle), ReadTimeout, default);
        }
        catch (InvalidOperationException ex)
        {
            _log.Warn("remote", "focus refused or unavailable", ex);
            return false;
        }
    }

    public async Task<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        if (source.EmergencyLatched)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.Cancelled, "긴급 중단 상태라 실행하지 않았습니다.");
        }

        if (source.Current is not { IsOpen: true } connection)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.Interrupted, "Operator 실행 프로세스와 연결되어 있지 않아 실행하지 않았습니다: " + source.UnavailableReason);
        }

        source.BeginAction();
        try
        {
            return await connection.CallAsync<ActionOutcome>(HostOp.Perform, new PerformArgs(request), null, cancellationToken).ConfigureAwait(false)
                   ?? ActionOutcome.PerformedButFailed("원격", ExecutionErrorKind.Internal, "Operator 실행 프로세스가 결과를 보내지 않았습니다(실행 여부 불확실).");
        }
        catch (HostUnavailableException ex)
        {
            // The request reached the host but no answer came back: input may have happened. Never repeat it blindly.
            _log.Warn("remote", $"host lost during {request}", ex);
            return ActionOutcome.PerformedButFailed("원격", ExecutionErrorKind.Interrupted,
                "Operator 실행 프로세스가 동작 중에 종료되어 실행 여부를 확인할 수 없습니다. 화면을 확인하고 필요하면 다시 요청하세요.");
        }
        catch (HostCallException ex)
        {
            return ex.Kind == HostErrorKind.Refused
                ? ActionOutcome.NotPerformed(ExecutionErrorKind.PolicyDenied, ex.Message)
                : ActionOutcome.PerformedButFailed("원격", ExecutionErrorKind.ActionFailed, ex.Message);
        }
        finally
        {
            source.EndAction();
        }
    }

    public async Task<LaunchResult> LaunchAsync(string executable, IReadOnlyCollection<string> expectedProcessNames, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (source.EmergencyLatched)
        {
            return new LaunchResult(false, 0, 0, "긴급 중단 상태라 프로그램을 실행하지 않았습니다.");
        }

        if (source.Current is not { IsOpen: true } connection)
        {
            return new LaunchResult(false, 0, 0, "Operator 실행 프로세스와 연결되어 있지 않습니다: " + source.UnavailableReason);
        }

        source.BeginAction();
        try
        {
            return await connection.CallAsync<LaunchResult>(HostOp.Launch, new LaunchArgs(executable, [.. expectedProcessNames], timeout.TotalSeconds),
                       timeout + TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false)
                   ?? new LaunchResult(false, 0, 0, "Operator 실행 프로세스가 결과를 보내지 않았습니다.");
        }
        catch (Exception ex) when (ex is HostUnavailableException or TimeoutException or HostCallException)
        {
            return new LaunchResult(false, 0, 0, "프로그램을 실행하지 못했습니다: " + ex.Message);
        }
        finally
        {
            source.EndAction();
        }
    }

    public ScreenImage? CaptureWindowImage(long windowHandle, int maxWidth)
    {
        try
        {
            return Read<ScreenImage>(HostOp.CaptureImage, new CaptureImageArgs(windowHandle, maxWidth), TreeTimeout, default);
        }
        catch (InvalidOperationException ex)
        {
            _log.Warn("remote", "window image unavailable", ex);
            return null;
        }
    }

    /// <summary>The host process belongs to the supervisor; nothing to release here.</summary>
    public void Dispose()
    {
    }

    private T? Read<T>(HostOp op, object? args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var connection = source.Current is { IsOpen: true } c ? c : throw new HostUnavailableException(source.UnavailableReason);
        try
        {
            return connection.CallAsync<T>(op, args, timeout, cancellationToken).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new HostUnavailableException($"Operator 실행 프로세스가 {timeout.TotalSeconds:0}초 안에 응답하지 않았습니다({op}).");
        }
        catch (HostCallException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }
}
