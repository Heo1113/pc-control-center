using System.Windows;
using System.Windows.Media;
using WindowsAiOperator.Presentation.ViewModels;

namespace WindowsAiOperator.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Window-wide text scale on top of the system DPI scale (WPF already scales for 175 % etc.).</summary>
    public void ApplyScale(double scale) =>
        Root.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
}
