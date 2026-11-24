using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace Iron.App.Hosting;

public sealed record LayoutIssue(string Kind, string Text, double Need, double Have, string Where);

/// <summary>
/// Checks a laid-out screen the way the UAT asks (글자 잘림 · 겹침 · 가로 스크롤 0건): text wider or taller than its box
/// (ellipsis included), text whose box is cut by the layout, elements past the right or bottom edge, horizontal scrolling,
/// and two texts drawn over each other.
/// </summary>
internal static class LayoutAudit
{
    public static List<LayoutIssue> Check(FrameworkElement root)
    {
        var issues = new List<LayoutIssue>();
        var texts = new List<(TextBlock Block, Rect Ink)>();
        var scale = VisualTreeHelper.GetDpi(root).PixelsPerDip;
        foreach (var element in Walk(root))
        {
            if (!IsShown(element, root))
            {
                continue;
            }

            if (element is ScrollViewer sv && sv.ExtentWidth > sv.ViewportWidth + 0.5 && sv.ViewportWidth > 0)
            {
                issues.Add(new LayoutIssue("hscroll", Name(sv), sv.ExtentWidth, sv.ViewportWidth, PathOf(sv, root)));
            }

            if (element is TextBlock tb && tb.ActualWidth > 0 && tb.ActualHeight > 0)
            {
                var text = TextOf(tb);
                if (string.IsNullOrWhiteSpace(text) || InsideTextBox(tb))
                {
                    continue;
                }

                var box = new Size(Math.Max(0, tb.ActualWidth - tb.Padding.Left - tb.Padding.Right), Math.Max(0, tb.ActualHeight - tb.Padding.Top - tb.Padding.Bottom));
                var ft = Format(tb, text, scale);
                if (tb.TextWrapping == TextWrapping.NoWrap)
                {
                    if (ft.WidthIncludingTrailingWhitespace > box.Width + 0.75)
                    {
                        issues.Add(new LayoutIssue("text-clipped", text, Math.Round(ft.WidthIncludingTrailingWhitespace, 1), Math.Round(box.Width, 1), PathOf(tb, root)));
                    }
                }
                else
                {
                    ft.MaxTextWidth = Math.Max(1, box.Width);
                    if (ft.Height > box.Height + 0.75)
                    {
                        issues.Add(new LayoutIssue("text-clipped-height", text, Math.Round(ft.Height, 1), Math.Round(box.Height, 1), PathOf(tb, root)));
                    }
                }

                if (LayoutInformation.GetLayoutClip(tb) is { } clip && !clip.IsEmpty() && clip.Bounds.Width + 0.75 < Math.Min(ft.WidthIncludingTrailingWhitespace, tb.ActualWidth))
                {
                    issues.Add(new LayoutIssue("text-layout-clip", text, Math.Round(tb.ActualWidth, 1), Math.Round(clip.Bounds.Width, 1), PathOf(tb, root)));
                }

                var origin = tb.TransformToAncestor(root).Transform(new Point(0, 0));
                var inkWidth = Math.Min(tb.TextWrapping == TextWrapping.NoWrap ? ft.WidthIncludingTrailingWhitespace : box.Width, tb.ActualWidth);
                var x = tb.TextAlignment switch
                {
                    TextAlignment.Right => origin.X + tb.ActualWidth - inkWidth,
                    TextAlignment.Center => origin.X + (tb.ActualWidth - inkWidth) / 2,
                    _ => origin.X,
                };
                texts.Add((tb, new Rect(x, origin.Y, inkWidth, tb.ActualHeight)));
            }

            if (element is FrameworkElement fe && fe != root && fe.ActualWidth > 0 && !InsideScrollContent(fe, root) && !InsidePopupOrTooltip(fe))
            {
                var bounds = fe.TransformToAncestor(root).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                if (bounds.Right > root.ActualWidth + 0.75 || bounds.Bottom > root.ActualHeight + 0.75)
                {
                    issues.Add(new LayoutIssue("outside-window", Name(fe), Math.Round(bounds.Right, 1), Math.Round(root.ActualWidth, 1), PathOf(fe, root)));
                }
            }
        }

        for (var i = 0; i < texts.Count; i++)
        {
            for (var j = i + 1; j < texts.Count; j++)
            {
                var a = texts[i].Ink;
                var b = texts[j].Ink;
                var overlap = Rect.Intersect(a, b);
                if (!overlap.IsEmpty && overlap.Width > 1.5 && overlap.Height > 3)
                {
                    issues.Add(new LayoutIssue("text-overlap", TextOf(texts[i].Block) + " ⟂ " + TextOf(texts[j].Block), Math.Round(overlap.Width, 1),
                        Math.Round(overlap.Height, 1), PathOf(texts[i].Block, root)));
                }
            }
        }

        return issues;
    }

    private static FormattedText Format(TextBlock tb, string text, double scale)
        => new(text, CultureInfo.CurrentUICulture, tb.FlowDirection,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black, null,
            TextOptions.GetTextFormattingMode(tb), scale);

    public static string TextOf(TextBlock tb)
    {
        if (tb.Inlines.Count == 0)
        {
            return tb.Text ?? "";
        }

        var sb = new StringBuilder();
        foreach (var inline in tb.Inlines)
        {
            sb.Append(inline is Run run ? run.Text : new TextRange(inline.ContentStart, inline.ContentEnd).Text);
        }

        return sb.ToString();
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = count - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(current, i));
            }
        }
    }

    /// <summary>Visible all the way up to the root (IsVisible needs a window; offscreen renders have none).</summary>
    private static bool IsShown(DependencyObject element, DependencyObject root)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is UIElement u && (u.Visibility != Visibility.Visible || u.Opacity == 0))
            {
                return false;
            }

            if (e is FrameworkElement fe && e != element && (fe.ActualWidth <= 0 || fe.ActualHeight <= 0) && e is not Canvas)
            {
                return false;
            }

            if (e == root)
            {
                return true;
            }
        }

        return true;
    }

    private static bool InsideTextBox(DependencyObject element)
    {
        for (var e = VisualTreeHelper.GetParent(element); e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is TextBoxBase or ComboBox)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Content of a vertical scroll viewer may extend below the window (it scrolls); only its width is checked.</summary>
    private static bool InsideScrollContent(DependencyObject element, DependencyObject root)
    {
        for (var e = VisualTreeHelper.GetParent(element); e is not null && e != root; e = VisualTreeHelper.GetParent(e))
        {
            if (e is ScrollContentPresenter)
            {
                return true;
            }
        }

        return false;
    }

    private static bool InsidePopupOrTooltip(DependencyObject element)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is Popup or ToolTip)
            {
                return true;
            }
        }

        return false;
    }

    private static string Name(FrameworkElement fe) => string.IsNullOrEmpty(fe.Name) ? fe.GetType().Name : $"{fe.GetType().Name}#{fe.Name}";

    private static string PathOf(DependencyObject element, DependencyObject root)
    {
        var parts = new List<string>();
        for (var e = element; e is not null && e != root && parts.Count < 7; e = VisualTreeHelper.GetParent(e))
        {
            if (e is FrameworkElement fe && (fe is UserControl or Border or ItemsControl || !string.IsNullOrEmpty(fe.Name)))
            {
                parts.Add(Name(fe));
            }
        }

        parts.Reverse();
        return string.Join(" > ", parts);
    }
}

/// <summary>Counts WPF binding errors (a binding path that does not exist, a converter that fails) while a check runs.</summary>
internal sealed class BindingErrorListener : TraceListener
{
    private readonly List<string> _messages = [];
    private readonly StringBuilder _line = new();

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    public static BindingErrorListener Attach()
    {
        var listener = new BindingErrorListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.ResourceDictionarySource.Listeners.Add(listener);
        PresentationTraceSources.ResourceDictionarySource.Switch.Level = SourceLevels.Warning;
        return listener;
    }

    public override void Write(string? message) => _line.Append(message);

    public override void WriteLine(string? message)
    {
        _line.Append(message);
        lock (_messages)
        {
            _messages.Add(_line.ToString());
        }

        _line.Clear();
    }
}
