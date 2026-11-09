using LocalAiPerf.Presentation.Charts;
using WinDiag.Core.Events;
using WinDiag.Storage;

namespace WinDiag.Presentation;

/// <summary>A vertical marker on a time chart (an event at that moment).</summary>
public sealed record ChartMarker(double X, string ColorHex, string Label);

/// <summary>Project A's chart model plus event markers drawn by this app.</summary>
public sealed record ChartData(ChartModel Model, IReadOnlyList<ChartMarker> Markers)
{
    public static ChartData Empty(string title, string unit, string emptyText)
        => new(new ChartModel { Title = title, Unit = unit, EmptyText = emptyText }, []);
}

/// <summary>Builds charts for sensors and event counts. X is the OLE date of the displayed time, as in project A.</summary>
public static class DiagCharts
{
    public static double X(DateTimeOffset time) => Fmt.Display.Convert(time).DateTime.ToOADate();

    public static double XMs(long ms) => X(DateTimeOffset.FromUnixTimeMilliseconds(ms));

    public static IReadOnlyList<ChartMarker> Markers(IEnumerable<EventPoint> events, int max = 200)
        => events.Where(e => e.Severity >= DiagSeverity.Warning && EventCategories.Get(e.Category).Kind is TimelineKind.Error or TimelineKind.ProgramCrash or TimelineKind.SystemCrash)
            .OrderByDescending(e => e.Severity).Take(max)
            .Select(e => new ChartMarker(XMs(e.TimeMs), e.Severity >= DiagSeverity.Error ? ChartPalette.Critical : ChartPalette.Warning, EventCategories.Name(e.Category)))
            .ToList();

    public static ChartData Gpu(string title, string unit, SensorSeries series, Func<GpuSensorPoint, double?> value, IReadOnlyList<ChartMarker> markers,
        double gapSeconds, double? yMin = null, double? yMax = null, string format = "0.0")
    {
        var points = series.Gpu.Where(g => g.GpuIndex == 0).Select(g => (g.TimeMs, Value: value(g))).Where(p => p.Value is double v && double.IsFinite(v)).ToList();
        return Build(title, unit, points.Select(p => (p.TimeMs, p.Value!.Value)).ToList(), markers, gapSeconds, yMin, yMax, format, series.IsDownsampled, ChartPalette.Slot(0));
    }

    public static ChartData System(string title, string unit, SensorSeries series, Func<SensorPoint, double?> value, IReadOnlyList<ChartMarker> markers,
        double gapSeconds, double? yMin = null, double? yMax = null, string format = "0.0")
    {
        var points = series.System.Select(s => (s.TimeMs, Value: value(s))).Where(p => p.Value is double v && double.IsFinite(v)).ToList();
        return Build(title, unit, points.Select(p => (p.TimeMs, p.Value!.Value)).ToList(), markers, gapSeconds, yMin, yMax, format, series.IsDownsampled, ChartPalette.Slot(1));
    }

    private static ChartData Build(string title, string unit, List<(long TimeMs, double Value)> points, IReadOnlyList<ChartMarker> markers, double gapSeconds,
        double? yMin, double? yMax, string format, bool downsampled, string color)
    {
        var model = new ChartModel
        {
            Title = downsampled ? title + " (구간 평균)" : title,
            Unit = unit,
            Series = points.Count == 0 ? [] : [new ChartSeries(title, points.Select(p => XMs(p.TimeMs)).ToArray(), points.Select(p => p.Value).ToArray(), color)],
            GapThreshold = gapSeconds / 86400.0,
            YMin = yMin,
            YMax = yMax,
            ValueFormat = format,
            EmptyText = "이 구간에는 센서 기록이 없습니다 (앱 미실행 또는 센서 없음, N/A)",
        };
        return new ChartData(model, points.Count == 0 ? [] : markers);
    }

    /// <summary>Daily counts of problem events by severity (critical, error, warning) over a range.</summary>
    public static ChartData DailyCounts(IReadOnlyList<EventPoint> points, DateTimeOffset from, DateTimeOffset to)
    {
        var days = new List<DateTime>();
        for (var d = Fmt.Display.Convert(from).Date; d <= Fmt.Display.Convert(to).Date; d = d.AddDays(1))
        {
            days.Add(d);
        }

        ChartSeries Series(DiagSeverity severity, string label, string color)
        {
            var byDay = points.Where(p => p.Severity == severity && EventCategories.Get(p.Category).Kind is TimelineKind.Error or TimelineKind.ProgramCrash or TimelineKind.SystemCrash)
                .GroupBy(p => Fmt.Display.Convert(p.Time).Date).ToDictionary(g => g.Key, g => (double)g.Count());
            return new ChartSeries(label, days.Select(d => d.ToOADate()).ToArray(), days.Select(d => byDay.GetValueOrDefault(d)).ToArray(), color);
        }

        var model = new ChartModel
        {
            Title = "일별 문제 이벤트 수",
            Unit = "건",
            Series =
            [
                Series(DiagSeverity.Critical, "심각", ChartPalette.Critical),
                Series(DiagSeverity.Error, "오류", ChartPalette.Serious),
                Series(DiagSeverity.Warning, "경고", ChartPalette.Warning),
            ],
            YMin = 0,
            ValueFormat = "0",
            EmptyText = "표시할 이벤트가 없습니다",
        };
        return new ChartData(model, []);
    }
}
