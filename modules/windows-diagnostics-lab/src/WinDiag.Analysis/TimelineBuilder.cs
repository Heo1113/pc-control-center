using System.Globalization;
using WinDiag.Core.Events;
using WinDiag.Core.Records;
using WinDiag.Core.Time;
using WinDiag.Storage;

namespace WinDiag.Analysis;

public sealed record TimelineOptions
{
    public IReadOnlySet<TimelineKind>? Kinds { get; init; }

    /// <summary>Merge consecutive events of the same group closer than this into one item with a count.</summary>
    public TimeSpan CollapseWithin { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Informational events of these categories are hidden unless asked for (they are frequent).</summary>
    public bool IncludeRoutineBootEvents { get; init; } = true;

    public int MaxEvents { get; init; } = 20_000;
}

/// <summary>
/// One time-ordered view of everything known: Windows events, configuration changes found by snapshot comparison,
/// user records and baselines, crash incidents, app recording periods, gaps, and time-order anomalies. Every item
/// keeps its source so measured, recorded and user-entered information stay distinguishable.
/// </summary>
public static class TimelineBuilder
{
    public static IReadOnlyList<TimelineItem> Build(DiagnosticsStore store, AnalysisRange range, TimelineOptions? options = null,
        IReadOnlyList<CrashIncident>? incidents = null)
    {
        options ??= new TimelineOptions();
        bool Wanted(TimelineKind kind) => options.Kinds is null || options.Kinds.Contains(kind);
        var items = new List<TimelineItem>();

        var events = store.QueryEvents(new EventQuery { From = range.From, To = range.To, Limit = options.MaxEvents, Descending = false });
        TimelineItem? open = null;
        foreach (var e in events)
        {
            var kind = EventCategories.Get(e.Category).Kind;
            if (e.Category == EventCategories.Other && e.Severity == DiagSeverity.Info)
            {
                kind = TimelineKind.Info;
            }

            if (!Wanted(kind) || (!options.IncludeRoutineBootEvents && e.Category is EventCategories.Boot or EventCategories.Power))
            {
                continue;
            }

            if (open is not null && open.GroupKey == e.GroupKey && e.TimeCreated - (open.End ?? open.Time) <= options.CollapseWithin)
            {
                open = open with { End = e.TimeCreated, Count = open.Count + 1, Severity = (DiagSeverity)Math.Max((int)open.Severity, (int)e.Severity) };
                items[^1] = open;
                continue;
            }

            open = new TimelineItem
            {
                Time = e.TimeCreated,
                Kind = kind,
                Source = TimelineSource.WindowsEvent,
                Title = e.Title,
                Detail = $"{e.Channel} · {e.Provider} {e.EventId.ToString(CultureInfo.InvariantCulture)} · Windows 수준 {LevelText.Windows(e.Level)}",
                Severity = e.Severity,
                Category = e.Category,
                EventRowId = e.Id,
                GroupKey = e.GroupKey,
                IsVirtual = e.IsVirtual,
            };
            items.Add(open);
        }

        if (Wanted(TimelineKind.SystemChange))
        {
            foreach (var change in SnapshotDiff.Detect(store.ListSnapshots()).Where(c => c.WindowTo >= range.From && c.WindowFrom <= range.To))
            {
                items.Add(new TimelineItem
                {
                    Time = change.WindowTo,
                    End = null,
                    TimeIsApproximate = true,
                    Kind = TimelineKind.SystemChange,
                    Source = TimelineSource.SnapshotChange,
                    Title = $"구성 변경 감지 ({change.Changes.Count}건): " + string.Join(", ", change.Changes.Take(3).Select(c => c.Describe())),
                    Detail = $"변경 시점은 {TimeDisplay.Local.Full(change.WindowFrom)} ~ {TimeDisplay.Local.Full(change.WindowTo)} 사이입니다 (스냅샷 비교로는 정확한 시각을 알 수 없음).",
                    Severity = DiagSeverity.Info,
                    IsVirtual = change.IsMock,
                    Count = change.Changes.Count,
                });
            }
        }

        if (Wanted(TimelineKind.UserRecord))
        {
            foreach (var record in store.ListUserRecords(range.From, range.To))
            {
                items.Add(new TimelineItem
                {
                    Time = record.Time,
                    Kind = TimelineKind.UserRecord,
                    Source = TimelineSource.UserRecord,
                    Title = $"{UserRecordKinds.Name(record.Kind)}: {record.Title}",
                    Detail = string.Join("; ", record.Values.Select(v => $"{v.Name} {v.Before} → {v.After}{(v.Unit is null ? "" : " " + v.Unit)}"))
                             + (record.Detail is null ? "" : $" {record.Detail}"),
                    IsUserInput = true,
                    IsVirtual = record.Title.StartsWith("[가상]", StringComparison.Ordinal),
                });
            }

            foreach (var baseline in store.ListBaselines().Where(b => range.Contains(b.Time)))
            {
                items.Add(new TimelineItem
                {
                    Time = baseline.Time,
                    Kind = TimelineKind.UserRecord,
                    Source = TimelineSource.Baseline,
                    Title = $"기준선: {baseline.Name}",
                    Detail = baseline.Description,
                    IsUserInput = true,
                    IsVirtual = baseline.Name.StartsWith("[가상]", StringComparison.Ordinal),
                });
            }
        }

        if (Wanted(TimelineKind.SystemCrash))
        {
            foreach (var incident in incidents ?? CrashAnalyzer.Analyze(store, range))
            {
                items.Add(new TimelineItem
                {
                    Time = incident.ReferenceTime,
                    TimeIsApproximate = incident.EstimatedCrashTime is null,
                    Kind = TimelineKind.SystemCrash,
                    Source = TimelineSource.CrashIncident,
                    Title = $"시스템 충돌·비정상 종료: {incident.KindText}",
                    Detail = $"충돌 시각: {incident.CrashTimeSource}. 재부팅 기록 {(incident.BootTime is { } b ? TimeDisplay.Local.Full(b) : "N/A")}.",
                    Severity = DiagSeverity.Critical,
                    IncidentId = incident.Id,
                    IsVirtual = incident.IsVirtual,
                });
            }
        }

        if (Wanted(TimelineKind.Hardware))
        {
            foreach (var run in store.SensorCoverage(range.From, range.To))
            {
                items.Add(new TimelineItem
                {
                    Time = run.From,
                    End = run.To,
                    Kind = TimelineKind.Hardware,
                    Source = TimelineSource.AppRun,
                    Title = $"센서 기록 구간 ({TimeDisplay.Duration(run.To - run.From)}, {run.Samples.ToString(CultureInfo.InvariantCulture)}개 샘플){(run.IsMock ? " · MOCK" : "")}",
                    Detail = "이 구간 밖에서는 센서 값이 없습니다 (앱이 실행 중일 때만 기록).",
                    IsVirtual = run.IsMock,
                });
            }

            foreach (var gap in store.ListGaps(range.From, range.To))
            {
                items.Add(new TimelineItem
                {
                    Time = gap.From ?? gap.Detected,
                    End = gap.To,
                    Kind = TimelineKind.Info,
                    Source = TimelineSource.Gap,
                    Title = $"{GapKinds.Name(gap.Kind)} ({gap.Scope})",
                    Detail = gap.Detail,
                    Severity = DiagSeverity.Warning,
                });
            }

            items.AddRange(TimeAnomalies(store, range));
        }

        return items.OrderBy(i => i.Time).ThenBy(i => i.EventRowId ?? long.MaxValue).ToList();
    }

    /// <summary>
    /// Events of one channel whose time goes backwards while the record number goes forward: the system clock was
    /// changed between them, so their time order (and intervals) cannot be trusted.
    /// </summary>
    public static IReadOnlyList<TimelineItem> TimeAnomalies(DiagnosticsStore store, AnalysisRange range)
    {
        var points = store.LoadEventPoints(new EventQuery { From = range.From.AddDays(-1), To = range.To.AddDays(1), Limit = 500_000 });
        var items = new List<TimelineItem>();
        foreach (var channel in points.GroupBy(p => p.Channel))
        {
            var ordered = channel.Where(p => !p.IsVirtual).OrderBy(p => p.RecordId).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var back = ordered[i - 1].TimeMs - ordered[i].TimeMs;
                if (back > TimeSpan.FromMinutes(2).TotalMilliseconds && ordered[i].RecordId > ordered[i - 1].RecordId)
                {
                    var time = DateTimeOffset.FromUnixTimeMilliseconds(ordered[i].TimeMs);
                    if (range.Contains(time))
                    {
                        items.Add(new TimelineItem
                        {
                            Time = time,
                            Kind = TimelineKind.Info,
                            Source = TimelineSource.TimeAnomaly,
                            Title = $"시간 역전 감지 ({channel.Key})",
                            Detail = $"레코드 번호는 증가했지만 기록 시각이 {TimeDisplay.Duration(TimeSpan.FromMilliseconds(back))} 거꾸로 갔습니다. 그 사이 시스템 시각이 바뀐 것으로 보이며, 이 시점 전후의 순서 비교에 주의하세요.",
                            Severity = DiagSeverity.Warning,
                            EventRowId = ordered[i].Id,
                        });
                    }
                }
            }
        }

        return items;
    }
}

/// <summary>Repeated problems: the same signature many times, new kinds, and whether they are increasing.</summary>
public static class RepeatAnalyzer
{
    public static IReadOnlyList<RepeatPattern> Analyze(DiagnosticsStore store, AnalysisRange range, int threshold, long acknowledgedEventId)
    {
        var groups = store.QueryGroups(new EventQuery { From = range.From, To = range.To, MinSeverity = DiagSeverity.Warning, Limit = 500 });
        var middle = range.From + range.Length / 2;
        var patterns = new List<RepeatPattern>();
        foreach (var group in groups)
        {
            var isNew = group.FirstEver >= range.From;
            if (group.Count < threshold && !isNew)
            {
                continue;
            }

            var points = store.LoadEventPoints(new EventQuery { From = range.From, To = range.To, GroupKey = group.GroupKey, Limit = 100_000 });
            var firstHalf = points.Count(p => p.Time < middle);
            var secondHalf = points.Count - firstHalf;
            var trend = points.Count < 4 ? "판단하기에 적음"
                : secondHalf >= 2 * Math.Max(1, firstHalf) ? "최근에 늘어남"
                : firstHalf >= 2 * Math.Max(1, secondHalf) ? "최근에 줄어듦"
                : "비슷함";
            var newSinceAck = acknowledgedEventId > 0 && points.Count > 0 && points.Min(p => p.Id) > acknowledgedEventId;
            patterns.Add(new RepeatPattern(group, group.Count / range.Days, isNew, newSinceAck, firstHalf, secondHalf, trend));
        }

        return patterns.OrderByDescending(p => p.Group.MaxSeverity).ThenByDescending(p => p.Group.Count).ToList();
    }
}
