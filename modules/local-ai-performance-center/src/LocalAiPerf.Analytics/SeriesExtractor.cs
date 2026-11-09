using LocalAiPerf.Core.Metrics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Storage;

namespace LocalAiPerf.Analytics;

/// <summary>Turns stored rows into one time series per metric. Missing values are skipped, never filled in.</summary>
public static class SeriesExtractor
{
    private const double GiB = 1024.0 * 1024 * 1024;

    public static IReadOnlyDictionary<string, MetricSeries> Extract(SessionData data, int gpuIndex = 0)
    {
        var result = new Dictionary<string, MetricSeries>(StringComparer.Ordinal);
        void Put(string key, IEnumerable<SeriesPoint> points) => result[key] = new MetricSeries(key, points.OrderBy(p => p.TimeMs).ToList());

        var gpu = data.Gpu.Where(g => g.GpuIndex == gpuIndex).ToList();
        Put(MetricKeys.GpuUtil, Points(gpu, g => g.Util));
        Put(MetricKeys.GpuMemUtil, Points(gpu, g => g.MemUtil));
        Put(MetricKeys.GpuTemp, Points(gpu, g => g.TempC));
        Put(MetricKeys.GpuPower, Points(gpu, g => g.PowerW));
        Put(MetricKeys.GpuPowerLimit, Points(gpu, g => g.PowerLimitW));
        Put(MetricKeys.GpuVramUsedGiB, Points(gpu, g => g.VramUsed / GiB));
        Put(MetricKeys.GpuVramUsedPct, Points(gpu, g => g.VramUsed is long u && g.VramTotal is long t && t > 0 ? 100.0 * u / t : null));
        Put(MetricKeys.GpuClockGraphics, Points(gpu, g => g.ClockGfxMhz));
        Put(MetricKeys.GpuClockMemory, Points(gpu, g => g.ClockMemMhz));
        Put(MetricKeys.GpuFan, Points(gpu, g => g.FanPct));
        Put(MetricKeys.GpuPcieTx, Points(gpu, g => g.PcieTxKBps / 1024.0));
        Put(MetricKeys.GpuPcieRx, Points(gpu, g => g.PcieRxKBps / 1024.0));

        Put(MetricKeys.CpuUtil, Points(data.System, s => s.CpuUtil));
        Put(MetricKeys.RamUsedGiB, Points(data.System, s => s.RamUsed / GiB));
        Put(MetricKeys.RamUsedPct, Points(data.System, s => s.RamUsed is long u && s.RamTotal is long t && t > 0 ? 100.0 * u / t : null));

        var reachable = data.Runtime.Where(r => r.State is RuntimeConnectionState.Connected or RuntimeConnectionState.Limited).ToList();
        Put(MetricKeys.RtAnswerTokS, Points(reachable, r => r.AnswerTokS));
        Put(MetricKeys.RtPrefillTokS, Points(reachable, r => r.PrefillTokS));

        // Aggregate throughput only while work is in flight: idle zeros would turn a throughput mean into a duty-cycle mean.
        var busy = reachable.Where(r => r.Active is > 0).ToList();
        Put(MetricKeys.RtAggregateTokS, Points(busy, r => r.AggregateTokS));
        Put(MetricKeys.RtActive, Points(reachable, r => (double?)r.Active));
        Put(MetricKeys.RtQueued, Points(reachable, r => (double?)r.Queued));
        Put(MetricKeys.RtContextUsed, Points(reachable, r => (double?)r.ContextUsed));
        Put(MetricKeys.RtContextFillPct, Points(reachable, r => r.ContextUsed is long u && r.ContextMax is long m && m > 0 ? 100.0 * u / m : null));

        var requests = data.Requests.Select(r => (Time: r.StartedMs ?? r.ObservedMs, Request: r.ToCompletedRequest())).ToList();
        Put(MetricKeys.ReqDecodeTokS, requests.Where(r => r.Request.DecodeTokPerSec is not null).Select(r => new SeriesPoint(r.Time, r.Request.DecodeTokPerSec!.Value)));
        Put(MetricKeys.ReqPrefillTokS, requests.Where(r => r.Request.PrefillTokPerSec is not null).Select(r => new SeriesPoint(r.Time, r.Request.PrefillTokPerSec!.Value)));
        Put(MetricKeys.ReqFirstTokenS, requests.Where(r => r.Request.FirstEngineTokenSeconds is not null).Select(r => new SeriesPoint(r.Time, r.Request.FirstEngineTokenSeconds!.Value)));
        Put(MetricKeys.ReqDurationS, requests.Where(r => r.Request.DurationSeconds is not null).Select(r => new SeriesPoint(r.Time, r.Request.DurationSeconds!.Value)));
        Put(MetricKeys.ReqOutputTokens, requests.Where(r => r.Request.OutputTokens is not null).Select(r => new SeriesPoint(r.Time, r.Request.OutputTokens!.Value)));

        Put(MetricKeys.EffTokensPerJoule, Efficiency(busy, gpu, Math.Max(1000, data.Session.SampleIntervalMs)));
        return result;
    }

    /// <summary>tokens/J = aggregate tok/s divided by GPU power measured at (nearly) the same moment.</summary>
    private static IEnumerable<SeriesPoint> Efficiency(IReadOnlyList<RuntimePoint> busy, IReadOnlyList<GpuPoint> gpu, int intervalMs)
    {
        if (gpu.Count == 0)
        {
            yield break;
        }

        var times = gpu.Select(g => g.TimeMs).ToArray();
        foreach (var r in busy)
        {
            if (r.AggregateTokS is not double tokS)
            {
                continue;
            }

            var index = Array.BinarySearch(times, r.TimeMs);
            if (index < 0)
            {
                index = ~index;
            }

            GpuPoint? nearest = null;
            foreach (var candidate in new[] { index - 1, index })
            {
                if (candidate >= 0 && candidate < gpu.Count && Math.Abs(gpu[candidate].TimeMs - r.TimeMs) <= intervalMs * 1.5
                    && (nearest is null || Math.Abs(gpu[candidate].TimeMs - r.TimeMs) < Math.Abs(nearest.TimeMs - r.TimeMs)))
                {
                    nearest = gpu[candidate];
                }
            }

            if (nearest?.PowerW is double watts && watts > 1)
            {
                yield return new SeriesPoint(r.TimeMs, tokS / watts);
            }
        }
    }

    private static IEnumerable<SeriesPoint> Points(IEnumerable<GpuPoint> rows, Func<GpuPoint, double?> value)
        => Select(rows, r => r.TimeMs, value);

    private static IEnumerable<SeriesPoint> Points(IEnumerable<SystemPoint> rows, Func<SystemPoint, double?> value)
        => Select(rows, r => r.TimeMs, value);

    private static IEnumerable<SeriesPoint> Points(IEnumerable<RuntimePoint> rows, Func<RuntimePoint, double?> value)
        => Select(rows, r => r.TimeMs, value);

    private static IEnumerable<SeriesPoint> Select<T>(IEnumerable<T> rows, Func<T, long> time, Func<T, double?> value)
    {
        foreach (var row in rows)
        {
            if (value(row) is double v && double.IsFinite(v))
            {
                yield return new SeriesPoint(time(row), v);
            }
        }
    }

    /// <summary>The metrics shown in session summaries and comparisons, in display order.</summary>
    public static IReadOnlyList<string> SummaryMetricKeys { get; } =
    [
        MetricKeys.RtAnswerTokS, MetricKeys.RtPrefillTokS, MetricKeys.RtAggregateTokS, MetricKeys.ReqDecodeTokS, MetricKeys.ReqPrefillTokS,
        MetricKeys.ReqFirstTokenS, MetricKeys.ReqDurationS, MetricKeys.EffTokensPerJoule,
        MetricKeys.GpuUtil, MetricKeys.GpuTemp, MetricKeys.GpuPower, MetricKeys.GpuVramUsedGiB, MetricKeys.GpuClockGraphics,
        MetricKeys.GpuClockMemory, MetricKeys.GpuMemUtil, MetricKeys.GpuPcieRx, MetricKeys.GpuPcieTx, MetricKeys.GpuFan,
        MetricKeys.CpuUtil, MetricKeys.RamUsedGiB, MetricKeys.RtContextUsed, MetricKeys.RtQueued,
    ];
}
