using System.Windows;
using System.Windows.Controls;

namespace Iron.App.Controls;

/// <summary>
/// One line of a process-name cell: the first child (the identified name) gets the width first and trims only when even
/// it does not fit; the second child (the muted executable name) is shown only when it fits completely after the name,
/// otherwise it is left out (arranged into an empty slot, so nothing of it is drawn). The cell never shows half a detail,
/// and the full text stays in the tooltip.
/// </summary>
public sealed class NameDetailPanel : Panel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(NameDetailPanel), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private bool _detailFits;

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0)
        {
            return default;
        }

        var name = InternalChildren[0];
        name.Measure(availableSize);
        var width = name.DesiredSize.Width;
        var height = name.DesiredSize.Height;
        _detailFits = false;
        if (InternalChildren.Count > 1)
        {
            var detail = InternalChildren[1];
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
        if (InternalChildren.Count == 0)
        {
            return finalSize;
        }

        var name = InternalChildren[0];
        var nameWidth = Math.Min(name.DesiredSize.Width, finalSize.Width);
        name.Arrange(new Rect(0, (finalSize.Height - name.DesiredSize.Height) / 2, nameWidth, name.DesiredSize.Height));
        for (var i = 1; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Arrange(i == 1 && _detailFits
                ? new Rect(nameWidth + Gap, (finalSize.Height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height)
                : new Rect(nameWidth, 0, 0, 0));
        }

        return finalSize;
    }
}
