using System.Collections;
using System.Windows;
using System.Windows.Media;
using PcControlCenter.Presentation;

namespace PcControlCenter.App.Controls;

/// <summary>
/// A thin horizontal bar made of segments (fractions of the full width) over a dim track, with fully rounded ends and a
/// 2 px gap in the surface colour between parts (no outlines). Estimated segments are drawn with diagonal hatching so they
/// never look like measurements. Drawn directly (no visual tree per segment).
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable), typeof(SegmentBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x2c, 0x2c, 0x2a)), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The surface the bar sits on: the gaps between parts are drawn in it.</summary>
    public static readonly DependencyProperty GapBrushProperty = DependencyProperty.Register(
        nameof(GapBrush), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x19)), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Dictionary<string, Brush> Brushes = [];
    private static readonly Dictionary<string, Brush> Hatches = [];

    public SegmentBar()
    {
        SnapsToDevicePixels = true;
        MinHeight = 4;
    }

    public IEnumerable? Segments
    {
        get => (IEnumerable?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush GapBrush
    {
        get => (Brush)GetValue(GapBrushProperty);
        set => SetValue(GapBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 8 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = height / 2;
        var track = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        dc.DrawGeometry(TrackBrush, null, track);
        if (Segments is null)
        {
            return;
        }

        dc.PushClip(track);
        var x = 0.0;
        foreach (var item in Segments)
        {
            if (item is not BarSegment segment || segment.Fraction <= 0)
            {
                continue;
            }

            var w = Math.Min(width - x, segment.Fraction * width);
            if (w <= 0)
            {
                break;
            }

            var rect = new Rect(x, 0, Math.Max(1, w), height);
            dc.DrawRectangle(BrushOf(segment.ColorHex), null, rect);
            if (segment.IsEstimate)
            {
                dc.DrawRectangle(HatchOf(segment.ColorHex), null, rect);
            }

            x += w;
            if (x < width - 1)
            {
                // A 2 px gap in the surface colour separates a part from the next one (or from the free track).
                dc.DrawRectangle(GapBrush, null, new Rect(x - 1, 0, 2, height));
            }
        }

        dc.Pop();
    }

    internal static Brush BrushOf(string hex)
    {
        if (!Brushes.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Brushes[hex] = brush;
        }

        return brush;
    }

    private static Brush HatchOf(string hex)
    {
        if (!Hatches.TryGetValue(hex, out var hatch))
        {
            var dark = new SolidColorBrush(Color.FromArgb(0x70, 0x12, 0x12, 0x11));
            dark.Freeze();
            var pen = new Pen(dark, 2);
            pen.Freeze();
            var drawing = new GeometryDrawing(null, pen, new LineGeometry(new Point(0, 6), new Point(6, 0)));
            drawing.Freeze();
            var brush = new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 6, 6),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 6, 6),
                ViewboxUnits = BrushMappingMode.Absolute,
            };
            brush.Freeze();
            Hatches[hex] = hatch = brush;
        }

        return hatch;
    }
}

/// <summary>A small filled square for legends and table cells.</summary>
public sealed class Swatch : FrameworkElement
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(Swatch), new FrameworkPropertyMetadata("#898781", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsEstimateProperty = DependencyProperty.Register(
        nameof(IsEstimate), typeof(bool), typeof(Swatch), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public Swatch()
    {
        Width = 9;
        Height = 9;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public bool IsEstimate
    {
        get => (bool)GetValue(IsEstimateProperty);
        set => SetValue(IsEstimateProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(SegmentBar.BrushOf(Color), null, rect, 2, 2);
        if (IsEstimate)
        {
            var pen = new Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x90, 0x12, 0x12, 0x11)), 1.5);
            dc.PushClip(new RectangleGeometry(rect, 2, 2));
            for (var x = -ActualHeight; x < ActualWidth; x += 4)
            {
                dc.DrawLine(pen, new Point(x, ActualHeight), new Point(x + ActualHeight, 0));
            }

            dc.Pop();
        }
    }
}
