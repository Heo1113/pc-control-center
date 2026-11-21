using System.Globalization;
using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace PcControlCenter.ConsoleSignal;

/// <summary>
/// Sends Ctrl+C to the console of one process. Every process attached to that console receives it, as when Ctrl+C is
/// pressed in the window. This process leaves its own console, joins the target's, ignores the Ctrl+C itself and sends it.
/// Exit codes: 0 sent, 2 no console to join (the process has none or has ended), 3 the signal could not be sent,
/// 64 bad arguments.
/// </summary>
internal static partial class Program
{
    private const uint CtrlCEvent = 0;

    private static int Main(string[] args)
    {
        if (args.Length != 1 || !uint.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 4)
        {
            Console.Error.WriteLine("usage: PcControlCenter.ConsoleSignal <pid>");
            return 64;
        }

        if (!OperatingSystem.IsWindows())
        {
            return 3;
        }

        FreeConsole(); // started with no window; leave whatever console there is before joining the target's
        if (!AttachConsole(pid))
        {
            return 2;
        }

        try
        {
            SetConsoleCtrlHandler(0, true); // this process ignores the Ctrl+C it is about to send
            if (!GenerateConsoleCtrlEvent(CtrlCEvent, 0))
            {
                return 3;
            }

            Thread.Sleep(300); // the console delivers the event asynchronously; stay attached until it is out
            return 0;
        }
        finally
        {
            FreeConsole();
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
