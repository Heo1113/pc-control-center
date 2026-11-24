using System.Globalization;
using System.Security;
using System.Text;
using WinDiag.Core.Events;
using WinDiag.Core.Settings;

namespace WinDiag.Collection.Sources;

public enum ChannelAccess
{
    Ok,
    NotFound,
    DisabledInWindows,
    AccessDenied,
    Error,
}

/// <summary>What a channel currently holds, read before every scan.</summary>
public sealed record ChannelProbe(
    string Channel,
    ChannelAccess Access,
    string? Message,
    long? OldestRecordId = null,
    DateTimeOffset? OldestTime = null,
    long? NewestRecordId = null,
    DateTimeOffset? NewestTime = null,
    long? RecordCount = null)
{
    public bool IsEmpty => Access == ChannelAccess.Ok && NewestRecordId is null;

    public ChannelStatus ToStatus() => Access switch
    {
        ChannelAccess.Ok => ChannelStatus.Ok,
        ChannelAccess.NotFound or ChannelAccess.DisabledInWindows => ChannelStatus.NotFound,
        ChannelAccess.AccessDenied => ChannelStatus.AccessDenied,
        _ => ChannelStatus.Error,
    };
}

/// <summary>One read: events of a channel matching the filter, after a record ID and/or inside a time range.</summary>
public sealed record EventReadRequest
{
    public required string Channel { get; init; }
    public required EventFilter Filter { get; init; }
    public long? AfterRecordId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}

/// <summary>Reads Windows event logs (or a substitute in tests). Implementations report problems through the probe.</summary>
public interface IEventLogSource
{
    string Name { get; }

    /// <summary>True when every event from this source is synthetic (tests and demos); such events are stored as virtual.</summary>
    bool IsVirtual { get; }

    ChannelProbe Probe(string channel);

    /// <summary>Events in record order. May throw <see cref="UnauthorizedAccessException"/> or <see cref="EventSourceException"/>.</summary>
    IEnumerable<RawEvent> Read(EventReadRequest request, CancellationToken cancellationToken);
}

public sealed class EventSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Which events of one channel are collected: critical and error events of every provider; warnings of catalogued
/// providers whose rules ask for them (or all warnings when enabled); informational events only for catalogue rules that ask for them.
/// Excluded providers are never collected. The same filter is evaluated in memory (tests) and turned into an
/// event log query (Windows), so both paths select the same events.
/// </summary>
public sealed record EventFilter
{
    public required string Channel { get; init; }
    public bool AllWarnings { get; init; }
    public required IReadOnlySet<string> WarningProviders { get; init; }

    /// <summary>Provider to event IDs (null: every ID of that provider) whose informational events are collected.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<int>?> Informational { get; init; }

    public required IReadOnlySet<string> Excluded { get; init; }

    public static EventFilter Build(string channel, EventCatalog catalog, CollectionSettings settings)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var rules = catalog.Rules
            .Where(r => r.Channels is null || r.Channels.Any(c => comparer.Equals(c, channel)))
            .ToList();
        var warnings = new HashSet<string>(rules.Where(r => r.CollectWarnings).Select(r => r.Provider), comparer);
        var informational = new Dictionary<string, IReadOnlyList<int>?>(comparer);
        foreach (var rule in rules.Where(r => r.CollectInformational))
        {
            if (rule.EventIds is null)
            {
                informational[rule.Provider] = null;
            }
            else if (!informational.TryGetValue(rule.Provider, out var existing))
            {
                informational[rule.Provider] = rule.EventIds.ToList();
            }
            else if (existing is not null)
            {
                informational[rule.Provider] = existing.Concat(rule.EventIds).Distinct().Order().ToList();
            }
        }

        return new EventFilter
        {
            Channel = channel,
            AllWarnings = settings.CollectAllWarnings,
            WarningProviders = warnings,
            Informational = informational,
            // A name with an apostrophe cannot be written into the XPath literal; such entries are ignored.
            Excluded = new HashSet<string>(settings.ExcludedProviders.Where(p => !string.IsNullOrWhiteSpace(p) && !p.Contains('\'')).Select(p => p.Trim()), comparer),
        };
    }

    public bool Matches(RawEvent e)
    {
        if (Excluded.Contains(e.Provider))
        {
            return false;
        }

        if (e.Level is 1 or 2)
        {
            return true;
        }

        if (e.Level == 3 && (AllWarnings || WarningProviders.Contains(e.Provider)))
        {
            return true;
        }

        return Informational.TryGetValue(e.Provider, out var ids) && (ids is null || ids.Contains(e.EventId));
    }

    /// <summary>
    /// The structured XML query for the Windows event log API. Long provider and ID lists are split into several
    /// Select elements to stay well inside the XPath limits of the event log service.
    /// </summary>
    public string ToStructuredQuery(long? afterRecordId = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var range = new StringBuilder();
        if (afterRecordId is long after)
        {
            range.Append(" and (EventRecordID &gt; ").Append(after.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        if (from is not null || to is not null)
        {
            range.Append(" and TimeCreated[");
            if (from is { } f)
            {
                range.Append("@SystemTime&gt;='").Append(XPathTime(f)).Append('\'');
            }

            if (to is { } t)
            {
                if (from is not null)
                {
                    range.Append(" and ");
                }

                range.Append("@SystemTime&lt;='").Append(XPathTime(t)).Append('\'');
            }

            range.Append(']');
        }

        var path = Escape(Channel);
        var selects = new List<string>
        {
            $"*[System[({(AllWarnings ? "Level=1 or Level=2 or Level=3" : "Level=1 or Level=2")}){range}]]",
        };
        if (!AllWarnings)
        {
            foreach (var chunk in WarningProviders.Order(StringComparer.OrdinalIgnoreCase).Chunk(10))
            {
                selects.Add($"*[System[Provider[{string.Join(" or ", chunk.Select(p => $"@Name='{Escape(p)}'"))}] and Level=3{range}]]");
            }
        }

        foreach (var (provider, ids) in Informational.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (ids is null)
            {
                selects.Add($"*[System[Provider[@Name='{Escape(provider)}']{range}]]");
                continue;
            }

            foreach (var chunk in ids.Chunk(10))
            {
                selects.Add($"*[System[Provider[@Name='{Escape(provider)}'] and ({string.Join(" or ", chunk.Select(id => $"EventID={id.ToString(CultureInfo.InvariantCulture)}"))}){range}]]");
            }
        }

        var xml = new StringBuilder();
        xml.Append("<QueryList><Query Id=\"0\" Path=\"").Append(path).Append("\">");
        foreach (var select in selects)
        {
            xml.Append("<Select Path=\"").Append(path).Append("\">").Append(select).Append("</Select>");
        }

        foreach (var chunk in Excluded.Order(StringComparer.OrdinalIgnoreCase).Chunk(10))
        {
            xml.Append("<Suppress Path=\"").Append(path).Append("\">*[System[Provider[")
                .Append(string.Join(" or ", chunk.Select(p => $"@Name='{Escape(p)}'"))).Append("]]]</Suppress>");
        }

        xml.Append("</Query></QueryList>");
        return xml.ToString();
    }

    private static string XPathTime(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>XML-escapes a value placed inside an attribute or an XPath string literal (apostrophes included).</summary>
    private static string Escape(string value) => SecurityElement.Escape(value) ?? "";
}
