using System.Runtime.CompilerServices;
using Iron.ProcessIdentity;
using Iron.Stage2;

namespace Iron.App.Presentation;

/// <summary>Where the readings of the window come from: the Stage 1 mock values or Stage 2's real readers.</summary>
public interface ITelemetrySource
{
    /// <summary>The reading for the UI timer (never waits for a sensor).</summary>
    TelemetryFrame Next(DateTimeOffset now);

    bool IsMock { get; }

    /// <summary>One line per source for the title-bar tooltip ("NVML (GPU): 실측", "HWiNFO REST: 미연결 …").</summary>
    string Describe();
}

/// <summary>
/// Stage 2: the newest values of the real readers (<see cref="Stage2Collector"/>) mapped to the Stage 1 frame. Nothing is
/// invented: a value no reader gives stays null (shown as N/A). FLUX PRO output is not part of Stage 2's data connection.
/// </summary>
public sealed class RealTelemetrySource(Func<DateTimeOffset, Stage2Frame> latest) : ITelemetrySource
{
    public Stage2Frame? LastFrame { get; private set; }

    public bool IsMock => false;

    public TelemetryFrame Next(DateTimeOffset now) => Map(LastFrame = latest(now));

    public string Describe() => Describe(LastFrame?.Sources ?? []);

    public static string Describe(IReadOnlyList<SourceStatus> sources) =>
        "실측 데이터 (Stage 2)\n" + string.Join("\n", sources.Select(s => s.Name + ": " + State(s.State) + (s.Detail is { Length: > 0 } d ? " — " + d : "")));

    private static string State(SourceState s) => s switch
    {
        SourceState.Live => "실측",
        SourceState.Connecting => "연결 중",
        SourceState.Stale => "값 지연(표시 안 함)",
        SourceState.Offline => "미연결",
        SourceState.Unavailable => "사용 불가",
        _ => "꺼짐",
    };

    public static TelemetryFrame Map(Stage2Frame f) => new()
    {
        Timestamp = f.Timestamp,
        IsMock = false,
        Phase = f.Phase switch
        {
            Stage2Phase.Ready => SensorPhase.Ready,
            Stage2Phase.NoDevice => SensorPhase.NoDevice,
            _ => SensorPhase.Initializing,
        },
        Gpus = f.Gpus.Select(g => new GpuReading
        {
            Index = g.Index,
            Name = g.Name,
            TemperatureC = g.TemperatureC,
            PowerW = g.PowerW,
            UtilizationPercent = g.UtilizationPercent,
            VramUsedGb = g.VramUsedGb,
            VramTotalGb = g.VramTotalGb, // null stays null (N/A): no assumed card size
            ClockMhz = g.ClockMhz,
        }).ToList(),
        Cpu = new CpuReading(f.Cpu.LoadPercent, f.Cpu.TctlC, f.Cpu.PackageC, f.Cpu.CoreMaxC),
        Ram = new RamReading(f.RamUsedGb, f.RamTotalGb), // null stays null (N/A): no assumed RAM size
        Ssd = new SsdReading(f.SsdTemperatureC, f.SsdReadMBps),
        WireView = f.WireView.Connected
            ? new WireViewReading
            {
                Connected = true,
                PowerW = f.WireView.PowerW,
                VoltageV = f.WireView.VoltageV,
                ConnectorInC = f.WireView.ConnectorInC,
                ConnectorOutC = f.WireView.ConnectorOutC,
                PinCurrentsA = f.WireView.PinCurrentsA,
            }
            : WireViewReading.None,
        Flux = new FluxReading(false, false),
        // the command line is the identity module's redacted text, never the process's own
        ProcessesUnavailableReason = f.ProcessesUnavailable,
        Processes = f.Processes.Select(p => new ProcessReading(p.Pid, p.ImageName, p.Adapter, p.DedicatedGb, p.SharedGb, p.UtilizationPercent, p.Engine,
            p.CommandLine, p.IsSoftwareAdapter, p.AdapterLuid, p.StartTicks,
            [.. (p.OtherAdapters ?? []).Select(o => new AdapterUsage(o.Adapter, o.Luid, o.DedicatedGb, o.SharedGb, o.UtilizationPercent, o.Engine, o.IsSoftwareAdapter))]))
            .ToList(),
    };
}

/// <summary>
/// Stage 2 identity of a row: the native resolver's result for the PID (looked up in its last snapshot, so a reused PID is
/// never given the old identity). Until it is resolved the row shows its executable name and "식별 중".
/// </summary>
public sealed class RealProcessIdentityProvider(Func<int, ProcessIdentityInfo?> identityOf) : IProcessIdentityProvider
{
    private readonly ConditionalWeakTable<ProcessIdentityInfo, ProcessIdentityView> _views = new();
    private readonly Dictionary<(int, string), ProcessIdentityView> _pending = [];

    public ProcessIdentityView Identify(ProcessReading reading)
    {
        if (identityOf(reading.Pid) is { } info)
        {
            return _views.GetValue(info, ProcessIdentityView.From);
        }

        if (_pending.Count > 512)
        {
            _pending.Clear();
        }

        var key = (reading.Pid, reading.Name);
        if (!_pending.TryGetValue(key, out var view))
        {
            _pending[key] = view = ProcessIdentityView.From(IdentityComposer.ComposePending(reading.Pid, reading.Name));
        }

        return view;
    }
}
