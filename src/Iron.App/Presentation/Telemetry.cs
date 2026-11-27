namespace Iron.App.Presentation;

/// <summary>Where the sensors are in their life cycle (the LED shows it before any value).</summary>
public enum SensorPhase
{
    /// <summary>Values are flowing.</summary>
    Ready,

    /// <summary>Sensors are being opened; no value yet (amber LED, "센서 초기화 중...").</summary>
    Initializing,

    /// <summary>No GPU sensor answers (grey LED, "미연결"). CPU and RAM counters may still work.</summary>
    NoDevice,
}

/// <summary>One GPU as the NVML reader reports it (Stage 1: simulated). Null = not measured, shown as "—" or N/A.</summary>
public sealed record GpuReading
{
    public required int Index { get; init; }

    /// <summary>Short model name shown on the card ("RTX 5080").</summary>
    public required string Name { get; init; }

    public double? TemperatureC { get; init; }
    public double? PowerW { get; init; }
    public double? UtilizationPercent { get; init; }
    public double? VramUsedGb { get; init; }

    /// <summary>Total VRAM as the reader reports it; null = not known (shown as N/A, never a guessed size).</summary>
    public double? VramTotalGb { get; init; }
    public double? ClockMhz { get; init; }
}

/// <summary>CPU load and the three temperature sensors the FLUX PRO display can show.</summary>
public sealed record CpuReading(double? LoadPercent, double? TctlC, double? PackageC, double? CoreMaxC);

/// <param name="TotalGb">Installed RAM as Windows reports it; null = not known (shown as N/A, never a guessed size).</param>
public sealed record RamReading(double? UsedGb, double? TotalGb);

public sealed record SsdReading(double? TemperatureC, double? ReadMBps);

/// <summary>
/// Thermal Grizzly WireView Pro II (12V-2x6 connector, read-only telemetry). Measures only what passes the connector;
/// never added to or substituted for the NVML board power.
/// </summary>
public sealed record WireViewReading
{
    public static WireViewReading None { get; } = new();

    public bool Connected { get; init; }
    public double? PowerW { get; init; }
    public double? VoltageV { get; init; }
    public double? ConnectorInC { get; init; }
    public double? ConnectorOutC { get; init; }
    public double? FanDutyPercent { get; init; }
    public int? CableRatingW { get; init; }

    /// <summary>Currents of the six 12 V pins P1..P6 (empty when not connected).</summary>
    public IReadOnlyList<double> PinCurrentsA { get; init; } = [];
}

/// <summary>Antec FLUX PRO side-panel display (one-way USB HID temperature output).</summary>
public sealed record FluxReading(bool Connected, bool IUnityRunning);

/// <summary>One process's memory and engine use on one GPU (that adapter's WDDM counters only; never summed across GPUs).</summary>
/// <param name="Luid">The adapter's LUID (Stage 2); 0 = not known (Stage 1 mock rows), then the name identifies the GPU.</param>
public sealed record AdapterUsage(string Adapter, long Luid, double DedicatedGb, double SharedGb, double UtilizationPercent, string Engine,
    bool IsSoftwareAdapter = false)
{
    /// <summary>
    /// Internal identity of the GPU: its LUID, never its model name (two cards of the same model stay apart). The name is
    /// used only for mock rows, which have no LUID.
    /// </summary>
    public string Id => Luid != 0 ? "luid:" + Luid.ToString("X", System.Globalization.CultureInfo.InvariantCulture) : "name:" + Adapter;
}

/// <summary>
/// One process's GPU memory as the Windows WDDM counters (PDH) report it. Never mixed with NVML values. The row values are
/// one GPU's: the representative GPU (most dedicated memory) in "전체", the chosen GPU under a GPU filter.
/// </summary>
/// <param name="AdapterLuid">LUID of <paramref name="Adapter"/> (Stage 2); 0 for mock rows.</param>
/// <param name="StartTicks">The process's creation time (UTC ticks, Stage 2): with the PID it tells a reused PID apart; 0 = not known.</param>
/// <param name="OtherAdapters">The process's use of the other GPUs (Stage 2), shown in the tooltip, never added to the row values.</param>
public sealed record ProcessReading(
    int Pid,
    string Name,
    string Adapter,
    double DedicatedGb,
    double SharedGb,
    double UtilizationPercent,
    string Engine,
    string CommandLine,
    bool IsSoftwareAdapter = false,
    long AdapterLuid = 0,
    long StartTicks = 0,
    IReadOnlyList<AdapterUsage>? OtherAdapters = null)
{
    /// <summary>The GPU the row values belong to.</summary>
    public AdapterUsage Shown => new(Adapter, AdapterLuid, DedicatedGb, SharedGb, UtilizationPercent, Engine, IsSoftwareAdapter);

    /// <summary>Every GPU the process uses: the shown one first.</summary>
    public IEnumerable<AdapterUsage> Usages => OtherAdapters is { Count: > 0 } o ? [Shown, .. o] : [Shown];

    /// <summary>The process instance: PID plus creation time (a reused PID is another process).</summary>
    public (int Pid, long StartTicks) Instance => (Pid, StartTicks);

    /// <summary>The same process with the values of one of its GPUs (the others move to <see cref="OtherAdapters"/>).</summary>
    public ProcessReading On(AdapterUsage u) => this with
    {
        Adapter = u.Adapter,
        AdapterLuid = u.Luid,
        DedicatedGb = u.DedicatedGb,
        SharedGb = u.SharedGb,
        UtilizationPercent = u.UtilizationPercent,
        Engine = u.Engine,
        IsSoftwareAdapter = u.IsSoftwareAdapter,
        OtherAdapters = [.. Usages.Where(x => x.Id != u.Id)],
    };
}

/// <summary>Everything one reading of the sensors gives (Stage 1: one simulated reading).</summary>
public sealed record TelemetryFrame
{
    public required DateTimeOffset Timestamp { get; init; }
    public SensorPhase Phase { get; init; } = SensorPhase.Ready;
    public required IReadOnlyList<GpuReading> Gpus { get; init; }
    public CpuReading Cpu { get; init; } = new(null, null, null, null);
    public RamReading Ram { get; init; } = new(null, null);
    public SsdReading Ssd { get; init; } = new(null, null);
    public WireViewReading WireView { get; init; } = WireViewReading.None;
    public FluxReading Flux { get; init; } = new(false, false);
    public IReadOnlyList<ProcessReading> Processes { get; init; } = [];

    /// <summary>Stage 2: why the process list is empty when the WDDM counters gave nothing current (null otherwise).</summary>
    public string? ProcessesUnavailableReason { get; init; }

    /// <summary>Stage 1: every value is simulated.</summary>
    public bool IsMock { get; init; } = true;
}
