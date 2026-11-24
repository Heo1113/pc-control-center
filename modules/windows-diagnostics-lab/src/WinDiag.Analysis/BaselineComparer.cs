using System.Globalization;
using WinDiag.Core.Events;
using WinDiag.Core.Records;
using WinDiag.Core.Snapshots;
using WinDiag.Core.Time;
using WinDiag.Storage;

namespace WinDiag.Analysis;

/// <summary>
/// Compares the periods before and after a baseline (a change such as a BIOS update or EXPO on): error frequency per
/// category with an exact rate test, problems that appeared or disappeared, sensor statistics, configuration
/// differences, and — so the comparison is not over-read — other changes in the same periods.
/// </summary>
public static class BaselineComparer
{
    public static BaselineComparison Compare(DiagnosticsStore store, string name, DateTimeOffset baseline, int beforeDays, int afterDays,
        DateTimeOffset now, long? baselineUserRecordId = null)
    {
        var notes = new List<string>();
        var before = new AnalysisRange(baseline.AddDays(-beforeDays), baseline);
        var after = new AnalysisRange(baseline, Min(baseline.AddDays(afterDays), now));
        if (after.To <= after.From)
        {
            notes.Add("기준선 이후 기간이 아직 없습니다 (기준 시각이 지금 이후).");
        }

        // Event data exists only from the earliest history the app imported (or Windows still had).
        var coverageStart = store.GetChannelStates()
            .Where(s => s.Status == ChannelStatus.Ok)
            .Select(s => s.HistoryFrom ?? s.OldestEventTime)
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .DefaultIfEmpty(before.From)
            .Min();
        var effectiveBeforeFrom = Max(before.From, coverageStart);
        if (effectiveBeforeFrom > before.From)
        {
            notes.Add($"이전 구간 중 {TimeDisplay.Local.Full(before.From)} ~ {TimeDisplay.Local.Full(effectiveBeforeFrom)}에는 이벤트 기록이 없습니다. 비교에는 기록이 있는 기간만 사용합니다.");
        }

        var beforeDaysEffective = Math.Max((before.To - effectiveBeforeFrom).TotalDays, 1.0 / 24);
        var afterDaysEffective = Math.Max(after.Length.TotalDays, 1.0 / 24);
        if (Math.Abs(beforeDaysEffective - afterDaysEffective) > 0.5)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"두 구간의 길이가 다릅니다 (이전 {beforeDaysEffective:0.#}일, 이후 {afterDaysEffective:0.#}일). 건수 대신 하루당 빈도로 비교합니다."));
        }

        var beforePoints = store.LoadEventPoints(new EventQuery { From = effectiveBeforeFrom, To = before.To.AddTicks(-1), Limit = 1_000_000 });
        var afterPoints = store.LoadEventPoints(new EventQuery { From = after.From, To = after.To, Limit = 1_000_000 });
        var includesVirtual = beforePoints.Any(p => p.IsVirtual) || afterPoints.Any(p => p.IsVirtual);
        if (includesVirtual)
        {
            notes.Add("가상(테스트) 데이터가 포함되어 있습니다. 실제 PC의 측정 결과가 아닙니다.");
        }

        RateComparison Rate(string key, string label, int b, int a)
        {
            var p = RateStatistics.RateRatioPValue(b, beforeDaysEffective, a, afterDaysEffective);
            var br = b / beforeDaysEffective;
            var ar = a / afterDaysEffective;
            return new RateComparison(key, label, b, a, br, ar, br > 0 ? ar / br : null, p, RateStatistics.Assess(b, a, p));
        }

        var categories = EventCategories.All
            .Where(c => c.Kind is TimelineKind.Error or TimelineKind.ProgramCrash or TimelineKind.SystemCrash)
            .Select(c => Rate(c.Key, c.Name,
                DiagnosticAnalyzer.CountOccurrences(beforePoints.Where(p => p.Severity >= DiagSeverity.Warning), c.Key, effectiveBeforeFrom, before.To),
                DiagnosticAnalyzer.CountOccurrences(afterPoints.Where(p => p.Severity >= DiagSeverity.Warning), c.Key, after.From, after.To)))
            .Where(r => r.BeforeCount + r.AfterCount > 0)
            .OrderByDescending(r => Math.Abs(r.AfterPerDay - r.BeforePerDay))
            .ToList();

        var severities = new[] { DiagSeverity.Critical, DiagSeverity.Error, DiagSeverity.Warning }
            .Select(s => Rate(s.ToString(), LevelText.Severity(s), beforePoints.Count(p => p.Severity == s), afterPoints.Count(p => p.Severity == s)))
            .ToList();

        var crashesBefore = CrashAnalyzer.Analyze(store, new AnalysisRange(effectiveBeforeFrom, before.To));
        var crashesAfter = CrashAnalyzer.Analyze(store, after);
        var crashes = Rate("crash", "시스템 충돌·비정상 종료", crashesBefore.Count, crashesAfter.Count);

        var beforeGroups = store.QueryGroups(new EventQuery { From = effectiveBeforeFrom, To = before.To.AddTicks(-1), MinSeverity = DiagSeverity.Warning, Limit = 2000 })
            .ToDictionary(g => g.GroupKey);
        var afterGroups = store.QueryGroups(new EventQuery { From = after.From, To = after.To, MinSeverity = DiagSeverity.Warning, Limit = 2000 })
            .ToDictionary(g => g.GroupKey);
        var newGroups = afterGroups.Values.Where(g => !beforeGroups.ContainsKey(g.GroupKey))
            .Select(g => new GroupChange(g.GroupKey, g.Title, g.Category, 0, (int)g.Count)).OrderByDescending(g => g.AfterCount).Take(20).ToList();
        var goneGroups = beforeGroups.Values.Where(g => !afterGroups.ContainsKey(g.GroupKey))
            .Select(g => new GroupChange(g.GroupKey, g.Title, g.Category, (int)g.Count, 0)).OrderByDescending(g => g.BeforeCount).Take(20).ToList();
        var changedGroups = afterGroups.Values.Where(g => beforeGroups.ContainsKey(g.GroupKey))
            .Select(g => new GroupChange(g.GroupKey, g.Title, g.Category, (int)beforeGroups[g.GroupKey].Count, (int)g.Count))
            .OrderByDescending(g => Math.Abs(g.AfterCount / afterDaysEffective - g.BeforeCount / beforeDaysEffective)).Take(20).ToList();

        var sensorBefore = store.AggregateSensors(effectiveBeforeFrom, before.To);
        var sensorAfter = store.AggregateSensors(after.From, after.To);
        var sensors = sensorBefore.Zip(sensorAfter)
            .Where(p => p.First.Count > 0 || p.Second.Count > 0)
            .Select(p => new SensorComparison(p.First.Metric, SensorMetrics.Name(p.First.Metric), SensorMetrics.Unit(p.First.Metric), p.First, p.Second))
            .ToList();
        if (sensors.Count == 0)
        {
            notes.Add("두 구간 중 하나 이상에 센서 기록이 없어 하드웨어 상태는 비교하지 않았습니다 (센서는 앱 실행 중에만 기록).");
        }
        else if (sensors.Any(s => s.Before.Count == 0 || s.After.Count == 0))
        {
            notes.Add("일부 센서 값은 한쪽 구간에만 있습니다. 해당 항목은 비교할 수 없습니다.");
        }

        var configChanges = new List<ConfigChange>();
        var snapBefore = store.SnapshotAt(baseline);
        var snapAfter = store.SnapshotAt(after.To);
        if (snapBefore is not null && snapAfter is not null && snapBefore.Id != snapAfter.Id)
        {
            if (snapBefore.IsMock != snapAfter.IsMock)
            {
                notes.Add("기준선 전후의 구성 스냅샷 중 하나가 MOCK이라 구성을 비교하지 않았습니다.");
            }
            else
            {
                configChanges.AddRange(SnapshotDiff.Compare(SystemSnapshot.FromJson(snapBefore.Json), SystemSnapshot.FromJson(snapAfter.Json)));
            }
        }
        else if (snapBefore is null)
        {
            notes.Add("기준선 이전의 구성 스냅샷이 없어 구성 차이를 비교할 수 없습니다 (스냅샷은 앱을 처음 실행한 뒤부터 저장).");
        }

        var others = new List<string>();
        foreach (var record in store.ListUserRecords(before.From, after.To).Where(r => r.Id != baselineUserRecordId && Math.Abs((r.Time - baseline).TotalMinutes) > 1))
        {
            others.Add($"[사용자 입력] {TimeDisplay.Local.Full(record.Time)} {UserRecordKinds.Name(record.Kind)}: {record.Title}");
        }

        foreach (var change in SnapshotDiff.Detect(store.ListSnapshots()).Where(c => c.WindowTo >= before.From && c.WindowTo <= after.To))
        {
            others.Add($"[스냅샷 비교] {TimeDisplay.Local.Full(change.WindowFrom)}~{TimeDisplay.Local.Full(change.WindowTo)}: "
                       + string.Join(", ", change.Changes.Take(3).Select(c => c.Describe())));
        }

        foreach (var group in beforePoints.Concat(afterPoints)
                     .Where(p => p.Category is EventCategories.DriverInstall or EventCategories.UpdateInstall or EventCategories.SoftwareInstall)
                     .GroupBy(p => (p.Category, Day: p.Time.ToLocalTime().Date)))
        {
            others.Add(string.Create(CultureInfo.InvariantCulture, $"[Windows 이벤트] {group.Key.Day:yyyy-MM-dd} {EventCategories.Name(group.Key.Category)} {group.Count()}건"));
        }

        if (others.Count > 0)
        {
            notes.Add("같은 기간에 다른 변경도 있습니다. 빈도 차이를 이 기준선 하나의 영향으로 단정할 수 없습니다.");
        }

        notes.Add("전후 비교는 시간적 관계만 보여 줍니다. 원인을 확정하려면 같은 조건에서 되돌려 보는 등 추가 검증이 필요합니다.");
        return new BaselineComparison
        {
            BaselineName = name,
            BaselineTime = baseline,
            Before = new AnalysisRange(effectiveBeforeFrom, before.To),
            After = after,
            BeforeDays = beforeDaysEffective,
            AfterDays = afterDaysEffective,
            Categories = categories,
            Severities = severities,
            Crashes = crashes,
            NewGroups = newGroups,
            GoneGroups = goneGroups,
            ChangedGroups = changedGroups,
            Sensors = sensors,
            ConfigChanges = configChanges,
            OtherChanges = others,
            Notes = notes,
            IncludesVirtual = includesVirtual,
        };
    }

    public static string Summary(BaselineComparison c)
    {
        var lines = new List<string>
        {
            $"기준선 '{c.BaselineName}' ({TimeDisplay.Local.Full(c.BaselineTime)}) 전후 비교: 이전 {c.BeforeDays.ToString("0.#", CultureInfo.InvariantCulture)}일, 이후 {c.AfterDays.ToString("0.#", CultureInfo.InvariantCulture)}일",
        };
        lines.AddRange(c.Categories.Take(5).Select(r =>
            $"- {r.Name}: {r.BeforeCount}건({r.BeforePerDay.ToString("0.##", CultureInfo.InvariantCulture)}/일) → {r.AfterCount}건({r.AfterPerDay.ToString("0.##", CultureInfo.InvariantCulture)}/일), {r.Assessment}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
