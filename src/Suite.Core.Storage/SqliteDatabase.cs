using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Suite.Core.Storage;

/// <summary>The file is not a usable SQLite database. It is left as it is.</summary>
public sealed class StorageCorruptException(string message) : Exception(message);

/// <summary>The database was written by a newer schema than this build knows. It is left untouched.</summary>
public sealed class StorageVersionException(string message) : Exception(message);

/// <summary>
/// Opening a product's own SQLite database the way projects A and B do: header check before anything is written, a
/// newer schema is refused (nothing is written into a file this build does not own), WAL journal, and additive
/// migrations numbered by <c>PRAGMA user_version</c>, each applied in one transaction.
/// </summary>
public static class SqliteDatabase
{
    private static readonly byte[] Header = "SQLite format 3\0"u8.ToArray();

    public static string ConnectionString(string path, SqliteOpenMode mode)
        => new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 15,
        }.ToString();

    /// <summary>
    /// Opens (or creates) <paramref name="path"/> and brings it to <c>migrations.Count</c>. <paramref name="migrations"/>[i]
    /// takes the schema from version i to i + 1; released migrations are never edited, only appended.
    /// </summary>
    /// <param name="applicationId">The product's mark (<c>PRAGMA application_id</c>), written into a new database. A
    /// database with another mark, or an unmarked one that already has tables (another program's file), is refused
    /// before anything is written, so a product never adds its tables to someone else's database.</param>
    public static SqliteConnection OpenAndMigrate(string path, IReadOnlyList<string> migrations, int applicationId)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        if (applicationId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(applicationId), "0은 표시가 없는 데이터베이스와 구별되지 않습니다.");
        }

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!HasValidHeader(full))
        {
            throw new StorageCorruptException($"'{Path.GetFileName(full)}'은(는) SQLite 데이터베이스가 아닙니다 (파일 헤더가 다름). 파일을 바꾸지 않았습니다.");
        }

        var connection = new SqliteConnection(ConnectionString(full, SqliteOpenMode.ReadWriteCreate));
        try
        {
            connection.Open();
            Exec(connection, "PRAGMA busy_timeout=10000;");
            var objects = Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master;") ?? 0L, CultureInfo.InvariantCulture); // reads the schema: detects most corruption cheaply
            var version = SchemaVersion(connection);
            var mark = Convert.ToInt32(Scalar(connection, "PRAGMA application_id;") ?? 0, CultureInfo.InvariantCulture);
            if (mark != applicationId && (mark != 0 || objects > 0 || version > 0))
            {
                throw new StorageCorruptException(
                    $"'{Path.GetFileName(full)}'은(는) 이 제품의 데이터베이스가 아닙니다 (application_id {mark:X8}, 기대 {applicationId:X8}). 파일을 바꾸지 않았습니다.");
            }

            if (version > migrations.Count)
            {
                throw new StorageVersionException(
                    $"이 데이터베이스는 더 새로운 버전에서 만들어졌습니다 (스키마 {version}, 지원 {migrations.Count}). 데이터를 보호하기 위해 열지 않습니다.");
            }

            Exec(connection, "PRAGMA journal_mode=WAL;");
            Exec(connection, "PRAGMA synchronous=NORMAL;");
            if (mark == 0)
            {
                Exec(connection, string.Create(CultureInfo.InvariantCulture, $"PRAGMA application_id={applicationId};"));
            }

            for (var v = (int)version; v < migrations.Count; v++)
            {
                using var tx = connection.BeginTransaction();
                Exec(connection, migrations[v], tx);
                Exec(connection, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version={v + 1};"), tx);
                tx.Commit();
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            connection.Dispose();
            throw new StorageCorruptException($"데이터베이스가 손상되었습니다 ({ex.Message}). 파일을 바꾸지 않았습니다.");
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    public static long SchemaVersion(SqliteConnection connection)
        => Convert.ToInt64(Scalar(connection, "PRAGMA user_version;") ?? 0L, CultureInfo.InvariantCulture);

    /// <summary>True when the file starts with the SQLite header, or is missing or empty (a new database).</summary>
    public static bool HasValidHeader(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
        {
            return true;
        }

        if (stream.Length < Header.Length)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[16];
        stream.ReadExactly(buffer);
        return buffer.SequenceEqual(Header);
    }

    public static void Exec(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }
}
