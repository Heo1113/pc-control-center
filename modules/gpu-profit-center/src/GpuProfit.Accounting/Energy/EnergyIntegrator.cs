namespace GpuProfit.Accounting.Energy;

/// <summary>One GPU power reading with its real timestamp (Unix milliseconds, UTC).</summary>
public readonly record struct PowerSample(long TimeMs, int DeviceIndex, double? PowerW, long? EnergyMilliJoules = null, double? UtilizationPercent = null);

/// <summary>A half-open time range [Start, End) in Unix milliseconds.</summary>
public readonly record struct TimeWindow(long StartMs, long EndMs)
{
    public double Seconds => EndMs > StartMs ? (EndMs - StartMs) / 1000.0 : 0;

    public static TimeWindow Of(DateTimeOffset start, DateTimeOffset end) => new(start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds());

    /// <summary>Sorts, drops empty ranges and merges overlapping ones, so no time is counted twice.</summary>
    public static IReadOnlyList<TimeWindow> Normalize(IEnumerable<TimeWindow> windows)
    {
        var sorted = windows.Where(w => w.EndMs > w.StartMs).OrderBy(w => w.StartMs).ToList();
        var merged = new List<TimeWindow>(sorted.Count);
        foreach (var w in sorted)
        {
            if (merged.Count > 0 && w.StartMs <= merged[^1].EndMs)
            {
                merged[^1] = merged[^1] with { EndMs = Math.Max(merged[^1].EndMs, w.EndMs) };
            }
            else
            {
                merged.Add(w);
            }
        }

        return merged;
    }
}

public static class GapReasons
{
    /// <summary>Two consecutive samples are further apart than the maximum sample gap (app closed, sleep, hang).</summary>
    public const string SampleGap = "sample_gap";

    /// <summary>Samples exist but the power value was missing or invalid (sensor failure, GPU lost).</summary>
    public const string SensorMissing = "sensor_missing";

    /// <summary>No sample before the start of the window within the maximum gap.</summary>
    public const string NoSampleBefore = "no_sample_before";

    /// <summary>No sample after the end of the window within the maximum gap.</summary>
    public const string NoSampleAfter = "no_sample_after";

    /// <summary>No samples at all for this device in the window.</summary>
    public const string NoSamples = "no_samples";

    public static string ToKorean(string reason) => reason switch
    {
        SampleGap => "샘플 간격 초과 (앱 종료·절전·중단 등)",
        SensorMissing => "전력 값 없음 (센서 오류·GPU 연결 끊김)",
        NoSampleBefore => "시작 직후 샘플 없음",
        NoSampleAfter => "종료 직전 샘플 없음",
        NoSamples => "이 구간에 샘플 없음",
        _ => reason,
    };
}

/// <summary>A time range where power was not observed. No energy is interpolated for it.</summary>
public sealed record EnergyGap(long FromMs, long ToMs, string Reason)
{
    public double Seconds => (ToMs - FromMs) / 1000.0;
}

/// <summary>Energy of one GPU over a set of windows.</summary>
public sealed record DeviceEnergy
{
    public required int DeviceIndex { get; init; }

    /// <summary>Energy integrated from observed power samples (trapezoid between samples no further apart than the max gap).</summary>
    public double MeasuredWh { get; init; }

    public double WindowSeconds { get; init; }
    public double ObservedSeconds { get; init; }
    public double UnobservedSeconds => Math.Max(0, WindowSeconds - ObservedSeconds);
    public double? Coverage => WindowSeconds > 0 ? ObservedSeconds / WindowSeconds : null;

    /// <summary>Samples inside the windows, and how many of them had a valid power value.</summary>
    public int SampleCount { get; init; }
    public int ValidPowerSamples { get; init; }

    public double? MinPowerW { get; init; }
    public double? MaxPowerW { get; init; }

    /// <summary>Time-weighted average power over observed time.</summary>
    public double? AveragePowerW => ObservedSeconds > 0 ? MeasuredWh * 3600.0 / ObservedSeconds : null;

    public IReadOnlyList<EnergyGap> Gaps { get; init; } = [];
    public double? LongestGapSeconds => Gaps.Count == 0 ? null : Gaps.Max(g => g.Seconds);

    /// <summary>
    /// Energy from NVML's cumulative energy counter over the same observed intervals (null when the GPU does not report
    /// it). Used to cross-check the integration, never mixed into it.
    /// </summary>
    public double? CounterWh { get; init; }
    public double CounterObservedSeconds { get; init; }
    public int CounterResets { get; init; }

    /// <summary>Observed time with utilisation at or above the activity threshold, and observed time with any utilisation value.</summary>
    public double GpuActiveSeconds { get; init; }
    public double UtilizationObservedSeconds { get; init; }

    public static DeviceEnergy Empty(int device, double windowSeconds, IReadOnlyList<EnergyGap> gaps) =>
        new() { DeviceIndex = device, WindowSeconds = windowSeconds, Gaps = gaps };
}

/// <summary>
/// Integrates GPU power samples into energy using their real timestamps.
/// <list type="bullet">
/// <item>Between two consecutive valid samples at most <c>maxGapSeconds</c> apart, power is taken as linear (trapezoid rule).</item>
/// <item>Intervals longer than the max gap, or touching a sample without a valid power value, are unobserved: no energy
/// is counted and the range is reported as a gap. Nothing is interpolated across them.</item>
/// <item>Intervals are clipped to the windows (session segments); power at a window edge is interpolated inside the
/// interval only, so samples just outside a window are used to cover its edges.</item>
/// </list>
/// </summary>
public static class EnergyIntegrator
{
    /// <summary>Power values outside this range are treated as invalid readings (no GPU board draws this).</summary>
    public const double MaxPlausiblePowerW = 5000;

    public static DeviceEnergy Integrate(int deviceIndex, IEnumerable<PowerSample> samples, IEnumerable<TimeWindow> windows,
        double maxGapSeconds, double activeUtilizationPercent = 10)
    {
        if (!(maxGapSeconds > 0) || !double.IsFinite(maxGapSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(maxGapSeconds), "최대 샘플 간격은 0보다 커야 합니다.");
        }

        var ws = TimeWindow.Normalize(windows);
        var windowSeconds = ws.Sum(w => w.Seconds);
        // Order by time; for equal timestamps keep the first reading (duplicates add no time).
        var list = samples.Where(s => s.DeviceIndex == deviceIndex)
            .Select((s, i) => (s, i)).OrderBy(x => x.s.TimeMs).ThenBy(x => x.i).Select(x => x.s)
            .ToList();
        var points = new List<PowerSample>(list.Count);
        foreach (var s in list)
        {
            if (points.Count == 0 || points[^1].TimeMs != s.TimeMs)
            {
                points.Add(s);
            }
        }

        if (ws.Count == 0)
        {
            return DeviceEnergy.Empty(deviceIndex, 0, []);
        }

        if (points.Count == 0)
        {
            return DeviceEnergy.Empty(deviceIndex, windowSeconds, ws.Select(w => new EnergyGap(w.StartMs, w.EndMs, GapReasons.NoSamples)).ToList());
        }

        var maxGapMs = maxGapSeconds * 1000.0;
        var times = points.Select(p => p.TimeMs).ToArray();
        double wh = 0, observed = 0, counterWh = 0, counterObserved = 0, active = 0, utilObserved = 0;
        int resets = 0, sampleCount = 0, valid = 0;
        double? min = null, max = null;
        var gaps = new List<EnergyGap>();

        void AddGap(long from, long to, string reason)
        {
            if (to <= from)
            {
                return;
            }

            if (gaps.Count > 0 && gaps[^1].Reason == reason && gaps[^1].ToMs == from)
            {
                gaps[^1] = gaps[^1] with { ToMs = to };
            }
            else
            {
                gaps.Add(new EnergyGap(from, to, reason));
            }
        }

        foreach (var w in ws)
        {
            var startIndex = LowerBound(times, w.StartMs);
            for (var k = startIndex; k < points.Count && points[k].TimeMs < w.EndMs; k++)
            {
                var p = points[k];
                {
                    sampleCount++;
                    if (IsValid(p.PowerW))
                    {
                        valid++;
                        min = Math.Min(min ?? double.MaxValue, p.PowerW!.Value);
                        max = Math.Max(max ?? double.MinValue, p.PowerW!.Value);
                    }
                }
            }

            // Leading edge: before the first sample (or when the first sample is after the window).
            var first = points[0].TimeMs;
            if (first > w.StartMs)
            {
                AddGap(w.StartMs, Math.Min(first, w.EndMs), first >= w.EndMs ? GapReasons.NoSamples : GapReasons.NoSampleBefore);
            }

            for (var i = Math.Max(0, startIndex - 1); i + 1 < points.Count; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                var x1 = Math.Max(a.TimeMs, w.StartMs);
                var x2 = Math.Min(b.TimeMs, w.EndMs);
                if (x2 <= x1)
                {
                    if (a.TimeMs >= w.EndMs)
                    {
                        break;
                    }

                    continue;
                }

                var span = (double)(b.TimeMs - a.TimeMs);
                if (span > maxGapMs)
                {
                    AddGap(x1, x2, GapReasons.SampleGap);
                    continue;
                }

                if (!IsValid(a.PowerW) || !IsValid(b.PowerW))
                {
                    AddGap(x1, x2, GapReasons.SensorMissing);
                    continue;
                }

                var pa = a.PowerW!.Value;
                var pb = b.PowerW!.Value;
                var p1 = pa + (pb - pa) * ((x1 - a.TimeMs) / span);
                var p2 = pa + (pb - pa) * ((x2 - a.TimeMs) / span);
                var seconds = (x2 - x1) / 1000.0;
                wh += (p1 + p2) / 2.0 * seconds / 3600.0;
                observed += seconds;

                if (a.EnergyMilliJoules is long ea && b.EnergyMilliJoules is long eb)
                {
                    if (eb >= ea)
                    {
                        counterWh += (eb - ea) / 3_600_000.0 * ((x2 - x1) / span);
                        counterObserved += seconds;
                    }
                    else
                    {
                        resets++; // counter restarted (driver reload); this interval has no counter energy
                    }
                }

                if (a.UtilizationPercent is double ua && b.UtilizationPercent is double ub && double.IsFinite(ua) && double.IsFinite(ub))
                {
                    utilObserved += seconds;
                    if ((ua + ub) / 2.0 >= activeUtilizationPercent)
                    {
                        active += seconds;
                    }
                }
            }

            var last = points[^1].TimeMs;
            if (last < w.EndMs)
            {
                AddGap(Math.Max(last, w.StartMs), w.EndMs, last < w.StartMs ? GapReasons.NoSamples : GapReasons.NoSampleAfter);
            }
        }

        return new DeviceEnergy
        {
            DeviceIndex = deviceIndex,
            MeasuredWh = wh,
            WindowSeconds = windowSeconds,
            ObservedSeconds = Math.Min(observed, windowSeconds),
            SampleCount = sampleCount,
            ValidPowerSamples = valid,
            MinPowerW = min,
            MaxPowerW = max,
            Gaps = gaps,
            CounterWh = counterObserved > 0 ? counterWh : null,
            CounterObservedSeconds = counterObserved,
            CounterResets = resets,
            GpuActiveSeconds = active,
            UtilizationObservedSeconds = utilObserved,
        };
    }

    /// <summary>Index of the first time that is not less than <paramref name="value"/>.</summary>
    private static int LowerBound(long[] times, long value)
    {
        int lo = 0, hi = times.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (times[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    public static bool IsValid(double? power) => power is double p && double.IsFinite(p) && p >= 0 && p <= MaxPlausiblePowerW;
}
