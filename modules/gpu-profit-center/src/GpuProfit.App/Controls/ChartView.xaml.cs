using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalAiPerf.Presentation.Charts;

namespace GpuProfit.App.Controls;

/// <summary>
/// Renders project A's <see cref="ChartModel"/> with A's <see cref="ChartRenderer"/> (one unit per chart, gaps kept as
/// gaps, display-scale aware). Adapted from project C's ChartView (commit 06cc9e5) without C's event markers.
/// </summary>
public partial class ChartView : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(ChartModel), typeof(ChartView), new PropertyMetadata(null, (d, _) => ((ChartView)d).Render()));

    public ChartView()
    {
        InitializeComponent();
        Plot.UserInputProcessor.Disable();
        ChartRenderer.ApplyStyle(Plot.Plot);
        Loaded += (_, _) => ApplyDisplayScale(VisualTreeHelper.GetDpi(this));
        Plot.Refresh();
    }

    public ChartModel? Data
    {
        get => (ChartModel?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyDisplayScale(newDpi);
    }

    /// <summary>ScottPlot draws in physical pixels; scale the figure with the Windows display scale (project A's fix).</summary>
    private void ApplyDisplayScale(DpiScale dpi)
    {
        var scale = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        if (Math.Abs(Plot.Plot.ScaleFactor - scale) < 0.001)
        {
            return;
        }

        Plot.Plot.ScaleFactor = scale;
        Plot.DisplayScale = (float)scale;
        Plot.Refresh();
    }

    private void Render()
    {
        var model = Data;
        TitleText.Text = model?.Title ?? "";
        UnitText.Text = model is null ? "" : $"[{model.Unit}]";
        var drawn = ChartRenderer.Render(Plot.Plot, model);
        if (drawn && model is { XIsTime: false, XLabel: { } xLabel })
        {
            Plot.Plot.Axes.Bottom.Label.Text = xLabel;
        }

        LegendItems.ItemsSource = ChartRenderer.LegendEntries(model).Select(e => new
        {
            e.Label,
            Brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(e.ColorHex)) { Opacity = e.IsBand ? 0.5 : 1 },
            Thickness = e.IsBand ? 8.0 : 2.0,
        }).ToList();
        EmptyText.Text = model?.EmptyText ?? "";
        EmptyText.Visibility = drawn ? Visibility.Collapsed : Visibility.Visible;
        Plot.Refresh();
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        var model = Data;
        if (model is null || model.IsEmpty)
        {
            return;
        }

        var pixel = Plot.GetPlotPixelPosition(e);
        var coordinates = Plot.Plot.GetCoordinates(pixel);
        var limits = Plot.Plot.Axes.GetLimits();
        var tolerance = Math.Abs(limits.Right - limits.Left) / 40;
        var parts = new List<string>();
        double? at = null;
        foreach (var series in model.Series)
        {
            if (Nearest(series.X, coordinates.X) is int i && Math.Abs(series.X[i] - coordinates.X) <= tolerance)
            {
                at ??= series.X[i];
                parts.Add($"{series.Label} {series.Y[i].ToString(model.ValueFormat, CultureInfo.InvariantCulture)} {model.Unit}");
            }
        }

        if (parts.Count == 0)
        {
            Readout.Text = "";
            return;
        }

        var x = at ?? coordinates.X;
        var position = model.XIsTime ? DateTime.FromOADate(x).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : x.ToString("0.##", CultureInfo.InvariantCulture);
        Readout.Text = $"{position} · {string.Join(" · ", parts)}";
    }

    private void OnPlotMouseLeave(object sender, MouseEventArgs e) => Readout.Text = "";

    private static int? Nearest(double[] xs, double x)
    {
        if (xs.Length == 0)
        {
            return null;
        }

        var index = Array.BinarySearch(xs, x);
        if (index >= 0)
        {
            return index;
        }

        index = ~index;
        if (index == 0)
        {
            return 0;
        }

        if (index >= xs.Length)
        {
            return xs.Length - 1;
        }

        return x - xs[index - 1] <= xs[index] - x ? index - 1 : index;
    }
}
