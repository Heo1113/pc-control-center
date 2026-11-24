using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GpuProfit.App.Services;

/// <summary>
/// Pages are one long scrollable column with tables and charts inside. WPF gives the mouse wheel to the control under
/// the pointer: a table (which has its own scroll area) or a chart swallows it, so the page stops scrolling whenever
/// the pointer is over one. This passes the wheel to the page when the inner control has nothing left to scroll in that
/// direction (or cannot scroll at all, like a chart).
/// </summary>
public static class WheelForwarding
{
    public static void Register()
    {
        foreach (var type in new[] { typeof(DataGrid), typeof(ListBox), typeof(ScottPlot.WPF.WpfPlot) })
        {
            EventManager.RegisterClassHandler(type, UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject element)
        {
            return;
        }

        if (Descendant<ScrollViewer>(element) is { } inner && inner.ScrollableHeight > 0)
        {
            var atTop = inner.VerticalOffset <= 0;
            var atBottom = inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
            if (!(e.Delta > 0 && atTop) && !(e.Delta < 0 && atBottom))
            {
                return; // the table itself still scrolls
            }
        }

        if (Ancestor<ScrollViewer>(VisualTreeHelper.GetParent(element)) is not { } page)
        {
            return;
        }

        e.Handled = true;
        page.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
    }

    private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return node as T;
    }

    private static T? Descendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T found)
            {
                return found;
            }

            if (Descendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
