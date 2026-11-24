using System.Windows;
using System.Windows.Media;

namespace GpuProfit.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Scales the whole window content (accessible text size). 1.0 = 100 %.</summary>
    public void ApplyScale(double scale)
    {
        scale = Math.Clamp(scale, 0.9, 1.6);
        Root.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
    }
}
