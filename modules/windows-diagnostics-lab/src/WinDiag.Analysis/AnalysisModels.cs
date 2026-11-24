using WinDiag.Core.Crash;
using WinDiag.Core.Events;
using WinDiag.Storage;

namespace WinDiag.Analysis;

public sealed record AnalysisRange(DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Length => To - From;
    public double Days => Math.Max(Length.TotalDays, 1.0 / 24);

    public static AnalysisRange LastDays(DateTimeOffset now, int days) => new(now.AddDays(-days), now);

    public bool Contains(DateTimeOffset time) => time >= From && time <= To;
}

/// <summary>A group of the same problem that repeats in the range.</summary>
public sealed record RepeatPattern(
    EventGroupRow Group,
    double PerDay,
    bool IsNewInRange,
    bool IsNewSinceAcknowledged,
    int FirstHalf,
    int SecondHalf,
    string Trend);

/// <summary>Where a timeline item comes from (shown next to it: measured, recorded by Windows, entered by the user...).</summary>
public enum TimelineSource
{
    WindowsEvent,
    SnapshotChange,
    UserRecord,
    Baseline,
    CrashIncident,
    AppRun,
    Gap,
    TimeAnomaly,
}

public sealed record TimelineItem
{
    public required DateTimeOffset Time { get; init; }
    public DateTimeOffset? End { get; init; }

    /// <summary>True when only a time window is known (for example a change found by comparing two snapshots).</summary>
    public bool TimeIsApproximate { get; init; }

    public required TimelineKind Kind { get; init; }
    public required TimelineSource Source { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public DiagSeverity Severity { get; init; }
    public string? Category { get; init; }
    public long? EventRowId { get; init; }
    public string? GroupKey { get; init; }
    public string? IncidentId { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsUserInput { get; init; }
    public int Count { get; init; } = 1;

    public string SourceLabel => Source switch
    {
        TimelineSource.WindowsEvent => "Windows 이벤트 기록",
        TimelineSource.SnapshotChange => "구성 스냅샷 비교 (시각은 구간)",
        TimelineSource.UserRecord => "사용자 입력 (실측 아님)",
        TimelineSource.Baseline => "사용자 지정 기준선",
        TimelineSource.CrashIncident => "이벤트 묶음 분석",
        TimelineSource.AppRun => "앱 기록 구간",
        TimelineSource.Gap => "수집 누락·제한",
        TimelineSource.TimeAnomaly => "시간 순서 검사",
        _ => Source.ToString(),
    };
}

public sealed record MetricWindowStats(
    string Metric,
    string Name,
    string Unit,
    double? BeforeMean,
    double? BeforeMax,
    double? AfterMean,
    double? AfterMax,
    double? AtEvent,
    int BeforeCount,
    int AfterCount);

/// <summary>Sensor data around one event, with an explicit note when the app was not recording at that time.</summary>
public sealed record EventSensorContext(
    DateTimeOffset EventTime,
    TimeSpan Window,
    SensorSeries Series,
    IReadOnlyList<MetricWindowStats> Stats,
    bool HasCoverage,
    bool IsMock,
    string CoverageNote);

public enum CrashKind
{
    BugCheck,
    NoBugCheckInfo,
    PowerButton,
}

/// <summary>An unexpected restart assembled from the events Windows writes at the next boot.</summary>
public sealed record CrashIncident
{
    public required string Id { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public DateTimeOffset? BootTime { get; init; }
    public DateTimeOffset? EstimatedCrashTime { get; init; }
    public required string CrashTimeSource { get; init; }

    /// <summary>The last moment the system was known to be running (last sensor sample or last recorded event).</summary>
    public DateTimeOffset? LastKnownAlive { get; init; }

    public string? LastKnownAliveSource { get; init; }
    public required CrashKind Kind { get; init; }
    public uint? BugCheckCode { get; init; }
    public IReadOnlyList<string> BugCheckParameters { get; init; } = [];
    public string? DumpPathRecorded { get; init; }
    public DumpFileInfo? Dump { get; init; }
    public IReadOnlyList<StoredEvent> Evidence { get; init; } = [];
    public IReadOnlyList<StoredEvent> EventsBefore { get; init; } = [];
    public EventSensorContext? Sensors { get; init; }
    public int SameCodeCount { get; init; }
    public bool IsVirtual { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    public DateTimeOffset ReferenceTime => EstimatedCrashTime ?? LastKnownAlive ?? BootTime ?? DetectedAt;

    public string KindText => Kind switch
    {
        CrashKind.BugCheck => BugCheckCode is uint code ? $"BugCheck {BugCheckCatalog.Describe(code)}" : "BugCheck",
        CrashKind.PowerButton => "전원 버튼 조작 기록이 있는 비정상 종료",
        _ => "BugCheck 정보 없는 비정상 종료",
    };
}

public enum ChangeKind
{
    Changed,
    Added,
    Removed,
}

public sealed record ConfigChange(string Area, string Item, string? Before, string? After, ChangeKind Kind)
{
    public string Describe() => Kind switch
    {
        ChangeKind.Added => $"{Area}: {Item} 추가 ({After})",
        ChangeKind.Removed => $"{Area}: {Item} 제거 ({Before})",
        _ => $"{Area}: {Item} {Before} → {After}",
    };
}

/// <summary>Configuration differences between two consecutive snapshots; the change happened somewhere in the window.</summary>
public sealed record DetectedChange(
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    long BeforeSnapshotId,
    long AfterSnapshotId,
    IReadOnlyList<ConfigChange> Changes,
    bool IsMock);

public sealed record RateComparison(
    string Key,
    string Name,
    int BeforeCount,
    int AfterCount,
    double BeforePerDay,
    double AfterPerDay,
    double? Ratio,
    double? PValue,
    string Assessment);

public sealed record SensorComparison(string Metric, string Name, string Unit, MetricAggregate Before, MetricAggregate After);

public sealed record GroupChange(string GroupKey, string Title, string Category, int BeforeCount, int AfterCount);

/// <summary>Before/after comparison around a baseline (a change the user marked).</summary>
public sealed record BaselineComparison
{
    public required string BaselineName { get; init; }
    public required DateTimeOffset BaselineTime { get; init; }
    public required AnalysisRange Before { get; init; }
    public required AnalysisRange After { get; init; }
    public double BeforeDays { get; init; }
    public double AfterDays { get; init; }
    public IReadOnlyList<RateComparison> Categories { get; init; } = [];
    public IReadOnlyList<RateComparison> Severities { get; init; } = [];
    public RateComparison? Crashes { get; init; }
    public IReadOnlyList<GroupChange> NewGroups { get; init; } = [];
    public IReadOnlyList<GroupChange> GoneGroups { get; init; } = [];
    public IReadOnlyList<GroupChange> ChangedGroups { get; init; } = [];
    public IReadOnlyList<SensorComparison> Sensors { get; init; } = [];
    public IReadOnlyList<ConfigChange> ConfigChanges { get; init; } = [];
    public IReadOnlyList<string> OtherChanges { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public bool IncludesVirtual { get; init; }
}

public enum FindingTier
{
    Fact,
    Association,
    Hypothesis,
}

public sealed record FindingEvidence(string Text, long? EventRowId = null, string? GroupKey = null, DateTimeOffset? Time = null);

public sealed record Finding
{
    public required string Id { get; init; }
    public required FindingTier Tier { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public DiagSeverity Severity { get; init; }
    public IReadOnlyList<FindingEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<string> Verification { get; init; } = [];
    public string? Caution { get; init; }

    /// <summary>For hypotheses: how much evidence supports it ("약함", "보통", "강함"). Never "확정".</summary>
    public string? Strength { get; init; }

    public double Score { get; init; }

    public string TierName => Tier switch
    {
        FindingTier.Fact => "확인된 사실",
        FindingTier.Association => "관측된 연관성",
        _ => "원인 후보 (추가 검증 필요)",
    };
}

public sealed record CoverageSummary(
    IReadOnlyList<ChannelState> Channels,
    IReadOnlyList<(DateTimeOffset From, DateTimeOffset To, long Samples, bool IsMock)> SensorRuns,
    double SensorCoverageHours,
    int GapCount);

public sealed record DiagnosticReport
{
    public required AnalysisRange Range { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public IReadOnlyList<Finding> Facts { get; init; } = [];
    public IReadOnlyList<Finding> Associations { get; init; } = [];
    public IReadOnlyList<Finding> Hypotheses { get; init; } = [];
    public IReadOnlyList<RepeatPattern> Repeats { get; init; } = [];
    public IReadOnlyList<CrashIncident> Crashes { get; init; } = [];
    public IReadOnlyList<DetectedChange> Changes { get; init; } = [];
    public IReadOnlyList<CollectionGap> Gaps { get; init; } = [];
    public required CoverageSummary Coverage { get; init; }
    public bool IncludesVirtual { get; init; }
    public bool IncludesMockSensors { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public int EventCount { get; init; }
}
