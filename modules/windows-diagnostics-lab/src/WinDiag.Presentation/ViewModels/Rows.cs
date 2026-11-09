using CommunityToolkit.Mvvm.ComponentModel;
using WinDiag.Core.Events;

namespace WinDiag.Presentation.ViewModels;

public enum StatusLevel
{
    Neutral,
    Good,
    Warning,
    Critical,
}

public sealed record NameValueRow(string Name, string Value, string? Note = null);

public sealed record EventRow(
    long Id, string Time, string Severity, DiagSeverity Level, string Category, string Title, string Source, string Provider, int EventId, string Channel,
    bool IsNew, bool IsVirtual, string GroupKey, DateTimeOffset TimeValue)
{
    public string Origin => IsVirtual ? "가상" : Source switch { EventSources.Live => "실시간", EventSources.History => "과거 조회", _ => Source };

    public static EventRow From(StoredEvent e, long acknowledgedId) => new(
        e.Id, Fmt.Time(e.TimeCreated), Fmt.Severity(e.Severity), e.Severity, EventCategories.Name(e.Category), e.Title, e.Source, e.Provider, e.EventId,
        e.Channel, acknowledgedId > 0 && e.Id > acknowledgedId, e.IsVirtual, e.GroupKey, e.TimeCreated);
}

public sealed record GroupRow(
    string GroupKey, string Title, string Category, string Severity, DiagSeverity Level, long Count, string First, string Last, int Days, bool IsNewType,
    long LatestId);

public sealed record ChannelRow(string Name, string Status, string Detail, string LastScan, string Oldest, StatusLevel Level);

public sealed record CrashRow(string Id, string Time, string Kind, string CrashTimeSource, string Boot, string Dump, string Before, bool IsVirtual);

public sealed record FindingRow(
    string Id, string Tier, string Title, string Detail, string Severity, DiagSeverity Level, IReadOnlyList<string> Evidence, IReadOnlyList<string> Verification,
    string Caution, string Strength, long? FirstEventId)
{
    public bool HasVerification => Verification.Count > 0;
    public bool HasCaution => Caution.Length > 0;
    public bool HasStrength => Strength.Length > 0;
}

public sealed record RepeatRow(string Title, string Category, long Count, int Days, string PerDay, string First, string Last, string Trend, bool IsNew, long LatestId, string Severity);

public sealed record TimelineRow(
    string Day, bool IsFirstOfDay, string Time, string Kind, string Source, string Title, string Detail, string Severity, DiagSeverity Level, int Count,
    bool IsUserInput, bool IsVirtual, bool IsApproximate, long? EventRowId, string? IncidentId, long? UserRecordId, DateTimeOffset TimeValue)
{
    public string CountText => Count > 1 ? $"×{Count}" : "";
    public string Flags => string.Join(" ", new[] { IsUserInput ? "사용자 입력" : null, IsVirtual ? "가상" : null, IsApproximate ? "시각 근사" : null }.Where(f => f is not null));
}

public sealed record CapabilityRow(string Name, string Source, string Status, string Elevation, string Detail);

public sealed record MetricStatsRow(string Metric, string BeforeMean, string BeforeMax, string AfterMean, string AfterMax, string AtEvent);

public sealed record RateRow(string Name, int BeforeCount, int AfterCount, string BeforePerDay, string AfterPerDay, string Change, string PValue, string Assessment, StatusLevel Level);

public sealed record SensorCompareRow(string Metric, string BeforeMean, string AfterMean, string BeforeMax, string AfterMax, string Samples);

public sealed record ConfigRow(string Area, string Item, string Before, string After, string Kind);

public sealed record BaselineRow(long Id, string Name, string Time, string Description, DateTimeOffset TimeValue, long? UserRecordId);

public sealed record UserRecordRow(long Id, string Time, string Kind, string Title, string Values, DateTimeOffset TimeValue);

public sealed record SnapshotRow(long Id, string FirstSeen, string LastConfirmed, string Source, string Summary, bool IsMock);

public sealed record NotificationRow(string Time, string Title, string Message, DiagSeverity Level, long EventId);

public sealed record Option<T>(string Name, T Value)
{
    public override string ToString() => Name;
}

/// <summary>A check box in a filter list.</summary>
public sealed partial class Selectable<T>(T value, string name, bool isChecked) : ObservableObject
{
    public T Value { get; } = value;
    public string Name { get; } = name;

    [ObservableProperty] public partial bool IsChecked { get; set; } = isChecked;
}
