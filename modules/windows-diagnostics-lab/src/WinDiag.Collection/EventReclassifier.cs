using WinDiag.Core.Events;
using WinDiag.Core.Privacy;
using WinDiag.Storage;

namespace WinDiag.Collection;

/// <summary>
/// Applies the current classification rules to events already in the database. Only category, severity, grouping and
/// title change; the event content stays as stored. Virtual (demo) events keep their own titles and are skipped.
/// </summary>
public static class EventReclassifier
{
    public const string MetaKey = "catalog_version";

    public sealed record Result(int Checked, int Changed);

    /// <summary>Re-classifies when the database was classified with an older catalog version; returns null when it was not needed.</summary>
    public static Result? RunIfOutdated(DiagnosticsStore store, EventClassifier classifier, PrivacyMasker masker)
    {
        if (store.GetMetaLong(MetaKey) >= EventCatalog.Version)
        {
            return null;
        }

        var result = Run(store, classifier, masker);
        store.SetMeta(MetaKey, EventCatalog.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return result;
    }

    public static Result Run(DiagnosticsStore store, EventClassifier classifier, PrivacyMasker masker, int pageSize = 1000)
    {
        var checkedCount = 0;
        var changedCount = 0;
        long after = 0;
        while (true)
        {
            var page = store.ReadEventPage(after, pageSize);
            if (page.Count == 0)
            {
                break;
            }

            after = page[^1].Id;
            var changes = new List<(long, EventClassification)>();
            foreach (var e in page)
            {
                if (e.Source == EventSources.Virtual)
                {
                    continue;
                }

                checkedCount++;
                // Collection classifies the masked event; an event stored unmasked is masked the same way first.
                IEventFields input = e.IsMasked ? e : masker.Mask(ToRaw(e));
                var c = classifier.Classify(input);
                if (c.Category != e.Category || c.Severity != e.Severity || c.GroupKey != e.GroupKey || c.Signature != e.Signature || c.Title != e.Title)
                {
                    changes.Add((e.Id, c));
                }
            }

            store.UpdateClassifications(changes);
            changedCount += changes.Count;
        }

        return new Result(checkedCount, changedCount);
    }

    private static RawEvent ToRaw(StoredEvent e) => new()
    {
        Channel = e.Channel,
        Provider = e.Provider,
        EventSourceName = e.EventSourceName,
        EventId = e.EventId,
        Qualifiers = e.Qualifiers,
        Level = e.Level,
        RecordId = e.RecordId,
        TimeCreated = e.TimeCreated,
        ProcessId = e.ProcessId,
        ThreadId = e.ThreadId,
        Data = e.Data,
        Message = e.Message,
        MessageError = e.MessageError,
    };
}
