namespace LocalAiPerf.Core.Hardware;

/// <summary>Why a sensor value is or is not available. A missing value is shown as N/A, never estimated.</summary>
public enum SensorStatus
{
    Ok,
    NotSupported,
    Unavailable,
    RequiresElevation,
    NotImplemented,
    Disabled,
    Error,
}

public static class SensorStatusText
{
    public static string ToKorean(SensorStatus status) => status switch
    {
        SensorStatus.Ok => "정상",
        SensorStatus.NotSupported => "장치/드라이버 미지원",
        SensorStatus.Unavailable => "사용 불가",
        SensorStatus.RequiresElevation => "관리자 권한 필요",
        SensorStatus.NotImplemented => "이 버전에서 수집하지 않음",
        SensorStatus.Disabled => "설정에서 꺼짐",
        SensorStatus.Error => "오류",
        _ => status.ToString(),
    };
}

/// <summary>One row of the sensor support table shown on the Hardware page.</summary>
public sealed record SensorCapability(
    string MetricKey,
    string DisplayName,
    string Source,
    SensorStatus Status,
    bool RequiresElevation,
    string? Detail = null);

/// <summary>A problem observed while sampling (recorded as a collection issue, not as a value).</summary>
public sealed record SensorIssue(string Source, string Scope, SensorStatus Status, string Message);

public sealed record GpuSample
{
    public required int Index { get; init; }
    public string? Name { get; init; }
    public double? UtilizationPercent { get; init; }
    public double? MemoryControllerUtilizationPercent { get; init; }
    public double? TemperatureC { get; init; }
    public double? PowerW { get; init; }
    public double? PowerLimitW { get; init; }
    public long? MemoryUsedBytes { get; init; }
    public long? MemoryTotalBytes { get; init; }
    public double? GraphicsClockMhz { get; init; }
    public double? MemoryClockMhz { get; init; }
    public double? FanSpeedPercent { get; init; }
    public double? PcieTxKBps { get; init; }
    public double? PcieRxKBps { get; init; }

    /// <summary>NVML total energy counter (mJ since the driver was loaded). Differences give measured energy.</summary>
    public long? TotalEnergyMilliJoules { get; init; }

    /// <summary>NVML clocks event (throttle) reason bit mask.</summary>
    public ulong? ThrottleReasons { get; init; }

    public double? MemoryUsedPercent => MemoryUsedBytes is long used && MemoryTotalBytes is long total && total > 0
        ? 100.0 * used / total
        : null;
}

public sealed record SystemSample
{
    public static SystemSample Empty { get; } = new();

    public double? CpuUtilizationPercent { get; init; }
    public long? MemoryUsedBytes { get; init; }
    public long? MemoryTotalBytes { get; init; }

    public double? MemoryUsedPercent => MemoryUsedBytes is long used && MemoryTotalBytes is long total && total > 0
        ? 100.0 * used / total
        : null;
}

public sealed record HardwareSnapshot
{
    public required DateTimeOffset Timestamp { get; init; }
    public SystemSample System { get; init; } = SystemSample.Empty;
    public IReadOnlyList<GpuSample> Gpus { get; init; } = [];
    public IReadOnlyList<SensorIssue> Issues { get; init; } = [];

    /// <summary>True when the values come from the mock provider (tests and demos only).</summary>
    public bool IsMock { get; init; }

    public GpuSample? Gpu(int index) => Gpus.FirstOrDefault(g => g.Index == index);
}

public sealed record GpuInfo(int Index, string? Name, long? MemoryTotalBytes, double? PowerLimitW);

/// <summary>Static facts about the machine, captured once per session. Contains no user or machine names.</summary>
public sealed record HardwareInfo
{
    public string? CpuName { get; init; }
    public int LogicalProcessors { get; init; }
    public long? RamTotalBytes { get; init; }
    public string OsDescription { get; init; } = "";
    public bool IsElevated { get; init; }
    public string? GpuDriverVersion { get; init; }
    public string? NvmlVersion { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public bool IsMock { get; init; }
}

/// <summary>NVML clocks event (throttle) reasons. Values from nvml.h.</summary>
[Flags]
public enum GpuThrottleReasons : ulong
{
    None = 0,
    GpuIdle = 0x1,
    ApplicationsClocksSetting = 0x2,
    SwPowerCap = 0x4,
    HwSlowdown = 0x8,
    SyncBoost = 0x10,
    SwThermalSlowdown = 0x20,
    HwThermalSlowdown = 0x40,
    HwPowerBrakeSlowdown = 0x80,
    DisplayClockSetting = 0x100,
}

public static class GpuThrottleReasonText
{
    /// <summary>Reasons that indicate a performance-limiting condition (idle and app clocks are excluded).</summary>
    public const GpuThrottleReasons Limiting = GpuThrottleReasons.SwPowerCap | GpuThrottleReasons.HwSlowdown
        | GpuThrottleReasons.SwThermalSlowdown | GpuThrottleReasons.HwThermalSlowdown | GpuThrottleReasons.HwPowerBrakeSlowdown;

    public static string Describe(ulong mask)
    {
        var reasons = (GpuThrottleReasons)mask;
        var parts = new List<string>();
        if (reasons.HasFlag(GpuThrottleReasons.SwPowerCap)) parts.Add("전력 제한(SW)");
        if (reasons.HasFlag(GpuThrottleReasons.HwSlowdown)) parts.Add("하드웨어 감속");
        if (reasons.HasFlag(GpuThrottleReasons.SwThermalSlowdown)) parts.Add("온도 제한(SW)");
        if (reasons.HasFlag(GpuThrottleReasons.HwThermalSlowdown)) parts.Add("온도 제한(HW)");
        if (reasons.HasFlag(GpuThrottleReasons.HwPowerBrakeSlowdown)) parts.Add("전원 브레이크(HW)");
        if (reasons.HasFlag(GpuThrottleReasons.GpuIdle)) parts.Add("유휴");
        if (reasons.HasFlag(GpuThrottleReasons.SyncBoost)) parts.Add("Sync Boost");
        if (reasons.HasFlag(GpuThrottleReasons.ApplicationsClocksSetting)) parts.Add("앱 클럭 설정");
        if (reasons.HasFlag(GpuThrottleReasons.DisplayClockSetting)) parts.Add("디스플레이 클럭");
        return parts.Count == 0 ? "없음" : string.Join(", ", parts);
    }
}
