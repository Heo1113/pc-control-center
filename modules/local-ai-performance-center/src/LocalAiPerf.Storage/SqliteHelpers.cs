using Microsoft.Data.Sqlite;

namespace LocalAiPerf.Storage;

internal static class SqliteHelpers
{
    public const int SqliteCorrupt = 11;
    public const int SqliteNotADb = 26;
    public const int SqliteFull = 13;
    public const int SqliteIoErr = 10;
    public const int SqliteReadOnly = 8;

    private static readonly byte[] Header = "SQLite format 3\0"u8.ToArray();

    public static string ConnectionString(string path, SqliteOpenMode mode, bool pooling = true)
        => new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = pooling,
            ForeignKeys = true,
            DefaultTimeout = 15,
        }.ToString();

    public static void Exec(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    public static long ScalarLong(SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
        => Convert.ToInt64(Scalar(connection, sql, transaction, parameters) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

    public static int Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    /// <summary>True when the file starts with the SQLite header (or is empty / missing).</summary>
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

    public static bool IsCorruption(Exception ex)
        => ex is StorageCorruptException
           || (ex is SqliteException se && se.SqliteErrorCode is SqliteCorrupt or SqliteNotADb);

    public static double? D(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public static long? L(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    public static int? I(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static object Db(object? value) => value ?? DBNull.Value;
}

public sealed class StorageCorruptException(string message) : Exception(message);

/// <summary>The database was written by a newer version of this app. It is left untouched.</summary>
public sealed class StorageVersionException(string message) : Exception(message);
