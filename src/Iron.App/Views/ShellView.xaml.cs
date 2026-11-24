using System.Windows;
using System.Windows.Controls;
using Iron.App.Presentation;

namespace Iron.App.Views;

/// <summary>
/// Window content. Follows its own width: below 1200 DIP (the 4K 175 % half-width window is 1097 DIP) it switches the
/// layout and the compact type metrics; nothing depends on the screen size.
/// </summary>
public partial class ShellView : UserControl
{
    private ResourceDictionary? _metrics;
    private LayoutMode? _mode;

    public ShellView()
    {
        InitializeComponent();
        ApplyLayout(LayoutMode.Full);
        SizeChanged += (_, e) => ApplyLayout(LayoutRules.For(e.NewSize.Width));
        DataContextChanged += (_, _) => ApplyLayout(LayoutRules.For(ActualWidth > 0 ? ActualWidth : 2000), force: true);
        TrayButton.Click += (_, _) => TrayRequested?.Invoke(this, EventArgs.Empty);
        LayoutButton.Click += (_, _) => LayoutToggleRequested?.Invoke(this, EventArgs.Empty);
        CloseButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? TrayRequested;
    public event EventHandler? LayoutToggleRequested;
    public event EventHandler? CloseRequested;

    /// <summary>The title bar element (the window uses it to find what is draggable).</summary>
    public FrameworkElement TitleBarElement => TitleBar;

    public LayoutMode Mode => _mode ?? LayoutMode.Full;

    public void ApplyLayout(LayoutMode mode, bool force = false)
    {
        if (DataContext is ShellViewModel vm)
        {
            vm.Layout = mode;
        }

        if (_mode == mode && !force)
        {
            return;
        }

        _mode = mode;
        var source = mode == LayoutMode.Full ? "Theme/Metrics.Full.xaml" : "Theme/Metrics.Compact.xaml";
        var metrics = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Iron.App;component/{source}", UriKind.Absolute) };
        if (_metrics is not null)
        {
            Resources.MergedDictionaries.Remove(_metrics);
        }

        Resources.MergedDictionaries.Add(metrics);
        _metrics = metrics;
    }
}
