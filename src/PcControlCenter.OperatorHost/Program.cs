using System.Runtime.CompilerServices;
using PcControlCenter.Operator;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Diagnostics;

namespace PcControlCenter.OperatorHost;

internal static class Program
{
    private static Task<int> Main(string[] args) => HostProgram.RunAsync(args, CreateRealDesktop);

    /// <summary>Project E's real desktop automation (FlaUI UIA3 + Win32), created on a thread-pool (MTA) thread as in E.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopAutomation CreateRealDesktop(IOperatorLog log) => new WindowsAiOperator.Automation.Windows.FlaUiDesktop(log);
}
