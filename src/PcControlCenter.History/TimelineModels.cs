namespace PcControlCenter.History;

public enum TimelineSeverity
{
    Info,
    Warning,
    Error,
    Critical,
}

public sealed record TimelinePoint(DateTimeOffset Time, double Value);

/// <summary>One measured quantity from one module's database. Points are the stored values (no interpolation).</summary>
public sealed record TimelineSeries(string Key, string Label, string Unit, string Source, IReadOnlyList<TimelinePoint> Points, bool IsMock, TimeSpan ExpectedInterval);

/// <summary>A time range a module recorded as not observed (sleep, sensor loss, app not running, clock jump ...).</summary>
public sealed record TimelineGap(DateTimeOffset From, DateTimeOffset To, string Source, string Kind, string Detail);

/// <summary>Something that happened at a time (an event, a session, a task). Shown side by side; no cause is inferred.</summary>
public sealed record TimelineEvent(DateTimeOffset Time, DateTimeOffset? End, string Source, string Kind, string Title, string? Detail, TimelineSeverity Severity,
    bool IsTestData);

/// <summary>What one source contributed to the requested range, and what it could not.</summary>
public sealed record SourceCoverage(string Source, string Database, bool Available, string? Problem, int Points, int Events, DateTimeOffset? First,
    DateTimeOffset? Last, TimeSpan RecordedGaps);

public sealed record TimelineResult(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<TimelineSeries> Series,
    IReadOnlyList<TimelineGap> Gaps,
    IReadOnlyList<TimelineEvent> Events,
    IReadOnlyList<SourceCoverage> Coverage);
