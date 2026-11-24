using System.Windows;
using System.Windows.Media;

namespace PcControlCenter.App;

public partial class MainWindow : Window
{
    /// <summary>
    /// Below this width (layout units, after the shell's own text scale) the menu shows icons only, so the modules' pages
    /// keep about the width they were designed for (a 1024×768 screen, a window snapped to half a screen).
    /// </summary>
    public const double CompactBelow = 1200;

    public static readonly DependencyProperty CompactNavigationProperty =
        DependencyProperty.Register(nameof(CompactNavigation), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    private double _scale = 1;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateCompactNavigation();
    }

    /// <summary>The menu shows icons only (see <see cref="CompactBelow"/>).</summary>
    public bool CompactNavigation
    {
        get => (bool)GetValue(CompactNavigationProperty);
        private set => SetValue(CompactNavigationProperty, value);
    }

    /// <summary>Window-wide text and layout scale on top of Windows' own DPI scaling (same as B's and E's windows).</summary>
    public void ApplyScale(double scale)
    {
        scale = Math.Clamp(scale, 0.8, 2.0);
        _scale = scale;
        Root.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        UpdateCompactNavigation();
    }

    private void UpdateCompactNavigation() => CompactNavigation = ActualWidth > 0 && ActualWidth / _scale < CompactBelow;
}
