using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Policy;

namespace PcControlCenter.Operator;

/// <summary>
/// Checks the Operator host makes by itself before any input, focus change or program start, independent of the
/// policy engine in the shell (defense in depth): PC Control Center's own processes, E's built-in block list
/// (shells, system and security tools, password managers, remote control), elevated or protected windows, a host that
/// runs elevated, and the emergency-stop latch. The shell's policy engine (allow list, approvals) still decides first;
/// this guard can only refuse more, never allow more.
/// </summary>
public sealed class HostGuard(IReadOnlyCollection<int> protectedProcessIds, string? refuseAllReason = null)
{
    private int _latched;

    public bool IsLatched => Volatile.Read(ref _latched) == 1;

    /// <summary>Why every changing request is refused (for example the host runs as administrator), or null.</summary>
    public string? RefuseAllReason { get; } = refuseAllReason;

    public void Latch() => Interlocked.Exchange(ref _latched, 1);

    public void Resume() => Interlocked.Exchange(ref _latched, 0);

    /// <summary>
    /// Null when the host may act on <paramref name="window"/>; otherwise the reason it refuses. A window the host could
    /// not identify (null: closed, or a handle that is not a window) is refused: nothing about it can be checked.
    /// </summary>
    public string? CheckTarget(WindowInfo? window)
    {
        if (RefuseAllReason is { } all)
        {
            return all;
        }

        if (IsLatched)
        {
            return "긴급 중단 상태라 실행하지 않습니다. Operator 화면에서 [재개]를 눌러야 다시 조작합니다.";
        }

        if (window is null)
        {
            return "대상 창을 찾지 못했습니다. 확인할 수 없는 창에는 키보드·마우스 입력을 보내지 않습니다(Operator 실행 프로세스의 독립 검사).";
        }

        if (protectedProcessIds.Contains(window.ProcessId))
        {
            return "PC Control Center 자신의 창은 조작하지 않습니다(Operator 실행 프로세스의 독립 검사).";
        }

        if (BuiltInBlockList.IsBlocked(window.ProcessName))
        {
            return $"'{window.ProcessName}'은(는) 차단 목록의 프로그램이라 조작하지 않습니다(Operator 실행 프로세스의 독립 검사).";
        }

        if (window.IsElevatedOrProtected)
        {
            return "관리자 권한(또는 보호된) 창이라 조작하지 않습니다(Operator 실행 프로세스의 독립 검사).";
        }

        return null;
    }

    public string? CheckLaunch(string executable)
    {
        if (RefuseAllReason is { } all)
        {
            return all;
        }

        if (IsLatched)
        {
            return "긴급 중단 상태라 프로그램을 실행하지 않습니다.";
        }

        var name = Path.GetFileNameWithoutExtension(executable.Trim().Trim('"'));
        return BuiltInBlockList.IsBlocked(name) ? $"'{name}'은(는) 차단 목록의 프로그램이라 실행하지 않습니다(Operator 실행 프로세스의 독립 검사)." : null;
    }
}
