using Iron.ProcessIdentity;

namespace Iron.Stage2;

/// <summary>Life cycle of the readers (the same meaning as Stage 1's sensor phase).</summary>
public enum Stage2Phase
{
    Ready,
    Initializing,

    /// <summary>No GPU reader answers (NVML missing or no NVIDIA GPU). CPU, RAM and the process list may still work.</summary>
    NoDevice,
}

/// <summary>State of one source for the status tooltip and the probe report.</summary>
public enum SourceState
{
    /// <summary>Not started or switched off.</summary>
    Off,
    Connecting,
    Live,

    /// <summary>Answers stopped recently; the last values are older than allowed and are no longer shown.</summary>
    Stale,
    Offline,

    /// <summary>Not available on this PC (no NVIDIA driver, no counters ...). Values stay N/A.</summary>
    Unavailable,
}

public sealed record SourceStatus(string Name, SourceState State, string? Detail = null);

public sealed record Stage2Gpu(int Index, string Name, double? TemperatureC, double? PowerW, double? UtilizationPercent, double? VramUsedGb,
    double? VramTotalGb, double? ClockMhz);

public sealed record Stage2Cpu(double? LoadPercent, double? TctlC, double? PackageC, double? CoreMaxC);

/// <summary>WireView Pro II as HWiNFO reports it (read only; IRON never opens its USB port).</summary>
public sealed record Stage2WireView(bool Connected, double? PowerW, double? VoltageV, double? ConnectorInC, double? ConnectorOutC,
    IReadOnlyList<double> PinCurrentsA, string Completeness)
{
    public static Stage2WireView None { get; } = new(false, null, null, null, null, [], "ABSENT");
}

/// <summary>A process's memory on one more adapter (WDDM counters of that adapter only).</summary>
/// <param name="Luid">The adapter's LUID: the GPU's identity (two cards of the same model have the same name).</param>
public sealed record AdapterShare(string Adapter, double DedicatedGb, double SharedGb, long Luid = 0, double UtilizationPercent = 0, string Engine = "",
    bool IsSoftwareAdapter = false);

/// <param name="CommandLine">Always the redacted command line (secrets and the user profile name removed); empty until identified.</param>
/// <param name="OtherAdapters">
/// The process's memory and engine use on adapters other than <paramref name="Adapter"/> (the representative GPU, most dedicated
/// memory). The window lists the process under each GPU filter it uses and names the others in the tooltip; see docs/IRON_STAGE2.md, "듀얼 GPU".
/// </param>
/// <param name="AdapterLuid">LUID of <paramref name="Adapter"/>.</param>
/// <param name="StartTicks">Creation time of the process (UTC ticks, from the WDDM reader's process table); 0 = not known.</param>
public sealed record Stage2Process(int Pid, string ImageName, string Adapter, double DedicatedGb, double SharedGb, double UtilizationPercent,
    string Engine, string CommandLine, bool IsSoftwareAdapter, ProcessIdentityInfo? Identity, IReadOnlyList<AdapterShare>? OtherAdapters = null,
    long AdapterLuid = 0, long StartTicks = 0);

public sealed record Stage2Frame
{
    public required DateTimeOffset Timestamp { get; init; }
    public Stage2Phase Phase { get; init; } = Stage2Phase.Initializing;
    public IReadOnlyList<Stage2Gpu> Gpus { get; init; } = [];
    public Stage2Cpu Cpu { get; init; } = new(null, null, null, null);
    public double? RamUsedGb { get; init; }
    public double? RamTotalGb { get; init; }
    public double? SsdTemperatureC { get; init; }
    public double? SsdReadMBps { get; init; }
    public Stage2WireView WireView { get; init; } = Stage2WireView.None;
    public IReadOnlyList<Stage2Process> Processes { get; init; } = [];

    /// <summary>Why <see cref="Processes"/> is empty because the WDDM counters gave nothing current (null when they did).</summary>
    public string? ProcessesUnavailable { get; init; }

    public IReadOnlyList<SourceStatus> Sources { get; init; } = [];
}
