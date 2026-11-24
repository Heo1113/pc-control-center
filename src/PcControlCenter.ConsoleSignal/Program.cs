using System.Globalization;
using Microsoft.Win32.SafeHandles;
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

    private static unsafe int Main(string[] args)
    {
        if (args.Length < 2 || !uint.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 4)
        {
            return 64;
        }

        var expected = new Dictionary<uint, long>();
        foreach (var argument in args.Skip(1))
        {
            var fields = argument.Split(':');
            if (fields.Length != 2 || !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 4 ||
                !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0 || !expected.TryAdd(id, ticks))
            {
                return 64;
            }
        }

        if (!expected.ContainsKey(pid))
        {
            return 4;
        }

        if (!OperatingSystem.IsWindows())
        {
            return 3;
        }

        using var target = Pin(pid, expected[pid]);
        if (target is null)
        {
            return 4;
        }

        FreeConsole(); // started with no window; leave whatever console there is before joining the target's
        if (!AttachConsole(pid))
        {
            return 2;
        }

        var handles = new Dictionary<uint, SafeProcessHandle>();
        try
        {
            if (!SetConsoleCtrlHandler(0, true))
            {
                return 3;
            }

            // GetConsoleProcessList includes this helper. Hold each verified process object through delivery.
            var ids = new uint[expected.Count + 1];
            fixed (uint* buffer = ids)
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    var count = GetConsoleProcessList(buffer, (uint)ids.Length);
                    if (count == 0 || count > ids.Length || !ids.AsSpan(0, (int)count).Contains(pid))
                    {
                        return 4;
                    }

                    foreach (var id in ids.AsSpan(0, (int)count))
                    {
                        if (id == Environment.ProcessId || handles.ContainsKey(id))
                        {
                            continue;
                        }

                        if (!expected.TryGetValue(id, out var ticks) || Pin(id, ticks) is not { } handle)
                        {
                            return 4;
                        }

                        handles.Add(id, handle);
                    }
                }
            }

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
            foreach (var handle in handles.Values)
            {
                handle.Dispose();
            }
        }
    }

    private static SafeProcessHandle? Pin(uint pid, long expectedUtcTicks)
    {
        const uint queryLimitedInformation = 0x1000;
        var handle = OpenProcess(queryLimitedInformation, false, pid);
        if (!handle.IsInvalid && GetProcessTimes(handle, out var creation, out var exit, out _, out _) &&
            creation > 0 && exit == 0 && DateTime.FromFileTimeUtc(creation).Ticks == expectedUtcTicks)
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }

    [LibraryImport("kernel32.dll")]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetConsoleProcessList(uint* buffer, uint length);

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
