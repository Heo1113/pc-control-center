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
public sealed record AdapterShare(string Adapter, double DedicatedGb, double SharedGb);

/// <param name="CommandLine">Always the redacted command line (secrets and the user profile name removed); empty until identified.</param>
/// <param name="OtherAdapters">
/// Diagnostic only (the probe report; not shown in the window): the process's memory on adapters other than <paramref name="Adapter"/>.
/// The row shows the adapter with the most dedicated memory; see docs/IRON_STAGE2.md, "듀얼 GPU".
/// </param>
public sealed record Stage2Process(int Pid, string ImageName, string Adapter, double DedicatedGb, double SharedGb, double UtilizationPercent,
    string Engine, string CommandLine, bool IsSoftwareAdapter, ProcessIdentityInfo? Identity, IReadOnlyList<AdapterShare>? OtherAdapters = null);

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
