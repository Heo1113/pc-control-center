using System.Diagnostics;
using System.Globalization;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Automation.Mock;
using WindowsAiOperator.Core.Diagnostics;

namespace PcControlCenter.Operator;

/// <summary>
/// The Operator host's main loop, shared by PcControlCenter.OperatorHost.exe (real desktop: FlaUI) and the mock-only
/// test host used on Linux. Started only by PC Control Center (--parent-pid); serves project E's desktop automation over
/// its own stdin/stdout and ends when the shell closes the pipe or exits.
/// </summary>
public static class HostProgram
{
    /// <param name="realDesktop">Creates E's real desktop automation, or null when this host can only simulate.</param>
    public static async Task<int> RunAsync(string[] args, Func<IOperatorLog, IDesktopAutomation>? realDesktop)
    {
        string? logDirectory = null;
        int? parentPid = null;
        var mock = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--log-dir" when i + 1 < args.Length:
                    logDirectory = args[++i];
                    break;
                case "--parent-pid" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid):
                    parentPid = pid;
                    i++;
                    break;
                case "--mock-desktop":
                    mock = true;
                    break;
            }
        }

        if (parentPid is null)
        {
            await Console.Error.WriteLineAsync("PcControlCenter.OperatorHost is started by PC Control Center only (--parent-pid).").ConfigureAwait(false);
            return 2;
        }

        using var log = new FileOperatorLog(logDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs"));
        var elevated = Environment.IsPrivilegedProcess;
        IDesktopAutomation desktop;
        string? refuseAll = null;
        try
        {
            desktop = mock ? MockDesktop.CreateDemo()
                : realDesktop is null ? throw new PlatformNotSupportedException("이 실행 파일은 MOCK 데스크톱만 지원합니다.")
                : await Task.Run(() => realDesktop(log)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Error("host", "desktop automation could not be started", ex);
            desktop = new UnavailableDesktop($"Windows UI Automation을 시작하지 못했습니다: {ex.Message}");
            refuseAll = "Windows UI Automation을 사용할 수 없어 조작하지 않습니다.";
        }

        if (elevated && !mock)
        {
            // Project E never automates elevated windows; a host running as administrator could (no UIPI barrier).
            refuseAll = "Operator 실행 프로세스가 관리자 권한으로 실행되어 화면을 조작하지 않습니다. PC Control Center를 일반 권한으로 실행하세요.";
        }

        var guard = new HostGuard([parentPid.Value, Environment.ProcessId], refuseAll);
        using var watch = WatchParent(parentPid.Value, log);
        var server = new DesktopServer(desktop, Console.OpenStandardInput(), Console.OpenStandardOutput(), guard, log, elevated);
        try
        {
            await server.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            desktop.Dispose();
        }

        return 0;
    }

    /// <summary>Ends the host if the shell disappears without closing the pipe (the job object normally does this first).</summary>
    private static Timer WatchParent(int parentPid, IOperatorLog log) => new(_ =>
    {
        bool gone;
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            gone = parent.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            gone = true;
        }

        if (gone)
        {
            log.Warn("host", "the shell process is gone; exiting");
            Environment.Exit(3);
        }
    }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
}

/// <summary>Placeholder desktop when UI Automation can not start: reports why and does nothing else.</summary>
public sealed class UnavailableDesktop(string reason) : IDesktopAutomation
{
    public string Name => "사용 불가";
    public bool IsMock => false;
    public DesktopStatus Status => new(false, reason);
    public int OwnProcessId => Environment.ProcessId;
    public IReadOnlyList<WindowInfo> ListWindows() => [];
    public WindowInfo? GetWindow(long handle) => null;
    public WindowInfo? GetForegroundWindow() => null;
    public UiSnapshot CaptureTree(long windowHandle, TreeCaptureOptions options, CancellationToken cancellationToken = default) => throw new InvalidOperationException(reason);
    public IReadOnlyList<ElementInfo> FindElements(long windowHandle, ElementQuery query, CancellationToken cancellationToken = default) => [];
    public ElementInfo? ElementFromPoint(PixelPoint point) => null;
    public bool FocusWindow(long windowHandle) => false;

    public Task<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ActionOutcome.NotPerformed(ExecutionErrorKind.NotSupported, reason));

    public Task<LaunchResult> LaunchAsync(string executable, IReadOnlyCollection<string> expectedProcessNames, TimeSpan timeout, CancellationToken cancellationToken)
        => Task.FromResult(new LaunchResult(false, 0, 0, reason));

    public ScreenImage? CaptureWindowImage(long windowHandle, int maxWidth) => null;

    public void Dispose()
    {
    }
}
