namespace LocalAiPerf.Core.Hardware;

/// <summary>Reads GPU sensors. Implementations must not throw from <see cref="ReadGpus"/>; report issues instead.</summary>
public interface IGpuSensorProvider : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    string? DriverVersion { get; }
    string? LibraryVersion { get; }
    IReadOnlyList<GpuInfo> Devices { get; }
    IReadOnlyList<SensorCapability> Capabilities { get; }
    IReadOnlyList<GpuSample> ReadGpus(ICollection<SensorIssue> issues);
}

/// <summary>Reads CPU and system memory. Implementations must not throw from <see cref="ReadSystem"/>.</summary>
public interface ISystemSensorProvider : IDisposable
{
    string Name { get; }
    string? CpuName { get; }
    int LogicalProcessors { get; }
    IReadOnlyList<SensorCapability> Capabilities { get; }
    SystemSample ReadSystem(ICollection<SensorIssue> issues);
}
