using System.Globalization;
using WinDiag.Core.Crash;
using WinDiag.Core.Events;
using WinDiag.Core.Records;
using WinDiag.Core.Time;
using WinDiag.Storage;
using static WinDiag.Core.Events.EventCategories;

namespace WinDiag.Analysis;

public sealed record DiagnosticOptions
{
    public int CorrelationWindowSeconds { get; init; } = 120;
    public int RepeatThreshold { get; init; } = 3;
    public int HighGpuTempC { get; init; } = 85;
    public int SensorWindowMinutes { get; init; } = 10;
    public int ChangeWindowDays { get; init; } = 14;
    public long AcknowledgedEventId { get; init; }
}

/// <summary>
/// Builds the diagnostic report in three clearly separated tiers:
/// facts (what the records say), observed associations (statistics on timing and sensor values) and cause candidates
/// that need further verification (knowledge rules triggered by evidence, each with verification steps).
/// Nothing in the report is presented as a confirmed cause.
/// </summary>
public static class DiagnosticAnalyzer
{
    private static readonly uint[] GpuBugChecks = [0x116, 0x117, 0x119, 0x10E, 0x113, 0x141, 0xEA];
    private static readonly uint[] HardwareBugChecks = [0x124, 0x9C, 0x101];
    private static readonly uint[] StorageBugChecks = [0x7A, 0x77, 0x24, 0x154, 0x7B];

    /// <summary>Category pairs that are recorded together by construction (one incident or one crash writes both).</summary>
    private static readonly HashSet<(string, string)> SameIncidentPairs =
    [
        (UnexpectedShutdown, BugCheck), (UnexpectedShutdown, CrashDump), (BugCheck, CrashDump),
        (AppCrash, WerReport), (AppHang, WerReport),
    ];

    public static DiagnosticReport Analyze(DiagnosticsStore store, AnalysisRange range, DiagnosticOptions options, DateTimeOffset now)
    {
        var points = store.LoadEventPoints(new EventQuery { From = range.From, To = range.To, Limit = 2_000_000 });
        var problems = points.Where(p => IsProblem(p)).ToList();
        var crashes = CrashAnalyzer.Analyze(store, range, TimeSpan.FromMinutes(options.SensorWindowMinutes));
        var repeats = RepeatAnalyzer.Analyze(store, range, options.RepeatThreshold, options.AcknowledgedEventId);
        var changes = SnapshotDiff.Detect(store.ListSnapshots()).Where(c => c.WindowTo >= range.From && c.WindowTo <= range.To).ToList();
        var gaps = store.ListGaps(range.From, range.To);
        var channels = store.GetChannelStates();
        var runs = store.SensorCoverage(range.From, range.To);
        var records = store.ListUserRecords(range.From, range.To);
        var anomalies = TimelineBuilder.TimeAnomalies(store, range);

        var facts = BuildFacts(store, range, problems, points, crashes, repeats, changes, records, channels, gaps, anomalies, options);
        var associations = new List<Finding>();
        associations.AddRange(ChangePointAssociations(store, range, problems, points, changes, records, options));
        associations.AddRange(CoOccurrence(problems, points, options));
        associations.AddRange(PreCrashPatterns(crashes));
        associations.AddRange(SensorAssociations(store, range, problems, crashes, options));
        var hypotheses = BuildHypotheses(problems, crashes, repeats, associations, store, range, options);

        var sensorHours = runs.Sum(r => (r.To - r.From).TotalHours);
        var limitations = new List<string>
        {
            "시간적으로 가깝거나 전후 관계가 있다는 것은 원인을 입증하지 않습니다. 원인 후보는 제시된 방법으로 직접 검증해야 합니다.",
            string.Create(CultureInfo.InvariantCulture,
                $"센서 값은 이 앱이 실행 중일 때만 기록됩니다. 분석 기간 {range.Days:0.#}일 중 센서 기록은 약 {sensorHours:0.#}시간입니다."),
        };
        var coverageStart = channels.Where(c => c.Status == ChannelStatus.Ok).Select(c => c.HistoryFrom ?? c.OldestEventTime).Where(t => t is not null).Min();
        if (coverageStart is { } start && start > range.From)
        {
            limitations.Add($"이벤트 기록은 {TimeDisplay.Local.Full(start)} 이후만 있습니다 (그 이전은 가져오지 않았거나 Windows에 남아 있지 않음).");
        }

        foreach (var channel in channels.Where(c => c.Status is ChannelStatus.AccessDenied or ChannelStatus.Error or ChannelStatus.NotFound))
        {
            limitations.Add($"{channel.Channel}: {ChannelStatusText.Name(channel.Status)} — 이 로그의 정보는 분석에 포함되지 않았습니다.");
        }

        if (gaps.Count > 0)
        {
            limitations.Add($"수집 누락·제한 구간 {gaps.Count}건이 있습니다 (타임라인 화면의 '수집 누락·제한' 항목 참조).");
        }

        if (anomalies.Count > 0)
        {
            limitations.Add($"시스템 시각 변경으로 보이는 시간 역전이 {anomalies.Count}건 있습니다. 그 전후의 시간 간격 비교는 부정확할 수 있습니다.");
        }

        var includesVirtual = points.Any(p => p.IsVirtual);
        var includesMock = runs.Any(r => r.IsMock);
        if (includesVirtual)
        {
            limitations.Add("가상(테스트) 이벤트가 포함되어 있습니다. 이 결과는 실제 PC의 진단이 아닙니다.");
        }

        if (includesMock)
        {
            limitations.Add("MOCK(가상) 센서 값이 포함되어 있습니다. 실제 하드웨어 측정이 아닙니다.");
        }

        return new DiagnosticReport
        {
            Range = range,
            GeneratedAt = now,
            Facts = facts,
            Associations = associations.OrderByDescending(a => a.Score).ToList(),
            Hypotheses = hypotheses.OrderByDescending(h => h.Score).ToList(),
            Repeats = repeats,
            Crashes = crashes,
            Changes = changes,
            Gaps = gaps,
            Coverage = new CoverageSummary(channels, runs, sensorHours, gaps.Count),
            IncludesVirtual = includesVirtual,
            IncludesMockSensors = includesMock,
            Limitations = limitations,
            EventCount = points.Count,
        };
    }

    private static bool IsProblem(EventPoint p)
    {
        var kind = Get(p.Category).Kind;
        return p.Severity >= DiagSeverity.Warning && kind is TimelineKind.Error or TimelineKind.ProgramCrash or TimelineKind.SystemCrash;
    }

    // ------------------------------------------------------------------ facts

    private static List<Finding> BuildFacts(DiagnosticsStore store, AnalysisRange range, List<EventPoint> problems, IReadOnlyList<EventPoint> points,
        IReadOnlyList<CrashIncident> crashes, IReadOnlyList<RepeatPattern> repeats, List<DetectedChange> changes, IReadOnlyList<UserRecord> records,
        IReadOnlyList<ChannelState> channels, IReadOnlyList<CollectionGap> gaps, IReadOnlyList<TimelineItem> anomalies, DiagnosticOptions options)
    {
        var facts = new List<Finding>();
        foreach (var incident in crashes)
        {
            var before = incident.EventsBefore.GroupBy(e => e.Category)
                .Select(g => $"{Name(g.Key)} {g.Count()}건").ToList();
            facts.Add(new Finding
            {
                Id = "fact-" + incident.Id,
                Tier = FindingTier.Fact,
                Title = $"시스템 비정상 종료: {incident.KindText} ({TimeDisplay.Local.Full(incident.ReferenceTime)})",
                Detail = $"충돌 시각: {incident.CrashTimeSource}. "
                         + (incident.LastKnownAlive is { } alive ? $"마지막 확인 시각 {TimeDisplay.Local.Full(alive)} ({incident.LastKnownAliveSource}). " : "")
                         + (before.Count > 0 ? $"직전 30분 기록: {string.Join(", ", before)}." : "직전 30분에 경고 이상 기록 없음.")
                         + (incident.SameCodeCount > 1 ? $" 같은 유형 {incident.SameCodeCount}건." : ""),
                Severity = DiagSeverity.Critical,
                Evidence = incident.Evidence.Select(e => new FindingEvidence(e.Title, e.Id, e.GroupKey, e.TimeCreated)).ToList(),
                Caution = BugCheckCatalog.Disclaimer,
                Score = 1000,
            });
        }

        foreach (var category in problems.GroupBy(p => p.Category).OrderByDescending(g => g.Max(p => p.Severity)).ThenByDescending(g => g.Count()))
        {
            var groups = category.GroupBy(p => p.GroupKey).OrderByDescending(g => g.Count()).ToList();
            var top = groups[0];
            var topEvent = store.GetEvent(top.Max(p => p.Id));
            facts.Add(new Finding
            {
                Id = "fact-cat-" + category.Key,
                Tier = FindingTier.Fact,
                Title = $"{Name(category.Key)} {category.Count()}건",
                Detail = $"서로 다른 유형 {groups.Count}개. 처음 {TimeDisplay.Local.Full(category.Min(p => p.Time))}, 마지막 {TimeDisplay.Local.Full(category.Max(p => p.Time))}. "
                         + $"가장 많은 유형: {topEvent?.Title ?? top.Key} ({top.Count()}건).",
                Severity = category.Max(p => p.Severity),
                Evidence = groups.Take(5).Select(g => new FindingEvidence($"{g.Count()}건", g.Max(p => p.Id), g.Key, g.Max(p => p.Time))).ToList(),
                Score = 100 * (int)category.Max(p => p.Severity) + category.Count(),
            });
        }

        foreach (var repeat in repeats.Where(r => r.Group.Count >= options.RepeatThreshold).Take(10))
        {
            facts.Add(new Finding
            {
                Id = "fact-repeat-" + repeat.Group.GroupKey,
                Tier = FindingTier.Fact,
                Title = $"같은 문제 반복: {repeat.Group.Title} {repeat.Group.Count}회",
                Detail = string.Create(CultureInfo.InvariantCulture,
                    $"{repeat.Group.DistinctDays}일에 걸쳐 하루 평균 {repeat.PerDay:0.##}회. 기간 앞/뒤 절반 {repeat.FirstHalf}/{repeat.SecondHalf}회 ({repeat.Trend})."),
                Severity = repeat.Group.MaxSeverity,
                Evidence = [new FindingEvidence("최근 기록", repeat.Group.LatestEventId, repeat.Group.GroupKey, repeat.Group.Last)],
                Score = 50 + repeat.Group.Count,
            });
        }

        foreach (var change in changes)
        {
            facts.Add(new Finding
            {
                Id = $"fact-change-{change.AfterSnapshotId}",
                Tier = FindingTier.Fact,
                Title = "구성 변경 감지 (스냅샷 비교): " + string.Join(", ", change.Changes.Take(3).Select(c => c.Describe())),
                Detail = $"{TimeDisplay.Local.Full(change.WindowFrom)} ~ {TimeDisplay.Local.Full(change.WindowTo)} 사이에 바뀌었습니다. 변경 {change.Changes.Count}건."
                         + (change.IsMock ? " (MOCK 스냅샷)" : ""),
                Severity = DiagSeverity.Info,
                Evidence = change.Changes.Take(20).Select(c => new FindingEvidence(c.Describe())).ToList(),
                Score = 40,
            });
        }

        var installs = points.Where(p => p.Category is DriverInstall or UpdateInstall or SoftwareInstall).ToList();
        if (installs.Count > 0)
        {
            facts.Add(new Finding
            {
                Id = "fact-installs",
                Tier = FindingTier.Fact,
                Title = $"드라이버·업데이트·프로그램 설치 기록 {installs.Count}건 (Windows 이벤트)",
                Detail = string.Join(", ", installs.GroupBy(p => p.Category).Select(g => $"{Name(g.Key)} {g.Count()}건")),
                Severity = DiagSeverity.Info,
                Evidence = installs.OrderByDescending(p => p.TimeMs).Take(10).Select(p => new FindingEvidence(Name(p.Category), p.Id, p.GroupKey, p.Time)).ToList(),
                Score = 30,
            });
        }

        if (records.Count > 0)
        {
            facts.Add(new Finding
            {
                Id = "fact-user-records",
                Tier = FindingTier.Fact,
                Title = $"사용자가 기록한 변경 {records.Count}건 (사용자 입력, 실측 아님)",
                Detail = string.Join("; ", records.Select(r => $"{TimeDisplay.Local.Full(r.Time)} {UserRecordKinds.Name(r.Kind)}: {r.Title}")),
                Severity = DiagSeverity.Info,
                Score = 35,
            });
        }

        var bigClock = points.Count(p => p.Category == TimeChange && p.Severity >= DiagSeverity.Warning);
        if (bigClock > 0 || anomalies.Count > 0)
        {
            facts.Add(new Finding
            {
                Id = "fact-clock",
                Tier = FindingTier.Fact,
                Title = $"큰 시스템 시각 변경 {bigClock}건, 시간 역전 {anomalies.Count}건",
                Detail = "이 시점 전후 이벤트의 시간 순서와 간격은 그대로 믿기 어렵습니다.",
                Severity = DiagSeverity.Warning,
                Score = 20,
            });
        }

        var limited = channels.Where(c => c.Status is ChannelStatus.AccessDenied or ChannelStatus.Error).ToList();
        if (limited.Count > 0 || gaps.Count > 0)
        {
            facts.Add(new Finding
            {
                Id = "fact-collection",
                Tier = FindingTier.Fact,
                Title = $"수집 제한: 읽지 못한 로그 {limited.Count}개, 누락 구간 {gaps.Count}건",
                Detail = string.Join("; ", limited.Select(c => $"{c.Channel} ({ChannelStatusText.Name(c.Status)})")
                    .Concat(gaps.GroupBy(g => g.Kind).Select(g => $"{GapKinds.Name(g.Key)} {g.Count()}건"))),
                Severity = DiagSeverity.Warning,
                Score = 10,
            });
        }

        return facts.OrderByDescending(f => f.Score).ToList();
    }

    // ------------------------------------------------------------------ associations

    private sealed record ChangePoint(DateTimeOffset Time, string Label, bool Approximate);

    /// <summary>
    /// Change points closer than a day are one change (a driver install writes several events and the next snapshot
    /// shows the same change), so the periods compared are never a few minutes long.
    /// </summary>
    private static List<ChangePoint> Cluster(List<ChangePoint> points)
    {
        var clusters = new List<List<ChangePoint>>();
        foreach (var point in points.OrderBy(p => p.Time))
        {
            if (clusters.Count > 0 && point.Time - clusters[^1][0].Time <= TimeSpan.FromDays(1))
            {
                clusters[^1].Add(point);
            }
            else
            {
                clusters.Add([point]);
            }
        }

        return clusters.Select(c => new ChangePoint(c[0].Time, string.Join(" + ", c.Select(p => p.Label).Distinct()), c.All(p => p.Approximate))).ToList();
    }

    private static List<ChangePoint> ChangePoints(IReadOnlyList<EventPoint> points, List<DetectedChange> changes, IReadOnlyList<UserRecord> records)
    {
        var list = new List<ChangePoint>();
        list.AddRange(records.Select(r => new ChangePoint(r.Time, $"[사용자 입력] {UserRecordKinds.Name(r.Kind)}: {r.Title}", false)));
        list.AddRange(changes.Select(c => new ChangePoint(c.WindowTo, "[구성 변경] " + string.Join(", ", c.Changes.Take(2).Select(x => x.Describe())), true)));
        foreach (var day in points.Where(p => p.Category is DriverInstall or UpdateInstall && p.Severity <= DiagSeverity.Info)
                     .GroupBy(p => (p.Category, Day: p.Time.ToLocalTime().Date)))
        {
            list.Add(new ChangePoint(day.Min(p => p.Time), $"[Windows 이벤트] {Name(day.Key.Category)} ({day.Count()}건, {day.Key.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})", false));
        }

        return list.OrderBy(c => c.Time).ToList();
    }

    private static IEnumerable<Finding> ChangePointAssociations(DiagnosticsStore store, AnalysisRange range, List<EventPoint> problems,
        IReadOnlyList<EventPoint> points, List<DetectedChange> changes, IReadOnlyList<UserRecord> records, DiagnosticOptions options)
    {
        var changePoints = Cluster(ChangePoints(points, changes, records));
        var window = TimeSpan.FromDays(options.ChangeWindowDays);
        var coverageStart = store.GetChannelStates().Where(c => c.Status == ChannelStatus.Ok).Select(c => c.HistoryFrom ?? c.OldestEventTime)
            .Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(range.From).Min();
        var results = new List<Finding>();
        for (var index = 0; index < changePoints.Count; index++)
        {
            var cp = changePoints[index];

            // Compare only the stretch since the previous change with the stretch until the next one, so another
            // change is never inside either period.
            var beforeFrom = Max(Max(range.From, coverageStart), cp.Time - window);
            if (index > 0)
            {
                beforeFrom = Max(beforeFrom, changePoints[index - 1].Time);
            }

            var afterTo = Min(range.To, cp.Time + window);
            if (index + 1 < changePoints.Count)
            {
                afterTo = Min(afterTo, changePoints[index + 1].Time);
            }

            var beforeDays = (cp.Time - beforeFrom).TotalDays;
            var afterDays = (afterTo - cp.Time).TotalDays;
            if (beforeDays < 1 || afterDays < 1)
            {
                continue;
            }

            var others = new List<string>();
            if (index + 1 < changePoints.Count && changePoints[index + 1].Time - cp.Time < window)
            {
                others.Add($"이후 구간은 다음 변경({changePoints[index + 1].Label}) 전까지로 줄였습니다");
            }

            foreach (var category in problems.Select(p => p.Category).Distinct())
            {
                var kb = CountOccurrences(problems, category, beforeFrom, cp.Time.AddTicks(-1));
                var ka = CountOccurrences(problems, category, cp.Time, afterTo);
                var p = RateStatistics.RateRatioPValue(kb, beforeDays, ka, afterDays);
                var increased = ka >= 3 && ka / afterDays > 2 * (kb / beforeDays) && p < 0.05;
                var decreased = kb >= 3 && kb / beforeDays > 2 * (ka / afterDays) && p < 0.05;
                if (!increased && !decreased)
                {
                    continue;
                }

                var evidence = problems.Where(x => x.Category == category && x.Time >= cp.Time && x.Time <= afterTo).Take(5)
                    .Select(x => new FindingEvidence(Name(x.Category), x.Id, x.GroupKey, x.Time)).ToList();
                results.Add(new Finding
                {
                    Id = $"assoc-change-{cp.Time.ToUnixTimeMilliseconds()}-{category}",
                    Tier = FindingTier.Association,
                    Title = $"{cp.Label} 이후 {Name(category)} 빈도 {(increased ? "증가" : "감소")}",
                    Detail = string.Create(CultureInfo.InvariantCulture,
                                 $"이전 {kb}건/{beforeDays:0.#}일({kb / beforeDays:0.##}/일) → 이후 {ka}건/{afterDays:0.#}일({ka / afterDays:0.##}/일), 정확 검정 p={p:0.###}. ")
                             + (cp.Approximate ? "변경 시각은 스냅샷 비교 구간의 끝을 기준으로 했습니다. " : ""),
                    Severity = increased ? (DiagSeverity)Math.Max((int)DiagSeverity.Warning,
                        (int)problems.Where(x => x.Category == category && x.Time >= cp.Time).Select(x => x.Severity).DefaultIfEmpty(DiagSeverity.Warning).Max()) : DiagSeverity.Info,
                    Evidence = evidence,
                    Caution = "시간적 선후 관계일 뿐 원인을 입증하지 않습니다. 같은 기간의 사용 패턴(게임·AI 작업 시간 등) 변화도 빈도에 영향을 줍니다."
                              + (others.Count > 0 ? $" {string.Join(" / ", others)}." : ""),
                    Score = (increased ? 300 : 100) - 100 * (p ?? 1),
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Events of a category in a period. For crash categories the events written at one boot (Kernel-Power 41 and
    /// EventLog 6008, or several dump records) count as one occurrence.
    /// </summary>
    internal static int CountOccurrences(IEnumerable<EventPoint> points, string category, DateTimeOffset from, DateTimeOffset to)
    {
        var times = points.Where(p => p.Category == category && p.Time >= from && p.Time <= to).Select(p => p.TimeMs).Order().ToList();
        if (!IsSystemCrash(category))
        {
            return times.Count;
        }

        var count = 0;
        long? start = null;
        foreach (var t in times)
        {
            if (start is null || t - start.Value > TimeSpan.FromMinutes(10).TotalMilliseconds)
            {
                count++;
                start = t;
            }
        }

        return count;
    }

    private static IEnumerable<Finding> CoOccurrence(List<EventPoint> problems, IReadOnlyList<EventPoint> points, DiagnosticOptions options)
    {
        var windowMs = options.CorrelationWindowSeconds * 1000L;
        // Approximate "time the PC was on" by the hours that have any event: events cluster in uptime, so the whole
        // calendar range would overstate how rare a coincidence is.
        var activeMs = Math.Max(3_600_000, points.Select(p => p.TimeMs / 3_600_000).Distinct().Count() * 3_600_000L);
        var byCategory = problems.GroupBy(p => p.Category).Where(g => g.Count() >= 2).ToDictionary(g => g.Key, g => g.OrderBy(p => p.TimeMs).ToList());
        var results = new List<Finding>();
        var keys = byCategory.Keys.Order(StringComparer.Ordinal).ToList();
        for (var i = 0; i < keys.Count; i++)
        {
            for (var j = i + 1; j < keys.Count; j++)
            {
                var a = keys[i];
                var b = keys[j];
                if (SameIncidentPairs.Contains((a, b)) || SameIncidentPairs.Contains((b, a)))
                {
                    continue;
                }

                var listA = byCategory[a];
                var listB = byCategory[b];
                var pairs = new List<(EventPoint A, EventPoint B)>();
                foreach (var ea in listA)
                {
                    var match = listB.FirstOrDefault(eb => Math.Abs(eb.TimeMs - ea.TimeMs) <= windowMs);
                    if (match is not null)
                    {
                        pairs.Add((ea, match));
                    }
                }

                var observed = pairs.Count;
                var expected = listA.Count * Math.Min(1.0, listB.Count * 2.0 * windowMs / activeMs);
                if (observed < 2 || observed < 3 * expected)
                {
                    continue;
                }

                var p = RateStatistics.PoissonUpperTail(observed, Math.Max(expected, 1e-9));
                if (p >= 0.01)
                {
                    continue;
                }

                results.Add(new Finding
                {
                    Id = $"assoc-co-{a}-{b}",
                    Tier = FindingTier.Association,
                    Title = $"{Name(a)} ↔ {Name(b)}: 같은 시각에 함께 기록되는 경향",
                    Detail = string.Create(CultureInfo.InvariantCulture,
                        $"{Name(a)} {listA.Count}건 중 {observed}건이 {Name(b)}와 {options.CorrelationWindowSeconds}초 이내에 기록되었습니다. 우연히 겹칠 것으로 기대되는 수는 약 {expected:0.##}건입니다 (p={p:0.####})."),
                    Severity = (DiagSeverity)Math.Max((int)listA.Max(x => x.Severity), (int)listB.Max(x => x.Severity)),
                    Evidence = pairs.Take(5).Select(x => new FindingEvidence($"{TimeDisplay.Local.Full(x.A.Time)} {Name(x.A.Category)} ↔ {Name(x.B.Category)}", x.A.Id, x.A.GroupKey, x.A.Time)).ToList(),
                    Caution = "같은 시각에 함께 기록된다는 것은 공통 원인이나 한쪽이 다른 쪽을 일으켰을 가능성을 시사할 뿐, 어느 쪽이 원인인지는 알려 주지 않습니다.",
                    Score = 200 + observed - 100 * p,
                });
            }
        }

        return results;
    }

    private static IEnumerable<Finding> PreCrashPatterns(IReadOnlyList<CrashIncident> crashes)
    {
        if (crashes.Count < 2)
        {
            yield break;
        }

        var categories = crashes.SelectMany(c => c.EventsBefore.Select(e => e.Category).Distinct()).GroupBy(c => c);
        foreach (var category in categories)
        {
            var count = category.Count();
            if (count < 2 || count < crashes.Count / 2.0)
            {
                continue;
            }

            yield return new Finding
            {
                Id = "assoc-precrash-" + category.Key,
                Tier = FindingTier.Association,
                Title = $"비정상 종료 직전에 {Name(category.Key)} 기록이 반복됨",
                Detail = $"비정상 종료 {crashes.Count}건 중 {count}건에서 직전 30분 이내에 {Name(category.Key)}이(가) 먼저 기록되었습니다.",
                Severity = DiagSeverity.Error,
                Evidence = crashes.Where(c => c.EventsBefore.Any(e => e.Category == category.Key))
                    .Select(c => new FindingEvidence($"{TimeDisplay.Local.Full(c.ReferenceTime)} {c.KindText}", c.EventsBefore.First(e => e.Category == category.Key).Id)).ToList(),
                Caution = "직전에 기록되었다는 것만으로 그 문제가 종료를 일으켰다고 볼 수는 없습니다.",
                Score = 250 + count,
            };
        }
    }

    private static IEnumerable<Finding> SensorAssociations(DiagnosticsStore store, AnalysisRange range, List<EventPoint> problems,
        IReadOnlyList<CrashIncident> crashes, DiagnosticOptions options)
    {
        var overall = store.QuerySensors(range.From, range.To, maxPoints: 5000).Gpu.Where(g => g.GpuIndex == 0).ToList();
        if (overall.Count < 20)
        {
            yield break;
        }

        var powers = overall.Select(g => g.PowerW).OfType<double>().Order().ToList();
        var targets = problems.Where(p => p.Category is GpuDriver or AppCrash or AppHang || IsHardware(p.Category))
            .GroupBy(p => p.Category)
            .Select(g => (Name: Name(g.Key), Key: g.Key, Times: g.Select(p => p.TimeMs).ToList()))
            .ToList();
        if (crashes.Count > 0)
        {
            targets.Add(("시스템 비정상 종료", "crash", crashes.Select(c => c.ReferenceTime.ToUnixTimeMilliseconds()).ToList()));
        }

        foreach (var (name, key, times) in targets)
        {
            var samples = new List<GpuSensorPoint>();
            foreach (var t in times)
            {
                samples.AddRange(store.QuerySensors(DateTimeOffset.FromUnixTimeMilliseconds(t) - TimeSpan.FromMinutes(2), DateTimeOffset.FromUnixTimeMilliseconds(t), 100_000).Gpu);
            }

            var power = SensorContext.MeansBefore(samples, times, TimeSpan.FromMinutes(2), g => g.PowerW);
            var temp = SensorContext.MeansBefore(samples, times, TimeSpan.FromMinutes(2), g => g.TempC);
            if (power.Count < 2 || powers.Count < 20)
            {
                continue;
            }

            var mean = power.Average();
            // Mid-rank: many samples at the same value (for example at the power limit) must not look like the very top.
            var rank = (powers.Count(v => v < mean) + 0.5 * powers.Count(v => v == mean)) / powers.Count;
            var covered = $"{times.Count}건 중 {power.Count}건에 직전 센서 기록 있음";
            if (rank >= 0.9)
            {
                yield return new Finding
                {
                    Id = $"assoc-sensor-power-{key}",
                    Tier = FindingTier.Association,
                    Title = $"{name}: 기록 직전 GPU 고부하 경향",
                    Detail = string.Create(CultureInfo.InvariantCulture,
                        $"직전 2분 GPU 전력 평균 {mean:0} W — 분석 기간 전체 GPU 전력 분포의 상위 {Math.Max(1, (1 - rank) * 100):0}% 수준입니다 ({covered})."),
                    Severity = DiagSeverity.Warning,
                    Caution = "고부하는 정상적인 사용(게임·AI 추론)일 수 있습니다. 부하가 높을 때 문제가 드러나는 경향을 뜻할 뿐 원인을 입증하지 않습니다.",
                    Score = 150 + power.Count,
                };
            }

            if (temp.Count >= 2 && temp.Average() >= options.HighGpuTempC)
            {
                yield return new Finding
                {
                    Id = $"assoc-sensor-temp-{key}",
                    Tier = FindingTier.Association,
                    Title = $"{name}: 기록 직전 GPU 온도 높음",
                    Detail = string.Create(CultureInfo.InvariantCulture,
                        $"직전 2분 GPU 온도 평균 {temp.Average():0}°C (설정한 고온 기준 {options.HighGpuTempC}°C 이상, {covered})."),
                    Severity = DiagSeverity.Warning,
                    Caution = "온도가 높았다는 관측이며, 그 온도가 문제를 일으켰는지는 별도 확인이 필요합니다.",
                    Score = 160 + temp.Count,
                };
            }
        }
    }

    // ------------------------------------------------------------------ hypotheses

    private static List<Finding> BuildHypotheses(List<EventPoint> problems, IReadOnlyList<CrashIncident> crashes, IReadOnlyList<RepeatPattern> repeats,
        IReadOnlyList<Finding> associations, DiagnosticsStore store, AnalysisRange range, DiagnosticOptions options)
    {
        int Count(Func<string, bool> category) => problems.Count(p => category(p.Category));
        bool Assoc(string text) => associations.Any(a => a.Title.Contains(text, StringComparison.Ordinal));
        var codes = crashes.Where(c => c.BugCheckCode is not null).Select(c => c.BugCheckCode!.Value).ToList();
        var list = new List<Finding>();

        var gpu = Count(c => c == GpuDriver);
        var gpuCodes = codes.Where(c => GpuBugChecks.Contains(c)).ToList();
        var gpuModules = repeats.Where(r => r.Group.Category == AppCrash && IsGpuModule(r.Group.Signature)).ToList();
        if (gpu >= 2 || gpuCodes.Count > 0 || gpuModules.Count > 0)
        {
            var strong = (gpu >= 3 && (Assoc(Name(GpuDriver)) || gpuCodes.Count > 0)) || gpuCodes.Count >= 2;
            var evidence = new List<FindingEvidence>();
            if (gpu > 0)
            {
                evidence.Add(new FindingEvidence($"GPU 드라이버 오류·재설정 {gpu}건"));
            }

            evidence.AddRange(gpuCodes.Select(c => new FindingEvidence($"BugCheck {BugCheckCatalog.Describe(c)}")));
            evidence.AddRange(gpuModules.Select(r => new FindingEvidence($"그래픽 드라이버 모듈에서 반복된 프로그램 충돌: {r.Group.Title} {r.Group.Count}회", r.Group.LatestEventId, r.Group.GroupKey)));
            evidence.AddRange(associations.Where(a => a.Title.Contains(Name(GpuDriver), StringComparison.Ordinal) || a.Title.Contains("GPU", StringComparison.Ordinal))
                .Select(a => new FindingEvidence("연관성: " + a.Title)));
            list.Add(Hypothesis("hyp-gpu", "GPU·디스플레이 드라이버 쪽 문제 (드라이버 버전, GPU 설정, 전원·발열 포함)", evidence,
                strong ? "강함" : gpu >= 3 || gpuCodes.Count > 0 ? "보통" : "약함",
                [
                    "문제가 없던 이전 GPU 드라이버 버전으로 되돌린 뒤 같은 작업에서 재현되는지 비교합니다 (비교 화면에서 드라이버 변경 시점을 기준선으로 지정). 드라이버 설치·제거는 이 프로그램이 하지 않습니다.",
                    "GPU 오버클럭·언더볼트·전력 제한 변경을 기본값으로 되돌린 상태에서 비교합니다.",
                    "하드웨어 화면에서 오류 시각 전후의 GPU 온도·전력·클럭을 확인합니다.",
                    "그래픽카드 보조 전원 커넥터 체결 상태와 전원 케이블을 점검합니다.",
                    "다른 프로그램이나 벤치마크에서도 같은 오류가 나는지 확인해 특정 프로그램 문제와 구분합니다.",
                ],
                "GPU 오류 기록만으로 GPU 하드웨어 불량이나 특정 드라이버 결함을 확정할 수 없습니다.",
                DiagSeverity.Error, gpu + 10 * gpuCodes.Count));
        }

        var whea = Count(c => c is HardwareCpu or HardwareMemory or HardwareOther);
        var wheaCodes = codes.Where(c => HardwareBugChecks.Contains(c)).ToList();
        if (whea > 0 || wheaCodes.Count > 0)
        {
            var settingChange = Assoc("EXPO") || Assoc("메모리") || Assoc("BIOS") || Assoc("전압");
            var evidence = new List<FindingEvidence> { new($"CPU·메모리 관련 WHEA 하드웨어 오류 {whea}건") };
            evidence.AddRange(wheaCodes.Select(c => new FindingEvidence($"BugCheck {BugCheckCatalog.Describe(c)}")));
            evidence.AddRange(associations.Where(a => a.Title.Contains("CPU", StringComparison.Ordinal) || a.Title.Contains("메모리", StringComparison.Ordinal)
                                                      || a.Title.Contains("하드웨어", StringComparison.Ordinal)).Select(a => new FindingEvidence("연관성: " + a.Title)));
            list.Add(Hypothesis("hyp-cpu-memory", "CPU·메모리·패브릭 안정성 문제 (EXPO/XMP, PBO, 전압·클럭 설정 포함)", evidence,
                (whea >= 3 && settingChange) || wheaCodes.Count > 0 ? "강함" : whea >= 3 ? "보통" : "약함",
                [
                    "EXPO/XMP, PBO, Curve Optimizer, 메모리·SoC 전압 등 바꾼 설정을 기본값으로 되돌린 기간과 WHEA 건수를 비교합니다 (사용자 기록 + 비교 화면).",
                    "Windows 메모리 진단 또는 신뢰할 수 있는 메모리 검사 도구로 메모리를 검사합니다.",
                    "BIOS/UEFI 버전을 확인하고, 변경할 경우 타임라인의 사용자 기록으로 남깁니다.",
                    "WHEA 이벤트 상세에서 MCA 뱅크·APIC ID가 매번 같은지 확인합니다 (특정 코어·뱅크 반복 여부).",
                ],
                "수정된(corrected) 하드웨어 오류는 시스템이 스스로 바로잡은 오류이며, 어느 부품의 문제인지는 이 기록만으로 확정할 수 없습니다.",
                wheaCodes.Count > 0 ? DiagSeverity.Critical : DiagSeverity.Error, whea + 10 * wheaCodes.Count));
        }

        var pcie = Count(c => c == HardwarePcie);
        if (pcie > 0)
        {
            list.Add(Hypothesis("hyp-pcie", "PCIe 장치·링크 문제 (슬롯, 라이저, 링크 속도 설정, 장치 자체)",
                [new FindingEvidence($"PCIe WHEA 오류 {pcie}건")], pcie >= 3 ? "보통" : "약함",
                [
                    "WHEA 이벤트 상세의 Vendor/Device ID로 어느 장치인지 확인합니다.",
                    "장치의 슬롯 장착, 라이저 케이블 사용 여부를 점검합니다.",
                    "BIOS에서 PCIe 링크 속도를 자동 대신 한 단계 낮게 고정했을 때와 비교합니다 (변경 시 사용자 기록 권장).",
                ],
                "수정된 PCIe 오류는 신호 품질 문제에서도 나타나며, 장치 고장을 뜻하지 않을 수 있습니다.", DiagSeverity.Warning, pcie));
        }

        var noBugcheck = crashes.Where(c => c.Kind != CrashKind.BugCheck).ToList();
        if (noBugcheck.Count > 0)
        {
            list.Add(Hypothesis("hyp-power", "전원 공급 중단 또는 시스템 완전 정지 후 강제 재시작",
                noBugcheck.Select(c => new FindingEvidence($"{TimeDisplay.Local.Full(c.ReferenceTime)} {c.KindText}", c.Evidence.FirstOrDefault()?.Id)).ToList(),
                noBugcheck.Count >= 2 ? "보통" : "약함",
                [
                    "그 시각에 정전, 멀티탭·차단기 문제, 전원 버튼 조작이 있었는지 확인합니다.",
                    "직전 센서 기록이 있다면 높은 부하에서만 반복되는지 확인합니다 (진단 화면의 충돌 상세).",
                    "PSU 케이블과 커넥터를 점검하고, 가능하면 다른 PSU로 교차 확인합니다.",
                    "BugCheck였다면 덤프가 남았어야 하므로 덤프 생성 설정과 volmgr 기록을 확인합니다.",
                ],
                "Kernel-Power 41과 BugCheck 정보 없음만으로 파워서플라이 고장이 입증되지 않습니다.", DiagSeverity.Error, 5 * noBugcheck.Count));
        }

        var storage = Count(c => c == EventCategories.Storage);
        var storageCodes = codes.Where(c => StorageBugChecks.Contains(c)).ToList();
        if (storage > 0 || storageCodes.Count > 0)
        {
            list.Add(Hypothesis("hyp-storage", "저장장치·연결 문제 (장치, 케이블·슬롯, 컨트롤러 드라이버, 펌웨어)",
                [new FindingEvidence($"디스크·스토리지 오류 {storage}건"), .. storageCodes.Select(c => new FindingEvidence($"BugCheck {BugCheckCatalog.Describe(c)}"))],
                storage >= 3 || storageCodes.Count > 0 ? "보통" : "약함",
                [
                    "중요한 데이터를 먼저 백업합니다.",
                    "저장장치 제조사 도구로 SMART 상태와 펌웨어 버전을 확인합니다.",
                    "케이블·M.2 슬롯 장착 상태를 점검합니다.",
                ],
                "재시도·재설정 기록은 일시적인 지연에서도 나타날 수 있어 장치 고장을 확정하지 않습니다.", DiagSeverity.Warning, storage + 10 * storageCodes.Count));
        }

        foreach (var app in repeats.Where(r => r.Group.Category is AppCrash or AppHang && r.Group.Count >= options.RepeatThreshold).Take(5))
        {
            var gpuModule = IsGpuModule(app.Group.Signature);
            list.Add(Hypothesis("hyp-app-" + app.Group.GroupKey, $"특정 프로그램 문제: {app.Group.Title}",
                [new FindingEvidence($"같은 프로그램·모듈·예외로 {app.Group.Count}회", app.Group.LatestEventId, app.Group.GroupKey, app.Group.Last)],
                app.Group.Count >= 5 ? "보통" : "약함",
                [
                    "해당 프로그램(과 플러그인)을 최신 버전으로 업데이트하거나 다시 설치한 뒤 비교합니다.",
                    "같은 시각에 다른 오류(GPU, WHEA)가 함께 기록되었는지 타임라인에서 확인합니다.",
                    gpuModule ? "오류 모듈이 그래픽 드라이버 구성 요소이므로 GPU 드라이버 후보와 함께 검토합니다." : "오류 모듈의 버전과 제공자를 확인합니다.",
                ],
                "오류가 드러난 모듈이 원인 모듈이라는 뜻은 아닙니다.", DiagSeverity.Warning, (int)Math.Min(app.Group.Count, 1000)));
        }

        foreach (var service in repeats.Where(r => r.Group.Category == ServiceFailure && r.Group.Count >= options.RepeatThreshold).Take(3))
        {
            list.Add(Hypothesis("hyp-service-" + service.Group.GroupKey, $"서비스 자체 문제: {service.Group.Title}",
                [new FindingEvidence($"{service.Group.Count}회 반복", service.Group.LatestEventId, service.Group.GroupKey, service.Group.Last)],
                service.Group.Count >= 5 ? "보통" : "약함",
                [
                    "서비스를 설치한 프로그램을 업데이트하거나 복구합니다.",
                    "서비스 자체 로그가 있다면 같은 시각을 확인합니다.",
                ],
                "서비스 종료가 다른 문제의 결과일 수도 있습니다.", DiagSeverity.Warning, (int)Math.Min(service.Group.Count, 1000)));
        }

        var updates = Count(c => c == UpdateFailure);
        if (updates >= 2)
        {
            list.Add(Hypothesis("hyp-update", "Windows 업데이트 설치 문제", [new FindingEvidence($"업데이트 실패 {updates}건")], updates >= 4 ? "보통" : "약함",
                [
                    "실패 이벤트의 오류 코드를 Microsoft 문서에서 확인합니다.",
                    "Windows 설정의 업데이트 문제 해결사를 실행하고 시스템 드라이브 여유 공간을 확인합니다.",
                ],
                "업데이트 실패는 시스템 불안정과 무관할 수도 있습니다.", DiagSeverity.Warning, updates));
        }

        var driverFailures = Count(c => c == DriverFailure);
        if (driverFailures >= 2)
        {
            list.Add(Hypothesis("hyp-driver-load", "장치 드라이버 로드·시작 문제", [new FindingEvidence($"드라이버·장치 문제 {driverFailures}건")], "약함",
                ["장치 관리자에서 해당 장치 상태와 문제 코드를 확인합니다.", "장치 제조사의 최신 드라이버를 확인합니다 (설치는 사용자가 판단)."],
                "부팅 때마다 기록되지만 영향이 없는 경우도 있습니다.", DiagSeverity.Info, driverFailures));
        }

        if (Count(c => c == Resource) > 0)
        {
            list.Add(Hypothesis("hyp-memory-pressure", "메모리(커밋) 부족", [new FindingEvidence($"자원 부족 진단 {Count(c => c == Resource)}건")], "보통",
                ["메시지에 기록된 메모리를 많이 쓴 프로그램을 확인합니다.", "페이지 파일 설정(시스템 관리 크기)을 확인합니다."],
                null, DiagSeverity.Warning, 3));
        }

        if (associations.Any(a => a.Id.StartsWith("assoc-sensor-temp", StringComparison.Ordinal)))
        {
            list.Add(Hypothesis("hyp-thermal", "발열 관련 가능성", associations.Where(a => a.Id.StartsWith("assoc-sensor-temp", StringComparison.Ordinal))
                    .Select(a => new FindingEvidence("연관성: " + a.Title)).ToList(), "약함",
                ["케이스 공기 흐름, 먼지, 팬 곡선을 점검합니다.", "같은 작업을 낮은 전력 제한에서 실행해 비교합니다."],
                "온도가 높았다는 관측만으로 원인이 확정되지 않습니다.", DiagSeverity.Warning, 2));
        }

        return list;
    }

    private static Finding Hypothesis(string id, string title, IReadOnlyList<FindingEvidence> evidence, string strength, IReadOnlyList<string> verification,
        string? caution, DiagSeverity severity, int weight) => new()
    {
        Id = id,
        Tier = FindingTier.Hypothesis,
        Title = title,
        Detail = $"근거 강도: {strength} (확정 아님, 추가 검증 필요)",
        Strength = strength,
        Evidence = evidence,
        Verification = verification,
        Caution = caution,
        Severity = severity,
        Score = (strength == "강함" ? 300 : strength == "보통" ? 200 : 100) + 10 * (int)severity + Math.Min(weight, 50),
    };

    private static bool IsGpuModule(string signature)
    {
        var module = signature.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("module=", StringComparison.Ordinal))?["module=".Length..];
        return module is not null && (module.StartsWith("nv", StringComparison.Ordinal) || module.StartsWith("amd", StringComparison.Ordinal)
                                      || module.StartsWith("ati", StringComparison.Ordinal) || module.StartsWith("igd", StringComparison.Ordinal)
                                      || module.StartsWith("igxe", StringComparison.Ordinal) || module.Contains("d3d", StringComparison.Ordinal)
                                      || module.Contains("dxgi", StringComparison.Ordinal));
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
