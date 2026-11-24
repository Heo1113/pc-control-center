using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalAiPerf.Presentation.Charts;
using ScottPlot;

namespace LocalAiPerf.App.Controls;

/// <summary>
/// Renders a <see cref="ChartModel"/> with ScottPlot: one unit per chart, 2 px lines, gaps kept as gaps,
/// a legend only for two or more series, and a hover readout listing every series at the pointer's time.
/// </summary>
public partial class ChartView : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(ChartModel), typeof(ChartView), new PropertyMetadata(null, (d, _) => ((ChartView)d).Render()));

    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.Register(
        nameof(Interactive), typeof(bool), typeof(ChartView), new PropertyMetadata(false, (d, _) => ((ChartView)d).ApplyInteractivity()));

    public ChartView()
    {
        InitializeComponent();
        ApplyInteractivity();
        ChartRenderer.ApplyStyle(Plot.Plot);
        Loaded += (_, _) => ApplyDisplayScale(VisualTreeHelper.GetDpi(this));
        Plot.Refresh();
    }

    /// <summary>
    /// ScottPlot's WPF control draws in physical pixels and does not apply the Windows display scale by itself, so at
    /// 150-200 % scaling the chart text came out at 1/1.5-1/2 of the size of the rest of the window. ScaleFactor scales the
    /// whole figure; ScottPlot divides pointer positions by the same factor, so hover and zoom stay aligned.
    /// </summary>
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

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyDisplayScale(newDpi);
    }

    public ChartModel? Model
    {
        get => (ChartModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Live charts are not interactive (they redraw every second); history/compare charts allow zoom and pan.</summary>
    public bool Interactive
    {
        get => (bool)GetValue(InteractiveProperty);
        set => SetValue(InteractiveProperty, value);
    }

    private void ApplyInteractivity()
    {
        if (Interactive)
        {
            Plot.UserInputProcessor.Enable();
        }
        else
        {
            Plot.UserInputProcessor.Disable();
        }
    }

    private void Render()
    {
        var model = Model;
        TitleText.Text = model?.Title ?? "";
        UnitText.Text = model is null ? "" : $"[{model.Unit}]";
        var drawn = ChartRenderer.Render(Plot.Plot, model);
        LegendItems.ItemsSource = ChartRenderer.LegendEntries(model).Select(e => new
        {
            e.Label,
            Brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(e.ColorHex)) { Opacity = e.IsBand ? 0.5 : 1 },
            Thickness = e.IsBand ? 8.0 : 2.0,
        }).ToList();
        EmptyText.Text = model?.EmptyText ?? "";
        EmptyText.Visibility = drawn ? Visibility.Collapsed : Visibility.Visible;
        Plot.Refresh();
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        var model = Model;
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

        var x = at!.Value;
        var position = model.XIsTime
            ? DateTime.FromOADate(x).ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : $"{x.ToString("0.0", CultureInfo.InvariantCulture)}분";
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
