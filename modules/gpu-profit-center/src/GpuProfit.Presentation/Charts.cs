using GpuProfit.Accounting.Analysis;
using GpuProfit.Accounting.Scenarios;
using GpuProfit.Storage;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Presentation.Charts;

namespace GpuProfit.Presentation;

/// <summary>Builds project A's <see cref="ChartModel"/> for this app's screens (one unit per chart, gaps stay gaps).</summary>
public static class Charts
{
    public static double X(DateTimeOffset t) => Fmt.Display(t).DateTime.ToOADate();

    public static double Days(double seconds) => seconds / 86400.0;

    /// <summary>
    /// Gives a chart without fixed limits a minimum vertical span, so that a sensor that moves in whole units (a GPU
    /// temperature flipping between 30 and 31 °C) is not stretched into full-height steps.
    /// </summary>
    public static ChartModel WithMinimumSpan(ChartModel model, double span)
    {
        if (model.YMin is not null || model.YMax is not null || model.IsEmpty || !(span > 0))
        {
            return model;
        }

        var values = model.Series.SelectMany(s => s.Y).Where(double.IsFinite).ToList();
        if (values.Count == 0)
        {
            return model;
        }

        var min = values.Min();
        var max = values.Max();
        if (max - min >= span)
        {
            return model;
        }

        var bottom = (min + max) / 2 - span / 2;
        if (min >= 0 && bottom < 0)
        {
            bottom = 0;
        }

        return model with { YMin = Math.Floor(bottom), YMax = Math.Floor(bottom) + span };
    }

    public static ChartModel Live(string title, string unit, IReadOnlyList<HardwareSnapshot> snapshots, Func<GpuSample, double?> value, double gapSeconds,
        string format = "0.0", double? yMin = null, double? yMax = null)
    {
        var indices = snapshots.SelectMany(s => s.Gpus.Select(g => g.Index)).Distinct().Order().ToList();
        var series = indices.Select(i =>
        {
            var points = snapshots.Select(s => (s.Timestamp, v: s.Gpu(i) is { } g ? value(g) : null)).Where(p => p.v is double d && double.IsFinite(d)).ToList();
            return new ChartSeries(indices.Count > 1 ? $"GPU {i}" : "GPU", points.Select(p => X(p.Timestamp)).ToArray(), points.Select(p => p.v!.Value).ToArray(), ChartPalette.Slot(i));
        }).ToList();
        return new ChartModel
        {
            Title = title, Unit = unit, Series = series, GapThreshold = Days(gapSeconds), ValueFormat = format, YMin = yMin, YMax = yMax,
            EmptyText = "GPU 값이 없습니다 (N/A: GPU 미지원·센서 꺼짐·측정 대기)",
        };
    }

    public static ChartModel LiveSystem(string title, string unit, IReadOnlyList<HardwareSnapshot> snapshots, Func<SystemSample, double?> value, double gapSeconds,
        double? yMax = null)
    {
        var points = snapshots.Select(s => (s.Timestamp, v: value(s.System))).Where(p => p.v is double d && double.IsFinite(d)).ToList();
        return new ChartModel
        {
            Title = title, Unit = unit, GapThreshold = Days(gapSeconds), YMin = 0, YMax = yMax,
            Series = [new ChartSeries(title, points.Select(p => X(p.Timestamp)).ToArray(), points.Select(p => p.v!.Value).ToArray(), ChartPalette.Slot(0))],
            EmptyText = "값이 없습니다 (N/A)",
        };
    }

    public static ChartModel GpuHistory(string title, string unit, IReadOnlyList<GpuSeriesPoint> points, Func<GpuSeriesPoint, double?> value, TimeSpan bucket,
        string format = "0.0")
    {
        var indices = points.Select(p => p.GpuIndex).Distinct().Order().ToList();
        var series = indices.Select(i =>
        {
            var pts = points.Where(p => p.GpuIndex == i && value(p) is double).ToList();
            return new ChartSeries(indices.Count > 1 ? $"GPU {i}" : "GPU", pts.Select(p => X(p.Time)).ToArray(), pts.Select(p => value(p)!.Value).ToArray(), ChartPalette.Slot(i));
        }).ToList();
        return new ChartModel
        {
            Title = title, Unit = unit, Series = series, GapThreshold = Days(bucket.TotalSeconds * 2.5), ValueFormat = format,
            EmptyText = "이 기간에 기록된 GPU 값이 없습니다 (N/A)",
        };
    }

    public static ChartModel PowerSamples(string title, IReadOnlyList<GpuProfit.Accounting.Energy.PowerSample> samples, double gapSeconds, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var fromMs = from?.ToUnixTimeMilliseconds() ?? long.MinValue;
        var toMs = to?.ToUnixTimeMilliseconds() ?? long.MaxValue;
        var indices = samples.Select(s => s.DeviceIndex).Distinct().Order().ToList();
        var stride = Math.Max(1, samples.Count / 4000); // a readable line; energy is always computed from every sample
        var series = indices.Select(i =>
        {
            var pts = samples.Where(s => s.DeviceIndex == i && s.TimeMs >= fromMs && s.TimeMs <= toMs && s.PowerW is double).Where((_, k) => k % stride == 0).ToList();
            return new ChartSeries(indices.Count > 1 ? $"GPU {i}" : "GPU 보드 전력", pts.Select(p => X(DateTimeOffset.FromUnixTimeMilliseconds(p.TimeMs))).ToArray(),
                pts.Select(p => p.PowerW!.Value).ToArray(), ChartPalette.Slot(i));
        }).ToList();
        return new ChartModel
        {
            Title = title, Unit = "W", Series = series, GapThreshold = Days(gapSeconds * Math.Max(1, stride)), YMin = 0,
            EmptyText = "이 세션 구간에 GPU 전력 샘플이 없습니다 (N/A)",
        };
    }

    /// <summary>Net profit against one condition, with the zero line and the breakeven point.</summary>
    public static ChartModel Sensitivity(SensitivitySeries s)
    {
        var xs = s.Points.Select(p => p.X).ToArray();
        var ys = s.Points.Select(p => (double)p.NetProfit).ToArray();
        var spans = new List<ChartSpan>();
        if (s.BreakevenInRange && s.Breakeven.Value is double be && xs.Length > 1)
        {
            var width = (xs[^1] - xs[0]) / 200;
            spans.Add(new ChartSpan(be - width, be + width, ChartPalette.Warning, $"손익분기점 {be:0.##}"));
        }

        var baseWidth = xs.Length > 1 ? (xs[^1] - xs[0]) / 300 : 0;
        spans.Add(new ChartSpan(s.BaseValue - baseWidth, s.BaseValue + baseWidth, ChartPalette.MutedInk, "현재 조건"));
        return new ChartModel
        {
            Title = $"순수익 — {SensitivityVariableText.ToKorean(s.Variable)}",
            Unit = "원",
            XIsTime = false,
            XLabel = SensitivityVariableText.ToKorean(s.Variable),
            Series = [new ChartSeries("순수익", xs, ys, ChartPalette.Slot(0))],
            Spans = spans,
            ReferenceLines = [new ChartReferenceLine(0, "손익 0", ChartPalette.Critical)],
            ValueFormat = "N0",
        };
    }

    public static ChartModel PeriodLines(string title, string unit, IReadOnlyList<PeriodBucket> buckets, params (string Label, Func<PeriodBucket, double> Value)[] lines)
    {
        var x = buckets.Select(b => X(b.Start)).ToArray();
        return new ChartModel
        {
            Title = title,
            Unit = unit,
            Series = lines.Select((l, i) => new ChartSeries(l.Label, x, buckets.Select(l.Value).ToArray(), ChartPalette.Slot(i))).ToList(),
            ReferenceLines = unit == "원" ? [new ChartReferenceLine(0, "0", ChartPalette.MutedInk)] : [],
            ValueFormat = unit == "원" ? "N0" : "0.###",
            EmptyText = "이 기간에 세션이 없습니다",
        };
    }

    public static ChartModel NetPerHourBySession(IReadOnlyList<CompareRow> rows)
    {
        var defined = rows.Where(r => r.NetPerHour.HasValue).OrderBy(r => r.Started).ToList();
        return new ChartModel
        {
            Title = "세션별 시간당 순수익",
            Unit = "원/시간",
            Series = [new ChartSeries("시간당 순수익", defined.Select(r => X(r.Started)).ToArray(), defined.Select(r => (double)r.NetPerHour.Value!.Value).ToArray(), ChartPalette.Slot(0))],
            ReferenceLines = [new ChartReferenceLine(0, "손익 0", ChartPalette.Critical)],
            GapThreshold = double.PositiveInfinity,
            ValueFormat = "N0",
            EmptyText = "시간당 순수익이 정의된 세션이 없습니다",
        };
    }
}
