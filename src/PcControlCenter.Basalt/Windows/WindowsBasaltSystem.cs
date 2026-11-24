using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalAiPerf.Core.Hardware;
using PcControlCenter.GpuMemory.Windows;
using static PcControlCenter.Basalt.Windows.NativeMethods;

namespace PcControlCenter.Basalt.Windows;

/// <summary>
/// The real system, normal user rights only: the Toolhelp process list with start times (PROCESS_QUERY_LIMITED_INFORMATION),
/// GetExtendedTcpTable, ShellExecute for the start window, PcControlCenter.ConsoleSignal.exe for Ctrl+C and
/// TerminateProcess after checking the start time again on the opened handle.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBasaltSystem : IBasaltSystem, IDisposable
{
    private readonly string? _signalHelper;
    private readonly BasaltHealthClient _health = new();

    /// <param name="signalHelper">PcControlCenter.ConsoleSignal.exe (or its .dll, started with the dotnet host); null = Ctrl+C cannot be sent.</param>
    public WindowsBasaltSystem(string? signalHelper)
    {
        _signalHelper = signalHelper;
    }

    public string Name => "Windows";
    public bool IsMock => false;
    public bool IsElevated => Environment.IsPrivilegedProcess;

    public IReadOnlyList<BasaltProcess>? Processes()
    {
        var issues = new List<SensorIssue>();
        var capturedAfter = DateTimeOffset.UtcNow;
        var list = ProcessQueries.List(issues);
        if (list is null)
        {
            return null;
        }

        var result = new List<BasaltProcess>(list.Count);
        foreach (var (pid, (parent, name)) in list)
        {
            var start = ProcessQueries.StartTime(pid);
            // Do not pair an earlier Toolhelp parent entry with an instance born during or after that enumeration.
            result.Add(new BasaltProcess(pid, parent, name, start <= capturedAfter ? start : null));
        }

        return result;
    }

    public IReadOnlyList<BasaltListener>? Listeners(int port)
    {
        var issues = new List<SensorIssue>();
        var all = ProcessQueries.Listeners(issues);
        if (issues.Count > 0 && all.Count == 0)
        {
            return null;
        }

        return [.. all.Where(l => l.Port == port).Select(l => new BasaltListener(l.Port, l.ProcessId)).Distinct()];
    }

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public BasaltLaunch Start(BasaltStartRequest request, string mode)
    {
        try
        {
            // ShellExecute: a console window of its own (minimized, not focused), as when the script is started by hand.
            using var process = Process.Start(new ProcessStartInfo(request.Executable, request.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = request.WorkingDirectory,
                WindowStyle = ProcessWindowStyle.Minimized,
            }) ?? throw new BasaltStartException("Windows가 프로세스를 만들지 않았습니다.");
            var handle = process.SafeHandle;
            DateTimeOffset? start = GetProcessTimes(handle.DangerousGetHandle(), out var created, out _, out _, out _) && created > 0
                ? new DateTimeOffset(DateTime.FromFileTimeUtc(created), TimeSpan.Zero) : null;
            GC.KeepAlive(handle);
            return new BasaltLaunch(process.Id, start, mode, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new BasaltStartException(ex.Message, ex);
        }
    }

    public ConsoleSignalResult SendCtrlC(int processId, IReadOnlyList<BasaltProcess> owned)
    {
        if (_signalHelper is null || !File.Exists(_signalHelper))
        {
            return ConsoleSignalResult.HelperMissing;
        }

        var dll = _signalHelper.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(dll ? "dotnet" : _signalHelper)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (dll)
        {
            info.ArgumentList.Add(_signalHelper);
        }

        info.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        foreach (var member in owned)
        {
            if (member.StartTime is { } start)
            {
                info.ArgumentList.Add(FormattableString.Invariant($"{member.Id}:{start.UtcTicks}"));
            }
        }

        try
        {
            using var helper = Process.Start(info);
            if (helper is null)
            {
                return ConsoleSignalResult.Failed;
            }

            if (!helper.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                helper.Kill();
                return ConsoleSignalResult.Failed;
            }

            return helper.ExitCode switch
            {
                0 => ConsoleSignalResult.Sent,
                2 => ConsoleSignalResult.NoConsole,
                4 => ConsoleSignalResult.UnverifiedConsole,
                _ => ConsoleSignalResult.Failed,
            };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ConsoleSignalResult.Failed;
        }
    }

    public TerminateResult Terminate(BasaltProcess process)
    {
        if (process.StartTime is not { } expected)
        {
            return TerminateResult.Unverified;
        }

        var handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation | Synchronize, false, (uint)process.Id);
        if (handle == 0)
        {
            return Marshal.GetLastPInvokeError() switch
            {
                ErrorInvalidParameter => TerminateResult.AlreadyGone,
                ErrorAccessDenied => TerminateResult.Denied,
                _ => TerminateResult.Failed,
            };
        }

        try
        {
            // The start time again, on this handle: the PID may have been reused since the list was read.
            if (!GetProcessTimes(handle, out var creation, out var exit, out _, out _) || creation <= 0)
            {
                return TerminateResult.Unverified;
            }

            if (new DateTimeOffset(DateTime.FromFileTimeUtc(creation), TimeSpan.Zero) != expected)
            {
                return TerminateResult.IdentityChanged;
            }

            if (exit > 0 && WaitForSingleObject(handle, 0) == 0)
            {
                return TerminateResult.AlreadyGone;
            }

            if (!TerminateProcess(handle, 1))
            {
                return Marshal.GetLastPInvokeError() == ErrorAccessDenied ? TerminateResult.Denied : TerminateResult.Failed;
            }

            WaitForSingleObject(handle, 2000);
            return TerminateResult.Ended;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public Task<BasaltHealth> HealthAsync(int port, CancellationToken cancellationToken) => _health.GetAsync(port, cancellationToken);

    public void Dispose() => _health.Dispose();
}
