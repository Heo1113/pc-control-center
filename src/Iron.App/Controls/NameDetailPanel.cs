using System.Windows;
using System.Windows.Controls;

namespace Iron.App.Controls;

/// <summary>
/// One line of a process-name cell. Children: [0] the identified name, [1] the muted executable name (detail), [2] an
/// optional small mark placed right after the name (the "~" of an inferred name). The name and its mark get the width
/// first (the name trims only when even it does not fit); the detail is shown only when it fits completely after them,
/// otherwise it is left out (arranged into an empty slot, so nothing of it is drawn). The cell never shows half a detail,
/// and the full text stays in the tooltip. Separate text elements (not runs of one text) keep each one's own font, so the
/// layout check measures them exactly.
/// </summary>
public sealed class NameDetailPanel : Panel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(NameDetailPanel), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MarkGapProperty = DependencyProperty.Register(
        nameof(MarkGap), typeof(double), typeof(NameDetailPanel), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private bool _detailFits;

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public double MarkGap
    {
        get => (double)GetValue(MarkGapProperty);
        set => SetValue(MarkGapProperty, value);
    }

    private UIElement? Child(int i) => InternalChildren.Count > i ? InternalChildren[i] : null;

    private double MarkWidth()
    {
        var mark = Child(2);
        return mark is null || mark.DesiredSize.Width <= 0 ? 0 : MarkGap + mark.DesiredSize.Width;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child(0) is not { } name)
        {
            return default;
        }

        Child(2)?.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var mark = MarkWidth();
        name.Measure(new Size(Math.Max(0, availableSize.Width - mark), availableSize.Height));
        var width = name.DesiredSize.Width + mark;
        var height = Math.Max(name.DesiredSize.Height, Child(2)?.DesiredSize.Height ?? 0);
        _detailFits = false;
        if (Child(1) is { } detail)
        {
            detail.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            var withDetail = width + Gap + detail.DesiredSize.Width;
            if (detail.DesiredSize.Width > 0 && withDetail <= availableSize.Width)
            {
                _detailFits = true;
                width = withDetail;
                height = Math.Max(height, detail.DesiredSize.Height);
            }
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child(0) is not { } name)
        {
            return finalSize;
        }

        var mark = MarkWidth();
        var nameWidth = Math.Min(name.DesiredSize.Width, Math.Max(0, finalSize.Width - mark));
        name.Arrange(new Rect(0, (finalSize.Height - name.DesiredSize.Height) / 2, nameWidth, name.DesiredSize.Height));
        var x = nameWidth;
        if (Child(2) is { } m)
        {
            m.Arrange(mark > 0 ? new Rect(x + MarkGap, (finalSize.Height - m.DesiredSize.Height) / 2, m.DesiredSize.Width, m.DesiredSize.Height) : new Rect(x, 0, 0, 0));
            x += mark;
        }

        if (Child(1) is { } detail)
        {
            detail.Arrange(_detailFits
                ? new Rect(x + Gap, (finalSize.Height - detail.DesiredSize.Height) / 2, detail.DesiredSize.Width, detail.DesiredSize.Height)
                : new Rect(x, 0, 0, 0));
        }

        return finalSize;
    }
}
