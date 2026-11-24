using WinDiag.Storage;

namespace WinDiag.Analysis;

/// <summary>
/// Sensor values before and after an event, at full resolution. When the app was not recording at the event time the
/// result says so instead of showing an empty chart without explanation.
/// </summary>
public static class SensorContext
{
    private static readonly (string Metric, Func<SensorPoint, double?>? System, Func<GpuSensorPoint, double?>? Gpu)[] Metrics =
    [
        (SensorMetrics.GpuPower, null, g => g.PowerW),
        (SensorMetrics.GpuTemp, null, g => g.TempC),
        (SensorMetrics.GpuUtil, null, g => g.Util),
        (SensorMetrics.GpuClock, null, g => g.ClockGfxMhz),
        (SensorMetrics.VramUsedPct, null, g => g.VramUsed is long u && g.VramTotal is long t && t > 0 ? 100.0 * u / t : null),
        (SensorMetrics.CpuUtil, s => s.CpuUtil, null),
        (SensorMetrics.RamUsedPct, s => s.RamUsedPercent, null),
    ];

    public static EventSensorContext Build(DiagnosticsStore store, DateTimeOffset eventTime, TimeSpan window, int gpuIndex = 0)
    {
        var series = store.QuerySensors(eventTime - window, eventTime + window, maxPoints: 20_000);
        var coverage = store.SensorCoverage(eventTime - window, eventTime + window);
        var isMock = coverage.Any(c => c.IsMock);
        var t = eventTime.ToUnixTimeMilliseconds();
        var near = TimeSpan.FromSeconds(90).TotalMilliseconds;
        var hasCoverage = series.System.Any(p => Math.Abs(p.TimeMs - t) <= near) || series.Gpu.Any(p => Math.Abs(p.TimeMs - t) <= near);
        var note = hasCoverage
            ? isMock ? "MOCK(가상) 센서 값입니다. 실제 하드웨어 측정이 아닙니다." : "이벤트 시각 전후에 센서 기록이 있습니다."
            : series.IsEmpty
                ? "이 시각 전후에는 센서 기록이 없습니다 (앱이 실행 중이 아니었거나 센서 기록이 꺼져 있었음)."
                : "이벤트 시각 바로 전후에는 센서 기록이 없고 구간 일부에만 있습니다.";

        var gpu = series.Gpu.Where(g => g.GpuIndex == gpuIndex).ToList();
        var stats = new List<MetricWindowStats>();
        foreach (var (metric, system, gpuValue) in Metrics)
        {
            var points = system is not null
                ? series.System.Select(p => (p.TimeMs, Value: system(p))).ToList()
                : gpu.Select(p => (p.TimeMs, Value: gpuValue!(p))).ToList();
            var before = points.Where(p => p.TimeMs < t && p.Value is double).Select(p => p.Value!.Value).ToList();
            var after = points.Where(p => p.TimeMs >= t && p.Value is double).Select(p => p.Value!.Value).ToList();
            var at = points.Where(p => p.Value is double && Math.Abs(p.TimeMs - t) <= near)
                .OrderBy(p => Math.Abs(p.TimeMs - t)).Select(p => p.Value).FirstOrDefault();
            if (before.Count == 0 && after.Count == 0)
            {
                continue;
            }

            stats.Add(new MetricWindowStats(metric, SensorMetrics.Name(metric), SensorMetrics.Unit(metric),
                before.Count > 0 ? before.Average() : null, before.Count > 0 ? before.Max() : null,
                after.Count > 0 ? after.Average() : null, after.Count > 0 ? after.Max() : null,
                at, before.Count, after.Count));
        }

        return new EventSensorContext(eventTime, window, series, stats, hasCoverage, isMock, note);
    }

    /// <summary>Mean of a GPU metric in the <paramref name="lookback"/> before each time (only times with data).</summary>
    internal static List<double> MeansBefore(IReadOnlyList<GpuSensorPoint> samples, IEnumerable<long> times, TimeSpan lookback, Func<GpuSensorPoint, double?> value)
    {
        var result = new List<double>();
        var span = (long)lookback.TotalMilliseconds;
        foreach (var t in times)
        {
            var values = samples.Where(s => s.TimeMs <= t && s.TimeMs >= t - span).Select(value).OfType<double>().ToList();
            if (values.Count > 0)
            {
                result.Add(values.Average());
            }
        }

        return result;
    }
}
