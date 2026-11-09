using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Storage;

namespace LocalAiPerf.Analytics;

/// <summary>A stretch where a metric stayed well below its own recent baseline. A candidate, not a diagnosis.</summary>
public sealed record DegradationCandidate(
    string MetricKey,
    long StartMs,
    long EndMs,
    int Points,
    double Mean,
    double Baseline,
    double DropPercent,
    IReadOnlyList<string> ConcurrentObservations)
{
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(0, EndMs - StartMs));
}

/// <summary>A stretch where NVML reported a performance-limiting clock reason or the GPU ran hot.</summary>
public sealed record ThrottleInterval(long StartMs, long EndMs, string Reason, double? MaxTempC);

public sealed record DetectionOptions(double ThresholdPercent, TimeSpan MinDuration, TimeSpan BaselineWindow, double HighGpuTempC, int MinBaselinePoints = 5)
{
    public static DetectionOptions Default { get; } = new(20, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120), 83);
}

public static class DegradationDetector
{
    /// <summary>
    /// Rolling-median baseline: a point is low when it is more than the threshold below the median of the preceding
    /// baseline window. Consecutive low points form a candidate when they last at least the minimum duration
    /// (or, for sparse per-request series, when at least <paramref name="minConsecutivePoints"/> points are low).
    /// </summary>
    public static IReadOnlyList<DegradationCandidate> Detect(MetricSeries series, DetectionOptions options, int minConsecutivePoints = 3)
    {
        var points = series.Points;
        var windowMs = (long)options.BaselineWindow.TotalMilliseconds;
        var results = new List<DegradationCandidate>();
        var run = new List<(SeriesPoint Point, double Baseline)>();
        var lo = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            while (lo < i && points[lo].TimeMs < current.TimeMs - windowMs)
            {
                lo++;
            }

            var window = new List<double>(i - lo);
            for (var j = lo; j < i; j++)
            {
                window.Add(points[j].Value);
            }

            var low = false;
            double baseline = double.NaN;
            if (window.Count >= options.MinBaselinePoints)
            {
                baseline = Statistics.Median(window);
                low = baseline > 0 && current.Value < baseline * (1 - options.ThresholdPercent / 100.0);
            }

            if (low)
            {
                run.Add((current, baseline));
            }
            else
            {
                Close(series.MetricKey, run, options, minConsecutivePoints, results);
                run.Clear();
            }
        }

        Close(series.MetricKey, run, options, minConsecutivePoints, results);
        return results;
    }

    private static void Close(string key, List<(SeriesPoint Point, double Baseline)> run, DetectionOptions options, int minPoints, List<DegradationCandidate> results)
    {
        if (run.Count < 2)
        {
            return;
        }

        var start = run[0].Point.TimeMs;
        var end = run[^1].Point.TimeMs;
        if (end - start < options.MinDuration.TotalMilliseconds && run.Count < minPoints)
        {
            return;
        }

        var mean = run.Average(r => r.Point.Value);
        var baseline = Statistics.Median(run.Select(r => r.Baseline));
        results.Add(new DegradationCandidate(key, start, end, run.Count, mean, baseline, (1 - mean / baseline) * 100, []));
    }

    /// <summary>
    /// Lists what else was observed during a candidate. These are overlaps in time only; the caller must not
    /// present them as causes.
    /// </summary>
    public static DegradationCandidate Annotate(DegradationCandidate candidate, SessionData data, int gpuIndex, double highTempC)
    {
        var notes = new List<string>();
        var from = candidate.StartMs - 1000;
        var to = candidate.EndMs + 1000;
        var gpu = data.Gpu.Where(g => g.GpuIndex == gpuIndex && g.TimeMs >= from && g.TimeMs <= to).ToList();
        if (gpu.Count > 0)
        {
            var maxTemp = gpu.Max(g => g.TempC);
            if (maxTemp is double t && t >= highTempC)
            {
                notes.Add($"GPU 온도 최고 {t:0}°C (고온 기준 {highTempC:0}°C 이상)");
            }

            var reasons = gpu.Aggregate(0UL, (acc, g) => acc | (ulong)(g.Throttle ?? 0)) & (ulong)GpuThrottleReasonText.Limiting;
            if (reasons != 0)
            {
                notes.Add($"NVML 클럭 제한 사유: {GpuThrottleReasonText.Describe(reasons)}");
            }

            var clock = gpu.Where(g => g.ClockGfxMhz is not null).Select(g => g.ClockGfxMhz!.Value).ToList();
            var before = data.Gpu.Where(g => g.GpuIndex == gpuIndex && g.TimeMs < from && g.TimeMs >= from - 120_000 && g.ClockGfxMhz is not null)
                .Select(g => g.ClockGfxMhz!.Value).ToList();
            if (clock.Count > 0 && before.Count >= 3)
            {
                var change = Statistics.PercentChange(before.Average(), clock.Average());
                if (change is double c && c <= -5)
                {
                    notes.Add($"GPU 그래픽 클럭 평균 {c:0}% (직전 2분 대비)");
                }
            }

            var vram = gpu.Where(g => g.VramUsed is not null && g.VramTotal is > 0).Select(g => 100.0 * g.VramUsed!.Value / g.VramTotal!.Value).ToList();
            if (vram.Count > 0 && vram.Max() >= 95)
            {
                notes.Add($"VRAM 사용 비율 최고 {vram.Max():0}%");
            }
        }

        var cpu = data.System.Where(s => s.TimeMs >= from && s.TimeMs <= to && s.CpuUtil is not null).Select(s => s.CpuUtil!.Value).ToList();
        if (cpu.Count > 0 && cpu.Average() >= 85)
        {
            notes.Add($"CPU 사용률 평균 {cpu.Average():0}%");
        }

        var runtime = data.Runtime.Where(r => r.TimeMs >= from && r.TimeMs <= to).ToList();
        if (runtime.Any(r => r.Queued is > 0))
        {
            notes.Add($"대기 요청 최대 {runtime.Max(r => r.Queued ?? 0)}건");
        }

        if (runtime.Count > 0 && runtime.Max(r => r.Active ?? 0) > 1)
        {
            notes.Add($"동시 처리 요청 최대 {runtime.Max(r => r.Active ?? 0)}건 (요청당 속도는 동시 처리 수에 따라 달라짐)");
        }

        var fill = runtime.Where(r => r.ContextUsed is not null && r.ContextMax is > 0).Select(r => 100.0 * r.ContextUsed!.Value / r.ContextMax!.Value).ToList();
        if (fill.Count > 0 && fill.Max() >= 50)
        {
            notes.Add($"컨텍스트 사용 비율 최고 {fill.Max():0}% (긴 컨텍스트는 생성 속도를 낮출 수 있음)");
        }

        var events = data.Events.Where(e => e.Time.ToUnixTimeMilliseconds() >= from && e.Time.ToUnixTimeMilliseconds() <= to
            && e.Severity >= EventSeverity.Warning).ToList();
        if (events.Count > 0)
        {
            notes.Add($"같은 구간의 경고/오류 이벤트 {events.Count}건");
        }

        return candidate with { ConcurrentObservations = notes };
    }
}

public static class ThrottleDetector
{
    public static IReadOnlyList<ThrottleInterval> Detect(IReadOnlyList<GpuPoint> gpu, int gpuIndex, double highTempC, int intervalMs)
    {
        var rows = gpu.Where(g => g.GpuIndex == gpuIndex).OrderBy(g => g.TimeMs).ToList();
        var results = new List<ThrottleInterval>();
        long? start = null;
        long last = 0;
        ulong reasons = 0;
        double? maxTemp = null;
        var maxGap = Math.Max(3 * intervalMs, 3000);

        void Flush()
        {
            if (start is long s)
            {
                var text = reasons != 0 ? GpuThrottleReasonText.Describe(reasons) : "";
                if (maxTemp is double t && t >= highTempC)
                {
                    text = string.IsNullOrEmpty(text) ? $"고온 {t:0}°C" : $"{text}, 고온 {t:0}°C";
                }

                results.Add(new ThrottleInterval(s, last, text, maxTemp));
            }

            start = null;
            reasons = 0;
            maxTemp = null;
        }

        foreach (var g in rows)
        {
            var limiting = (ulong)(g.Throttle ?? 0) & (ulong)GpuThrottleReasonText.Limiting;
            var hot = g.TempC is double temp && temp >= highTempC;
            if (limiting != 0 || hot)
            {
                if (start is not null && g.TimeMs - last > maxGap)
                {
                    Flush();
                }

                start ??= g.TimeMs;
                last = g.TimeMs;
                reasons |= limiting;
                if (g.TempC is double t2)
                {
                    maxTemp = Math.Max(maxTemp ?? t2, t2);
                }
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return results;
    }
}
