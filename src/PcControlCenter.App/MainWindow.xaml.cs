using System.Windows;
using System.Windows.Media;

namespace PcControlCenter.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Window-wide text and layout scale on top of Windows' own DPI scaling (same as B's and E's windows).</summary>
    public void ApplyScale(double scale)
    {
        scale = Math.Clamp(scale, 0.8, 2.0);
        Root.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
    }
}
