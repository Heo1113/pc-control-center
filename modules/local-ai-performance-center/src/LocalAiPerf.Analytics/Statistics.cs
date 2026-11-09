namespace LocalAiPerf.Analytics;

public readonly record struct SeriesPoint(long TimeMs, double Value);

public sealed record MetricSeries(string MetricKey, IReadOnlyList<SeriesPoint> Points)
{
    public static MetricSeries Empty(string key) => new(key, []);

    /// <summary>Points re-expressed as seconds since <paramref name="startMs"/> (for overlaying sessions).</summary>
    public IReadOnlyList<(double Seconds, double Value)> Elapsed(long startMs)
        => Points.Select(p => ((p.TimeMs - startMs) / 1000.0, p.Value)).ToList();
}

public sealed record SummaryStats(int Count, double? Mean, double? Min, double? Max, double? StdDev, double? P50, double? P95)
{
    public static SummaryStats Empty { get; } = new(0, null, null, null, null, null, null);
}

/// <summary>Trend over the measured interval: first 10 % versus last 10 % of the session, and a least-squares slope.</summary>
public sealed record TrendInfo(double? FirstWindowMean, double? LastWindowMean, double? ChangePercent, double? SlopePerHour);

public static class Statistics
{
    public static SummaryStats Summarize(IEnumerable<double> source)
    {
        var values = source.Where(double.IsFinite).ToArray();
        if (values.Length == 0)
        {
            return SummaryStats.Empty;
        }

        Array.Sort(values);
        var mean = values.Average();
        var variance = values.Length > 1 ? values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1) : 0;
        return new SummaryStats(values.Length, mean, values[0], values[^1], Math.Sqrt(variance), Percentile(values, 0.50), Percentile(values, 0.95));
    }

    /// <summary>Linear interpolation percentile over an already sorted array.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        var rank = Math.Clamp(p, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? double.NaN : Percentile(sorted, 0.5);
    }

    /// <summary>Least-squares slope in value units per hour; null with fewer than 3 points or no time spread.</summary>
    public static double? SlopePerHour(IReadOnlyList<SeriesPoint> points)
    {
        if (points.Count < 3)
        {
            return null;
        }

        var t0 = points[0].TimeMs;
        double n = points.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var p in points)
        {
            var x = (p.TimeMs - t0) / 3_600_000.0;
            sx += x;
            sy += p.Value;
            sxx += x * x;
            sxy += x * p.Value;
        }

        var denominator = n * sxx - sx * sx;
        return Math.Abs(denominator) < 1e-12 ? null : (n * sxy - sx * sy) / denominator;
    }

    public static TrendInfo Trend(IReadOnlyList<SeriesPoint> points, int minPointsPerWindow = 3)
    {
        if (points.Count < 2 * minPointsPerWindow)
        {
            return new TrendInfo(null, null, null, SlopePerHour(points));
        }

        var start = points[0].TimeMs;
        var end = points[^1].TimeMs;
        var span = Math.Max(1, end - start);
        var first = points.Where(p => p.TimeMs <= start + span / 10).Select(p => p.Value).ToList();
        var last = points.Where(p => p.TimeMs >= end - span / 10).Select(p => p.Value).ToList();
        if (first.Count < minPointsPerWindow)
        {
            first = points.Take(minPointsPerWindow).Select(p => p.Value).ToList();
        }

        if (last.Count < minPointsPerWindow)
        {
            last = points.TakeLast(minPointsPerWindow).Select(p => p.Value).ToList();
        }

        var a = first.Average();
        var b = last.Average();
        double? change = Math.Abs(a) > 1e-9 ? (b - a) / Math.Abs(a) * 100 : null;
        return new TrendInfo(a, b, change, SlopePerHour(points));
    }

    /// <summary>Percent difference of <paramref name="value"/> relative to <paramref name="baseline"/>; null when undefined.</summary>
    public static double? PercentChange(double? baseline, double? value)
        => baseline is double b && value is double v && Math.Abs(b) > 1e-9 ? (v - b) / Math.Abs(b) * 100 : null;
}
