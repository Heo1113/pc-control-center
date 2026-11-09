// PC Control Center integration (not in the original project A).
// Lets several consumers (A's TelemetryCollector, B's TelemetryRecorder, C's SensorRecorder) use one sensor reader:
// a HardwareMonitor created with HardwareMonitor.Shared(source) never opens NVML or the Windows counters itself; its
// Sample() hands out the snapshot the shared reader measured, with that reading's original timestamp and issues.
using LocalAiPerf.Core.Hardware;

namespace LocalAiPerf.Telemetry;

/// <summary>One measurement of the shared reader. <see cref="Sequence"/> increases by one per real reading.</summary>
public sealed record SharedHardwareSample(long Sequence, HardwareSnapshot Snapshot, bool IsStale);

/// <summary>A single hardware reader shared by several <see cref="HardwareMonitor"/> views.</summary>
public interface IHardwareSnapshotSource
{
    bool IsMock { get; }
    HardwareInfo Info { get; }

    string GpuProviderName { get; }
    bool GpuAvailable { get; }
    string? GpuUnavailableReason { get; }
    string? GpuDriverVersion { get; }
    string? GpuLibraryVersion { get; }
    IReadOnlyList<GpuInfo> GpuDevices { get; }
    IReadOnlyList<SensorCapability> GpuCapabilities { get; }

    string SystemProviderName { get; }
    string? CpuName { get; }
    int LogicalProcessors { get; }
    IReadOnlyList<SensorCapability> SystemCapabilities { get; }

    /// <summary>The newest reading, or null before the first one.</summary>
    SharedHardwareSample? Latest { get; }

    /// <summary>
    /// Returns the newest reading when its sequence is greater than <paramref name="afterSequence"/>; otherwise waits a
    /// bounded time for the next reading. When none arrives in time, returns a stale placeholder (timestamp = now, no
    /// values, one issue that says why) instead of repeating an old value. Never throws.
    /// </summary>
    SharedHardwareSample NextAfter(long afterSequence);
}

/// <summary>GPU provider facade over the shared reader: metadata of the real provider, values from the shared readings.</summary>
internal sealed class SharedGpuProviderView(IHardwareSnapshotSource source) : IGpuSensorProvider
{
    public string Name => source.GpuProviderName;
    public bool IsAvailable => source.GpuAvailable;
    public string? UnavailableReason => source.GpuUnavailableReason;
    public string? DriverVersion => source.GpuDriverVersion;
    public string? LibraryVersion => source.GpuLibraryVersion;
    public IReadOnlyList<GpuInfo> Devices => source.GpuDevices;
    public IReadOnlyList<SensorCapability> Capabilities => source.GpuCapabilities;

    /// <summary>Returns the GPUs of the newest shared reading; it never reads the device itself.</summary>
    public IReadOnlyList<GpuSample> ReadGpus(ICollection<SensorIssue> issues) => source.Latest?.Snapshot.Gpus ?? [];

    /// <summary>The shared reader owns the device handles.</summary>
    public void Dispose()
    {
    }
}

/// <summary>System provider facade over the shared reader.</summary>
internal sealed class SharedSystemProviderView(IHardwareSnapshotSource source) : ISystemSensorProvider
{
    public string Name => source.SystemProviderName;
    public string? CpuName => source.CpuName;
    public int LogicalProcessors => source.LogicalProcessors;
    public IReadOnlyList<SensorCapability> Capabilities => source.SystemCapabilities;

    public SystemSample ReadSystem(ICollection<SensorIssue> issues) => source.Latest?.Snapshot.System ?? SystemSample.Empty;

    public void Dispose()
    {
    }
}
