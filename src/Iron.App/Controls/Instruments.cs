using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Iron.App.Controls;

/// <summary>Pixel-snapping helpers: edges land on device pixels at any DPI (175 % included), so 1 px lines stay sharp.</summary>
internal static class Snap
{
    public static double Scale(Visual v) => VisualTreeHelper.GetDpi(v).PixelsPerDip;

    public static double At(double value, double scale) => Math.Round(value * scale) / scale;

    public static Rect Rect(double x, double y, double w, double h, double scale)
    {
        var left = At(x, scale);
        var top = At(y, scale);
        return new Rect(left, top, Math.Max(0, At(x + w, scale) - left), Math.Max(0, At(y + h, scale) - top));
    }

    public static FormattedText Text(Visual v, string text, double size, Brush brush, FrameworkElement source, FontWeight? weight = null)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(source), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, Scale(v));
}

/// <summary>Segmented LED gauge (16 cells): filled cells = round(ratio × cells). Null ratio = all cells off.</summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty RatioProperty = Register(nameof(Ratio), typeof(double?), null);
    public static readonly DependencyProperty SegmentsProperty = Register(nameof(Segments), typeof(int), 16);
    public static readonly DependencyProperty GapProperty = Register(nameof(Gap), typeof(double), 4.0);
    public static readonly DependencyProperty OnBrushProperty = Register(nameof(OnBrush), typeof(Brush), Brushes.OrangeRed);
    public static readonly DependencyProperty OffBrushProperty = Register(nameof(OffBrush), typeof(Brush), Brushes.DimGray);

    public double? Ratio { get => (double?)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }
    public int Segments { get => (int)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public Brush OnBrush { get => (Brush)GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }
    public Brush OffBrush { get => (Brush)GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }

    /// <summary>How many cells are lit for a ratio (0..1, clamped; null = none).</summary>
    public static int Lit(double? ratio, int segments) => ratio is double r && double.IsFinite(r) ? (int)Math.Round(Math.Clamp(r, 0, 1) * segments) : 0;

    protected override void OnRender(DrawingContext dc)
    {
        var n = Math.Max(1, Segments);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var scale = Snap.Scale(this);
        var cell = (w - Gap * (n - 1)) / n;
        var lit = Lit(Ratio, n);
        for (var i = 0; i < n; i++)
        {
            dc.DrawRectangle(i < lit ? OnBrush : OffBrush, null, Snap.Rect(i * (cell + Gap), 0, cell, h, scale));
        }
    }

    internal static DependencyProperty Register(string name, Type type, object? defaultValue)
        => DependencyProperty.Register(name, type, typeof(SegmentBar), new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}

/// <summary>Continuous mini meter of the stone modules.</summary>
public sealed class RatioBar : FrameworkElement
{
    public static readonly DependencyProperty RatioProperty = Reg(nameof(Ratio), typeof(double?), null);
    public static readonly DependencyProperty FillProperty = Reg(nameof(Fill), typeof(Brush), Brushes.OliveDrab);
    public static readonly DependencyProperty TrackProperty = Reg(nameof(Track), typeof(Brush), Brushes.LightGray);

    public double? Ratio { get => (double?)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var scale = Snap.Scale(this);
        dc.DrawRectangle(Track, null, Snap.Rect(0, 0, ActualWidth, ActualHeight, scale));
        if (Ratio is double r && double.IsFinite(r) && r > 0)
        {
            dc.DrawRectangle(Fill, null, Snap.Rect(0, 0, ActualWidth * Math.Clamp(r, 0, 1), ActualHeight, scale));
        }
    }

    private static DependencyProperty Reg(string name, Type type, object? d)
        => DependencyProperty.Register(name, type, typeof(RatioBar), new FrameworkPropertyMetadata(d, FrameworkPropertyMetadataOptions.AffectsRender));
}

/// <summary>Vertical meter of one 12 V pin: track, fill from the bottom (current / maximum) and the UI threshold tick.</summary>
public sealed class PinMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = Reg(nameof(Value), typeof(double?), null);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), typeof(double), 9.5);
    public static readonly DependencyProperty ThresholdProperty = Reg(nameof(Threshold), typeof(double?), null);
    public static readonly DependencyProperty FillProperty = Reg(nameof(Fill), typeof(Brush), Brushes.OrangeRed);
    public static readonly DependencyProperty TrackProperty = Reg(nameof(Track), typeof(Brush), Brushes.DimGray);
    public static readonly DependencyProperty ThresholdBrushProperty = Reg(nameof(ThresholdBrush), typeof(Brush), Brushes.Goldenrod);

    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double? Threshold { get => (double?)GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush ThresholdBrush { get => (Brush)GetValue(ThresholdBrushProperty); set => SetValue(ThresholdBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var scale = Snap.Scale(this);
        dc.DrawRectangle(Track, null, Snap.Rect(0, 0, w, h, scale));
        if (Value is double v && double.IsFinite(v) && v > 0 && Maximum > 0)
        {
            var fill = h * Math.Clamp(v / Maximum, 0, 1);
            dc.DrawRectangle(Fill, null, Snap.Rect(0, h - fill, w, fill, scale));
        }

        if (Threshold is double t && Maximum > 0 && t > 0 && t < Maximum)
        {
            var y = h - h * t / Maximum;
            var tick = Snap.Rect(-3, y, w + 6, 1.0 / scale * Math.Max(1, Math.Round(scale)), scale);
            dc.DrawRectangle(ThresholdBrush, null, tick);
        }
    }

    private static DependencyProperty Reg(string name, Type type, object? d)
        => DependencyProperty.Register(name, type, typeof(PinMeter), new FrameworkPropertyMetadata(d, FrameworkPropertyMetadataOptions.AffectsRender));
}

/// <summary>
/// Two series over the last 60 readings (RTX 5080 NVML board power, solid; WireView 12V-2x6, dashed). Gaps (null) break
/// the line. With no value at all it draws no axis — only a note (no empty "-10..10" chart).
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty Series1Property = Reg(nameof(Series1), typeof(double?[]), null);
    public static readonly DependencyProperty Series2Property = Reg(nameof(Series2), typeof(double?[]), null);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), typeof(double), 360.0);
    public static readonly DependencyProperty Brush1Property = Reg(nameof(Brush1), typeof(Brush), Brushes.LightSteelBlue);
    public static readonly DependencyProperty Brush2Property = Reg(nameof(Brush2), typeof(Brush), Brushes.OrangeRed);
    public static readonly DependencyProperty GridBrushProperty = Reg(nameof(GridBrush), typeof(Brush), Brushes.DimGray);
    public static readonly DependencyProperty LabelBrushProperty = Reg(nameof(LabelBrush), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty LabelSizeProperty = Reg(nameof(LabelSize), typeof(double), 12.0);
    public static readonly DependencyProperty EmptyTextProperty = Reg(nameof(EmptyText), typeof(string), "측정값 없음");
    public static readonly DependencyProperty UnitProperty = Reg(nameof(Unit), typeof(string), "W");
    public static readonly DependencyProperty CapacityProperty = Reg(nameof(Capacity), typeof(int), 60);

    public double?[]? Series1 { get => (double?[]?)GetValue(Series1Property); set => SetValue(Series1Property, value); }
    public double?[]? Series2 { get => (double?[]?)GetValue(Series2Property); set => SetValue(Series2Property, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Brush1 { get => (Brush)GetValue(Brush1Property); set => SetValue(Brush1Property, value); }
    public Brush Brush2 { get => (Brush)GetValue(Brush2Property); set => SetValue(Brush2Property, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public double LabelSize { get => (double)GetValue(LabelSizeProperty); set => SetValue(LabelSizeProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 40 || h <= 30)
        {
            return;
        }

        var scale = Snap.Scale(this);
        var any = (Series1?.Any(v => v is not null) ?? false) || (Series2?.Any(v => v is not null) ?? false);
        if (!any)
        {
            var empty = Snap.Text(this, EmptyText, LabelSize, LabelBrush, this);
            dc.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
            return;
        }

        var label = Snap.Text(this, Maximum.ToString("0", CultureInfo.InvariantCulture), LabelSize, LabelBrush, this);
        var padL = label.Width + 10;
        var padB = label.Height + 6;
        var padT = label.Height / 2 + 2;
        var cw = w - padL - 4;
        var ch = h - padT - padB;
        var pen = new Pen(GridBrush, 1 / scale) { DashStyle = new DashStyle([2, 3], 0) };
        pen.Freeze();
        for (var i = 0; i <= 4; i++)
        {
            var value = Maximum * i / 4;
            var y = Snap.At(padT + ch - ch * i / 4, scale) + 0.5 / scale;
            dc.DrawLine(pen, new Point(padL, y), new Point(w - 4, y));
            var t = Snap.Text(this, value.ToString("0", CultureInfo.InvariantCulture), LabelSize, LabelBrush, this);
            dc.DrawText(t, new Point(padL - 6 - t.Width, y - t.Height / 2));
        }

        var left = Snap.Text(this, $"-{Capacity}s", LabelSize, LabelBrush, this);
        dc.DrawText(left, new Point(padL, h - left.Height));
        var right = Snap.Text(this, $"지금 ({Unit})", LabelSize, LabelBrush, this);
        dc.DrawText(right, new Point(w - 4 - right.Width, h - right.Height));

        Draw(dc, Series1, Brush1, false, padL, padT, cw, ch, scale);
        Draw(dc, Series2, Brush2, true, padL, padT, cw, ch, scale);
    }

    private void Draw(DrawingContext dc, double?[]? series, Brush brush, bool dashed, double x0, double y0, double cw, double ch, double scale)
    {
        if (series is null || series.Length == 0)
        {
            return;
        }

        var pen = new Pen(brush, dashed ? 1.8 : 2.0) { LineJoin = PenLineJoin.Round };
        if (dashed)
        {
            pen.DashStyle = new DashStyle([3, 1.5], 0);
        }

        pen.Freeze();
        var slots = Math.Max(2, Capacity);
        var offset = slots - series.Length; // newest reading at the right edge
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var open = false;
            for (var i = 0; i < series.Length; i++)
            {
                if (series[i] is not double v || !double.IsFinite(v))
                {
                    open = false;
                    continue;
                }

                var x = x0 + cw * (offset + i) / (slots - 1);
                var y = y0 + ch - ch * Math.Clamp(v / Maximum, 0, 1);
                if (!open)
                {
                    ctx.BeginFigure(new Point(x, y), false, false);
                    open = true;
                }
                else
                {
                    ctx.LineTo(new Point(x, y), true, true);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static DependencyProperty Reg(string name, Type type, object? d)
        => DependencyProperty.Register(name, type, typeof(TrendChart), new FrameworkPropertyMetadata(d, FrameworkPropertyMetadataOptions.AffectsRender));
}

/// <summary>GPU utilization of one process over the last 60 s (0..100 %).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = Reg(nameof(Values), typeof(double?[]), null);
    public static readonly DependencyProperty StrokeProperty = Reg(nameof(Stroke), typeof(Brush), Brushes.OrangeRed);
    public static readonly DependencyProperty BaselineProperty = Reg(nameof(Baseline), typeof(Brush), Brushes.DimGray);

    public double?[]? Values { get => (double?[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Baseline { get => (Brush)GetValue(BaselineProperty); set => SetValue(BaselineProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 4 || h <= 4)
        {
            return;
        }

        var scale = Snap.Scale(this);
        dc.DrawRectangle(Baseline, null, Snap.Rect(0, h - 1 / scale, w, 1 / scale, scale));
        dc.DrawRectangle(Baseline, null, Snap.Rect(0, 0, w, 1 / scale, scale));
        if (Values is not { Length: > 0 } values)
        {
            return;
        }

        var pen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var open = false;
            const int slots = 60;
            var offset = slots - values.Length;
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] is not double v)
                {
                    open = false;
                    continue;
                }

                var p = new Point(w * (offset + i) / (slots - 1), h - 2 - (h - 4) * Math.Clamp(v / 100, 0, 1));
                if (!open)
                {
                    ctx.BeginFigure(p, false, false);
                    open = true;
                }
                else
                {
                    ctx.LineTo(p, true, true);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static DependencyProperty Reg(string name, Type type, object? d)
        => DependencyProperty.Register(name, type, typeof(Sparkline), new FrameworkPropertyMetadata(d, FrameworkPropertyMetadataOptions.AffectsRender));
}

/// <summary>A word drawn with letter spacing (WPF text has none): only for the IRON wordmark.</summary>
public sealed class LetterSpacedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = Reg(nameof(Text), typeof(string), "");
    public static readonly DependencyProperty SpacingEmProperty = Reg(nameof(SpacingEm), typeof(double), 0.34);
    public static readonly DependencyProperty FontSizeProperty = Reg(nameof(FontSize), typeof(double), 22.0);
    public static readonly DependencyProperty ForegroundProperty = Reg(nameof(Foreground), typeof(Brush), Brushes.White);

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double SpacingEm { get => (double)GetValue(SpacingEmProperty); set => SetValue(SpacingEmProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0.0;
        var height = 0.0;
        foreach (var t in Glyphs())
        {
            width += t.WidthIncludingTrailingWhitespace + FontSize * SpacingEm;
            height = Math.Max(height, t.Height);
        }

        return new Size(Math.Max(0, width - FontSize * SpacingEm), height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var x = 0.0;
        foreach (var t in Glyphs())
        {
            dc.DrawText(t, new Point(Snap.At(x, Snap.Scale(this)), 0));
            x += t.WidthIncludingTrailingWhitespace + FontSize * SpacingEm;
        }
    }

    private IEnumerable<FormattedText> Glyphs() => (Text ?? "").Select(c => Snap.Text(this, c.ToString(), FontSize, Foreground, this, FontWeights.Bold));

    private static DependencyProperty Reg(string name, Type type, object? d)
        => DependencyProperty.Register(name, type, typeof(LetterSpacedText),
            new FrameworkPropertyMetadata(d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
}
