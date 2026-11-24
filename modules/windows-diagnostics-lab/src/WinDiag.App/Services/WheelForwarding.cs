using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinDiag.App.Services;

/// <summary>
/// Makes the mouse wheel scroll the page when the pointer is over a table, list or text box that cannot scroll further
/// in that direction (or has nothing to scroll). A WPF ScrollViewer marks every wheel event as handled, even at its end,
/// so without this the page stops scrolling whenever the pointer is over a table. Charts forward the wheel themselves
/// (see <c>ChartView</c>).
/// </summary>
internal static class WheelForwarding
{
    private const double Epsilon = 0.01;

    /// <summary>Called once at start-up.</summary>
    public static void Register()
        => EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // The preview event passes every scroll viewer from the window down; only the innermost one under the pointer decides.
        if (e.Handled || sender is not ScrollViewer viewer || !ReferenceEquals(Innermost(e.OriginalSource as DependencyObject), viewer))
        {
            return;
        }

        if (CanScroll(viewer, e.Delta))
        {
            return; // the table or list scrolls itself first
        }

        if (ScrollOuter(viewer, e.Delta))
        {
            e.Handled = true;
        }
    }

    /// <summary>Scrolls the nearest enclosing scroll viewer that can still move in the wheel's direction. Returns whether one did.</summary>
    public static bool ScrollOuter(DependencyObject from, int delta)
    {
        for (var node = Parent(from); node is not null; node = Parent(node))
        {
            if (node is ScrollViewer outer && CanScroll(outer, delta))
            {
                outer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = outer });
                return true;
            }
        }

        return false;
    }

    public static bool CanScroll(ScrollViewer viewer, int delta)
        => viewer.ScrollableHeight > Epsilon
           && (delta > 0 ? viewer.VerticalOffset > Epsilon : viewer.VerticalOffset < viewer.ScrollableHeight - Epsilon);

    private static ScrollViewer? Innermost(DependencyObject? node)
    {
        for (; node is not null; node = Parent(node))
        {
            if (node is ScrollViewer viewer)
            {
                return viewer;
            }
        }

        return null;
    }

    private static DependencyObject? Parent(DependencyObject node)
        => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
