using GpuProfit.Core.Domain;

namespace GpuProfit.Storage;

public sealed record AppRun
{
    public long Id { get; init; }
    public DateTimeOffset Started { get; init; }
    public DateTimeOffset? Ended { get; init; }
    public string? EndReason { get; init; }
    public DateTimeOffset? LastHeartbeat { get; init; }
    public string AppVersion { get; init; } = "";
    public bool IsMock { get; init; }
    public int SampleIntervalMs { get; init; }
}

public static class RunEndReasons
{
    public const string Normal = "normal";
    public const string RecoveredAfterCrash = "recovered_after_crash";
    public const string WindowsSessionEnd = "windows_session_end";
    public const string Restore = "restore";
}

public static class GapKinds
{
    public const string SensorUnavailable = "sensor_unavailable";
    public const string SampleGap = "sample_gap";
    public const string ClockJump = "clock_jump";
    public const string Suspend = "suspend";
    public const string AppEndedUncleanly = "app_unclean_end";
    public const string StorageError = "storage_error";

    public static string ToKorean(string kind) => kind switch
    {
        SensorUnavailable => "GPU 센서 사용 불가",
        SampleGap => "샘플 공백",
        ClockJump => "시스템 시각 변경",
        Suspend => "절전·최대 절전",
        AppEndedUncleanly => "앱 비정상 종료",
        StorageError => "저장 오류",
        _ => kind,
    };
}

/// <summary>A recorded period without data (or a known reason for missing data).</summary>
public sealed record CollectionGap
{
    public long Id { get; init; }
    public required string Scope { get; init; }
    public required string Kind { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public DateTimeOffset Detected { get; init; }
    public string? Detail { get; init; }
}

public sealed record DeviceRecord(long Id, int GpuIndex, string Name, bool IsMock, long? MemoryTotalBytes, double? PowerLimitW, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

/// <summary>Time-bucketed GPU values for history charts (averages, with min/max power).</summary>
public sealed record GpuSeriesPoint(DateTimeOffset Time, int GpuIndex, double? PowerW, double? PowerMinW, double? PowerMaxW, double? Utilization,
    double? TemperatureC, double? VramUsedGiB, double? ClockGraphicsMhz, double? ClockMemoryMhz, int Samples);

public sealed record SystemSeriesPoint(DateTimeOffset Time, double? CpuPercent, double? RamUsedGiB, int Samples);

public sealed record DatabaseStats(string Path, long FileBytes, long WalBytes, long Runs, long Devices, long GpuSamples, long SystemSamples,
    long Gaps, long Workloads, long Sessions, long RevenueEntries, long CostEntries, long Results, long Scenarios,
    DateTimeOffset? FirstSample, DateTimeOffset? LastSample);

public sealed record BackupValidation(bool IsValid, string Message, int SchemaVersion, long Sessions, long GpuSamples);

public sealed record RetentionResult(long GpuSamplesRemoved, long SystemSamplesRemoved);

public sealed record SessionQuery
{
    public string? Text { get; init; }
    public long? WorkloadId { get; init; }
    public WorkloadType? Type { get; init; }
    public SessionState? State { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public bool? IsMock { get; init; }
    public int Limit { get; init; } = 1000;
}

/// <summary>The key figures of a saved calculation (the full result is recomputed from the stored inputs when needed).</summary>
public sealed record StoredResultSummary
{
    public double RecordedHours { get; init; }
    public double BasisHours { get; init; }
    public double? TotalKwh { get; init; }
    public double GpuMeasuredKwh { get; init; }
    public double? Coverage { get; init; }
    public double EstimatedShare { get; init; }
    public decimal? ElectricityCost { get; init; }
    public decimal? RateKrwPerKwh { get; init; }
    public decimal? RealizedRevenue { get; init; }
    public decimal? RealizedNet { get; init; }
    public decimal? UnconfirmedRevenue { get; init; }
    public decimal? UnconfirmedNet { get; init; }
    public decimal? ExpectedRevenue { get; init; }
    public decimal? ExpectedNet { get; init; }
    public decimal? TotalCost { get; init; }
    public decimal? BreakevenRevenue { get; init; }
    public decimal? BreakevenRate { get; init; }
    public string EnergyBasis { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record StoredResult
{
    public long Id { get; init; }
    public long SessionId { get; init; }
    public DateTimeOffset Computed { get; init; }
    public int EngineVersion { get; init; }
    public required string InputsHash { get; init; }
    public required string Reason { get; init; }
    public required StoredResultSummary Summary { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Conditions { get; init; } = [];
}

public sealed record ScenarioRecord
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public int Kind { get; init; }
    public required string ParamsJson { get; init; }
    public long? SourceSessionId { get; init; }

    /// <summary>Inputs hash of the source session when the parameters were taken from it (detects later changes).</summary>
    public string? SourceHash { get; init; }

    public string? Notes { get; init; }
    public string? LastResultJson { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; init; }
}
