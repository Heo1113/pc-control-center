using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Sessions;
using LocalAiPerf.Storage;
using Microsoft.Data.Sqlite;
using Xunit;
using static LocalAiPerf.Tests.FrameFactory;

namespace LocalAiPerf.Tests.Storage;

public class TelemetryStoreTests
{
    private static void WriteFrames(TelemetryStore store, long sessionId, int count, DateTimeOffset start, Func<int, double?>? tok = null)
    {
        for (var i = 0; i < count; i++)
        {
            store.WriteFrame(new FrameWrite { SessionId = sessionId, Frame = Frame(start.AddSeconds(i), tokS: tok?.Invoke(i) ?? 40, seq: i) });
        }
    }

    [Fact]
    public void Frames_round_trip_with_nulls_preserved()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var session = store.StartSession(Start(T0));
        var request = new CompletedRequest { Id = "abc123abc123", OutputTokens = 100, DecodeMs = 2500, PromptTokens = 900, PromptMs = 1000, Finish = "stop" };
        store.WriteFrame(new FrameWrite
        {
            SessionId = session.Id,
            Frame = Frame(T0, tokS: null),
            NewRequests = [request],
            InstanceId = "inst-1",
            Conditions = new RuntimeConditions { RuntimeKind = "strata", ModelId = "model-a", EngineVersion = "0.1.55", Settings = [new RuntimeSetting("temperature", "0.6")] },
            Events = [new EventRecord(session.Id, T0, EventSeverity.Warning, EventCategories.Sensor, "test event")],
        });

        var data = store.LoadSessionData(session.Id)!;

        Assert.Single(data.System);
        Assert.Equal(12.5, data.System[0].CpuUtil);
        Assert.Single(data.Gpu);
        Assert.Null(data.Gpu[0].FanPct); // N/A stays N/A
        Assert.Equal(250, data.Gpu[0].PowerW);
        Assert.Single(data.Runtime);
        Assert.Null(data.Runtime[0].AnswerTokS);
        Assert.Equal(5000, data.Runtime[0].ContextUsed);
        Assert.Single(data.Requests);
        Assert.Equal(40.0, data.Requests[0].ToCompletedRequest().DecodeTokPerSec);
        Assert.Equal("0.6", data.RuntimeInfo.Single().Conditions.Settings.Single().Value);
        Assert.Contains(data.Events, e => e.Message == "test event");
        Assert.Equal(1, data.Session.SampleCount);
        Assert.Equal("model-a", data.Session.ModelSummary);
    }

    [Fact]
    public void Duplicate_requests_are_ignored()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var session = store.StartSession(Start(T0));
        var request = new CompletedRequest { Id = "abc123abc123", OutputTokens = 10 };
        store.WriteFrame(new FrameWrite { SessionId = session.Id, Frame = Frame(T0), NewRequests = [request], InstanceId = "i" });
        store.WriteFrame(new FrameWrite { SessionId = session.Id, Frame = Frame(T0.AddSeconds(1)), NewRequests = [request], InstanceId = "i" });

        Assert.Single(store.LoadSessionData(session.Id)!.Requests);
    }

    [Fact]
    public void Committed_ticks_survive_an_abrupt_stop_and_the_session_is_recovered()
    {
        using var dir = new TempDir();
        var path = dir.File("lapc.db");
        var crashCopy = dir.File("crash.db");
        using (var store = TelemetryStore.Open(path))
        {
            var session = store.StartSession(Start(T0));
            WriteFrames(store, session.Id, 25, T0);

            // Simulate a crash: copy the files while the writer is still open (no clean shutdown, WAL not checkpointed).
            File.Copy(path, crashCopy);
            if (File.Exists(path + "-wal"))
            {
                File.Copy(path + "-wal", crashCopy + "-wal");
            }
        }

        using var reopened = TelemetryStore.Open(crashCopy);
        Assert.Null(reopened.OpenNotice);
        Assert.Equal(1, reopened.RecoverUnfinishedSessions());
        var recovered = reopened.ListSessions().Single();
        Assert.Equal(SessionEndReasons.RecoveredAfterCrash, recovered.EndReason);
        Assert.Equal(T0.AddSeconds(24), recovered.EndedAt);
        Assert.Equal(25, reopened.LoadSessionData(recovered.Id)!.System.Count);
    }

    [Fact]
    public void A_corrupt_file_is_moved_aside_and_a_new_database_is_created()
    {
        using var dir = new TempDir();
        var path = dir.File("lapc.db");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x42, 8192).ToArray());

        using var store = TelemetryStore.Open(path);

        Assert.NotNull(store.OpenNotice);
        Assert.Single(Directory.GetFiles(dir.Path, "lapc.corrupt-*.db"));
        Assert.Empty(store.ListSessions());
        store.StartSession(Start(T0)); // usable
    }

    [Fact]
    public void A_database_from_a_newer_app_version_is_not_touched()
    {
        using var dir = new TempDir();
        var path = dir.File("lapc.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE x (a INTEGER); PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var before = File.ReadAllBytes(path);
        Assert.Throws<StorageVersionException>(() => TelemetryStore.Open(path));
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Active_session_cannot_be_deleted_but_finished_one_can()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var active = store.StartSession(Start(T0, "active"));
        var finished = store.StartSession(Start(T0.AddMinutes(1), "finished"));
        WriteFrames(store, finished.Id, 3, T0.AddMinutes(1));
        store.EndSession(finished.Id, T0.AddMinutes(2), SessionEndReasons.UserStopped);

        Assert.False(store.DeleteSession(active.Id));
        Assert.True(store.DeleteSession(finished.Id));
        Assert.Null(store.LoadSessionData(finished.Id));
    }

    [Fact]
    public void Retention_deletes_old_sessions_and_trims_the_old_part_of_a_spanning_session()
    {
        using var dir = new TempDir();
        var now = T0.AddDays(40);
        var time = new ManualTime(now);
        using var store = TelemetryStore.Open(dir.File("lapc.db"), time);
        var old = store.StartSession(Start(T0, "old"));
        WriteFrames(store, old.Id, 5, T0);
        store.EndSession(old.Id, T0.AddSeconds(5), SessionEndReasons.UserStopped);
        var spanning = store.StartSession(Start(now.AddDays(-30).AddMinutes(-1), "spanning"));
        WriteFrames(store, spanning.Id, 3, now.AddDays(-30).AddMinutes(-1)); // before the cutoff
        WriteFrames(store, spanning.Id, 3, now.AddDays(-29)); // after the cutoff

        var result = store.ApplyRetention(30, 1L << 40, activeSessionId: spanning.Id);

        Assert.Equal(1, result.SessionsDeleted);
        Assert.Null(store.GetSession(old.Id));
        Assert.Equal(3, store.LoadSessionData(spanning.Id)!.System.Count);
    }

    [Fact]
    public void Size_limit_removes_oldest_sessions_first_and_keeps_the_active_one()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"), new ManualTime(T0.AddDays(1)));
        var ids = new List<long>();
        for (var s = 0; s < 4; s++)
        {
            var session = store.StartSession(Start(T0.AddHours(s), $"s{s}"));
            WriteFrames(store, session.Id, 400, T0.AddHours(s));
            if (s < 3)
            {
                store.EndSession(session.Id, T0.AddHours(s).AddMinutes(10), SessionEndReasons.UserStopped);
            }

            ids.Add(session.Id);
        }

        var used = store.UsedBytes();
        var result = store.ApplyRetention(3650, used / 2, activeSessionId: ids[^1]);

        Assert.True(result.SessionsDeleted >= 1);
        Assert.Null(store.GetSession(ids[0]));
        Assert.NotNull(store.GetSession(ids[^1]));
        Assert.True(store.UsedBytes() < used);
    }

    [Fact]
    public void Backup_validate_and_restore_round_trip()
    {
        using var dir = new TempDir();
        var path = dir.File("lapc.db");
        var backup = dir.File("backup.db");
        long sessionId;
        using (var store = TelemetryStore.Open(path))
        {
            sessionId = store.StartSession(Start(T0, "keep me")).Id;
            WriteFrames(store, sessionId, 10, T0);
            store.BackupTo(backup);
            Assert.Throws<IOException>(() => store.BackupTo(backup)); // never overwrites silently
            store.StartSession(Start(T0.AddHours(1), "after backup"));
        }

        var validation = TelemetryStore.ValidateBackup(backup);
        Assert.True(validation.IsValid, validation.Message);
        Assert.Equal(1, validation.Sessions);

        var aside = TelemetryStore.RestoreFrom(backup, path);

        Assert.NotNull(aside);
        Assert.True(File.Exists(aside));
        using var restored = TelemetryStore.Open(path);
        var sessions = restored.ListSessions();
        Assert.Equal("keep me", Assert.Single(sessions).Name);
        Assert.Equal(10, restored.LoadSessionData(sessionId)!.System.Count);
    }

    [Fact]
    public void Restore_rejects_a_file_that_is_not_a_backup_and_leaves_the_database_alone()
    {
        using var dir = new TempDir();
        var path = dir.File("lapc.db");
        var bogus = dir.File("bogus.db");
        File.WriteAllText(bogus, "not a database");
        using (var store = TelemetryStore.Open(path))
        {
            store.StartSession(Start(T0, "original"));
        }

        Assert.False(TelemetryStore.ValidateBackup(bogus).IsValid);
        Assert.Throws<InvalidDataException>(() => TelemetryStore.RestoreFrom(bogus, path));
        using var reopened = TelemetryStore.Open(path);
        Assert.Equal("original", reopened.ListSessions().Single().Name);
    }

    [Fact]
    public void Sessions_can_be_filtered_by_model_and_name()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var a = store.StartSession(Start(T0, "before update"));
        store.WriteFrame(new FrameWrite { SessionId = a.Id, Frame = Frame(T0), Conditions = new RuntimeConditions { ModelId = "qwen-q4" } });
        var b = store.StartSession(Start(T0.AddHours(1), "after update"));
        store.WriteFrame(new FrameWrite { SessionId = b.Id, Frame = Frame(T0.AddHours(1)), Conditions = new RuntimeConditions { ModelId = "qwen-q6" } });

        Assert.Equal("after update", Assert.Single(store.ListSessions(new SessionFilter("q6"))).Name);
        Assert.Equal(2, store.ListSessions(new SessionFilter("update")).Count);
        Assert.Equal("ok", store.IntegrityCheck());
        Assert.Equal(2, store.GetStats().Sessions);
    }

    [Fact]
    public void Databases_from_the_build_with_measurement_runs_still_open_and_keep_their_rows()
    {
        using var dir = new TempDir();
        var path = dir.File("lapc.db");
        long sessionId;
        using (var store = TelemetryStore.Open(path))
        {
            sessionId = store.StartSession(Start(T0)).Id;
        }

        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE benchmark_runs (id INTEGER PRIMARY KEY, session_id INTEGER REFERENCES sessions(id) ON DELETE CASCADE, t_ms INTEGER NOT NULL);
                INSERT INTO benchmark_runs (session_id, t_ms) VALUES ({sessionId}, 1);
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }

        using (var store = TelemetryStore.Open(path))
        {
            Assert.NotNull(store.GetSession(sessionId));
        }

        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM benchmark_runs;";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }

        Assert.Equal(2, TelemetryStore.ValidateBackup(path).SchemaVersion);
    }
}

