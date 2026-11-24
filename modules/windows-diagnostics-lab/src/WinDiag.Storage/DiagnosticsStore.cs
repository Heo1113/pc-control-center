using System.Globalization;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Storage;
using Microsoft.Data.Sqlite;
using WinDiag.Core.Events;
using static LocalAiPerf.Storage.SqliteHelpers;

namespace WinDiag.Storage;

/// <summary>
/// This app's own SQLite database (never another program's). WAL journal with synchronous=FULL: every committed write
/// survives an app crash or power loss, and a crash never corrupts earlier data. A corrupt file is moved aside, never
/// deleted. Opening, recovery and backup follow project A's TelemetryStore (commit 5b6aae1), adapted to this schema.
/// </summary>
public sealed partial class DiagnosticsStore : IDisposable
{
    public const string MetaLastAckEventId = "last_ack_event_id";
    public const string MetaLastAckTime = "last_ack_ms";
    public const string MetaCreated = "created_ms";
    public const string MetaLastRetention = "last_retention_ms";

    private readonly SqliteConnection _writer;
    private readonly object _writeGate = new();
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private bool _disposed;

    private DiagnosticsStore(string path, SqliteConnection writer, string? notice, TimeProvider time, IAppLogger log)
    {
        DatabasePath = path;
        _writer = writer;
        OpenNotice = notice;
        _time = time;
        _log = log;
    }

    public string DatabasePath { get; }

    /// <summary>Set when the store had to recover from a problem while opening (for example a quarantined corrupt file).</summary>
    public string? OpenNotice { get; }

    public static DiagnosticsStore Open(string path, TimeProvider? time = null, IAppLogger? log = null)
    {
        time ??= TimeProvider.System;
        log ??= NullLogger.Instance;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            return OpenCore(path, null, time, log);
        }
        catch (Exception ex) when (IsCorruption(ex))
        {
            var aside = Quarantine(path, "corrupt", time);
            var notice = $"데이터베이스 파일이 손상되어 '{Path.GetFileName(aside)}'(으)로 옮겨 보관하고 새 데이터베이스를 만들었습니다. 보관한 파일은 삭제하지 않았습니다. ({ex.Message})";
            log.Error("storage", notice, ex);
            return OpenCore(path, notice, time, log);
        }
    }

    private static DiagnosticsStore OpenCore(string path, string? notice, TimeProvider time, IAppLogger log)
    {
        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        if (!HasValidHeader(path))
        {
            throw new StorageCorruptException("SQLite 파일 헤더가 올바르지 않습니다.");
        }

        var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadWriteCreate, pooling: false));
        try
        {
            connection.Open();
            Exec(connection, "PRAGMA busy_timeout=10000;");
            _ = ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master;");
            var version = ScalarLong(connection, "PRAGMA user_version;");
            if (version > Schema.CurrentVersion)
            {
                throw new StorageVersionException(
                    $"이 데이터베이스는 더 새로운 버전의 앱에서 만들어졌습니다 (스키마 {version}, 지원 {Schema.CurrentVersion}). 데이터를 보호하기 위해 열지 않습니다.");
            }

            if (!isNew && version == 0 && ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';") > 0)
            {
                throw new StorageVersionException("이 파일은 이 앱의 데이터베이스가 아닙니다. 다른 프로그램의 파일을 수정하지 않기 위해 열지 않습니다.");
            }

            if (isNew)
            {
                Exec(connection, "PRAGMA auto_vacuum=INCREMENTAL;");
            }

            Exec(connection, "PRAGMA journal_mode=WAL;");
            Exec(connection, "PRAGMA synchronous=FULL;");
            if (version < Schema.CurrentVersion)
            {
                using var tx = connection.BeginTransaction();
                Exec(connection, Schema.V1, tx);
                Execute(connection, "INSERT OR IGNORE INTO meta (key, value) VALUES (@k, @v);", tx,
                    ("@k", MetaCreated), ("@v", time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
                Exec(connection, $"PRAGMA user_version={Schema.CurrentVersion};", tx);
                tx.Commit();
            }

            if (!isNew && ScalarLong(connection, "SELECT COUNT(*) FROM runs WHERE ended_ms IS NULL;") > 0)
            {
                // The previous run did not end cleanly: verify the file before writing more data into it.
                var check = Scalar(connection, "PRAGMA quick_check;") as string;
                if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new StorageCorruptException($"무결성 검사 실패: {check}");
                }
            }
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new DiagnosticsStore(path, connection, notice, time, log);
    }

    /// <summary>Moves the database and its WAL/SHM side files aside (never deletes them). Returns the new main file path.</summary>
    internal static string Quarantine(string path, string label, TimeProvider time)
    {
        SqliteConnection.ClearAllPools();
        var stamp = time.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var aside = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}.{label}-{stamp}{Path.GetExtension(path)}");
        var counter = 1;
        while (File.Exists(aside))
        {
            aside = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}.{label}-{stamp}-{counter++}{Path.GetExtension(path)}");
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(path + suffix))
            {
                File.Move(path + suffix, aside + suffix, overwrite: false);
            }
        }

        return aside;
    }

    /// <summary>A separate read connection (WAL allows reading while the collector writes).</summary>
    public SqliteConnection OpenReader()
    {
        ThrowIfDisposed();
        var connection = new SqliteConnection(ConnectionString(DatabasePath, SqliteOpenMode.ReadOnly));
        connection.Open();
        Exec(connection, "PRAGMA busy_timeout=10000;");
        return connection;
    }

    // ------------------------------------------------------------------ meta

    public string? GetMeta(string key)
    {
        using var reader = OpenReader();
        return Scalar(reader, "SELECT value FROM meta WHERE key = @k;", null, ("@k", key)) as string;
    }

    public void SetMeta(string key, string value)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            Execute(_writer, "INSERT INTO meta (key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;", null,
                ("@k", key), ("@v", value));
        }
    }

    public long GetMetaLong(string key) => long.TryParse(GetMeta(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // ------------------------------------------------------------------ runs

    public long StartRun(DateTimeOffset started, string appVersion, bool isMock, int sensorIntervalMs, bool elevated)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            return ScalarLong(_writer, """
                INSERT INTO runs (started_ms, heartbeat_ms, app_version, is_mock, sensor_interval_ms, elevated)
                VALUES (@s, @s, @v, @m, @i, @e) RETURNING id;
                """, null,
                ("@s", started.ToUnixTimeMilliseconds()), ("@v", appVersion), ("@m", isMock ? 1 : 0), ("@i", sensorIntervalMs), ("@e", elevated ? 1 : 0));
        }
    }

    /// <summary>
    /// Records a session that is already over (imported or virtual data) in one statement, so it can never be left open
    /// and later be taken for a session of this app that ended uncleanly.
    /// </summary>
    public long AddFinishedRun(DateTimeOffset started, DateTimeOffset ended, string endReason, string appVersion, bool isMock, int sensorIntervalMs,
        bool elevated)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            return ScalarLong(_writer, """
                INSERT INTO runs (started_ms, heartbeat_ms, ended_ms, end_reason, app_version, is_mock, sensor_interval_ms, elevated)
                VALUES (@s, @t, @t, @r, @v, @m, @i, @e) RETURNING id;
                """, null,
                ("@s", started.ToUnixTimeMilliseconds()), ("@t", ended.ToUnixTimeMilliseconds()), ("@r", endReason), ("@v", appVersion),
                ("@m", isMock ? 1 : 0), ("@i", sensorIntervalMs), ("@e", elevated ? 1 : 0));
        }
    }

    public void Heartbeat(long runId, DateTimeOffset time)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            Execute(_writer, "UPDATE runs SET heartbeat_ms = MAX(COALESCE(heartbeat_ms, 0), @t) WHERE id = @id AND ended_ms IS NULL;", null,
                ("@t", time.ToUnixTimeMilliseconds()), ("@id", runId));
        }
    }

    public void EndRun(long runId, DateTimeOffset ended, string reason)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            Execute(_writer, "UPDATE runs SET ended_ms = @t, end_reason = @r, heartbeat_ms = @t WHERE id = @id AND ended_ms IS NULL;", null,
                ("@t", ended.ToUnixTimeMilliseconds()), ("@r", reason), ("@id", runId));
        }
    }

    /// <summary>
    /// Closes runs left open by a crash or power loss, using the last heartbeat or sample as the end time, and records
    /// each as an "app ended uncleanly" gap. Returns the recovered runs.
    /// </summary>
    public IReadOnlyList<AppRun> RecoverUnfinishedRuns()
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            var open = ReadRuns(_writer, "WHERE r.ended_ms IS NULL", null);
            if (open.Count == 0)
            {
                return [];
            }

            using var tx = _writer.BeginTransaction();
            var now = _time.GetUtcNow();
            var recovered = new List<AppRun>();
            foreach (var run in open)
            {
                var lastSample = Scalar(_writer, "SELECT MAX(t_ms) FROM sensor_samples WHERE run_id = @id;", tx, ("@id", run.Id));
                var lastMs = Math.Max(run.LastHeartbeat?.ToUnixTimeMilliseconds() ?? run.Started.ToUnixTimeMilliseconds(),
                    lastSample is null ? 0 : Convert.ToInt64(lastSample, CultureInfo.InvariantCulture));
                Execute(_writer, "UPDATE runs SET ended_ms = @t, end_reason = @r WHERE id = @id;", tx,
                    ("@t", lastMs), ("@r", RunEndReasons.RecoveredAfterCrash), ("@id", run.Id));
                InsertGap(new CollectionGap
                {
                    Scope = "app",
                    Kind = GapKinds.AppEndedUncleanly,
                    From = DateTimeOffset.FromUnixTimeMilliseconds(lastMs),
                    To = now,
                    Detected = now,
                    Detail = "이전 실행이 정상 종료되지 않았습니다 (앱 강제 종료, 시스템 충돌 또는 전원 차단). 마지막 기록까지의 데이터는 보존되었습니다.",
                }, tx);
                recovered.Add(run with { Ended = DateTimeOffset.FromUnixTimeMilliseconds(lastMs), EndReason = RunEndReasons.RecoveredAfterCrash });
            }

            tx.Commit();
            return recovered;
        }
    }

    public IReadOnlyList<AppRun> ListRuns(DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        using var reader = OpenReader();
        var where = "WHERE (@to IS NULL OR r.started_ms <= @to) AND (@from IS NULL OR COALESCE(r.ended_ms, r.heartbeat_ms, r.started_ms) >= @from)";
        return ReadRuns(reader, where, null, ("@from", from?.ToUnixTimeMilliseconds()), ("@to", to?.ToUnixTimeMilliseconds()));
    }

    private static List<AppRun> ReadRuns(SqliteConnection connection, string where, SqliteTransaction? tx, params (string, object?)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"""
            SELECT r.id, r.started_ms, r.ended_ms, r.end_reason, r.heartbeat_ms, r.app_version, r.is_mock, r.sensor_interval_ms, r.elevated
            FROM runs r {where} ORDER BY r.started_ms;
            """;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var runs = new List<AppRun>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            runs.Add(new AppRun
            {
                Id = r.GetInt64(0),
                Started = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)),
                Ended = L(r, 2) is long e ? DateTimeOffset.FromUnixTimeMilliseconds(e) : null,
                EndReason = S(r, 3),
                LastHeartbeat = L(r, 4) is long h ? DateTimeOffset.FromUnixTimeMilliseconds(h) : null,
                AppVersion = r.GetString(5),
                IsMock = r.GetInt64(6) != 0,
                SensorIntervalMs = r.GetInt32(7),
                Elevated = r.GetInt64(8) != 0,
            });
        }

        return runs;
    }

    // ------------------------------------------------------------------ channel state and gaps

    public IReadOnlyList<ChannelState> GetChannelStates()
    {
        using var reader = OpenReader();
        return ReadChannelStates(reader, null, null);
    }

    public ChannelState? GetChannelState(string channel)
    {
        using var reader = OpenReader();
        return ReadChannelStates(reader, null, channel).FirstOrDefault();
    }

    public void SaveChannelState(ChannelState state)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            UpsertChannelState(state, null);
        }
    }

    private void UpsertChannelState(ChannelState s, SqliteTransaction? tx)
        => Execute(_writer, """
            INSERT INTO channel_state (channel, status, status_message, last_record_id, last_scan_ms, last_event_ms, oldest_record_id,
                                       oldest_event_ms, record_count, history_from_ms)
            VALUES (@c, @st, @sm, @lr, @ls, @le, @or, @oe, @rc, @hf)
            ON CONFLICT(channel) DO UPDATE SET status = excluded.status, status_message = excluded.status_message,
                last_record_id = excluded.last_record_id, last_scan_ms = excluded.last_scan_ms, last_event_ms = excluded.last_event_ms,
                oldest_record_id = excluded.oldest_record_id, oldest_event_ms = excluded.oldest_event_ms,
                record_count = excluded.record_count, history_from_ms = excluded.history_from_ms;
            """, tx,
            ("@c", s.Channel), ("@st", (int)s.Status), ("@sm", s.StatusMessage), ("@lr", s.LastRecordId),
            ("@ls", s.LastScan?.ToUnixTimeMilliseconds()), ("@le", s.LastEventTime?.ToUnixTimeMilliseconds()), ("@or", s.OldestRecordId),
            ("@oe", s.OldestEventTime?.ToUnixTimeMilliseconds()), ("@rc", s.RecordCount), ("@hf", s.HistoryFrom?.ToUnixTimeMilliseconds()));

    private static List<ChannelState> ReadChannelStates(SqliteConnection connection, SqliteTransaction? tx, string? channel)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT channel, status, status_message, last_record_id, last_scan_ms, last_event_ms, oldest_record_id, oldest_event_ms,
                   record_count, history_from_ms
            FROM channel_state WHERE (@c IS NULL OR channel = @c) ORDER BY channel;
            """;
        command.Parameters.AddWithValue("@c", (object?)channel ?? DBNull.Value);
        var states = new List<ChannelState>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            states.Add(new ChannelState
            {
                Channel = r.GetString(0),
                Status = (ChannelStatus)r.GetInt32(1),
                StatusMessage = S(r, 2),
                LastRecordId = L(r, 3),
                LastScan = Ms(L(r, 4)),
                LastEventTime = Ms(L(r, 5)),
                OldestRecordId = L(r, 6),
                OldestEventTime = Ms(L(r, 7)),
                RecordCount = L(r, 8),
                HistoryFrom = Ms(L(r, 9)),
            });
        }

        return states;
    }

    public long AddGap(CollectionGap gap)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            return InsertGap(gap, null);
        }
    }

    private long InsertGap(CollectionGap gap, SqliteTransaction? tx)
        => ScalarLong(_writer, """
            INSERT INTO gaps (scope, kind, from_ms, to_ms, detected_ms, detail) VALUES (@s, @k, @f, @t, @d, @x) RETURNING id;
            """, tx,
            ("@s", gap.Scope), ("@k", gap.Kind), ("@f", gap.From?.ToUnixTimeMilliseconds()), ("@t", gap.To?.ToUnixTimeMilliseconds()),
            ("@d", gap.Detected.ToUnixTimeMilliseconds()), ("@x", gap.Detail));

    public bool GapExists(string scope, string kind, DateTimeOffset to)
    {
        using var reader = OpenReader();
        return ScalarLong(reader, "SELECT COUNT(*) FROM gaps WHERE scope = @s AND kind = @k AND to_ms = @t;", null,
            ("@s", scope), ("@k", kind), ("@t", to.ToUnixTimeMilliseconds())) > 0;
    }

    /// <summary>Gaps that overlap the range (or were detected in it when they have no time range).</summary>
    public IReadOnlyList<CollectionGap> ListGaps(DateTimeOffset? from = null, DateTimeOffset? to = null, int limit = 1000)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = """
            SELECT id, scope, kind, from_ms, to_ms, detected_ms, detail FROM gaps
            WHERE (@to IS NULL OR COALESCE(from_ms, detected_ms) <= @to) AND (@from IS NULL OR COALESCE(to_ms, detected_ms) >= @from)
            ORDER BY COALESCE(from_ms, detected_ms) DESC LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@from", (object?)from?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        command.Parameters.AddWithValue("@to", (object?)to?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        command.Parameters.AddWithValue("@limit", limit);
        var gaps = new List<CollectionGap>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            gaps.Add(new CollectionGap
            {
                Id = r.GetInt64(0),
                Scope = r.GetString(1),
                Kind = r.GetString(2),
                From = Ms(L(r, 3)),
                To = Ms(L(r, 4)),
                Detected = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)),
                Detail = S(r, 6),
            });
        }

        return gaps;
    }

    // ------------------------------------------------------------------ maintenance

    public DatabaseStats GetStats()
    {
        using var reader = OpenReader();
        long Count(string sql) => ScalarLong(reader, sql);
        DateTimeOffset? Time(string sql) => Scalar(reader, sql) is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(v, CultureInfo.InvariantCulture)) : null;
        var wal = DatabasePath + "-wal";
        return new DatabaseStats(
            DatabasePath,
            File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0,
            File.Exists(wal) ? new FileInfo(wal).Length : 0,
            Count("SELECT COUNT(*) FROM events;"),
            Count("SELECT COUNT(*) FROM events WHERE source = 'virtual';"),
            Count("SELECT COUNT(*) FROM sensor_samples;"),
            Count("SELECT COUNT(*) FROM snapshots;"),
            Count("SELECT COUNT(*) FROM runs;"),
            Count("SELECT COUNT(*) FROM gaps;"),
            Time("SELECT MIN(time_ms) FROM events;"),
            Time("SELECT MAX(time_ms) FROM events;"),
            Time("SELECT MIN(t_ms) FROM sensor_samples;"),
            Time("SELECT MAX(t_ms) FROM sensor_samples;"));
    }

    public string IntegrityCheck()
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            using var command = _writer.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            using var r = command.ExecuteReader();
            var lines = new List<string>();
            while (r.Read() && lines.Count < 20)
            {
                lines.Add(r.GetString(0));
            }

            return string.Join("; ", lines);
        }
    }

    /// <summary>
    /// Removes rows older than the configured retention (events only when a limit is set; sensor samples by their limit).
    /// User records, baselines, notes and snapshots are kept. The database file itself is never deleted.
    /// </summary>
    public RetentionResult ApplyRetention(int eventDays, int sensorDays)
    {
        var notes = new List<string>();
        long events = 0, sensors = 0;
        lock (_writeGate)
        {
            ThrowIfDisposed();
            var now = _time.GetUtcNow();
            using var tx = _writer.BeginTransaction();
            if (eventDays > 0)
            {
                var cutoff = now.AddDays(-eventDays).ToUnixTimeMilliseconds();
                events = Execute(_writer, "DELETE FROM events WHERE time_ms < @c;", tx, ("@c", cutoff));
            }

            if (sensorDays > 0)
            {
                var cutoff = now.AddDays(-sensorDays).ToUnixTimeMilliseconds();
                sensors = Execute(_writer, "DELETE FROM sensor_samples WHERE t_ms < @c;", tx, ("@c", cutoff));
                sensors += Execute(_writer, "DELETE FROM gpu_samples WHERE t_ms < @c;", tx, ("@c", cutoff));
            }

            Execute(_writer, "INSERT INTO meta (key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;", tx,
                ("@k", MetaLastRetention), ("@v", now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
            tx.Commit();
            if (events + sensors > 0)
            {
                Exec(_writer, "PRAGMA incremental_vacuum(2000);");
            }
        }

        if (events > 0)
        {
            notes.Add($"보존 기간({eventDays}일)이 지난 이벤트 {events}건을 정리했습니다.");
        }

        if (sensors > 0)
        {
            notes.Add($"보존 기간({sensorDays}일)이 지난 센서 기록 {sensors}행을 정리했습니다.");
        }

        return new RetentionResult(events, sensors, notes);
    }

    public void BackupTo(string destinationPath, bool overwrite = false)
    {
        var full = Path.GetFullPath(destinationPath);
        if (string.Equals(full, Path.GetFullPath(DatabasePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("백업 파일은 사용 중인 데이터베이스와 다른 파일이어야 합니다.");
        }

        if (File.Exists(full) && !overwrite)
        {
            throw new IOException($"백업 파일이 이미 있습니다: {Path.GetFileName(full)}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".partial";
        if (File.Exists(temp))
        {
            File.Delete(temp); // our own unfinished temporary file from an earlier attempt
        }

        using (var destination = new SqliteConnection(ConnectionString(temp, SqliteOpenMode.ReadWriteCreate, pooling: false)))
        {
            destination.Open();
            lock (_writeGate)
            {
                ThrowIfDisposed();
                _writer.BackupDatabase(destination);
            }

            Exec(destination, "PRAGMA journal_mode=DELETE;");
        }

        File.Move(temp, full, overwrite);
    }

    public static BackupValidation ValidateBackup(string path)
    {
        if (!File.Exists(path))
        {
            return new BackupValidation(false, "파일이 없습니다.", 0, 0, null, null);
        }

        try
        {
            if (!HasValidHeader(path) || new FileInfo(path).Length == 0)
            {
                return new BackupValidation(false, "SQLite 데이터베이스 파일이 아닙니다.", 0, 0, null, null);
            }

            using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly, pooling: false));
            connection.Open();
            var check = Scalar(connection, "PRAGMA quick_check;") as string;
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new BackupValidation(false, $"무결성 검사 실패: {check}", 0, 0, null, null);
            }

            var version = (int)ScalarLong(connection, "PRAGMA user_version;");
            if (version < 1 || version > Schema.CurrentVersion)
            {
                return new BackupValidation(false, $"지원하지 않는 스키마 버전입니다 ({version}).", version, 0, null, null);
            }

            foreach (var table in Schema.RequiredTables)
            {
                if (ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @n;", null, ("@n", table)) == 0)
                {
                    return new BackupValidation(false, $"필수 테이블이 없습니다: {table}", version, 0, null, null);
                }
            }

            var events = ScalarLong(connection, "SELECT COUNT(*) FROM events;");
            var oldest = Scalar(connection, "SELECT MIN(time_ms) FROM events;");
            var newest = Scalar(connection, "SELECT MAX(time_ms) FROM events;");
            return new BackupValidation(true, "정상", version, events,
                oldest is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(oldest, CultureInfo.InvariantCulture)),
                newest is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(newest, CultureInfo.InvariantCulture)));
        }
        catch (SqliteException ex)
        {
            return new BackupValidation(false, $"백업 파일을 열 수 없습니다: {ex.Message}", 0, 0, null, null);
        }
    }

    /// <summary>
    /// Replaces the database with a validated backup. The store must be closed first. The current database is moved
    /// aside as "before-restore" (not deleted). Returns that path, or null when there was no database.
    /// </summary>
    public static string? RestoreFrom(string backupPath, string databasePath, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var validation = ValidateBackup(backupPath);
        if (!validation.IsValid)
        {
            throw new InvalidDataException($"백업 파일이 올바르지 않습니다: {validation.Message}");
        }

        SqliteConnection.ClearAllPools();
        var aside = File.Exists(databasePath) ? Quarantine(databasePath, "before-restore", time) : null;
        try
        {
            using var source = new SqliteConnection(ConnectionString(backupPath, SqliteOpenMode.ReadOnly, pooling: false));
            using var destination = new SqliteConnection(ConnectionString(databasePath, SqliteOpenMode.ReadWriteCreate, pooling: false));
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
            Exec(destination, "PRAGMA journal_mode=WAL;");
        }
        catch
        {
            // Undo: remove the half-written copy and put the previous database back where it was.
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(databasePath + suffix))
                {
                    File.Delete(databasePath + suffix);
                }

                if (aside is not null && File.Exists(aside + suffix))
                {
                    File.Move(aside + suffix, databasePath + suffix);
                }
            }

            throw;
        }

        return aside;
    }

    public void Checkpoint()
    {
        lock (_writeGate)
        {
            if (!_disposed)
            {
                Exec(_writer, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
        }
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Exec(_writer, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (SqliteException ex)
            {
                _log.Warn("storage", "종료 시 WAL 체크포인트 실패 (다음 실행에서 자동 반영)", ex);
            }

            _writer.Dispose();
            _disposed = true;
        }

        SqliteConnection.ClearAllPools();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal static DateTimeOffset? Ms(long? value) => value is long v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;
}
