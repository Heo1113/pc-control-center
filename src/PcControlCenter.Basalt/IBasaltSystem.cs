namespace PcControlCenter.Basalt;

/// <summary>
/// What the controller needs from the operating system. Windows: Toolhelp process list, GetExtendedTcpTable, ShellExecute,
/// PcControlCenter.ConsoleSignal.exe and TerminateProcess, all with normal user rights. Tests use a fake.
/// </summary>
public interface IBasaltSystem
{
    /// <summary>Shown in the page's details ("Windows", "MOCK", ...).</summary>
    string Name { get; }

    /// <summary>A simulated server: the page shows MOCK and no real process is ever touched.</summary>
    bool IsMock { get; }

    /// <summary>This app runs with administrator rights (Basalt would inherit them, so it is not started).</summary>
    bool IsElevated { get; }

    /// <summary>The port to use instead of the settings' one (the MOCK server listens on a free port of its own).</summary>
    int? PortOverride => null;

    /// <summary>Why this system cannot be used at all (another OS); null when it can.</summary>
    string? UnavailableReason => null;

    /// <summary>Every running process with parent and start time; null when the list cannot be read.</summary>
    IReadOnlyList<BasaltProcess>? Processes();

    /// <summary>The processes listening on <paramref name="port"/>; null when the TCP table cannot be read.</summary>
    IReadOnlyList<BasaltListener>? Listeners(int port);

    bool FileExists(string path);
    bool DirectoryExists(string path);

    /// <summary>Starts the request in a console window of its own. Throws <see cref="BasaltStartException"/> when Windows refuses.</summary>
    BasaltLaunch Start(BasaltStartRequest request, string mode);

    /// <summary>Sends Ctrl+C to the console <paramref name="processId"/> is attached to (every process on it receives it).</summary>
    ConsoleSignalResult SendCtrlC(int processId, IReadOnlyList<BasaltProcess> owned);

    /// <summary>Ends one process, only if it is still the same process (PID and start time).</summary>
    TerminateResult Terminate(BasaltProcess process);

    /// <summary><c>GET http://127.0.0.1:{port}/health</c>, short timeout, loopback only.</summary>
    Task<BasaltHealth> HealthAsync(int port, CancellationToken cancellationToken);
}

public sealed class BasaltStartException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Any OS other than Windows: nothing can be listed or started (tests use their own fake).</summary>
public sealed class UnavailableBasaltSystem : IBasaltSystem
{
    public string Name => "사용 불가";
    public bool IsMock => false;
    public bool IsElevated => false;
    public string? UnavailableReason => "Basalt 서버 제어는 Windows에서만 동작합니다.";
    public IReadOnlyList<BasaltProcess>? Processes() => null;
    public IReadOnlyList<BasaltListener>? Listeners(int port) => null;
    public bool FileExists(string path) => false;
    public bool DirectoryExists(string path) => false;
    public BasaltLaunch Start(BasaltStartRequest request, string mode) => throw new BasaltStartException(UnavailableReason!);
    public ConsoleSignalResult SendCtrlC(int processId, IReadOnlyList<BasaltProcess> owned) => ConsoleSignalResult.Failed;
    public TerminateResult Terminate(BasaltProcess process) => TerminateResult.Failed;
    public Task<BasaltHealth> HealthAsync(int port, CancellationToken cancellationToken) => Task.FromResult(BasaltHealth.NoAnswer(UnavailableReason!));
}
