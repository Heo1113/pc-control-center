using System.Globalization;
using LocalAiPerf.Core.Hardware;
using Microsoft.Data.Sqlite;
using static LocalAiPerf.Storage.SqliteHelpers;

namespace WinDiag.Storage;

public sealed partial class DiagnosticsStore
{
    /// <summary>
    /// Stores one sensor snapshot from project A's HardwareMonitor. Values the sensor did not provide stay NULL
    /// (shown as N/A), never estimated. Returns false when a sample with the same timestamp already exists.
    /// </summary>
    public bool WriteSensorSample(long runId, HardwareSnapshot snapshot) => WriteSensorSamples(runId, [snapshot]) > 0;

    /// <summary>Stores many samples in one transaction (imports and the virtual scenario). Returns how many were new.</summary>
    public int WriteSensorSamples(long runId, IEnumerable<HardwareSnapshot> snapshots)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            using var tx = _writer.BeginTransaction();
            var inserted = 0;
            long last = 0;
            foreach (var snapshot in snapshots)
            {
                if (WriteOne(runId, snapshot, tx))
                {
                    inserted++;
                }

                last = Math.Max(last, snapshot.Timestamp.ToUnixTimeMilliseconds());
            }

            Execute(_writer, "UPDATE runs SET heartbeat_ms = MAX(COALESCE(heartbeat_ms, 0), @t) WHERE id = @id AND ended_ms IS NULL;", tx,
                ("@t", last), ("@id", runId));
            tx.Commit();
            return inserted;
        }
    }

    private bool WriteOne(long runId, HardwareSnapshot snapshot, SqliteTransaction tx)
    {
        var t = snapshot.Timestamp.ToUnixTimeMilliseconds();
        var flags = SensorFlags.None;
        if (snapshot.IsMock)
        {
            flags |= SensorFlags.Mock;
        }

        if (snapshot.Issues.Count > 0)
        {
            flags |= SensorFlags.SensorIssue;
        }

        if (snapshot.Gpus.Count == 0)
        {
            flags |= SensorFlags.GpuUnavailable;
        }

        {
            var inserted = Execute(_writer, """
                INSERT OR IGNORE INTO sensor_samples (t_ms, run_id, cpu_util, ram_used, ram_total, flags)
                VALUES (@t, @run, @cpu, @used, @total, @flags);
                """, tx,
                ("@t", t), ("@run", runId), ("@cpu", Finite(snapshot.System.CpuUtilizationPercent)), ("@used", snapshot.System.MemoryUsedBytes),
                ("@total", snapshot.System.MemoryTotalBytes), ("@flags", (int)flags));
            if (inserted > 0)
            {
                foreach (var g in snapshot.Gpus)
                {
                    Execute(_writer, """
                        INSERT OR IGNORE INTO gpu_samples (t_ms, gpu_index, util, mem_util, temp_c, power_w, power_limit_w, vram_used, vram_total,
                                                           clock_gfx_mhz, clock_mem_mhz, fan_pct, throttle)
                        VALUES (@t, @i, @u, @mu, @tc, @p, @pl, @vu, @vt, @cg, @cm, @f, @th);
                        """, tx,
                        ("@t", t), ("@i", g.Index), ("@u", Finite(g.UtilizationPercent)), ("@mu", Finite(g.MemoryControllerUtilizationPercent)),
                        ("@tc", Finite(g.TemperatureC)), ("@p", Finite(g.PowerW)), ("@pl", Finite(g.PowerLimitW)), ("@vu", g.MemoryUsedBytes),
                        ("@vt", g.MemoryTotalBytes), ("@cg", Finite(g.GraphicsClockMhz)), ("@cm", Finite(g.MemoryClockMhz)),
                        ("@f", Finite(g.FanSpeedPercent)), ("@th", g.ThrottleReasons is ulong th ? unchecked((long)th) : null));
                }
            }

            return inserted > 0;
        }
    }

    /// <summary>
    /// Sensor data in a range. With more than <paramref name="maxPoints"/> samples the data is averaged into equal time
    /// buckets (BucketMs &gt; 0); peaks are then flattened, so event windows should use a short range at full resolution.
    /// </summary>
    public SensorSeries QuerySensors(DateTimeOffset from, DateTimeOffset to, int maxPoints = 2000)
    {
        var fromMs = from.ToUnixTimeMilliseconds();
        var toMs = to.ToUnixTimeMilliseconds();
        if (toMs < fromMs)
        {
            return SensorSeries.Empty;
        }

        using var reader = OpenReader();
        var count = ScalarLong(reader, "SELECT COUNT(*) FROM sensor_samples WHERE t_ms BETWEEN @f AND @t;", null, ("@f", fromMs), ("@t", toMs));
        long bucket = 0;
        if (count > maxPoints && maxPoints > 0)
        {
            bucket = Math.Max(1000, (long)Math.Ceiling((toMs - fromMs) / (double)maxPoints));
        }

        var system = new List<SensorPoint>();
        using (var command = reader.CreateCommand())
        {
            command.CommandText = bucket == 0
                ? "SELECT t_ms, cpu_util, ram_used, ram_total FROM sensor_samples WHERE t_ms BETWEEN @f AND @t ORDER BY t_ms;"
                : """
                  SELECT (t_ms / @b) * @b AS bt, AVG(cpu_util), CAST(AVG(ram_used) AS INTEGER), MAX(ram_total)
                  FROM sensor_samples WHERE t_ms BETWEEN @f AND @t GROUP BY bt ORDER BY bt;
                  """;
            command.Parameters.AddWithValue("@f", fromMs);
            command.Parameters.AddWithValue("@t", toMs);
            command.Parameters.AddWithValue("@b", Math.Max(1, bucket));
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                system.Add(new SensorPoint(r.GetInt64(0), D(r, 1), L(r, 2), L(r, 3)));
            }
        }

        var gpu = new List<GpuSensorPoint>();
        using (var command = reader.CreateCommand())
        {
            command.CommandText = bucket == 0
                ? """
                  SELECT t_ms, gpu_index, util, mem_util, temp_c, power_w, power_limit_w, vram_used, vram_total, clock_gfx_mhz, clock_mem_mhz,
                         fan_pct, throttle
                  FROM gpu_samples WHERE t_ms BETWEEN @f AND @t ORDER BY gpu_index, t_ms;
                  """
                : """
                  SELECT (t_ms / @b) * @b AS bt, gpu_index, AVG(util), AVG(mem_util), AVG(temp_c), AVG(power_w), MAX(power_limit_w),
                         CAST(AVG(vram_used) AS INTEGER), MAX(vram_total), AVG(clock_gfx_mhz), AVG(clock_mem_mhz), AVG(fan_pct), NULL
                  FROM gpu_samples WHERE t_ms BETWEEN @f AND @t GROUP BY gpu_index, bt ORDER BY gpu_index, bt;
                  """;
            command.Parameters.AddWithValue("@f", fromMs);
            command.Parameters.AddWithValue("@t", toMs);
            command.Parameters.AddWithValue("@b", Math.Max(1, bucket));
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                gpu.Add(new GpuSensorPoint(r.GetInt64(0), r.GetInt32(1), D(r, 2), D(r, 3), D(r, 4), D(r, 5), D(r, 6), L(r, 7), L(r, 8),
                    D(r, 9), D(r, 10), D(r, 11), L(r, 12)));
            }
        }

        return new SensorSeries(system, gpu, bucket);
    }

    /// <summary>Full-resolution count/mean/min/max per metric over a range (GPU metrics of one GPU).</summary>
    public IReadOnlyList<MetricAggregate> AggregateSensors(DateTimeOffset from, DateTimeOffset to, int gpuIndex = 0)
    {
        using var reader = OpenReader();
        var result = new List<MetricAggregate>();
        var f = from.ToUnixTimeMilliseconds();
        var t = to.ToUnixTimeMilliseconds();
        void Add(string metric, string table, string expression, string extra = "")
        {
            using var command = reader.CreateCommand();
            command.CommandText = $"SELECT COUNT({expression}), AVG({expression}), MIN({expression}), MAX({expression}) FROM {table} WHERE t_ms BETWEEN @f AND @t {extra};";
            command.Parameters.AddWithValue("@f", f);
            command.Parameters.AddWithValue("@t", t);
            command.Parameters.AddWithValue("@g", gpuIndex);
            using var r = command.ExecuteReader();
            if (r.Read())
            {
                result.Add(new MetricAggregate(metric, r.GetInt64(0), D(r, 1), D(r, 2), D(r, 3)));
            }
        }

        Add(SensorMetrics.CpuUtil, "sensor_samples", "cpu_util");
        Add(SensorMetrics.RamUsedPct, "sensor_samples", "CASE WHEN ram_total > 0 THEN 100.0 * ram_used / ram_total END");
        Add(SensorMetrics.GpuUtil, "gpu_samples", "util", "AND gpu_index = @g");
        Add(SensorMetrics.GpuTemp, "gpu_samples", "temp_c", "AND gpu_index = @g");
        Add(SensorMetrics.GpuPower, "gpu_samples", "power_w", "AND gpu_index = @g");
        Add(SensorMetrics.GpuClock, "gpu_samples", "clock_gfx_mhz", "AND gpu_index = @g");
        Add(SensorMetrics.VramUsedPct, "gpu_samples", "CASE WHEN vram_total > 0 THEN 100.0 * vram_used / vram_total END", "AND gpu_index = @g");
        return result;
    }

    /// <summary>Time ranges with sensor samples (one per app run that recorded any), clipped to the range.</summary>
    public IReadOnlyList<(DateTimeOffset From, DateTimeOffset To, long Samples, bool IsMock)> SensorCoverage(DateTimeOffset from, DateTimeOffset to)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = """
            SELECT MIN(s.t_ms), MAX(s.t_ms), COUNT(*), MAX(r.is_mock)
            FROM sensor_samples s JOIN runs r ON r.id = s.run_id
            WHERE s.t_ms BETWEEN @f AND @t GROUP BY s.run_id ORDER BY MIN(s.t_ms);
            """;
        command.Parameters.AddWithValue("@f", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@t", to.ToUnixTimeMilliseconds());
        var list = new List<(DateTimeOffset, DateTimeOffset, long, bool)>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add((DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)),
                r.GetInt64(2), r.GetInt64(3) != 0));
        }

        return list;
    }

    private static double? Finite(double? value) => value is double v && double.IsFinite(v) ? v : null;
}

/// <summary>Sensor metric keys used by aggregates and comparisons (aligned with project A's MetricKeys where they exist).</summary>
public static class SensorMetrics
{
    public const string CpuUtil = LocalAiPerf.Core.Metrics.MetricKeys.CpuUtil;
    public const string RamUsedPct = LocalAiPerf.Core.Metrics.MetricKeys.RamUsedPct;
    public const string GpuUtil = LocalAiPerf.Core.Metrics.MetricKeys.GpuUtil;
    public const string GpuTemp = LocalAiPerf.Core.Metrics.MetricKeys.GpuTemp;
    public const string GpuPower = LocalAiPerf.Core.Metrics.MetricKeys.GpuPower;
    public const string GpuClock = LocalAiPerf.Core.Metrics.MetricKeys.GpuClockGraphics;
    public const string VramUsedPct = LocalAiPerf.Core.Metrics.MetricKeys.GpuVramUsedPct;

    public static string Name(string metric) => metric switch
    {
        CpuUtil => "CPU 사용률",
        RamUsedPct => "RAM 사용 비율",
        GpuUtil => "GPU 사용률",
        GpuTemp => "GPU 온도",
        GpuPower => "GPU 전력",
        GpuClock => "GPU 그래픽 클럭",
        VramUsedPct => "VRAM 사용 비율",
        _ => metric,
    };

    public static string Unit(string metric) => metric switch
    {
        CpuUtil or RamUsedPct or GpuUtil or VramUsedPct => "%",
        GpuTemp => "°C",
        GpuPower => "W",
        GpuClock => "MHz",
        _ => "",
    };

    public static string Format(double? value) => value is double v && double.IsFinite(v) ? v.ToString("0.#", CultureInfo.InvariantCulture) : "N/A";
}
