using WinDiag.Core.Events;
using WinDiag.Core.Settings;

namespace WinDiag.Collection.Notifications;

public sealed record DiagNotification(
    DateTimeOffset Time,
    DiagSeverity Severity,
    string Title,
    string Message,
    string GroupKey,
    long EventId,
    int Count);

/// <summary>
/// Turns newly collected events into on-screen notifications: only live events (not history imports) at or above the
/// configured severity, and repeats of the same problem within the quiet window are merged into one notification.
/// </summary>
public sealed class NotificationService(Func<NotificationSettings> settings, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _lastShown = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IReadOnlyList<DiagNotification> Evaluate(IReadOnlyList<StoredEvent> events)
    {
        var s = settings();
        if (!s.Enabled || events.Count == 0)
        {
            return [];
        }

        var now = _time.GetUtcNow();
        var quiet = TimeSpan.FromMinutes(s.RepeatQuietMinutes);
        var result = new List<DiagNotification>();
        lock (_gate)
        {
            foreach (var group in events.Where(e => e.Severity >= s.MinimumSeverity && !e.IsVirtual).GroupBy(e => e.GroupKey))
            {
                var latest = group.MaxBy(e => e.TimeCreated)!;
                if (_lastShown.TryGetValue(group.Key, out var shown) && now - shown < quiet)
                {
                    continue;
                }

                _lastShown[group.Key] = now;
                var count = group.Count();
                result.Add(new DiagNotification(
                    now,
                    latest.Severity,
                    $"{LevelText.Severity(latest.Severity)}: {EventCategories.Name(latest.Category)}",
                    count > 1 ? $"{latest.Title} (새로 {count}건)" : latest.Title,
                    group.Key,
                    latest.Id,
                    count));
            }
        }

        return result.OrderByDescending(n => n.Severity).ToList();
    }
}
