using System.Globalization;

namespace WinDiag.Core.Events;

/// <summary>Windows event levels as written in the event XML (System/Level).</summary>
public enum WinEventLevel
{
    LogAlways = 0,
    Critical = 1,
    Error = 2,
    Warning = 3,
    Information = 4,
    Verbose = 5,
}

/// <summary>
/// Diagnostic importance assigned by this app. Kept separate from the Windows level, which is always shown as recorded.
/// </summary>
public enum DiagSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Critical = 3,
}

public static class LevelText
{
    public static string Windows(int level) => level switch
    {
        0 => "항상 기록(0)",
        1 => "위험(1)",
        2 => "오류(2)",
        3 => "경고(3)",
        4 => "정보(4)",
        5 => "자세한 정보(5)",
        _ => $"수준 {level}",
    };

    public static string Severity(DiagSeverity severity) => severity switch
    {
        DiagSeverity.Info => "정보",
        DiagSeverity.Warning => "경고",
        DiagSeverity.Error => "오류",
        DiagSeverity.Critical => "심각",
        _ => severity.ToString(),
    };

    public static DiagSeverity FromWindowsLevel(int level) => level switch
    {
        1 => DiagSeverity.Critical,
        2 => DiagSeverity.Error,
        3 => DiagSeverity.Warning,
        _ => DiagSeverity.Info,
    };
}

/// <summary>One EventData/UserData value. Name is null for classic events whose data items are unnamed.</summary>
public sealed record EventDataItem(string? Name, string Value);

/// <summary>Read access to the parts of an event that classification and interpretation use.</summary>
public interface IEventFields
{
    string Channel { get; }
    string Provider { get; }
    string? EventSourceName { get; }
    int EventId { get; }
    int Level { get; }
    DateTimeOffset TimeCreated { get; }
    IReadOnlyList<EventDataItem> Data { get; }
    string? Message { get; }
}

public static class EventFieldExtensions
{
    /// <summary>Value of a named data item (case-insensitive), or null.</summary>
    public static string? Get(this IEventFields e, string name)
    {
        foreach (var item in e.Data)
        {
            if (item.Name is not null && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return item.Value;
            }
        }

        return null;
    }

    /// <summary>Value of the data item at a position, or null.</summary>
    public static string? GetAt(this IEventFields e, int index) => index >= 0 && index < e.Data.Count ? e.Data[index].Value : null;

    /// <summary>
    /// Field spec "Name" or "Name|3": the named item when present, otherwise the item at the position
    /// (classic events often carry unnamed data in a fixed order).
    /// </summary>
    public static string? Field(this IEventFields e, string spec)
    {
        var bar = spec.IndexOf('|', StringComparison.Ordinal);
        if (bar < 0)
        {
            return e.Get(spec);
        }

        var name = spec[..bar];
        var named = e.Get(name);
        if (named is not null)
        {
            return named;
        }

        // Positional fallback only for unnamed data: a named event that lacks the field must not borrow another field.
        if (e.Data.Count > 0 && e.Data.All(d => d.Name is null) &&
            int.TryParse(spec[(bar + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return e.GetAt(index);
        }

        return null;
    }

    public static bool IsProvider(this IEventFields e, string provider)
        => string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase)
           || (e.EventSourceName is not null && string.Equals(e.EventSourceName, provider, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An event as read from a Windows event log or a test fixture, before classification and masking.</summary>
public sealed record RawEvent : IEventFields
{
    public required string Channel { get; init; }
    public required string Provider { get; init; }
    public string? ProviderGuid { get; init; }
    public string? EventSourceName { get; init; }
    public required int EventId { get; init; }
    public int? Qualifiers { get; init; }
    public int? Version { get; init; }
    public int Level { get; init; }
    public int? Task { get; init; }
    public int? Opcode { get; init; }
    public long? Keywords { get; init; }
    public required long RecordId { get; init; }
    public required DateTimeOffset TimeCreated { get; init; }
    public int? ProcessId { get; init; }
    public int? ThreadId { get; init; }

    /// <summary>Computer name from the event. Used only to mask that name; never stored.</summary>
    public string? Computer { get; init; }

    /// <summary>User SID from the event. Never stored.</summary>
    public string? UserSid { get; init; }

    public IReadOnlyList<EventDataItem> Data { get; init; } = [];

    /// <summary>Rendered message text (provider message table), when it could be rendered.</summary>
    public string? Message { get; init; }

    /// <summary>Why the message could not be rendered (for example the provider's message file is missing).</summary>
    public string? MessageError { get; init; }
}

/// <summary>Where an event row came from.</summary>
public static class EventSources
{
    /// <summary>Read from the Windows event log while the app was running (incremental polling).</summary>
    public const string Live = "live";

    /// <summary>Read from the Windows event log by a history import (events older than the app's first scan).</summary>
    public const string History = "history";

    /// <summary>Imported from an XML file (for example wevtutil output).</summary>
    public const string Import = "import";

    /// <summary>Synthetic test or demo data. Always labelled as virtual in the UI and in exports.</summary>
    public const string Virtual = "virtual";
}

/// <summary>An event as stored by this app: classified and (by default) masked. The original XML is not stored.</summary>
public sealed record StoredEvent : IEventFields
{
    public long Id { get; init; }
    public required string Channel { get; init; }
    public required string Provider { get; init; }
    public string? EventSourceName { get; init; }
    public required int EventId { get; init; }
    public int? Qualifiers { get; init; }
    public int Level { get; init; }
    public required long RecordId { get; init; }
    public required DateTimeOffset TimeCreated { get; init; }
    public DateTimeOffset Ingested { get; init; }
    public required string Category { get; init; }
    public DiagSeverity Severity { get; init; }
    public required string GroupKey { get; init; }
    public required string Signature { get; init; }
    public required string Title { get; init; }
    public string? Message { get; init; }
    public string? MessageError { get; init; }
    public IReadOnlyList<EventDataItem> Data { get; init; } = [];
    public int? ProcessId { get; init; }
    public int? ThreadId { get; init; }
    public string Source { get; init; } = EventSources.Live;
    public bool IsMasked { get; init; } = true;

    public bool IsVirtual => Source == EventSources.Virtual;
}
