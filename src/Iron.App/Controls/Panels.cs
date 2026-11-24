using System.Windows;
using System.Windows.Controls;

namespace Iron.App.Controls;

/// <summary>
/// Equal-width cells in <see cref="Columns"/> columns with gaps; each row is as tall as its tallest cell, or, with
/// <see cref="StretchRows"/>, the rows share the available height. Used for the GPU cards (2 → 1), the system modules
/// (4 or an even 2×2, never 3 + 1), the pins and the settings sections.
/// </summary>
public sealed class GapGrid : Panel
{
    public static readonly DependencyProperty ColumnsProperty = Reg(nameof(Columns), typeof(int), 2);
    public static readonly DependencyProperty ColumnGapProperty = Reg(nameof(ColumnGap), typeof(double), 12.0);
    public static readonly DependencyProperty RowGapProperty = Reg(nameof(RowGap), typeof(double), 12.0);
    public static readonly DependencyProperty StretchRowsProperty = Reg(nameof(StretchRows), typeof(bool), false);

    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double ColumnGap { get => (double)GetValue(ColumnGapProperty); set => SetValue(ColumnGapProperty, value); }
    public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }
    public bool StretchRows { get => (bool)GetValue(StretchRowsProperty); set => SetValue(StretchRowsProperty, value); }

    private int Cols => Math.Max(1, Columns);

    protected override Size MeasureOverride(Size available)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var cols = Math.Min(Cols, Math.Max(1, visible.Count));
        var rows = (visible.Count + Cols - 1) / Cols;
        var cellW = double.IsInfinity(available.Width) ? double.PositiveInfinity : Math.Max(0, (available.Width - ColumnGap * (Cols - 1)) / Cols);
        var cellH = StretchRows && !double.IsInfinity(available.Height) && rows > 0
            ? Math.Max(0, (available.Height - RowGap * (rows - 1)) / rows)
            : double.PositiveInfinity;
        var total = 0.0;
        var maxW = 0.0;
        for (var r = 0; r < rows; r++)
        {
            var rowH = 0.0;
            for (var c = 0; c < Cols && r * Cols + c < visible.Count; c++)
            {
                var child = visible[r * Cols + c];
                child.Measure(new Size(cellW, cellH));
                rowH = Math.Max(rowH, child.DesiredSize.Height);
                maxW = Math.Max(maxW, child.DesiredSize.Width);
            }

            total += rowH;
        }

        total += RowGap * Math.Max(0, rows - 1);
        var width = double.IsInfinity(available.Width) ? maxW * cols + ColumnGap * (cols - 1) : available.Width;
        return new Size(width, StretchRows && !double.IsInfinity(available.Height) ? available.Height : total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var rows = (visible.Count + Cols - 1) / Cols;
        var cellW = Math.Max(0, (final.Width - ColumnGap * (Cols - 1)) / Cols);
        var stretchH = rows > 0 ? Math.Max(0, (final.Height - RowGap * (rows - 1)) / rows) : 0;
        var y = 0.0;
        for (var r = 0; r < rows; r++)
        {
            var rowH = 0.0;
            for (var c = 0; c < Cols && r * Cols + c < visible.Count; c++)
            {
                rowH = Math.Max(rowH, visible[r * Cols + c].DesiredSize.Height);
            }

            if (StretchRows)
            {
                rowH = stretchH;
            }

            for (var c = 0; c < Cols && r * Cols + c < visible.Count; c++)
            {
                visible[r * Cols + c].Arrange(new Rect(c * (cellW + ColumnGap), y, cellW, rowH));
            }

            y += rowH + RowGap;
        }

        return final;
    }

    private static DependencyProperty Reg(string name, Type type, object d)
        => DependencyProperty.Register(name, type, typeof(GapGrid), new FrameworkPropertyMetadata(d, FrameworkPropertyMetadataOptions.AffectsMeasure));
}

/// <summary>
/// Two children side by side (first gets <see cref="FirstShare"/> of the width) or stacked (first gets that share of the
/// height), with a gap. The overview's processes | WireView split, the processes tab's table | inspector.
/// </summary>
public sealed class RatioSplitPanel : Panel
{
    public static readonly DependencyProperty SideBySideProperty = Reg(nameof(SideBySide), typeof(bool), true);
    public static readonly DependencyProperty FirstShareProperty = Reg(nameof(FirstShare), typeof(double), 0.64);
    public static readonly DependencyProperty GapProperty = Reg(nameof(Gap), typeof(double), 12.0);

    /// <summary>Stacked mode: the second child keeps at least this height (the first may be shorter).</summary>
    public static readonly DependencyProperty SecondMinHeightProperty = Reg(nameof(SecondMinHeight), typeof(double), 0.0);

    public bool SideBySide { get => (bool)GetValue(SideBySideProperty); set => SetValue(SideBySideProperty, value); }
    public double FirstShare { get => (double)GetValue(FirstShareProperty); set => SetValue(FirstShareProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double SecondMinHeight { get => (double)GetValue(SecondMinHeightProperty); set => SetValue(SecondMinHeightProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        var (a, b) = Split(available);
        Child(0)?.Measure(a.Size);
        Child(1)?.Measure(b.Size);
        if (SideBySide)
        {
            var h = Math.Max(Child(0)?.DesiredSize.Height ?? 0, Child(1)?.DesiredSize.Height ?? 0);
            return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, double.IsInfinity(available.Height) ? h : available.Height);
        }

        var stacked = (Child(0)?.DesiredSize.Height ?? 0) + Gap + (Child(1)?.DesiredSize.Height ?? 0);
        return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, double.IsInfinity(available.Height) ? stacked : available.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (a, b) = Split(final);
        Child(0)?.Arrange(a);
        Child(1)?.Arrange(b);
        return final;
    }

    private (Rect First, Rect Second) Split(Size size)
    {
        var share = Math.Clamp(FirstShare, 0.1, 0.9);
        if (SideBySide)
        {
            var w = double.IsInfinity(size.Width) ? 0 : Math.Max(0, size.Width - Gap);
            var h = double.IsInfinity(size.Height) ? double.PositiveInfinity : size.Height;
            var first = Math.Round(w * share);
            return (new Rect(0, 0, first, h), new Rect(first + Gap, 0, Math.Max(0, w - first), h));
        }

        var width = double.IsInfinity(size.Width) ? double.PositiveInfinity : size.Width;
        if (double.IsInfinity(size.Height))
        {
            return (new Rect(0, 0, width, double.PositiveInfinity), new Rect(0, 0, width, double.PositiveInfinity));
        }

        var total = Math.Max(0, size.Height - Gap);
        var top = Math.Round(Math.Min(total * share, Math.Max(0, total - SecondMinHeight)));
        return (new Rect(0, 0, width, top), new Rect(0, top + Gap, width, Math.Max(0, total - top)));
    }

    private UIElement? Child(int i) => InternalChildren.Count > i ? InternalChildren[i] : null;

    private static DependencyProperty Reg(string name, Type type, object d)
        => DependencyProperty.Register(name, type, typeof(RatioSplitPanel), new FrameworkPropertyMetadata(d, FrameworkPropertyMetadataOptions.AffectsMeasure));
}
