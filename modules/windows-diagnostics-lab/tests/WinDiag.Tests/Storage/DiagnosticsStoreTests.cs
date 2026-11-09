using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Storage;
using Microsoft.Data.Sqlite;
using WinDiag.Core.Events;
using WinDiag.Core.Records;
using WinDiag.Core.Snapshots;
using WinDiag.Storage;

namespace WinDiag.Tests.Storage;

public class DiagnosticsStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static NewEvent New(RawEvent e, string source = EventSources.Live)
        => new(e, EventClassifier.Default.Classify(e), source, true);

    [Fact]
    public void Same_event_is_stored_once_even_when_read_again()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        var e = Fixtures.Event("kernel-power-41.xml");
        var first = store.InsertEvents([New(e), New(Fixtures.Event("whea-19.xml"))]);
        var second = store.InsertEvents([New(e)]);
        Assert.Equal(2, first.Inserted);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, second.Duplicates);
        Assert.Equal(2, store.CountEvents(new EventQuery()));
    }

    [Fact]
    public void Events_survive_reopening_with_classification_and_data()
    {
        using var dir = new TempDir();
        var path = dir.File("wdl.db");
        using (var store = DiagnosticsStore.Open(path))
        {
            store.InsertEvents([New(Fixtures.Event("app-error-1000.xml"))]);
        }

        using var reopened = DiagnosticsStore.Open(path);
        var stored = Assert.Single(reopened.QueryEvents(new EventQuery()));
        Assert.Equal(EventCategories.AppCrash, stored.Category);
        Assert.Equal("game.exe", stored.Get("AppName"));
        Assert.Equal(DiagSeverity.Error, stored.Severity);
        Assert.True(stored.IsMasked);
    }

    [Fact]
    public void Query_filters_search_and_groups()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        var events = new List<NewEvent>();
        for (var i = 0; i < 5; i++)
        {
            events.Add(New(Ev.Make("Display", 4101, level: 3, time: T0.AddDays(-i), data: [(null, "nvlddmkm")])));
        }

        events.Add(New(Ev.Make("Service Control Manager", 7031, time: T0, data: [("param1", "Spooler")])));
        events.Add(New(Ev.Make("Service Control Manager", 7031, time: T0.AddHours(-1), data: [("param1", "Audio 50%_x")])));
        store.InsertEvents(events);

        Assert.Equal(5, store.CountEvents(new EventQuery { Categories = [EventCategories.GpuDriver] }));
        Assert.Equal(3, store.CountEvents(new EventQuery { From = T0.AddDays(-2).AddMinutes(-1), Categories = [EventCategories.GpuDriver] }));
        Assert.Single(store.QueryEvents(new EventQuery { Text = "spooler" }));
        Assert.Single(store.QueryEvents(new EventQuery { Text = "50%_" }));
        Assert.Equal(7, store.CountEvents(new EventQuery { Text = null, MinSeverity = DiagSeverity.Error }));
        Assert.Equal(5, store.CountEvents(new EventQuery { Text = "4101" }));
        Assert.Equal(0, store.CountEvents(new EventQuery { Categories = [] }));

        var groups = store.QueryGroups(new EventQuery());
        var tdr = Assert.Single(groups, g => g.Category == EventCategories.GpuDriver);
        Assert.Equal(5, tdr.Count);
        Assert.Equal(5, tdr.DistinctDays);
        Assert.Equal(T0.AddDays(-4), tdr.FirstEver);
        Assert.Equal(3, groups.Count);
    }

    [Fact]
    public void Acknowledge_splits_new_events_from_confirmed_ones()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        store.InsertEvents([New(Ev.Make("disk", 7))]);
        var ack = store.Acknowledge();
        store.InsertEvents([New(Ev.Make("disk", 11))]);
        var fresh = store.QueryEvents(new EventQuery { AfterId = store.LastAcknowledgedEventId });
        Assert.Equal(ack, store.LastAcknowledgedEventId);
        Assert.Equal(11, Assert.Single(fresh).EventId);
    }

    [Fact]
    public void Channel_progress_and_events_commit_together()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        var state = new ChannelState { Channel = "System", Status = ChannelStatus.Ok, LastRecordId = 48211, LastScan = T0 };
        store.InsertEvents([New(Fixtures.Event("kernel-power-41.xml"))], state);
        var loaded = store.GetChannelState("System")!;
        Assert.Equal(48211, loaded.LastRecordId);
        Assert.Equal(T0, loaded.LastScan);
    }

    [Fact]
    public void Unfinished_runs_are_recovered_with_their_last_sample_and_a_gap()
    {
        using var dir = new TempDir();
        var path = dir.File("wdl.db");
        long run;
        using (var store = DiagnosticsStore.Open(path))
        {
            run = store.StartRun(T0, "test", false, 5000, false);
            store.WriteSensorSample(run, Sample(T0.AddSeconds(5)));
            store.WriteSensorSample(run, Sample(T0.AddSeconds(10)));
            // no EndRun: simulated crash
        }

        using var reopened = DiagnosticsStore.Open(path);
        var recovered = Assert.Single(reopened.RecoverUnfinishedRuns());
        Assert.Equal(run, recovered.Id);
        Assert.Equal(T0.AddSeconds(10), recovered.Ended);
        Assert.Contains(reopened.ListGaps(), g => g.Kind == GapKinds.AppEndedUncleanly);
        Assert.Empty(reopened.RecoverUnfinishedRuns());
        Assert.Equal(2, reopened.GetStats().SensorSamples);
    }

    [Fact]
    public void A_session_added_as_finished_is_never_taken_for_an_unclean_end()
    {
        // The virtual scenario records its past mock session this way; when seeding is interrupted after this call the
        // session must not become an "app ended uncleanly" gap (found by killing `wdl demo` while it was writing).
        using var dir = new TempDir();
        var path = dir.File("wdl.db");
        using (var store = DiagnosticsStore.Open(path))
        {
            var run = store.AddFinishedRun(T0, T0.AddHours(3), RunEndReasons.Normal, "test+virtual", isMock: true, sensorIntervalMs: 5000, elevated: false);
            store.WriteSensorSample(run, Sample(T0.AddSeconds(5)));
        }

        using var reopened = DiagnosticsStore.Open(path);
        Assert.Empty(reopened.RecoverUnfinishedRuns());
        Assert.Empty(reopened.ListGaps());
        var run1 = Assert.Single(reopened.ListRuns());
        Assert.Equal(T0.AddHours(3), run1.Ended);
        Assert.Equal(RunEndReasons.Normal, run1.EndReason);
    }

    [Fact]
    public void Sensor_samples_keep_missing_values_as_null_and_downsample_on_request()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        var run = store.StartRun(T0, "test", false, 1000, false);
        for (var i = 0; i < 600; i++)
        {
            store.WriteSensorSample(run, Sample(T0.AddSeconds(i), gpu: i % 2 == 0));
        }

        Assert.False(store.WriteSensorSample(run, Sample(T0.AddSeconds(1))));
        var full = store.QuerySensors(T0, T0.AddMinutes(10), maxPoints: 10_000);
        Assert.False(full.IsDownsampled);
        Assert.Equal(600, full.System.Count);
        Assert.Equal(300, full.Gpu.Count);
        Assert.Null(full.Gpu[0].FanPct);

        var reduced = store.QuerySensors(T0, T0.AddMinutes(10), maxPoints: 60);
        Assert.True(reduced.IsDownsampled);
        Assert.True(reduced.System.Count <= 61);

        var aggregates = store.AggregateSensors(T0, T0.AddMinutes(10));
        var temp = Assert.Single(aggregates, a => a.Metric == SensorMetrics.GpuTemp);
        Assert.Equal(300, temp.Count);
        Assert.Equal(70, temp.Max);
        var coverage = Assert.Single(store.SensorCoverage(T0, T0.AddHours(1)));
        Assert.Equal(600, coverage.Samples);
    }

    [Fact]
    public void Unchanged_snapshot_only_moves_its_confirmation_time()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        var a = new SystemSnapshot { CollectedAt = T0, Os = new OsInfo { CurrentBuild = "26100", Ubr = 4061 } };
        var first = store.SaveSnapshot(a, "auto");
        var same = store.SaveSnapshot(a with { CollectedAt = T0.AddHours(6), AppVersion = "x" }, "auto");
        var changed = store.SaveSnapshot(a with { CollectedAt = T0.AddHours(12), Os = new OsInfo { CurrentBuild = "26100", Ubr = 4202 } }, "auto");
        Assert.True(first.IsNew);
        Assert.False(same.IsNew);
        Assert.True(changed.IsNew);
        var list = store.ListSnapshots();
        Assert.Equal(2, list.Count);
        Assert.Equal(T0.AddHours(6), list[0].LastConfirmed);
        Assert.Equal(list[0].Id, store.SnapshotAt(T0.AddHours(11))!.Id);
    }

    [Fact]
    public void User_records_baselines_notes_and_comparisons_round_trip()
    {
        using var dir = new TempDir();
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"));
        var id = store.AddUserRecord(new UserRecord
        {
            Time = T0, Kind = UserRecordKinds.MemoryVoltage, Title = "VDD 1.35 → 1.40",
            Values = [new UserValueChange("VDD", "1.35", "1.40", "V")],
        });
        var baseline = store.AddBaseline(new Baseline { Name = "전압 변경", Time = T0, UserRecordId = id });
        store.SaveNote(NoteTargets.Group, "abc", "  확인 필요  ");
        store.SaveComparison(baseline, "비교", "{}");

        var record = Assert.Single(store.ListUserRecords());
        Assert.Equal("1.40", record.Values[0].After);
        Assert.Equal(id, Assert.Single(store.ListBaselines()).UserRecordId);
        Assert.Equal("확인 필요", store.GetNote(NoteTargets.Group, "abc")!.Text);
        Assert.Single(store.ListComparisons(baseline));

        store.SaveNote(NoteTargets.Group, "abc", "");
        Assert.Null(store.GetNote(NoteTargets.Group, "abc"));
        Assert.True(store.DeleteUserRecord(id));
        Assert.Null(Assert.Single(store.ListBaselines()).UserRecordId);
    }

    [Fact]
    public void Backup_validates_and_restores_without_deleting_the_previous_database()
    {
        using var dir = new TempDir();
        var path = dir.File("wdl.db");
        var backup = dir.File("backups/b1.db");
        using (var store = DiagnosticsStore.Open(path))
        {
            store.InsertEvents([New(Fixtures.Event("kernel-power-41.xml"))]);
            store.BackupTo(backup);
            Assert.Throws<IOException>(() => store.BackupTo(backup));
            store.InsertEvents([New(Fixtures.Event("whea-19.xml"))]);
        }

        var validation = DiagnosticsStore.ValidateBackup(backup);
        Assert.True(validation.IsValid, validation.Message);
        Assert.Equal(1, validation.Events);

        var aside = DiagnosticsStore.RestoreFrom(backup, path);
        Assert.NotNull(aside);
        Assert.True(File.Exists(aside));
        using var restored = DiagnosticsStore.Open(path);
        Assert.Equal(1, restored.CountEvents(new EventQuery()));
    }

    [Fact]
    public void Invalid_backup_is_refused_before_anything_changes()
    {
        using var dir = new TempDir();
        var path = dir.File("wdl.db");
        using (var store = DiagnosticsStore.Open(path))
        {
            store.InsertEvents([New(Fixtures.Event("kernel-power-41.xml"))]);
        }

        var bogus = dir.File("bogus.db");
        File.WriteAllText(bogus, "not a database");
        Assert.False(DiagnosticsStore.ValidateBackup(bogus).IsValid);
        Assert.Throws<InvalidDataException>(() => DiagnosticsStore.RestoreFrom(bogus, path));
        using var unchanged = DiagnosticsStore.Open(path);
        Assert.Equal(1, unchanged.CountEvents(new EventQuery()));
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_never_deleted()
    {
        using var dir = new TempDir();
        var path = dir.File("wdl.db");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]);
        using var store = DiagnosticsStore.Open(path);
        Assert.NotNull(store.OpenNotice);
        var aside = Directory.GetFiles(dir.Path, "wdl.corrupt-*.db");
        Assert.Single(aside);
        Assert.Equal(20, new FileInfo(aside[0]).Length);
    }

    [Fact]
    public void Newer_or_foreign_databases_are_not_opened()
    {
        using var dir = new TempDir();
        var newer = dir.File("newer.db");
        using (var c = new SqliteConnection($"Data Source={newer}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (x INTEGER); PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }

        var foreign = dir.File("foreign.db");
        using (var c = new SqliteConnection($"Data Source={foreign}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE other_app (x INTEGER);";
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        Assert.Throws<StorageVersionException>(() => DiagnosticsStore.Open(newer));
        Assert.Throws<StorageVersionException>(() => DiagnosticsStore.Open(foreign));
        Assert.True(File.Exists(newer));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void Retention_removes_only_old_rows_and_keeps_records()
    {
        using var dir = new TempDir();
        var time = new ManualTime(T0);
        using var store = DiagnosticsStore.Open(dir.File("wdl.db"), time);
        store.InsertEvents([New(Ev.Make("disk", 7, time: T0.AddDays(-400))), New(Ev.Make("disk", 7, time: T0.AddDays(-1)))]);
        var run = store.StartRun(T0.AddDays(-60), "test", false, 5000, false);
        store.WriteSensorSample(run, Sample(T0.AddDays(-60)));
        store.WriteSensorSample(run, Sample(T0.AddDays(-1)));
        store.AddUserRecord(new UserRecord { Time = T0.AddDays(-500), Kind = UserRecordKinds.Other, Title = "old" });

        var keepAll = store.ApplyRetention(eventDays: 0, sensorDays: 30);
        Assert.Equal(0, keepAll.EventsDeleted);
        Assert.Equal(2, store.CountEvents(new EventQuery()));
        Assert.Equal(1, store.GetStats().SensorSamples);

        var limited = store.ApplyRetention(eventDays: 365, sensorDays: 30);
        Assert.Equal(1, limited.EventsDeleted);
        Assert.Single(store.ListUserRecords());
    }

    internal static HardwareSnapshot Sample(DateTimeOffset t, bool gpu = true) => new()
    {
        Timestamp = t,
        System = new SystemSample { CpuUtilizationPercent = 10, MemoryUsedBytes = 8L << 30, MemoryTotalBytes = 64L << 30 },
        Gpus = gpu ? [new GpuSample { Index = 0, UtilizationPercent = 50, TemperatureC = 70, PowerW = 200 }] : [],
    };
}
