using System.Globalization;
using WinDiag.Analysis;
using WinDiag.Collection;
using WinDiag.Collection.Sources;
using WinDiag.Core.Events;
using WinDiag.Core.Privacy;
using WinDiag.Core.Settings;
using WinDiag.Core.Snapshots;
using WinDiag.Storage;

namespace WinDiag.Tests.Analysis;

/// <summary>The 30-day virtual scenario loaded into a temporary database (virtual data, never a real PC).</summary>
internal sealed class Scenario : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir;

    private Scenario(TempDir dir, DiagnosticsStore store)
    {
        _dir = dir;
        Store = store;
    }

    public DiagnosticsStore Store { get; }
    public AnalysisRange Range => AnalysisRange.LastDays(Now, 30);

    public static async Task<Scenario> CreateAsync()
    {
        var dir = new TempDir();
        var store = DiagnosticsStore.Open(dir.File("wdl.db"), new ManualTime(Now));
        var settings = new AppSettings();
        settings.Normalize();
        var collector = new EventCollector(VirtualScenario.BuildSource(Now), store, EventClassifier.Default, PrivacyMasker.Generic, () => settings, new ManualTime(Now));
        await collector.ImportHistoryAsync(Now.AddDays(-31), Now, null, TestContext.Current.CancellationToken);
        VirtualScenario.SeedSideData(store, Now);
        return new Scenario(dir, store);
    }

    public void Dispose()
    {
        Store.Dispose();
        _dir.Dispose();
    }
}

public class RateStatisticsTests
{
    [Fact]
    public void Exact_tests_match_known_values()
    {
        Assert.Equal(0.001953125, RateStatistics.BinomialTwoSided(0, 10, 0.5), 9);
        Assert.Equal(1.0, RateStatistics.BinomialTwoSided(5, 10, 0.5), 9);
        Assert.Equal(0.0625, RateStatistics.RateRatioPValue(0, 7, 5, 7)!.Value, 9);
        Assert.Equal(1 - Math.Exp(-0.1) * 1.1, RateStatistics.PoissonUpperTail(2, 0.1), 9);
        Assert.Equal(Math.Log(24), RateStatistics.LogGamma(5), 9);
        Assert.Null(RateStatistics.RateRatioPValue(0, 1, 0, 1));
        Assert.Equal("표본이 적어 판단 어려움", RateStatistics.Assess(1, 2, 0.5));
    }
}

public class SnapshotDiffTests
{
    private static SystemSnapshot Base() => VirtualScenario.MockSnapshot(Scenario.Now, "32.0.15.7680", "581.80") with { IsMock = false };

    [Fact]
    public void Reports_changed_added_and_removed_items_and_ignores_unreadable_parts()
    {
        var before = Base();
        var after = before with
        {
            Os = before.Os! with { Ubr = 7100 },
            Gpus = [before.Gpus[0] with { DriverVersion = "32.0.15.8115" }],
            NvidiaDriverVersion = "581.15",
            Hotfixes = [.. before.Hotfixes, new HotfixInfo { HotFixId = "KB5070100" }],
            Bios = null, // could not be read this time: not a change
            Memory = before.Memory! with { Modules = [before.Memory!.Modules[0] with { ConfiguredSpeedMts = 4800 }, before.Memory.Modules[1]] },
        };

        var changes = SnapshotDiff.Compare(before, after);

        Assert.Contains(changes, c => c.Area == "Windows" && c.Item == "빌드" && c.After == "26200.7100");
        Assert.Contains(changes, c => c.Area == "GPU" && c.Kind == ChangeKind.Changed && c.After!.Contains("32.0.15.8115", StringComparison.Ordinal));
        Assert.Contains(changes, c => c.Area == "GPU 드라이버" && c.Before == "581.80" && c.After == "581.15");
        Assert.Contains(changes, c => c.Area == "업데이트" && c.Item == "KB5070100" && c.Kind == ChangeKind.Added);
        Assert.Contains(changes, c => c.Area == "메모리 모듈" && c.Item == "DIMM 1");
        Assert.DoesNotContain(changes, c => c.Area.StartsWith("BIOS", StringComparison.Ordinal));
    }

    [Fact]
    public void Detect_gives_the_window_between_snapshots_and_never_mixes_mock_with_real()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        var a = Base() with { CollectedAt = Scenario.Now.AddDays(-3) };
        store.SaveSnapshot(a, "auto");
        store.SaveSnapshot(a with { CollectedAt = Scenario.Now.AddDays(-2) }, "auto");
        store.SaveSnapshot(a with { CollectedAt = Scenario.Now.AddDays(-1), NvidiaDriverVersion = "581.15" }, "auto");
        store.SaveSnapshot(a with { CollectedAt = Scenario.Now, IsMock = true, NvidiaDriverVersion = "mock" }, "auto");

        var change = Assert.Single(SnapshotDiff.Detect(store.ListSnapshots()));
        Assert.Equal(Scenario.Now.AddDays(-2), change.WindowFrom);
        Assert.Equal(Scenario.Now.AddDays(-1), change.WindowTo);
        Assert.Single(change.Changes);
    }
}

public class CrashAnalyzerTests
{
    [Fact]
    public async Task Assembles_boot_time_records_into_incidents_with_context()
    {
        using var s = await Scenario.CreateAsync();
        var incidents = CrashAnalyzer.Analyze(s.Store, s.Range);

        Assert.Equal(2, incidents.Count);
        var bugcheck = Assert.Single(incidents, i => i.Kind == CrashKind.BugCheck);
        Assert.Equal(0x116u, bugcheck.BugCheckCode);
        Assert.Equal(4, bugcheck.BugCheckParameters.Count);
        Assert.NotNull(bugcheck.BootTime);
        Assert.Contains(bugcheck.Evidence, e => e.Provider == EventCatalog.KernelPower);
        Assert.Contains(bugcheck.Evidence, e => e.Category == EventCategories.BugCheck);
        Assert.Contains(bugcheck.EventsBefore, e => e.Category == EventCategories.GpuDriver);
        Assert.Contains(bugcheck.EventsBefore, e => e.Category == EventCategories.HardwareCpu);
        Assert.NotNull(bugcheck.Sensors);
        Assert.True(bugcheck.Sensors!.IsMock);
        Assert.True(bugcheck.IsVirtual);
        Assert.Contains(bugcheck.Notes, n => n.Contains("덤프", StringComparison.Ordinal));

        var noInfo = Assert.Single(incidents, i => i.Kind == CrashKind.NoBugCheckInfo);
        Assert.Null(noInfo.BugCheckCode);
        Assert.Null(noInfo.Sensors);
        Assert.Contains(noInfo.Notes, n => n.Contains("파워서플라이", StringComparison.Ordinal));
        Assert.Contains(noInfo.Notes, n => n.Contains("센서 기록이 없습니다", StringComparison.Ordinal));
    }

    [Fact]
    public void Mismatching_codes_are_both_kept_and_a_lonely_dump_record_is_not_an_incident()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        var t = Scenario.Now.AddDays(-1);
        NewEvent New(RawEvent e) => new(e, EventClassifier.Default.Classify(e), EventSources.History, true);
        store.InsertEvents(
        [
            New(Ev.Make("Microsoft-Windows-Kernel-Power", 41, 1, t, data: [("BugcheckCode", "292"), ("PowerButtonTimestamp", "0")])),
            New(Ev.Make(EventCatalog.WerSystem, 1001, 2, t.AddSeconds(30), data: [("param1", "0x0000003b (0x1, 0x2, 0x3, 0x4)")])),
            New(Ev.Make("volmgr", 162, 4, t.AddDays(-5))),
        ]);

        var incident = Assert.Single(CrashAnalyzer.Analyze(store, new AnalysisRange(t.AddDays(-10), t.AddDays(1))));
        Assert.Equal(0x3Bu, incident.BugCheckCode);
        Assert.Contains(incident.Notes, n => n.Contains("다릅니다", StringComparison.Ordinal));
        Assert.Null(incident.BootTime);
    }

    [Fact]
    public void Event_6008_localized_time_is_parsed_only_when_plausible()
    {
        var local = new DateTimeOffset(new DateTime(2026, 10, 30, 22, 41, 10, DateTimeKind.Local));
        var e = new StoredEvent
        {
            Channel = "System", Provider = "EventLog", EventId = 6008, RecordId = 1, TimeCreated = local.AddMinutes(3), Category = EventCategories.UnexpectedShutdown,
            GroupKey = "g", Signature = "s", Title = "t", Data = [new EventDataItem(null, "‎오후 10:41:10"), new EventDataItem(null, "‎2026-10-30")],
        };
        Assert.True(CrashAnalyzer.TryParse6008(e, local.AddMinutes(3), out var parsed));
        Assert.Equal(local.ToUniversalTime(), parsed);
        Assert.False(CrashAnalyzer.TryParse6008(e, local.AddDays(5), out _));
    }
}

public class SensorContextTests
{
    [Fact]
    public async Task Shows_values_around_an_event_or_says_there_are_none()
    {
        using var s = await Scenario.CreateAsync();
        var tdr = s.Store.QueryEvents(new EventQuery { Providers = ["Display"], Limit = 1 }).Single();

        var context = SensorContext.Build(s.Store, tdr.TimeCreated, TimeSpan.FromMinutes(10));
        Assert.True(context.HasCoverage);
        Assert.True(context.IsMock);
        var power = Assert.Single(context.Stats, m => m.Metric == SensorMetrics.GpuPower);
        Assert.True(power.BeforeMax > power.AfterMean);
        Assert.NotNull(power.AtEvent);

        var empty = SensorContext.Build(s.Store, Scenario.Now.AddDays(-25), TimeSpan.FromMinutes(10));
        Assert.False(empty.HasCoverage);
        Assert.Empty(empty.Stats);
        Assert.Contains("센서 기록이 없습니다", empty.CoverageNote);
    }
}

public class TimelineAndRepeatTests
{
    [Fact]
    public async Task Timeline_merges_sources_in_time_order_and_labels_them()
    {
        using var s = await Scenario.CreateAsync();
        var items = TimelineBuilder.Build(s.Store, s.Range);

        Assert.Equal(items.OrderBy(i => i.Time).Select(i => i.Time), items.Select(i => i.Time));
        Assert.Contains(items, i => i.Source == TimelineSource.SnapshotChange && i.TimeIsApproximate);
        Assert.Contains(items, i => i.Source == TimelineSource.UserRecord && i.IsUserInput);
        Assert.Contains(items, i => i.Source == TimelineSource.Baseline);
        Assert.Equal(2, items.Count(i => i.Source == TimelineSource.CrashIncident));
        Assert.Contains(items, i => i.Source == TimelineSource.AppRun && i.Kind == TimelineKind.Hardware);
        Assert.Contains(items, i => i.Kind == TimelineKind.ProgramCrash);

        var onlyChanges = TimelineBuilder.Build(s.Store, s.Range, new TimelineOptions { Kinds = new HashSet<TimelineKind> { TimelineKind.SystemChange } });
        Assert.All(onlyChanges, i => Assert.Equal(TimelineKind.SystemChange, i.Kind));
    }

    [Fact]
    public void Repeats_close_together_are_collapsed_and_clock_reversals_are_flagged()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        var t = Scenario.Now.AddHours(-5);
        NewEvent New(RawEvent e) => new(e, EventClassifier.Default.Classify(e), EventSources.Live, true);
        store.InsertEvents(
        [
            New(Ev.Make("disk", 153, 3, t, record: 10, data: [(null, @"\Device\Harddisk1\DR1")])),
            New(Ev.Make("disk", 153, 3, t.AddMinutes(5), record: 11, data: [(null, @"\Device\Harddisk1\DR1")])),
            New(Ev.Make("disk", 153, 3, t.AddMinutes(9), record: 12, data: [(null, @"\Device\Harddisk1\DR1")])),
            New(Ev.Make("disk", 7, 2, t.AddMinutes(30), record: 13)),
            New(Ev.Make("disk", 51, 2, t.AddMinutes(-60), record: 14)), // later record, earlier time: the clock went back
        ]);

        var items = TimelineBuilder.Build(store, new AnalysisRange(t.AddHours(-2), t.AddHours(2)));
        var merged = Assert.Single(items, i => i.Source == TimelineSource.WindowsEvent && i.Count == 3);
        Assert.Equal(t.AddMinutes(9), merged.End);
        Assert.Single(items, i => i.Source == TimelineSource.TimeAnomaly);
    }

    [Fact]
    public async Task Repeat_patterns_count_and_mark_new_types()
    {
        using var s = await Scenario.CreateAsync();
        var repeats = RepeatAnalyzer.Analyze(s.Store, s.Range, threshold: 3, acknowledgedEventId: 0);
        var tdr = Assert.Single(repeats, r => r.Group.Provider == "Display");
        Assert.Equal(5, tdr.Group.Count);
        Assert.True(tdr.IsNewInRange);
        Assert.Contains(repeats, r => r.Group.Category == EventCategories.ServiceFailure && r.Group.Count == 4);
    }
}

public class BaselineComparerTests
{
    [Fact]
    public async Task Compares_rates_groups_sensors_and_configuration_and_lists_other_changes()
    {
        using var s = await Scenario.CreateAsync();
        var driver = VirtualScenario.DriverUpdate(Scenario.Now);

        var result = BaselineComparer.Compare(s.Store, "GPU 드라이버", driver, beforeDays: 14, afterDays: 14, Scenario.Now);

        var gpu = Assert.Single(result.Categories, c => c.Key == EventCategories.GpuDriver);
        Assert.Equal(0, gpu.BeforeCount);
        Assert.True(gpu.AfterCount >= 10);
        Assert.NotNull(gpu.PValue);
        Assert.True(gpu.PValue < 0.05);
        Assert.Contains(result.NewGroups, g => g.Category == EventCategories.GpuDriver);
        Assert.Contains(result.ConfigChanges, c => c.Area == "GPU 드라이버");
        Assert.Contains(result.OtherChanges, o => o.StartsWith("[사용자 입력]", StringComparison.Ordinal));
        Assert.True(result.IncludesVirtual);
        Assert.Contains(result.Notes, n => n.Contains("가상", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("원인을 확정하려면", StringComparison.Ordinal));
        Assert.Equal(2, result.Crashes!.AfterCount);
        Assert.Equal(0, result.Crashes.BeforeCount);
        Assert.NotEmpty(BaselineComparer.Summary(result));
    }

    [Fact]
    public void A_baseline_with_no_data_before_says_so_instead_of_inventing_a_rate()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        store.SaveChannelState(new ChannelState { Channel = "System", Status = ChannelStatus.Ok, HistoryFrom = Scenario.Now.AddDays(-2), LastRecordId = 1 });
        var result = BaselineComparer.Compare(store, "x", Scenario.Now.AddDays(-1), 14, 14, Scenario.Now);
        Assert.Contains(result.Notes, n => n.Contains("이벤트 기록이 없습니다", StringComparison.Ordinal));
        Assert.True(result.BeforeDays <= 1.01);
        Assert.Contains(result.Notes, n => n.Contains("센서 기록이 없어", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("스냅샷이 없어", StringComparison.Ordinal));
    }
}

public class DiagnosticAnalyzerTests
{
    [Fact]
    public async Task Report_separates_facts_associations_and_hypotheses()
    {
        using var s = await Scenario.CreateAsync();
        var report = DiagnosticAnalyzer.Analyze(s.Store, s.Range, new DiagnosticOptions(), Scenario.Now);

        Assert.All(report.Facts, f => Assert.Equal(FindingTier.Fact, f.Tier));
        Assert.All(report.Associations, f => Assert.Equal(FindingTier.Association, f.Tier));
        Assert.All(report.Hypotheses, f => Assert.Equal(FindingTier.Hypothesis, f.Tier));

        Assert.Equal(2, report.Facts.Count(f => f.Id.StartsWith("fact-crash", StringComparison.Ordinal)));
        Assert.Contains(report.Associations, a => a.Id.StartsWith("assoc-change", StringComparison.Ordinal) && a.Title.Contains("GPU", StringComparison.Ordinal));
        Assert.Contains(report.Associations, a => a.Id == $"assoc-co-{EventCategories.AppCrash}-{EventCategories.GpuDriver}");
        Assert.Contains(report.Associations, a => a.Id.StartsWith("assoc-sensor-power", StringComparison.Ordinal));
        Assert.All(report.Associations, a => Assert.False(string.IsNullOrEmpty(a.Caution)));

        var gpu = Assert.Single(report.Hypotheses, h => h.Id == "hyp-gpu");
        Assert.Equal("강함", gpu.Strength);
        Assert.NotEmpty(gpu.Verification);
        Assert.Contains("확정", gpu.Caution);
        Assert.Contains(report.Hypotheses, h => h.Id == "hyp-cpu-memory");
        Assert.Contains(report.Hypotheses, h => h.Id == "hyp-power");
        Assert.Contains(report.Hypotheses, h => h.Id.StartsWith("hyp-service", StringComparison.Ordinal));
        Assert.All(report.Hypotheses, h => Assert.Contains("확정 아님", h.Detail));

        Assert.True(report.IncludesVirtual);
        Assert.True(report.IncludesMockSensors);
        Assert.Contains(report.Limitations, l => l.Contains("원인을 입증하지 않습니다", StringComparison.Ordinal));
        Assert.Contains(report.Limitations, l => l.Contains("가상", StringComparison.Ordinal));
        Assert.Equal(2, report.Crashes.Count);
        Assert.NotEmpty(report.Changes);
    }

    [Fact]
    public void Empty_database_gives_an_empty_but_honest_report()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        var report = DiagnosticAnalyzer.Analyze(store, AnalysisRange.LastDays(Scenario.Now, 7), new DiagnosticOptions(), Scenario.Now);
        Assert.Empty(report.Facts);
        Assert.Empty(report.Hypotheses);
        Assert.Contains(report.Limitations, l => l.Contains(string.Create(CultureInfo.InvariantCulture, $"센서 기록은 약 0시간"), StringComparison.Ordinal));
    }
}
