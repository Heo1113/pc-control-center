using PcControlCenter.Basalt;
using PcControlCenter.Core;

namespace PcControlCenter.Tests;

/// <summary>What the fake does when Ctrl+C is sent to a process of the tree.</summary>
internal enum FakeSignal
{
    /// <summary>Every process of the tree and the listener go away (as with the real Basalt).</summary>
    EndsAll,

    /// <summary>Sent, but nothing ends (a hung server).</summary>
    Ignored,

    /// <summary>No process has a console.</summary>
    NoConsole,
}

/// <summary>
/// A process table, a TCP table and a health answer in memory. The default tree: explorer (100) → PowerShell window (500)
/// → basalt.exe (510) → python.exe (520, listening) → basalt-engine.exe (530), plus conhost.exe (501) under the window.
/// </summary>
internal sealed class FakeBasaltSystem : IBasaltSystem
{
    public const int Explorer = 100;
    public const int Launcher = 500;
    public const int ConsoleHost = 501;
    public const int Wrapper = 510;
    public const int Server = 520;
    public const int Engine = 530;
    public const int Self = 999;

    private readonly object _gate = new();
    private readonly List<BasaltProcess> _processes = [];
    private readonly List<BasaltListener> _listeners = [];
    private readonly List<int> _signals = [];
    private readonly List<IReadOnlyList<BasaltProcess>> _signalOwned = [];
    private readonly List<int> _terminated = [];
    private readonly List<BasaltStartRequest> _starts = [];

    public FakeBasaltSystem(DateTimeOffset? origin = null)
    {
        Origin = origin ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        Add(new BasaltProcess(Explorer, 4, "explorer.exe", Origin.AddHours(-1)));
        Add(new BasaltProcess(Self, Explorer, "testhost.exe", Origin.AddMinutes(-30)));
    }

    public DateTimeOffset Origin { get; }
    public int Port { get; set; } = 8080;
    public bool IsElevated { get; set; }
    public bool ListenersUnreadable { get; set; }
    public bool MissingFiles { get; set; }
    public FakeSignal Signal { get; set; } = FakeSignal.EndsAll;
    public TimeSpan SignalDelay { get; set; } = TimeSpan.FromMilliseconds(30);
    public BasaltHealth Health { get; set; } = new(200, "basalt", true, null);

    /// <summary>Called after a process was ended (for example to start a new engine, as a restarting server would).</summary>
    public Action<FakeBasaltSystem, BasaltProcess>? AfterTerminate { get; set; }

    /// <summary>Overrides the outcome for one process (Denied, IdentityChanged, ...).</summary>
    public Func<BasaltProcess, TerminateResult?>? TerminateOverride { get; set; }

    /// <summary>What a start creates: true = the default tree (listening at once), false = only the window (it then closes).</summary>
    public bool StartCreatesTree { get; set; } = true;

    public string Name => "FAKE";
    public bool IsMock => false;

    public IReadOnlyList<int> Signals
    {
        get
        {
            lock (_gate)
            {
                return [.. _signals];
            }
        }
    }

    public IReadOnlyList<IReadOnlyList<BasaltProcess>> SignalOwned
    {
        get
        {
            lock (_gate)
            {
                return [.. _signalOwned];
            }
        }
    }

    public IReadOnlyList<int> Terminated
    {
        get
        {
            lock (_gate)
            {
                return [.. _terminated];
            }
        }
    }

    public IReadOnlyList<BasaltStartRequest> Starts
    {
        get
        {
            lock (_gate)
            {
                return [.. _starts];
            }
        }
    }

    public void Add(BasaltProcess process)
    {
        lock (_gate)
        {
            _processes.RemoveAll(p => p.Id == process.Id);
            _processes.Add(process);
        }
    }

    public void Remove(int id)
    {
        lock (_gate)
        {
            _processes.RemoveAll(p => p.Id == id);
            _listeners.RemoveAll(l => l.ProcessId == id);
        }
    }

    public void Listen(int pid, int? port = null)
    {
        lock (_gate)
        {
            _listeners.Add(new BasaltListener(port ?? Port, pid));
        }
    }

    public bool Has(int id)
    {
        lock (_gate)
        {
            return _processes.Any(p => p.Id == id);
        }
    }

    /// <summary>The default tree, started at <paramref name="start"/>, listening on <see cref="Port"/>.</summary>
    public void AddTree(DateTimeOffset start, bool listening = true, bool withLauncher = true)
    {
        if (withLauncher)
        {
            Add(new BasaltProcess(Launcher, Explorer, "powershell.exe", start));
            Add(new BasaltProcess(ConsoleHost, Launcher, "conhost.exe", start));
        }

        Add(new BasaltProcess(Wrapper, withLauncher ? Launcher : Explorer, "basalt.exe", start.AddMilliseconds(100)));
        Add(new BasaltProcess(Server, Wrapper, "python.exe", start.AddMilliseconds(200)));
        Add(new BasaltProcess(Engine, Server, "basalt-engine.exe", start.AddMilliseconds(300)));
        if (listening)
        {
            Listen(Server);
        }
    }

    public IReadOnlyList<BasaltProcess>? Processes()
    {
        lock (_gate)
        {
            return [.. _processes];
        }
    }

    public IReadOnlyList<BasaltListener>? Listeners(int port)
    {
        lock (_gate)
        {
            return ListenersUnreadable ? null : [.. _listeners.Where(l => l.Port == port)];
        }
    }

    public bool FileExists(string path) => !MissingFiles;
    public bool DirectoryExists(string path) => true;

    public BasaltLaunch Start(BasaltStartRequest request, string mode)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _starts.Add(request);
        }

        if (StartCreatesTree)
        {
            AddTree(now);
        }
        else
        {
            Add(new BasaltProcess(Launcher, Explorer, "powershell.exe", now));
        }

        return new BasaltLaunch(Launcher, now, mode, now);
    }

    public ConsoleSignalResult SendCtrlC(int processId, IReadOnlyList<BasaltProcess> owned)
    {
        lock (_gate)
        {
            _signals.Add(processId);
            _signalOwned.Add([.. owned]);
        }

        switch (Signal)
        {
            case FakeSignal.NoConsole:
                return ConsoleSignalResult.NoConsole;
            case FakeSignal.Ignored:
                return ConsoleSignalResult.Sent;
            default:
                _ = Task.Run(async () =>
                {
                    await Task.Delay(SignalDelay);
                    foreach (var member in owned)
                    {
                        Remove(member.Id);
                    }
                    foreach (var id in new[] { Launcher, ConsoleHost, Wrapper, Server, Engine })
                    {
                        Remove(id);
                    }
                });
                return ConsoleSignalResult.Sent;
        }
    }

    public TerminateResult Terminate(BasaltProcess process)
    {
        lock (_gate)
        {
            _terminated.Add(process.Id);
        }

        if (TerminateOverride?.Invoke(process) is { } forced)
        {
            return forced;
        }

        BasaltProcess? current;
        lock (_gate)
        {
            current = _processes.FirstOrDefault(p => p.Id == process.Id);
        }

        if (current is null)
        {
            return TerminateResult.AlreadyGone;
        }

        if (!current.SameAs(process))
        {
            return TerminateResult.IdentityChanged;
        }

        Remove(process.Id);
        AfterTerminate?.Invoke(this, process);
        return TerminateResult.Ended;
    }

    public Task<BasaltHealth> HealthAsync(int port, CancellationToken cancellationToken) => Task.FromResult(Health);
}

internal static class BasaltTest
{
    public static readonly BasaltTimings Fast = new()
    {
        Poll = TimeSpan.FromMilliseconds(30),
        FastPoll = TimeSpan.FromMilliseconds(20),
        IdleScan = TimeSpan.FromMilliseconds(30),
        RespawnWatch = TimeSpan.FromMilliseconds(400),
        RespawnQuiet = TimeSpan.FromMilliseconds(150),
        LauncherGrace = TimeSpan.FromMilliseconds(100),
        // Long enough for the fake's 30 ms Ctrl+C on a busy CI runner; the cleanup tests use ShortStopWait.
        StopWait = TimeSpan.FromSeconds(10),
        StartWait = TimeSpan.FromSeconds(5),
    };

    /// <summary>For trees that ignore Ctrl+C: the cleanup starts after 300 ms.</summary>
    public static readonly BasaltTimings ShortStopWait = Fast with { StopWait = TimeSpan.FromMilliseconds(300) };

    public static BasaltController Controller(FakeBasaltSystem system, BasaltSettings? settings = null, BasaltTimings? timings = null)
        => new(system, settings ?? new BasaltSettings(), timings: timings ?? Fast, selfId: FakeBasaltSystem.Self);

    public static async Task<BasaltStatus> WaitFor(BasaltController controller, Func<BasaltStatus, bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            await controller.RefreshAsync(TestContext.Current.CancellationToken);
            var status = controller.Status;
            if (condition(status))
            {
                return status;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"state {status.State} ({status.Detail}; notice {status.Notice})");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
