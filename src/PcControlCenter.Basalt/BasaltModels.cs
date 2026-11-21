using System.Globalization;

namespace PcControlCenter.Basalt;

/// <summary>
/// One running process as Windows lists it. A PID can be reused by a newer process, so a process is identified by its
/// PID and its start time together; a process whose start time could not be read is never ended.
/// </summary>
public sealed record BasaltProcess(int Id, int ParentId, string Name, DateTimeOffset? StartTime)
{
    /// <summary>The same process: same PID and same (known) start time.</summary>
    public bool SameAs(BasaltProcess other) => Id == other.Id && StartTime is not null && StartTime == other.StartTime;

    public string Label => $"PID {Id.ToString(CultureInfo.InvariantCulture)} {Name}";
}

/// <summary>A process listening on a TCP port of this PC (loopback or all addresses, IPv4 or IPv6).</summary>
public sealed record BasaltListener(int Port, int ProcessId);

/// <summary>The PowerShell window this app started to run Basalt's start script.</summary>
public sealed record BasaltLaunch(int ProcessId, DateTimeOffset? StartTime, string Mode, DateTimeOffset StartedAt);

/// <summary>What the app runs to start Basalt: Windows PowerShell with the start script and the configuration file.</summary>
public sealed record BasaltStartRequest(string Executable, string Arguments, string WorkingDirectory);

/// <summary>The outcome of sending Ctrl+C to the console of a process.</summary>
public enum ConsoleSignalResult
{
    Sent,

    /// <summary>The process has no console to join (started without one) or has ended.</summary>
    NoConsole,

    Failed,

    /// <summary>PcControlCenter.ConsoleSignal.exe was not found next to the app.</summary>
    HelperMissing,
}

/// <summary>The outcome of ending one process.</summary>
public enum TerminateResult
{
    Ended,
    AlreadyGone,

    /// <summary>The PID now belongs to another process (different start time): left alone.</summary>
    IdentityChanged,

    /// <summary>The start time could not be read, so it cannot be told apart from a newer process with that PID: left alone.</summary>
    Unverified,

    /// <summary>Windows refused (another user's or a protected process).</summary>
    Denied,

    Failed,
}

/// <summary>
/// Basalt's <c>GET /health</c>. <see cref="StatusCode"/> is null when nothing answered (refused, timed out); the other
/// fields are read only when the body is a JSON object that has them.
/// </summary>
public sealed record BasaltHealth(int? StatusCode, string? Service, bool? Loaded, string? Error)
{
    public static BasaltHealth NoAnswer(string error) => new(null, null, null, error);

    /// <summary>The server answers: any HTTP status except "busy starting" (502/503/504, how llama.cpp-style servers say they are loading).</summary>
    public bool IsUp => StatusCode is int code && code is not (502 or 503 or 504);
}

public enum BasaltState
{
    /// <summary>Not looked yet.</summary>
    Unknown,

    Off,

    /// <summary>Started by this app (or its processes run) and not answering yet.</summary>
    Starting,

    Running,
    Stopping,

    /// <summary>Another program listens on Basalt's port: Basalt is not started and that program is never ended.</summary>
    PortBusy,

    /// <summary>Basalt's processes are left (nothing listens on the port): the stop button cleans them up.</summary>
    Leftover,

    /// <summary>A start did not come up (window closed, no answer in time) or a stop left processes behind.</summary>
    Failed,

    /// <summary>The process list or the TCP table cannot be read on this system (or this OS).</summary>
    Unavailable,
}

/// <summary>What a stop did: whether Ctrl+C was enough, what was ended, what is left, whether the port is free.</summary>
public sealed record BasaltStopReport
{
    public required DateTimeOffset At { get; init; }
    public required bool Graceful { get; init; }

    /// <summary>Where Ctrl+C went, or why it could not be sent.</summary>
    public required string Signal { get; init; }

    public IReadOnlyList<string> Ended { get; init; } = [];
    public IReadOnlyList<string> Left { get; init; } = [];
    public required bool PortFree { get; init; }
    public string? PortOwner { get; init; }
    public TimeSpan Took { get; init; }

    public bool Clean => Left.Count == 0 && PortFree;

    public string Summary => Clean
        ? (Graceful ? "정상 종료됨 · 남은 프로세스 없음 · 포트 비어 있음" : $"정리됨 (프로세스 {Ended.Count}개 종료) · 남은 프로세스 없음 · 포트 비어 있음")
        : $"남은 프로세스 {Left.Count}개{(PortFree ? "" : $" · 포트 사용 중 ({PortOwner ?? "알 수 없음"})")}";
}

/// <summary>A start or stop request that was not carried out, and why (shown on the page).</summary>
public sealed record BasaltRefusal(string Message);

/// <summary>The controller's view of Basalt, immutable, handed to the UI.</summary>
public sealed record BasaltStatus
{
    public required BasaltState State { get; init; }
    public required int Port { get; init; }
    public required bool IsMock { get; init; }

    /// <summary>One line: what the state means right now.</summary>
    public string Detail { get; init; } = "";

    /// <summary>The configuration this app started Basalt with; null when it was started elsewhere (or is off).</summary>
    public string? Mode { get; init; }

    public bool StartedHere { get; init; }

    /// <summary>When the current start or stop began.</summary>
    public DateTimeOffset? Since { get; init; }

    public BasaltTree? Tree { get; init; }
    public BasaltHealth? Health { get; init; }
    public BasaltStopReport? LastStop { get; init; }

    /// <summary>The result of the last start or stop request that was refused or failed (null after a later success).</summary>
    public string? Notice { get; init; }

    public DateTimeOffset CheckedAt { get; init; }
}
