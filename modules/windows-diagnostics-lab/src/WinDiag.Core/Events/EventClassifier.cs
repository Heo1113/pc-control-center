using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WinDiag.Core.Events;

/// <summary>
/// Classifies events with an <see cref="EventCatalog"/>. The provider, event ID, channel and event data all take part;
/// the result also carries the "same problem" signature used to group repeated events.
/// </summary>
public sealed partial class EventClassifier(EventCatalog catalog)
{
    public static EventClassifier Default { get; } = new(EventCatalog.Default);

    public EventCatalog Catalog { get; } = catalog;

    public EventClassification Classify(IEventFields e)
    {
        var rule = Catalog.Find(e);
        Interpretation interpretation;
        try
        {
            interpretation = rule?.Interpret?.Invoke(e) ?? Interpretation.None;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or InvalidOperationException)
        {
            // A malformed field must not stop collection: the event is kept with the rule's plain classification.
            interpretation = Interpretation.None;
        }

        var category = interpretation.Category ?? rule?.Category ?? EventCategories.Other;
        var severity = interpretation.Severity ?? rule?.Severity ?? LevelText.FromWindowsLevel(e.Level);
        var baseTitle = rule?.Title ?? $"{e.Provider} 이벤트 {e.EventId.ToString(CultureInfo.InvariantCulture)}";
        var title = string.IsNullOrWhiteSpace(interpretation.TitleDetail) ? baseTitle : $"{baseTitle}: {interpretation.TitleDetail}";
        var signature = interpretation.Signature
                        ?? FieldSignature(e, rule)
                        ?? MessageTemplate(e.Message)
                        ?? DataTemplate(e)
                        ?? "(데이터 없음)";
        var groupKey = GroupKey(category, e.Provider, e.EventId, signature);
        return new EventClassification(
            category,
            severity,
            title,
            interpretation.Meaning ?? rule?.Meaning,
            interpretation.Caution ?? rule?.Caution,
            signature,
            groupKey,
            interpretation.Facts,
            rule is not null);
    }

    public static string GroupKey(string category, string provider, int eventId, string signature)
    {
        var text = string.Join('\u001f', category, provider.ToLowerInvariant(), eventId.ToString(CultureInfo.InvariantCulture), signature);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    private static string? FieldSignature(IEventFields e, EventRule? rule)
    {
        if (rule is null || rule.SignatureFields.Count == 0)
        {
            return null;
        }

        var parts = rule.SignatureFields.Select(spec => (Key: spec.Split('|')[0], Value: e.Field(spec))).ToArray();
        return EventInterpreters.Sig(parts);
    }

    /// <summary>The first message line with numbers, hex values and GUIDs replaced, so repeats of one message match.</summary>
    public static string? MessageTemplate(string? message)
    {
        var line = EventInterpreters.FirstLine(message);
        if (line is null)
        {
            return null;
        }

        line = Guid().Replace(line, "{G}");
        line = Hex().Replace(line, "0x#");
        line = Number().Replace(line, "#");
        line = Spaces().Replace(line, " ").Trim();
        return "msg=" + (line.Length > 160 ? line[..160] : line).ToLowerInvariant();
    }

    private static string? DataTemplate(IEventFields e)
    {
        if (e.Data.Count == 0)
        {
            return null;
        }

        var text = string.Join(" | ", e.Data.Take(4).Select(d => Number().Replace(Hex().Replace(Guid().Replace(d.Value, "{G}"), "0x#"), "#")));
        text = Spaces().Replace(text, " ").Trim();
        return "data=" + (text.Length > 160 ? text[..160] : text).ToLowerInvariant();
    }

    [GeneratedRegex(@"\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?", RegexOptions.CultureInvariant)]
    private static partial Regex Guid();

    [GeneratedRegex(@"0x[0-9a-fA-F]+", RegexOptions.CultureInvariant)]
    private static partial Regex Hex();

    // Whole numbers only: digits inside names (Harddisk1, DR2) and the "0x#" placeholder stay as they are.
    [GeneratedRegex(@"\b\d+\b", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
