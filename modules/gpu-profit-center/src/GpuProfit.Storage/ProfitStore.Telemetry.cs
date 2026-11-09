using System.Globalization;
using GpuProfit.Accounting.Energy;
using LocalAiPerf.Core.Hardware;
using Microsoft.Data.Sqlite;
using static LocalAiPerf.Storage.SqliteHelpers;

namespace GpuProfit.Storage;

public sealed partial class ProfitStore
{
    private readonly Dictionary<(int Index, string Name, bool Mock), long> _deviceIds = [];

    // ------------------------------------------------------------------ app runs

    public long StartRun(DateTimeOffset started, string appVersion, bool isMock, int sampleIntervalMs) => Write(c => ScalarLong(c, """
        INSERT INTO app_runs (started_ms, heartbeat_ms, app_version, is_mock, sample_interval_ms) VALUES (@s, @s, @v, @m, @i) RETURNING id;
        """, null, ("@s", started.ToUnixTimeMilliseconds()), ("@v", appVersion), ("@m", isMock ? 1 : 0), ("@i", sampleIntervalMs)));

    public void Heartbeat(long runId, DateTimeOffset time) => Write(c => Execute(c,
        "UPDATE app_runs SET heartbeat_ms = MAX(COALESCE(heartbeat_ms, 0), @t) WHERE id = @id AND ended_ms IS NULL;", null,
        ("@t", time.ToUnixTimeMilliseconds()), ("@id", runId)));

    public void EndRun(long runId, DateTimeOffset ended, string reason) => Write(c => Execute(c,
        "UPDATE app_runs SET ended_ms = @t, end_reason = @r, heartbeat_ms = @t WHERE id = @id AND ended_ms IS NULL;", null,
        ("@t", ended.ToUnixTimeMilliseconds()), ("@r", reason), ("@id", runId)));

    /// <summary>Last moment a run is known to have been alive: its heartbeat or its last sample.</summary>
    private static long LastAlive(SqliteConnection c, SqliteTransaction? tx, long runId, long fallback)
    {
        var heartbeat = Scalar(c, "SELECT heartbeat_ms FROM app_runs WHERE id = @id;", tx, ("@id", runId));
        var system = Scalar(c, "SELECT MAX(t_ms) FROM system_samples WHERE run_id = @id;", tx, ("@id", runId));
        var gpu = Scalar(c, "SELECT MAX(t_ms) FROM gpu_samples WHERE run_id = @id;", tx, ("@id", runId));
        return new[] { fallback, ToLong(heartbeat), ToLong(system), ToLong(gpu) }.Max();
    }

    private static long ToLong(object? value) => value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Closes runs left open by a crash, power loss or kill, at their last heartbeat or sample, and records each as an
    /// "app ended uncleanly" gap. Returns the recovered runs.
    /// </summary>
    public IReadOnlyList<AppRun> RecoverUnfinishedRuns() => WriteTx((c, tx) =>
    {
        var open = ReadRuns(c, tx, "WHERE ended_ms IS NULL");
        var now = _time.GetUtcNow();
        var recovered = new List<AppRun>();
        foreach (var run in open)
        {
            var last = LastAlive(c, tx, run.Id, run.Started.ToUnixTimeMilliseconds());
            Execute(c, "UPDATE app_runs SET ended_ms = @t, end_reason = @r WHERE id = @id;", tx,
                ("@t", last), ("@r", RunEndReasons.RecoveredAfterCrash), ("@id", run.Id));
            InsertGap(c, tx, new CollectionGap
            {
                Scope = "app",
                Kind = GapKinds.AppEndedUncleanly,
                From = At(last),
                To = now,
                Detected = now,
                Detail = "이전 실행이 정상 종료되지 않았습니다 (앱 강제 종료, 시스템 충돌 또는 전원 차단). 마지막 기록까지의 데이터는 보존되었습니다.",
            });
            recovered.Add(run with { Ended = At(last), EndReason = RunEndReasons.RecoveredAfterCrash });
        }

        return (IReadOnlyList<AppRun>)recovered;
    });

    public IReadOnlyList<AppRun> ListRuns(int limit = 200)
    {
        using var reader = OpenReader();
        return ReadRuns(reader, null, $"ORDER BY started_ms DESC LIMIT {limit.ToString(CultureInfo.InvariantCulture)}");
    }

    private static List<AppRun> ReadRuns(SqliteConnection connection, SqliteTransaction? tx, string tail)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT id, started_ms, ended_ms, end_reason, heartbeat_ms, app_version, is_mock, sample_interval_ms FROM app_runs {tail};";
        var runs = new List<AppRun>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            runs.Add(new AppRun
            {
                Id = r.GetInt64(0),
                Started = At(r.GetInt64(1)),
                Ended = Ms(L(r, 2)),
                EndReason = S(r, 3),
                LastHeartbeat = Ms(L(r, 4)),
                AppVersion = r.GetString(5),
                IsMock = r.GetInt64(6) != 0,
                SampleIntervalMs = r.GetInt32(7),
            });
        }

        return runs;
    }

    // ------------------------------------------------------------------ devices and samples

    private long EnsureDevice(SqliteConnection c, SqliteTransaction tx, GpuSample gpu, bool isMock, long t)
    {
        var name = string.IsNullOrWhiteSpace(gpu.Name) ? $"GPU {gpu.Index}" : gpu.Name!.Trim();
        var key = (gpu.Index, name, isMock);
        if (_deviceIds.TryGetValue(key, out var id))
        {
            return id;
        }

        id = ScalarLong(c, """
            INSERT INTO devices (gpu_index, name, is_mock, memory_total, power_limit_w, first_seen_ms, last_seen_ms)
            VALUES (@i, @n, @m, @mt, @pl, @t, @t)
            ON CONFLICT (gpu_index, name, is_mock) DO UPDATE SET last_seen_ms = MAX(last_seen_ms, excluded.last_seen_ms),
                memory_total = COALESCE(excluded.memory_total, memory_total), power_limit_w = COALESCE(excluded.power_limit_w, power_limit_w)
            RETURNING id;
            """, tx, ("@i", gpu.Index), ("@n", name), ("@m", isMock ? 1 : 0), ("@mt", gpu.MemoryTotalBytes), ("@pl", gpu.PowerLimitW), ("@t", t));
        _deviceIds[key] = id;
        return id;
    }

    /// <summary>
    /// Stores one snapshot (system values and every GPU) in one transaction. A snapshot whose time is already stored is
    /// ignored (no duplicates). Returns false when nothing new was written.
    /// </summary>
    public bool WriteSnapshot(long runId, HardwareSnapshot snapshot) => WriteTx((c, tx) => InsertSnapshot(c, tx, runId, snapshot));

    /// <summary>
    /// Stores many snapshots in one transaction (one disk sync instead of one per sample), for imported or generated
    /// data. Returns how many snapshots added new rows.
    /// </summary>
    public int WriteSnapshots(long runId, IEnumerable<HardwareSnapshot> snapshots) => WriteTx((c, tx) =>
    {
        var written = 0;
        foreach (var snapshot in snapshots)
        {
            if (InsertSnapshot(c, tx, runId, snapshot))
            {
                written++;
            }
        }

        return written;
    });

    private bool InsertSnapshot(SqliteConnection c, SqliteTransaction tx, long runId, HardwareSnapshot snapshot)
    {
        var t = snapshot.Timestamp.ToUnixTimeMilliseconds();
        {
            var written = Execute(c, """
                INSERT OR IGNORE INTO system_samples (t_ms, run_id, is_mock, cpu, ram_used, ram_total) VALUES (@t, @r, @m, @c, @u, @tot);
                """, tx, ("@t", t), ("@r", runId), ("@m", snapshot.IsMock ? 1 : 0), ("@c", snapshot.System.CpuUtilizationPercent),
                ("@u", snapshot.System.MemoryUsedBytes), ("@tot", snapshot.System.MemoryTotalBytes));
            foreach (var g in snapshot.Gpus)
            {
                var device = EnsureDevice(c, tx, g, snapshot.IsMock, t);
                written += Execute(c, """
                    INSERT OR IGNORE INTO gpu_samples (device_id, t_ms, run_id, power_w, util, mem_util, temp_c, vram_used, vram_total, clk_gfx, clk_mem, energy_mj)
                    VALUES (@d, @t, @r, @p, @u, @mu, @tc, @vu, @vt, @cg, @cm, @e);
                    """, tx, ("@d", device), ("@t", t), ("@r", runId), ("@p", Finite(g.PowerW)), ("@u", Finite(g.UtilizationPercent)),
                    ("@mu", Finite(g.MemoryControllerUtilizationPercent)), ("@tc", Finite(g.TemperatureC)), ("@vu", g.MemoryUsedBytes),
                    ("@vt", g.MemoryTotalBytes), ("@cg", Finite(g.GraphicsClockMhz)), ("@cm", Finite(g.MemoryClockMhz)), ("@e", g.TotalEnergyMilliJoules));
            }

            if (snapshot.Gpus.Count > 0)
            {
                Execute(c, "UPDATE devices SET last_seen_ms = MAX(last_seen_ms, @t) WHERE id IN (SELECT device_id FROM gpu_samples WHERE t_ms = @t);", tx, ("@t", t));
            }

            Execute(c, "UPDATE app_runs SET heartbeat_ms = MAX(COALESCE(heartbeat_ms, 0), @t) WHERE id = @id AND ended_ms IS NULL;", tx, ("@t", t), ("@id", runId));
            return written > 0;
        }
    }

    private static double? Finite(double? value) => value is double v && double.IsFinite(v) ? v : null;

    public IReadOnlyList<DeviceRecord> ListDevices()
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT id, gpu_index, name, is_mock, memory_total, power_limit_w, first_seen_ms, last_seen_ms FROM devices ORDER BY is_mock, gpu_index, last_seen_ms DESC;";
        var list = new List<DeviceRecord>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DeviceRecord(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetInt64(3) != 0, L(r, 4), D(r, 5), At(r.GetInt64(6)), At(r.GetInt64(7))));
        }

        return list;
    }

    /// <summary>GPU power samples (by GPU index) in [from, to], from real or mock sensors only.</summary>
    public List<PowerSample> LoadPowerSamples(DateTimeOffset from, DateTimeOffset to, bool isMock)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = """
            SELECT s.t_ms, d.gpu_index, s.power_w, s.energy_mj, s.util
            FROM gpu_samples s JOIN devices d ON d.id = s.device_id
            WHERE s.t_ms >= @f AND s.t_ms <= @t AND d.is_mock = @m
            ORDER BY s.t_ms;
            """;
        command.Parameters.AddWithValue("@f", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@t", to.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@m", isMock ? 1 : 0);
        var list = new List<PowerSample>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PowerSample(r.GetInt64(0), r.GetInt32(1), D(r, 2), L(r, 3), D(r, 4)));
        }

        return list;
    }

    /// <summary>GPU values averaged per time bucket (for history charts). Buckets without samples are absent (gaps stay gaps).</summary>
    public IReadOnlyList<GpuSeriesPoint> QueryGpuSeries(DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, bool? isMock = null)
    {
        var bucketMs = Math.Max(1000, (long)bucket.TotalMilliseconds);
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = """
            SELECT (s.t_ms / @b) * @b AS bt, d.gpu_index, AVG(s.power_w), MIN(s.power_w), MAX(s.power_w), AVG(s.util), AVG(s.temp_c),
                   AVG(s.vram_used), AVG(s.clk_gfx), AVG(s.clk_mem), COUNT(*)
            FROM gpu_samples s JOIN devices d ON d.id = s.device_id
            WHERE s.t_ms >= @f AND s.t_ms <= @t AND (@m IS NULL OR d.is_mock = @m)
            GROUP BY bt, d.gpu_index ORDER BY bt, d.gpu_index;
            """;
        command.Parameters.AddWithValue("@b", bucketMs);
        command.Parameters.AddWithValue("@f", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@t", to.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@m", isMock is bool m ? m ? 1 : 0 : DBNull.Value);
        var list = new List<GpuSeriesPoint>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new GpuSeriesPoint(At(r.GetInt64(0) + bucketMs / 2), r.GetInt32(1), D(r, 2), D(r, 3), D(r, 4), D(r, 5), D(r, 6),
                D(r, 7) is double vram ? vram / (1024.0 * 1024 * 1024) : null, D(r, 8), D(r, 9), r.GetInt32(10)));
        }

        return list;
    }

    public IReadOnlyList<SystemSeriesPoint> QuerySystemSeries(DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, bool? isMock = null)
    {
        var bucketMs = Math.Max(1000, (long)bucket.TotalMilliseconds);
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = """
            SELECT (t_ms / @b) * @b AS bt, AVG(cpu), AVG(ram_used), COUNT(*)
            FROM system_samples WHERE t_ms >= @f AND t_ms <= @t AND (@m IS NULL OR is_mock = @m)
            GROUP BY bt ORDER BY bt;
            """;
        command.Parameters.AddWithValue("@b", bucketMs);
        command.Parameters.AddWithValue("@f", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@t", to.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@m", isMock is bool m ? m ? 1 : 0 : DBNull.Value);
        var list = new List<SystemSeriesPoint>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SystemSeriesPoint(At(r.GetInt64(0) + bucketMs / 2), D(r, 1), D(r, 2) is double ram ? ram / (1024.0 * 1024 * 1024) : null, r.GetInt32(3)));
        }

        return list;
    }

    // ------------------------------------------------------------------ gaps

    public long AddGap(CollectionGap gap) => Write(c => InsertGap(c, null, gap));

    private static long InsertGap(SqliteConnection c, SqliteTransaction? tx, CollectionGap gap) => ScalarLong(c, """
        INSERT INTO gaps (scope, kind, from_ms, to_ms, detected_ms, detail) VALUES (@s, @k, @f, @t, @d, @x) RETURNING id;
        """, tx, ("@s", gap.Scope), ("@k", gap.Kind), ("@f", ToMs(gap.From)), ("@t", ToMs(gap.To)), ("@d", gap.Detected.ToUnixTimeMilliseconds()), ("@x", gap.Detail));

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
        command.Parameters.AddWithValue("@from", (object?)ToMs(from) ?? DBNull.Value);
        command.Parameters.AddWithValue("@to", (object?)ToMs(to) ?? DBNull.Value);
        command.Parameters.AddWithValue("@limit", limit);
        var gaps = new List<CollectionGap>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            gaps.Add(new CollectionGap
            {
                Id = r.GetInt64(0), Scope = r.GetString(1), Kind = r.GetString(2), From = Ms(L(r, 3)), To = Ms(L(r, 4)),
                Detected = At(r.GetInt64(5)), Detail = S(r, 6),
            });
        }

        return gaps;
    }
}
