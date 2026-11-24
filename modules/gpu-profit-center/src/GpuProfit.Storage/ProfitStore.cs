using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GpuProfit.Core.Domain;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Storage;
using Microsoft.Data.Sqlite;
using static LocalAiPerf.Storage.SqliteHelpers;

namespace GpuProfit.Storage;

/// <summary>
/// This app's own SQLite database (never another program's). WAL journal with synchronous=FULL: every committed write
/// survives an app crash or power loss. A corrupt file is moved aside, never deleted; a newer or foreign database is
/// not opened. Opening, recovery and backup are adapted from project C's DiagnosticsStore (commit 06cc9e5), which
/// follows project A's TelemetryStore (commit 5b6aae1).
/// </summary>
public sealed partial class ProfitStore : IDisposable
{
    public const string MetaAppId = "app_id";
    public const string MetaCreated = "created_ms";
    public const string MetaProfile = "accounting_profile";
    public const string MetaLastRetention = "last_retention_ms";

    internal static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly SqliteConnection _writer;
    private readonly object _writeGate = new();
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private bool _disposed;

    private ProfitStore(string path, SqliteConnection writer, string? notice, TimeProvider time, IAppLogger log)
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

    public static ProfitStore Open(string path, TimeProvider? time = null, IAppLogger? log = null)
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
            var notice = $"데이터베이스 파일이 손상되어 '{Path.GetFileName(aside)}'(으)로 옮겨 보관하고 새 데이터베이스를 만들었습니다. 보관한 파일은 삭제하지 않았습니다. 백업이 있으면 설정 화면에서 복원할 수 있습니다. ({ex.Message})";
            log.Error("storage", notice, ex);
            return OpenCore(path, notice, time, log);
        }
    }

    private static ProfitStore OpenCore(string path, string? notice, TimeProvider time, IAppLogger log)
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

            var tables = ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';");
            if (!isNew && version == 0 && tables > 0)
            {
                throw new StorageVersionException("이 파일은 이 앱의 데이터베이스가 아닙니다. 다른 프로그램의 파일을 수정하지 않기 위해 열지 않습니다.");
            }

            if (version > 0 && !string.Equals(ReadMeta(connection, MetaAppId), Schema.AppId, StringComparison.Ordinal))
            {
                throw new StorageVersionException("이 파일은 GPU Profit & Compute Center의 데이터베이스가 아닙니다. 다른 프로그램의 파일을 수정하지 않기 위해 열지 않습니다.");
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
                Execute(connection, "INSERT OR IGNORE INTO meta (key, value) VALUES (@k, @v);", tx, ("@k", MetaAppId), ("@v", Schema.AppId));
                Exec(connection, $"PRAGMA user_version={Schema.CurrentVersion};", tx);
                tx.Commit();
            }

            if (!isNew && ScalarLong(connection, "SELECT COUNT(*) FROM app_runs WHERE ended_ms IS NULL;") > 0)
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

        return new ProfitStore(path, connection, notice, time, log);
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        if (ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta';") == 0)
        {
            return null;
        }

        return Scalar(connection, "SELECT value FROM meta WHERE key = @k;", null, ("@k", key)) as string;
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

    /// <summary>A separate read connection (WAL allows reading while the recorder writes).</summary>
    public SqliteConnection OpenReader()
    {
        ThrowIfDisposed();
        var connection = new SqliteConnection(ConnectionString(DatabasePath, SqliteOpenMode.ReadOnly));
        connection.Open();
        Exec(connection, "PRAGMA busy_timeout=10000;");
        return connection;
    }

    private T Write<T>(Func<SqliteConnection, T> work)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            return work(_writer);
        }
    }

    private T WriteTx<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            using var tx = _writer.BeginTransaction();
            var result = work(_writer, tx);
            tx.Commit();
            return result;
        }
    }

    // ------------------------------------------------------------------ meta and accounting profile

    public string? GetMeta(string key)
    {
        using var reader = OpenReader();
        return Scalar(reader, "SELECT value FROM meta WHERE key = @k;", null, ("@k", key)) as string;
    }

    public void SetMeta(string key, string value) => Write(c => Execute(c,
        "INSERT INTO meta (key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;", null, ("@k", key), ("@v", value)));

    /// <summary>The accounting profile stored with the data; defaults when none was saved yet.</summary>
    public AccountingProfile GetProfile()
    {
        var json = GetMeta(MetaProfile);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AccountingProfile();
        }

        try
        {
            return JsonSerializer.Deserialize<AccountingProfile>(json, Json) ?? new AccountingProfile();
        }
        catch (JsonException ex)
        {
            _log.Warn("storage", "저장된 회계 조건을 읽을 수 없어 기본값을 사용합니다 (저장된 값은 그대로 둠).", ex);
            return new AccountingProfile();
        }
    }

    public void SaveProfile(AccountingProfile profile)
    {
        var problems = profile.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems));
        }

        SetMeta(MetaProfile, JsonSerializer.Serialize(profile, Json));
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
            Count("SELECT COUNT(*) FROM app_runs;"),
            Count("SELECT COUNT(*) FROM devices;"),
            Count("SELECT COUNT(*) FROM gpu_samples;"),
            Count("SELECT COUNT(*) FROM system_samples;"),
            Count("SELECT COUNT(*) FROM gaps;"),
            Count("SELECT COUNT(*) FROM workloads;"),
            Count("SELECT COUNT(*) FROM sessions;"),
            Count("SELECT COUNT(*) FROM revenue_entries;"),
            Count("SELECT COUNT(*) FROM cost_entries;"),
            Count("SELECT COUNT(*) FROM session_results;"),
            Count("SELECT COUNT(*) FROM scenarios;"),
            Time("SELECT MIN(t_ms) FROM system_samples;"),
            Time("SELECT MAX(t_ms) FROM system_samples;"));
    }

    public string IntegrityCheck() => Write(c =>
    {
        using var command = c.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var r = command.ExecuteReader();
        var lines = new List<string>();
        while (r.Read() && lines.Count < 20)
        {
            lines.Add(r.GetString(0));
        }

        return string.Join("; ", lines);
    });

    /// <summary>
    /// Removes sensor samples older than <paramref name="days"/> days, except samples inside (or within
    /// <paramref name="marginSeconds"/> of) any session segment: session energy can always be recalculated.
    /// Sessions, revenue, costs and results are never removed. 0 days keeps everything.
    /// </summary>
    public RetentionResult ApplyRetention(int days, double marginSeconds = 60)
    {
        if (days <= 0)
        {
            return new RetentionResult(0, 0);
        }

        var now = _time.GetUtcNow();
        var cutoff = now.AddDays(-days).ToUnixTimeMilliseconds();
        var margin = (long)(marginSeconds * 1000);
        var (gpu, system) = WriteTx((c, tx) =>
        {
            const string keep = """
                NOT EXISTS (SELECT 1 FROM session_segments g
                            WHERE {0}.t_ms >= g.start_ms - @m AND {0}.t_ms <= COALESCE(g.end_ms, @now) + @m)
                """;
            var g = Execute(c, $"DELETE FROM gpu_samples WHERE t_ms < @c AND {string.Format(CultureInfo.InvariantCulture, keep, "gpu_samples")};", tx,
                ("@c", cutoff), ("@m", margin), ("@now", now.ToUnixTimeMilliseconds()));
            var s = Execute(c, $"DELETE FROM system_samples WHERE t_ms < @c AND {string.Format(CultureInfo.InvariantCulture, keep, "system_samples")};", tx,
                ("@c", cutoff), ("@m", margin), ("@now", now.ToUnixTimeMilliseconds()));
            Execute(c, "INSERT INTO meta (key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;", tx,
                ("@k", MetaLastRetention), ("@v", now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
            return ((long)g, (long)s);
        });
        if (gpu + system > 0)
        {
            Write(c =>
            {
                Exec(c, "PRAGMA incremental_vacuum(2000);");
                return 0;
            });
        }

        return new RetentionResult(gpu, system);
    }

    /// <summary>Writes a consistent copy (SQLite online backup) to a new file. The live database is never overwritten.</summary>
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
            return new BackupValidation(false, "파일이 없습니다.", 0, 0, 0);
        }

        try
        {
            if (!HasValidHeader(path) || new FileInfo(path).Length == 0)
            {
                return new BackupValidation(false, "SQLite 데이터베이스 파일이 아닙니다.", 0, 0, 0);
            }

            using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly, pooling: false));
            connection.Open();
            var check = Scalar(connection, "PRAGMA quick_check;") as string;
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new BackupValidation(false, $"무결성 검사 실패: {check}", 0, 0, 0);
            }

            var version = (int)ScalarLong(connection, "PRAGMA user_version;");
            if (version < 1 || version > Schema.CurrentVersion)
            {
                return new BackupValidation(false, $"지원하지 않는 스키마 버전입니다 ({version}).", version, 0, 0);
            }

            foreach (var table in Schema.RequiredTables)
            {
                if (ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @n;", null, ("@n", table)) == 0)
                {
                    return new BackupValidation(false, $"필수 테이블이 없습니다: {table}", version, 0, 0);
                }
            }

            if (!string.Equals(Scalar(connection, "SELECT value FROM meta WHERE key = @k;", null, ("@k", MetaAppId)) as string, Schema.AppId, StringComparison.Ordinal))
            {
                return new BackupValidation(false, "이 앱의 데이터베이스 백업이 아닙니다.", version, 0, 0);
            }

            return new BackupValidation(true, "정상", version, ScalarLong(connection, "SELECT COUNT(*) FROM sessions;"),
                ScalarLong(connection, "SELECT COUNT(*) FROM gpu_samples;"));
        }
        catch (SqliteException ex)
        {
            return new BackupValidation(false, $"백업 파일을 열 수 없습니다: {ex.Message}", 0, 0, 0);
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

        if (string.Equals(Path.GetFullPath(backupPath), Path.GetFullPath(databasePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("사용 중인 데이터베이스 자신으로는 복원할 수 없습니다.");
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

    internal static DateTimeOffset At(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    internal static long? ToMs(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds();

    internal static string? Dec(decimal? value) => value is decimal d ? DecimalText.Format(d) : null;

    internal static decimal? Dec(string? text) => text is null ? null : DecimalText.Parse(text);
}
