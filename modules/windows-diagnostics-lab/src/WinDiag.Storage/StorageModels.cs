using WinDiag.Core.Events;

namespace WinDiag.Storage;

/// <summary>An event ready to be stored: already classified, and masked unless masking was switched off.</summary>
public sealed record NewEvent(RawEvent Event, EventClassification Classification, string Source, bool IsMasked);

public sealed record InsertResult(int Inserted, int Duplicates, IReadOnlyList<long> NewIds)
{
    public static InsertResult Empty { get; } = new(0, 0, []);
}

/// <summary>Filter for event searches. Every criterion is optional.</summary>
public sealed record EventQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public IReadOnlyList<string>? Categories { get; init; }
    public DiagSeverity? MinSeverity { get; init; }
    public IReadOnlyList<string>? Providers { get; init; }
    public IReadOnlyList<string>? Channels { get; init; }
    public int? EventId { get; init; }

    /// <summary>Case-insensitive text found in the title, message, provider or signature.</summary>
    public string? Text { get; init; }

    public string? GroupKey { get; init; }

    /// <summary>Only events stored after this row id (new since the user last confirmed).</summary>
    public long? AfterId { get; init; }

    public bool IncludeVirtual { get; init; } = true;
    public int Limit { get; init; } = 500;
    public int Offset { get; init; }
    public bool Descending { get; init; } = true;
}

/// <summary>Repeated events with the same signature, summarised over the queried range.</summary>
public sealed record EventGroupRow(
    string GroupKey,
    string Category,
    string Provider,
    int EventId,
    string Title,
    string Signature,
    DiagSeverity MaxSeverity,
    long Count,
    DateTimeOffset First,
    DateTimeOffset Last,
    long LatestEventId,
    DateTimeOffset FirstEver,
    int DistinctDays);

/// <summary>A light event reference for analysis (no message or data).</summary>
public sealed record EventPoint(
    long Id,
    long TimeMs,
    string Category,
    DiagSeverity Severity,
    string GroupKey,
    string Provider,
    int EventId,
    string Channel,
    long RecordId,
    bool IsVirtual)
{
    public DateTimeOffset Time => DateTimeOffset.FromUnixTimeMilliseconds(TimeMs);
}

public sealed record SensorPoint(long TimeMs, double? CpuUtil, long? RamUsed, long? RamTotal)
{
    public double? RamUsedPercent => RamUsed is long u && RamTotal is long t && t > 0 ? 100.0 * u / t : null;
}

public sealed record GpuSensorPoint(
    long TimeMs, int GpuIndex, double? Util, double? MemUtil, double? TempC, double? PowerW, double? PowerLimitW,
    long? VramUsed, long? VramTotal, double? ClockGfxMhz, double? ClockMemMhz, double? FanPct, long? Throttle);

public sealed record SensorSeries(IReadOnlyList<SensorPoint> System, IReadOnlyList<GpuSensorPoint> Gpu, long BucketMs)
{
    public static SensorSeries Empty { get; } = new([], [], 0);

    public bool IsDownsampled => BucketMs > 0;
    public bool IsEmpty => System.Count == 0 && Gpu.Count == 0;
}

/// <summary>Full-resolution aggregate of one metric over a range.</summary>
public sealed record MetricAggregate(string Metric, long Count, double? Mean, double? Min, double? Max);

/// <summary>Flags stored with each sensor sample.</summary>
[Flags]
public enum SensorFlags
{
    None = 0,
    Mock = 1,
    SensorIssue = 2,
    GpuUnavailable = 4,
}

public sealed record StoredSnapshot(long Id, DateTimeOffset FirstSeen, DateTimeOffset LastConfirmed, string ContentHash, string Source, bool IsMock, string Json);

public sealed record SnapshotSaveResult(long Id, bool IsNew, StoredSnapshot? Previous);

public sealed record StoredComparison(long Id, long? BaselineId, DateTimeOffset Created, string Title, string ResultJson);

public sealed record DatabaseStats(
    string Path,
    long FileBytes,
    long WalBytes,
    long Events,
    long VirtualEvents,
    long SensorSamples,
    long Snapshots,
    long Runs,
    long Gaps,
    DateTimeOffset? OldestEvent,
    DateTimeOffset? NewestEvent,
    DateTimeOffset? OldestSensor,
    DateTimeOffset? NewestSensor);

public sealed record RetentionResult(long EventsDeleted, long SensorRowsDeleted, IReadOnlyList<string> Notes);

public sealed record BackupValidation(bool IsValid, string Message, int SchemaVersion, long Events, DateTimeOffset? Oldest, DateTimeOffset? Newest);
