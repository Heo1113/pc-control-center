using ScottPlot;

namespace LocalAiPerf.Presentation.Charts;

/// <summary>
/// Draws a <see cref="ChartModel"/> onto a ScottPlot plot. Shared by the WPF control and by tests that render PNGs,
/// so the drawing code that runs in the app is the code that is tested.
/// Rules: one unit per chart, 2 px lines, gaps stay gaps, legend only for two or more series (or highlighted spans),
/// recessive grid and axes, status colors only for highlighted states.
/// </summary>
public static class ChartRenderer
{
    private static readonly string[] HangulFontCandidates = ["Malgun Gothic", "NanumGothic", "Noto Sans CJK KR", "Noto Sans KR"];

    /// <summary>
    /// The font name charts use for Korean text, chosen the way ScottPlot will actually draw it.
    /// ScottPlot only uses a font whose name appears in SKFontManager.Default.FontFamilies, and on Korean Windows that list
    /// holds the localized name ("맑은 고딕"), not "Malgun Gothic"; any other name silently falls back to a font without Hangul
    /// (labels drawn as boxes on the user's PC). So every candidate, including the localized name Skia reports for it, is
    /// checked through ScottPlot's own typeface lookup for a Hangul glyph.
    /// </summary>
    internal static readonly string? Font = ResolveHangulFont(
        HangulFontCandidates,
        name =>
        {
            using var typeface = SkiaSharp.SKFontManager.Default.MatchFamily(name);
            return typeface?.FamilyName;
        },
        SkiaSharp.SKFontManager.Default.FontFamilies,
        name => ScottPlot.Fonts.GetTypeface(name, bold: false, italic: false));

    /// <param name="preferred">Known Hangul fonts, in order of preference.</param>
    /// <param name="reportedName">The family name the font system reports for a name (may be localized), or null.</param>
    /// <param name="installed">All installed family names, tried last.</param>
    /// <param name="drawWith">The typeface the chart library will draw with for a name (not disposed: the library caches it).</param>
    internal static string? ResolveHangulFont(IEnumerable<string> preferred, Func<string, string?> reportedName,
        IEnumerable<string> installed, Func<string, SkiaSharp.SKTypeface?> drawWith)
    {
        var names = new List<string>();
        foreach (var name in preferred)
        {
            if (reportedName(name) is string reported)
            {
                names.Add(reported);
            }

            names.Add(name);
        }

        names.AddRange(installed);
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (drawWith(name) is { } typeface && typeface.ContainsGlyph('가') && typeface.ContainsGlyph('A'))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Legend rows for the host to draw: one per series with data (only when there are two or more) and one per span label.</summary>
    public static IReadOnlyList<LegendEntry> LegendEntries(ChartModel? model)
    {
        if (model is null || model.IsEmpty)
        {
            return [];
        }

        var series = model.Series.Where(x => x.X.Length > 0).ToList();
        if (series.Count < 2 && model.Spans.Count == 0)
        {
            return [];
        }

        var entries = series.Select(x => new LegendEntry(x.Label, x.ColorHex, false)).ToList();
        entries.AddRange(model.Spans.DistinctBy(x => x.Label).Select(x => new LegendEntry(x.Label, x.ColorHex, true)));
        return entries;
    }

    /// <summary>Returns false when there is nothing to draw (the caller shows <see cref="ChartModel.EmptyText"/>).</summary>
    public static bool Render(Plot plot, ChartModel? model)
    {
        plot.Clear();
        ApplyStyle(plot);
        if (model is null || model.IsEmpty)
        {
            foreach (var panel in plot.Axes.GetPanels().OfType<ScottPlot.Panels.LegendPanel>().ToList())
            {
                plot.Axes.Remove(panel);
            }

            plot.HideLegend();
            return false;
        }

        var showLegend = model.Series.Count(s => s.X.Length > 0) >= 2 || model.Spans.Count > 0;
        var dataMax = double.MinValue;
        foreach (var series in model.Series)
        {
            var color = Color.FromHex(series.ColorHex);
            var first = true;
            foreach (var (xs, ys) in Segments(series, model.GapThreshold))
            {
                var line = plot.Add.ScatterLine(xs, ys, color);
                line.LineWidth = 2;
                line.MarkerSize = xs.Length == 1 ? 5 : 0;
                if (first && showLegend)
                {
                    line.LegendText = series.Label;
                }

                first = false;
                dataMax = Math.Max(dataMax, ys.Max());
            }
        }

        var labelled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var span in model.Spans)
        {
            var band = plot.Add.HorizontalSpan(span.X1, span.X2, Color.FromHex(span.ColorHex).WithAlpha(0.22));
            band.LineWidth = 0;
            if (labelled.Add(span.Label))
            {
                band.LegendText = span.Label;
            }
        }

        var maxX = model.Series.Where(x => x.X.Length > 0).Select(x => x.X[^1]).DefaultIfEmpty(0).Max();
        foreach (var reference in model.ReferenceLines)
        {
            // A limit is a threshold, so it is the one dashed line on the chart. Its label sits inside the plot, at the right end.
            plot.Add.HorizontalLine(reference.Y, 1, Color.FromHex(reference.ColorHex), LinePattern.Dashed);
            var label = plot.Add.Text(reference.Label, maxX, reference.Y);
            label.LabelAlignment = Alignment.UpperRight; // below the line: never clipped at the top of the plot
            label.LabelFontColor = Color.FromHex(ChartPalette.SecondaryInk);
            label.LabelBackgroundColor = Color.FromHex(ChartPalette.Surface);
            label.LabelFontSize = 11;
            if (Font is not null)
            {
                label.LabelFontName = Font;
            }

            dataMax = Math.Max(dataMax, reference.Y);
        }

        if (model.XIsTime)
        {
            var axis = plot.Axes.DateTimeTicksBottom();
            if (axis.TickGenerator is ScottPlot.TickGenerators.DateTimeAutomatic ticks)
            {
                var minX = model.Series.Where(x => x.X.Length > 0).Select(x => x.X[0]).DefaultIfEmpty(maxX).Min();
                var spanDays = maxX - minX;
                var format = spanDays >= 1 ? "MM-dd HH:mm" : spanDays >= 2.0 / 24 ? "HH:mm" : "HH:mm:ss";
                ticks.LabelFormatter = time => time.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        else if (model.XLabel is not null)
        {
            plot.XLabel(model.XLabel, 12);
        }

        plot.Axes.AutoScale();
        if (model.YMin is not null || model.YMax is not null)
        {
            var limits = plot.Axes.GetLimits();
            var bottom = model.YMin ?? limits.Bottom;
            var top = model.YMax is double max ? Math.Max(max, dataMax) : Math.Max(limits.Top, dataMax * 1.08);
            if (top <= bottom)
            {
                top = bottom + 1;
            }

            plot.Axes.SetLimitsY(bottom, top);
        }

        // A legend panel above the plot never hides data. ShowLegend(Edge) adds a panel on every call, so old ones go first.
        foreach (var panel in plot.Axes.GetPanels().OfType<ScottPlot.Panels.LegendPanel>().ToList())
        {
            plot.Axes.Remove(panel);
        }

        // The legend is drawn by the host (WPF) above the chart, see LegendEntries: a ScottPlot legend panel did not grow
        // with the display scale and its text was clipped at 200 %.
        plot.HideLegend();

        ApplyStyle(plot); // axes created above (date axis) get the same ink
        if (Font is null)
        {
            plot.Font.Automatic(); // fallback: choose fonts from the text of each label
        }

        return true;
    }

    public static void ApplyStyle(Plot plot)
    {
        if (Font is not null)
        {
            plot.Font.Set(Font);
        }

        plot.FigureBackground.Color = Color.FromHex(ChartPalette.Surface);
        plot.DataBackground.Color = Color.FromHex(ChartPalette.Surface);
        plot.Axes.Color(Color.FromHex(ChartPalette.MutedInk));
        plot.Axes.FrameColor(Color.FromHex(ChartPalette.Axis));
        plot.Grid.MajorLineColor = Color.FromHex(ChartPalette.Grid);
        plot.Legend.BackgroundColor = Color.FromHex(ChartPalette.Surface);
        plot.Legend.FontColor = Color.FromHex(ChartPalette.SecondaryInk);
        plot.Legend.OutlineColor = Color.FromHex(ChartPalette.Surface);
    }

    /// <summary>Splits a series wherever consecutive points are further apart than <paramref name="gap"/>.</summary>
    public static IEnumerable<(double[] Xs, double[] Ys)> Segments(ChartSeries series, double gap)
    {
        var start = 0;
        for (var i = 1; i <= series.X.Length; i++)
        {
            if (i == series.X.Length || series.X[i] - series.X[i - 1] > gap)
            {
                if (i > start)
                {
                    yield return (series.X[start..i], series.Y[start..i]);
                }

                start = i;
            }
        }
    }
}

public sealed record LegendEntry(string Label, string ColorHex, bool IsBand);
