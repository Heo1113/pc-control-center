using System.Globalization;
using LocalAiPerf.Core.Hardware;
using Microsoft.Data.Sqlite;
using Suite.Core.Hardware;
using Suite.Core.Storage;

namespace Iron.Core;

/// <summary>
/// IRON's own database (<c>data\iron\iron-telemetry.db</c>). Tables are named after the source of their values, so
/// values of different sources never share a column: <c>nvml_gpu_samples</c> (the shared hub's NVML readings, never the
/// WDDM counters), <c>system_samples</c> (Windows CPU/RAM), <c>gpu_power_totals</c> (the all-GPU total, which the
/// database itself refuses to store as a number unless every GPU reported power).
/// </summary>
public sealed class IronTelemetryStore : IDisposable
{
    /// <summary>"IRON" in ASCII: marks the file as IRON's (PRAGMA application_id).</summary>
    public const int ApplicationId = 0x49524F4E;

    /// <summary>Schema migrations; index i takes version i to i + 1. Released entries are never edited, only appended.</summary>
    public static IReadOnlyList<string> Migrations { get; } =
    [
        """
        CREATE TABLE nvml_gpu_samples (
            ts_ms INTEGER NOT NULL,
            device_key TEXT NOT NULL,
            gpu_index INTEGER NOT NULL,
            name TEXT,
            temperature_c REAL,
            power_w REAL,
            utilization_pct REAL,
            memory_used_bytes INTEGER,
            memory_total_bytes INTEGER,
            graphics_clock_mhz REAL,
            is_mock INTEGER NOT NULL DEFAULT 0 CHECK (is_mock IN (0, 1)),
            PRIMARY KEY (ts_ms, device_key)
        ) WITHOUT ROWID;

        CREATE TABLE system_samples (
            ts_ms INTEGER PRIMARY KEY,
            cpu_pct REAL,
            ram_used_bytes INTEGER,
            ram_total_bytes INTEGER,
            is_mock INTEGER NOT NULL DEFAULT 0 CHECK (is_mock IN (0, 1))
        );

        CREATE TABLE gpu_power_totals (
            ts_ms INTEGER PRIMARY KEY,
            state TEXT NOT NULL CHECK (state IN ('COMPLETE', 'PARTIAL', 'UNAVAILABLE')),
            total_w REAL,
            reported INTEGER NOT NULL CHECK (reported >= 0),
            expected INTEGER NOT NULL CHECK (expected >= reported),
            is_mock INTEGER NOT NULL DEFAULT 0 CHECK (is_mock IN (0, 1)),
            CHECK ((state = 'COMPLETE') = (total_w IS NOT NULL)),
            CHECK (state <> 'COMPLETE' OR (reported = expected AND expected > 0))
        );
        """,
    ];

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    private IronTelemetryStore(string path, SqliteConnection connection)
    {
        Path = path;
        _connection = connection;
    }

    public string Path { get; }

    /// <exception cref="StorageCorruptException">Not a SQLite file, damaged, or another program's database.</exception>
    /// <exception cref="StorageVersionException">Written by a newer IRON.</exception>
    public static IronTelemetryStore Open(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return new IronTelemetryStore(full, SqliteDatabase.OpenAndMigrate(full, Migrations, ApplicationId));
    }

    public static string StateText(PowerTotalState state) => state switch
    {
        PowerTotalState.Complete => "COMPLETE",
        PowerTotalState.Partial => GpuPowerTotal.PartialLabel,
        _ => "UNAVAILABLE",
    };

    /// <summary>
    /// Stores one reading of the shared hub as measured (null stays null: a gap, never a 0). Storing the same reading
    /// twice keeps the first copy.
    /// </summary>
    public void Append(HardwareSnapshot snapshot, GpuPowerTotal total)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(total);
        var ts = snapshot.Timestamp.ToUnixTimeMilliseconds();
        var mock = snapshot.IsMock ? 1 : 0;
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var gpu in snapshot.Gpus)
            {
                Execute(tx, """
                    INSERT OR IGNORE INTO nvml_gpu_samples
                        (ts_ms, device_key, gpu_index, name, temperature_c, power_w, utilization_pct, memory_used_bytes, memory_total_bytes, graphics_clock_mhz, is_mock)
                    VALUES ($ts, $key, $index, $name, $temp, $power, $util, $used, $total, $clock, $mock);
                    """,
                    ("$ts", ts), ("$key", GpuDeviceKey.Of(gpu)), ("$index", gpu.Index), ("$name", gpu.Name), ("$temp", Finite(gpu.TemperatureC)),
                    ("$power", GpuPowerTotal.IsValidPower(gpu.PowerW) ? gpu.PowerW : null), ("$util", Finite(gpu.UtilizationPercent)),
                    ("$used", gpu.MemoryUsedBytes), ("$total", gpu.MemoryTotalBytes), ("$clock", Finite(gpu.GraphicsClockMhz)), ("$mock", mock));
            }

            Execute(tx, "INSERT OR IGNORE INTO system_samples (ts_ms, cpu_pct, ram_used_bytes, ram_total_bytes, is_mock) VALUES ($ts, $cpu, $used, $total, $mock);",
                ("$ts", ts), ("$cpu", Finite(snapshot.System.CpuUtilizationPercent)), ("$used", snapshot.System.MemoryUsedBytes),
                ("$total", snapshot.System.MemoryTotalBytes), ("$mock", mock));
            Execute(tx, "INSERT OR IGNORE INTO gpu_power_totals (ts_ms, state, total_w, reported, expected, is_mock) VALUES ($ts, $state, $w, $reported, $expected, $mock);",
                ("$ts", ts), ("$state", StateText(total.State)), ("$w", total.State == PowerTotalState.Complete ? total.TotalW : null),
                ("$reported", total.Reported), ("$expected", total.Expected), ("$mock", mock));
            tx.Commit();
        }
    }

    /// <summary>Removes stored readings older than <paramref name="cutoff"/>. Returns the number of rows removed.</summary>
    public int PruneOlderThan(DateTimeOffset cutoff)
    {
        var ms = cutoff.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var removed = 0;
            foreach (var table in new[] { "nvml_gpu_samples", "system_samples", "gpu_power_totals" })
            {
                removed += Execute(tx, $"DELETE FROM {table} WHERE ts_ms < $cutoff;", ("$cutoff", ms));
            }

            tx.Commit();
            return removed;
        }
    }

    public long Count(string table)
    {
        if (table is not ("nvml_gpu_samples" or "system_samples" or "gpu_power_totals"))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "IRON 텔레메트리 표가 아닙니다.");
        }

        lock (_gate)
        {
            return Convert.ToInt64(SqliteDatabase.Scalar(_connection, $"SELECT COUNT(*) FROM {table};") ?? 0L, CultureInfo.InvariantCulture);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }

    internal SqliteConnection ConnectionForTests => _connection;

    private static double? Finite(double? value) => value is double v && double.IsFinite(v) ? v : null;

    private int Execute(SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }
}
