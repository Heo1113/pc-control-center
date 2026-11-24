using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Metrics;

namespace LocalAiPerf.Telemetry.Mock;

/// <summary>
/// Synthetic GPU readings for automated tests and UI checks on machines without an NVIDIA GPU.
/// Only enabled explicitly (--mock-sensors); every snapshot is flagged IsMock and the UI shows a banner.
/// </summary>
public sealed class MockGpuProvider : IGpuSensorProvider
{
    public const string MockName = "MOCK GPU (테스트용 가상 센서)";
    private const long VramTotal = 16L * 1024 * 1024 * 1024;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _start;
    private double _energyMj;
    private DateTimeOffset _lastRead;

    public MockGpuProvider(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _start = _time.GetUtcNow();
        _lastRead = _start;
        Capabilities = MetricCatalog.All.Where(m => m.Group == MetricGroup.Gpu && m.Origin == MetricOrigin.Measured)
            .Select(m => new SensorCapability(m.Key, m.DisplayName, "MOCK", SensorStatus.Ok, false, "테스트용 가상 값"))
            .ToList();
    }

    public string Name => "Mock GPU";
    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public string? DriverVersion => "mock";
    public string? LibraryVersion => "mock";
    public IReadOnlyList<GpuInfo> Devices { get; } = [new GpuInfo(0, MockName, VramTotal, 360)];
    public IReadOnlyList<SensorCapability> Capabilities { get; }

    public IReadOnlyList<GpuSample> ReadGpus(ICollection<SensorIssue> issues)
    {
        var now = _time.GetUtcNow();
        var t = (now - _start).TotalSeconds;
        var util = 55 + 40 * Math.Sin(t / 20.0);
        var power = 90 + 2.5 * util;
        _energyMj += power * Math.Max(0, (now - _lastRead).TotalSeconds) * 1000;
        _lastRead = now;
        return
        [
            new GpuSample
            {
                Index = 0,
                Name = MockName,
                UtilizationPercent = Math.Round(util, 1),
                MemoryControllerUtilizationPercent = Math.Round(util * 0.6, 1),
                TemperatureC = Math.Round(52 + 18 * Math.Sin(t / 60.0) + util * 0.1, 1),
                PowerW = Math.Round(power, 1),
                PowerLimitW = 360,
                MemoryUsedBytes = (long)(VramTotal * (0.62 + 0.05 * Math.Sin(t / 90.0))),
                MemoryTotalBytes = VramTotal,
                GraphicsClockMhz = Math.Round(2400 + 200 * Math.Sin(t / 30.0)),
                MemoryClockMhz = 15000,
                FanSpeedPercent = Math.Round(35 + 20 * Math.Sin(t / 60.0)),
                PcieTxKBps = Math.Round(2000 + 1500 * Math.Sin(t / 10.0)),
                PcieRxKBps = Math.Round(9000 + 6000 * Math.Sin(t / 12.0)),
                TotalEnergyMilliJoules = (long)_energyMj,
                ThrottleReasons = util > 90 ? (ulong)GpuThrottleReasons.SwPowerCap : 0,
            },
        ];
    }

    public void Dispose()
    {
    }
}

public sealed class MockSystemProvider : ISystemSensorProvider
{
    private const long RamTotal = 64L * 1024 * 1024 * 1024;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _start;

    public MockSystemProvider(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _start = _time.GetUtcNow();
    }

    public string Name => "Mock";
    public string? CpuName => "MOCK CPU (테스트용 가상 센서)";
    public int LogicalProcessors => 16;

    public IReadOnlyList<SensorCapability> Capabilities { get; } =
    [
        new SensorCapability(MetricKeys.CpuUtil, "CPU 사용률", "MOCK", SensorStatus.Ok, false, "테스트용 가상 값"),
        new SensorCapability(MetricKeys.RamUsedGiB, "시스템 RAM 사용량/전체", "MOCK", SensorStatus.Ok, false, "테스트용 가상 값"),
    ];

    public SystemSample ReadSystem(ICollection<SensorIssue> issues)
    {
        var t = (_time.GetUtcNow() - _start).TotalSeconds;
        return new SystemSample
        {
            CpuUtilizationPercent = Math.Round(18 + 12 * Math.Sin(t / 15.0), 1),
            MemoryUsedBytes = (long)(RamTotal * (0.45 + 0.05 * Math.Sin(t / 45.0))),
            MemoryTotalBytes = RamTotal,
        };
    }

    public void Dispose()
    {
    }
}
