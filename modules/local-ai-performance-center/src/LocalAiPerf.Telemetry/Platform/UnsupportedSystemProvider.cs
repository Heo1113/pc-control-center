using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Metrics;

namespace LocalAiPerf.Telemetry.Platform;

/// <summary>Used when no system provider exists for the OS or system sensors are switched off.</summary>
public sealed class UnsupportedSystemProvider(SensorStatus status, string reason) : ISystemSensorProvider
{
    public string Name => "없음";
    public string? CpuName => null;
    public int LogicalProcessors => Environment.ProcessorCount;

    public IReadOnlyList<SensorCapability> Capabilities { get; } =
    [
        new SensorCapability(MetricKeys.CpuUtil, "CPU 사용률", "-", status, false, reason),
        new SensorCapability(MetricKeys.RamUsedGiB, "시스템 RAM 사용량/전체", "-", status, false, reason),
    ];

    public SystemSample ReadSystem(ICollection<SensorIssue> issues) => SystemSample.Empty;

    public void Dispose()
    {
    }
}

/// <summary>Used when GPU sensors are switched off in the settings.</summary>
public sealed class DisabledGpuProvider : IGpuSensorProvider
{
    public string Name => "GPU 센서 꺼짐";
    public bool IsAvailable => false;
    public string? UnavailableReason => "설정에서 GPU 센서가 꺼져 있습니다.";
    public string? DriverVersion => null;
    public string? LibraryVersion => null;
    public IReadOnlyList<GpuInfo> Devices => [];

    public IReadOnlyList<SensorCapability> Capabilities { get; } =
        MetricCatalog.All.Where(m => m.Group == MetricGroup.Gpu && m.Origin == MetricOrigin.Measured)
            .Select(m => new SensorCapability(m.Key, m.DisplayName, "NVML", SensorStatus.Disabled, false, "설정에서 꺼짐"))
            .ToList();

    public IReadOnlyList<GpuSample> ReadGpus(ICollection<SensorIssue> issues) => [];

    public void Dispose()
    {
    }
}
