using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowsAiOperator.Core.Diagnostics;

namespace PcControlCenter.Operator;

public sealed record OperatorHostOptions
{
    /// <summary>PcControlCenter.OperatorHost.exe, or its .dll (started with the dotnet host; development and tests).</summary>
    public required string HostPath { get; init; }

    public required string LogDirectory { get; init; }

    /// <summary>Run E's simulated desktop instead of the real one (tests, demos). Shown as MOCK everywhere.</summary>
    public bool MockDesktop { get; init; }

    public int MaxAutomaticRestarts { get; init; } = 3;
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(1);
}

public enum HostState
{
    NotStarted,
    Starting,
    Connected,
    Restarting,
    Failed,
    Stopped,
}

/// <summary>
/// Starts PcControlCenter.OperatorHost.exe as a separate process with normal user rights (if the shell itself runs
/// elevated, the host gets a normal-user token: <see cref="RestrictedHostLauncher"/>; on Windows inside a job object that
/// kills it with the shell and forbids shutting down Windows or changing system parameters, display settings or
/// desktops), connects to it, restarts it a limited number of times when it dies, and implements the emergency stop:
/// latch, cancel, and kill the process when input may be in flight.
/// </summary>
public sealed class OperatorHostSupervisor : IHostConnectionSource, IAsyncDisposable
{
    private readonly OperatorHostOptions _options;
    private readonly IOperatorLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateGate = new();
    private WindowsJob? _job;
    private Process? _process;
    private object? _restricted;
    private HostConnection? _connection;
    private int _actionsInFlight;
    private int _restarts;
    private bool _stopping;
    private HostState _state = HostState.NotStarted;
    private string _reason = "Operator 실행 프로세스가 아직 시작되지 않았습니다.";

    public OperatorHostSupervisor(OperatorHostOptions options, IOperatorLog? log = null)
    {
        _options = options;
        _log = log ?? NullOperatorLog.Instance;
    }

    public HostState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public HostConnection? Current => _connection is { IsOpen: true } c ? c : null;
    public string UnavailableReason => _reason;
    public bool EmergencyLatched { get; private set; }
    public bool ExpectMock => _options.MockDesktop;
    public int Restarts => _restarts;
    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;

    /// <summary>True when the shell runs elevated and the host was started with a normal-user token instead.</summary>
    public bool HostDeElevated { get; private set; }

    /// <summary>Raised on any thread when the state, the latch or the connection changed.</summary>
    public event Action? Changed;

    public void BeginAction() => Interlocked.Increment(ref _actionsInFlight);

    public void EndAction() => Interlocked.Decrement(ref _actionsInFlight);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopping = false;
            await LaunchAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A manual restart (for example after the automatic restarts were used up). Resets the restart count.</summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _restarts = 0;
            await KillAsync("수동 재시작").ConfigureAwait(false);
            _stopping = false;
            await LaunchAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Emergency stop: nothing changes the screen until <see cref="ResumeAsync"/>. The host cancels everything and
    /// latches; if an action may be running right now (or the host does not confirm within a second) the host process is
    /// killed, which ends any input at once. It is restarted, still latched.
    /// </summary>
    public async Task EmergencyStopAsync()
    {
        EmergencyLatched = true;
        RaiseChanged();
        _log.Warn("supervisor", "emergency stop");
        var connection = Current;
        var inFlight = Volatile.Read(ref _actionsInFlight) > 0;
        var confirmed = connection is not null && !inFlight && await connection.TrySendControlAsync(HostOp.EmergencyStop, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        if (!confirmed && _process is { HasExited: false } process)
        {
            _log.Warn("supervisor", inFlight ? "action in flight: killing the host process" : "host did not confirm the stop: killing it");
            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log.Warn("supervisor", "kill failed", ex);
            }
        }
    }

    public async Task ResumeAsync()
    {
        EmergencyLatched = false;
        if (Current is { } connection)
        {
            await connection.TrySendControlAsync(HostOp.Resume, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        _log.Info("supervisor", "resumed after emergency stop");
        RaiseChanged();
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopping = true;
            if (Current is { } connection)
            {
                await connection.TrySendControlAsync(HostOp.Shutdown, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }

            if (_process is { } process && !process.WaitForExit(TimeSpan.FromSeconds(3)))
            {
                await KillAsync("종료").ConfigureAwait(false);
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            DisposeRestricted();
            SetState(HostState.Stopped, "Operator 실행 프로세스를 종료했습니다.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        DisposeRestricted();
        _job?.Dispose();
        _job = null;
    }

    private async Task LaunchAsync(CancellationToken cancellationToken)
    {
        SetState(HostState.Starting, "Operator 실행 프로세스를 시작하는 중입니다.");
        if (!File.Exists(_options.HostPath))
        {
            SetState(HostState.Failed, $"Operator 실행 프로세스 파일이 없습니다: {Path.GetFileName(_options.HostPath)}");
            return;
        }

        var useDotnet = _options.HostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var executable = useDotnet ? DotnetHost() : _options.HostPath;
        var arguments = new List<string>();
        if (useDotnet)
        {
            arguments.Add(_options.HostPath);
        }

        arguments.Add("--parent-pid");
        arguments.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("--log-dir");
        arguments.Add(_options.LogDirectory);
        if (_options.MockDesktop)
        {
            arguments.Add("--mock-desktop");
        }

        var workingDirectory = Path.GetDirectoryName(_options.HostPath)!;
        DisposeRestricted();
        Process process;
        Stream fromHost;
        Stream toHost;
        Action? resume = null;
        Stream? errors = null;
        if (OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess
            && TryStartRestricted(executable, arguments, workingDirectory) is { } restricted)
        {
            process = restricted.Process;
            fromHost = restricted.FromHost;
            toHost = restricted.ToHost;
            errors = restricted.Errors;
            resume = restricted.Resume;
            _restricted = restricted;
            HostDeElevated = true;
        }
        else
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = workingDirectory,
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            try
            {
                process = Process.Start(info) ?? throw new InvalidOperationException("프로세스를 시작하지 못했습니다.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _log.Error("supervisor", "host start failed", ex);
                SetState(HostState.Failed, "Operator 실행 프로세스를 시작하지 못했습니다: " + ex.Message);
                return;
            }

            AssignToJob(process.Handle);
            fromHost = process.StandardOutput.BaseStream;
            toHost = process.StandardInput.BaseStream;
            HostDeElevated = false;
        }

        process.EnableRaisingEvents = true;
        if (errors is null)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _log.Warn("host-stderr", e.Data);
                }
            };
            process.BeginErrorReadLine();
        }
        else
        {
            _ = Task.Run(() => PumpErrorsAsync(errors), CancellationToken.None);
        }

        process.Exited += OnProcessExited;
        _process = process;
        if (resume is not null)
        {
            try
            {
                resume();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _log.Error("supervisor", "host could not be resumed", ex);
                await KillAsync("시작 실패").ConfigureAwait(false);
                SetState(HostState.Failed, "Operator 실행 프로세스를 시작하지 못했습니다: " + ex.Message);
                return;
            }
        }
        try
        {
            var connection = await HostConnection.OpenAsync(fromHost, toHost, _options.HelloTimeout,
                cancellationToken).ConfigureAwait(false);
            connection.Closed += reason => _log.Warn("supervisor", "connection closed: " + reason);
            _connection = connection;
            if (EmergencyLatched)
            {
                await connection.TrySendControlAsync(HostOp.EmergencyStop, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }

            var hello = connection.Hello;
            _log.Info("supervisor", $"host pid {hello.ProcessId}: desktop '{hello.DesktopName}', mock={hello.IsMock}, elevated={hello.HostElevated}, " +
                $"de-elevated by the shell={HostDeElevated}, status={hello.Status.Description}");
            SetState(HostState.Connected, hello.Status.Available ? "연결됨" : hello.Status.Description);
        }
        catch (Exception ex) when (ex is HostUnavailableException or TimeoutException or HostCallException or OperationCanceledException)
        {
            _log.Error("supervisor", "host did not answer", ex);
            await KillAsync("시작 응답 없음").ConfigureAwait(false);
            SetState(HostState.Failed, "Operator 실행 프로세스가 시작 후 응답하지 않았습니다: " + ex.Message);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var process = (Process)sender!;
        int code;
        try
        {
            code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }

        if (_stopping || !ReferenceEquals(process, _process))
        {
            return;
        }

        _log.Warn("supervisor", $"host exited unexpectedly (code {code})");
        if (_restarts >= _options.MaxAutomaticRestarts)
        {
            SetState(HostState.Failed, $"Operator 실행 프로세스가 반복해서 종료되어(코드 {code}) 다시 시작하지 않았습니다. [다시 시작]으로 시작할 수 있습니다. 다른 모듈은 영향을 받지 않습니다.");
            return;
        }

        _restarts++;
        SetState(HostState.Restarting, $"Operator 실행 프로세스가 종료되어(코드 {code}) 다시 시작합니다 ({_restarts}/{_options.MaxAutomaticRestarts}). 진행 중이던 작업은 다시 실행하지 않습니다.");
        _ = Task.Run(async () =>
        {
            await Task.Delay(_options.RestartDelay).ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_stopping && ReferenceEquals(process, _process))
                {
                    await LaunchAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private async Task KillAsync(string why)
    {
        if (_process is { } process)
        {
            _process = null;
            process.Exited -= OnProcessExited;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                    await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                _log.Warn("supervisor", $"kill ({why}) failed", ex);
            }

            process.Dispose();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        DisposeRestricted();
    }

    private void SetState(HostState state, string reason)
    {
        lock (_stateGate)
        {
            _state = state;
            _reason = reason;
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("supervisor", "state handler failed", ex);
        }
    }

    private RestrictedHostLauncher.LaunchedHost? TryStartRestricted(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var launched = RestrictedHostLauncher.Start(executable, arguments, workingDirectory, AssignToJob);
            _log.Info("supervisor", "the shell runs elevated: host started with a normal-user token (medium integrity)");
            return launched;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or ArgumentException)
        {
            // Safe fallback: started normally, the host runs elevated and therefore refuses to change anything.
            _log.Warn("supervisor", "could not start the host with a normal-user token; it will refuse to change the screen while elevated", ex);
            return null;
        }
    }

    private void AssignToJob(nint processHandle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _job ??= WindowsJob.Create();
            _job.Assign(processHandle);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The host still runs with normal user rights and watches its pipe and the shell's process id; log and continue.
            _log.Warn("supervisor", "host could not be placed in a job object", ex);
        }
    }

    private async Task PumpErrorsAsync(Stream errors)
    {
        try
        {
            using var reader = new StreamReader(errors, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _log.Warn("host-stderr", line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The host ended.
        }
    }

    private void DisposeRestricted()
    {
        if (Interlocked.Exchange(ref _restricted, null) is IDisposable restricted)
        {
            restricted.Dispose();
        }
    }

    private static string DotnetHost()
        => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host
            : Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? self
            : "dotnet";
}

/// <summary>
/// A Windows job object for the Operator host: killed when the shell's handle closes (the shell ended in any way),
/// programs it starts break away silently (closing the host never closes the user's Notepad), and the host may not
/// shut down Windows, change system parameters or display settings, or switch desktops.
/// </summary>
internal sealed partial class WindowsJob : IDisposable
{
    private const int JobObjectBasicUiRestrictions = 4;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint LimitKillOnJobClose = 0x2000;
    private const uint LimitSilentBreakawayOk = 0x1000;
    private const uint LimitDieOnUnhandledException = 0x400;
    private const uint LimitProcessMemory = 0x100;
    private const uint UiLimitSystemParameters = 0x8;
    private const uint UiLimitDisplaySettings = 0x10;
    private const uint UiLimitDesktop = 0x40;
    private const uint UiLimitExitWindows = 0x80;

    private nint _handle;

    private WindowsJob(nint handle) => _handle = handle;

    public static WindowsJob Create()
    {
        var handle = CreateJobObjectW(0, null);
        if (handle == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        var job = new WindowsJob(handle);
        var limits = new JobObjectExtendedLimit
        {
            BasicLimitInformation = new JobObjectBasicLimit
            {
                LimitFlags = LimitKillOnJobClose | LimitSilentBreakawayOk | LimitDieOnUnhandledException | LimitProcessMemory,
            },
            ProcessMemoryLimit = (nuint)(2048L * 1024 * 1024),
        };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JobObjectExtendedLimit>()))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new System.ComponentModel.Win32Exception(error);
        }

        var ui = new JobObjectUiRestrictions { UiRestrictionsClass = UiLimitSystemParameters | UiLimitDisplaySettings | UiLimitDesktop | UiLimitExitWindows };
        if (!SetInformationJobObjectUi(handle, JobObjectBasicUiRestrictions, ref ui, Marshal.SizeOf<JobObjectUiRestrictions>()))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new System.ComponentModel.Win32Exception(error);
        }

        return job;
    }

    public void Assign(nint processHandle)
    {
        if (!AssignProcessToJobObject(_handle, processHandle))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
            _handle = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
    {
        public JobObjectBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectUiRestrictions
    {
        public uint UiRestrictionsClass;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimit info, int length);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObjectUi(nint job, int infoClass, ref JobObjectUiRestrictions info, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
