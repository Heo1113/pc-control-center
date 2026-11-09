using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalAiPerf.Presentation.Charts;
using ScottPlot;
using WinDiag.App.Services;
using WinDiag.Presentation;

namespace WinDiag.App.Controls;

/// <summary>
/// Renders project A's <see cref="ChartModel"/> with A's <see cref="ChartRenderer"/> (one unit per chart, gaps kept as
/// gaps, display-scale aware) and adds this app's event markers as vertical lines. Adapted from project A's ChartView.
/// </summary>
public partial class ChartView : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(ChartData), typeof(ChartView), new PropertyMetadata(null, (d, _) => ((ChartView)d).Render()));

    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.Register(
        nameof(Interactive), typeof(bool), typeof(ChartView), new PropertyMetadata(false, (d, _) => ((ChartView)d).ApplyInteractivity()));

    public ChartView()
    {
        InitializeComponent();
        ApplyInteractivity();
        ChartRenderer.ApplyStyle(Plot.Plot);
        Loaded += (_, _) => ApplyDisplayScale(VisualTreeHelper.GetDpi(this));
        PreviewMouseWheel += OnPreviewMouseWheel;
        Plot.Refresh();
    }

    public ChartData? Data
    {
        get => (ChartData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public bool Interactive
    {
        get => (bool)GetValue(InteractiveProperty);
        set => SetValue(InteractiveProperty, value);
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
        var data = Data;
        var model = data?.Model;
        TitleText.Text = model?.Title ?? "";
        UnitText.Text = model is null ? "" : $"[{model.Unit}]";
        var drawn = ChartRenderer.Render(Plot.Plot, model);
        if (drawn && data is not null)
        {
            foreach (var marker in data.Markers)
            {
                Plot.Plot.Add.VerticalLine(marker.X, 1, ScottPlot.Color.FromHex(marker.ColorHex).WithAlpha(0.75), LinePattern.Dashed);
            }
        }

        var legend = ChartRenderer.LegendEntries(model).Select(e => new
        {
            e.Label,
            Brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(e.ColorHex)) { Opacity = e.IsBand ? 0.5 : 1 },
            Thickness = e.IsBand ? 8.0 : 2.0,
        }).ToList();
        if (drawn && data is { Markers.Count: > 0 })
        {
            legend.Add(new
            {
                Label = "세로선: 이벤트",
                Brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(ChartPalette.Critical)) { Opacity = 0.75 },
                Thickness = 2.0,
            });
        }

        LegendItems.ItemsSource = legend;
        EmptyText.Text = model?.EmptyText ?? "";
        EmptyText.Visibility = drawn ? Visibility.Collapsed : Visibility.Visible;
        Plot.Refresh();
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        var data = Data;
        var model = data?.Model;
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

        var marker = data!.Markers.Where(m => Math.Abs(m.X - coordinates.X) <= tolerance / 2).Select(m => m.Label).FirstOrDefault();
        if (parts.Count == 0 && marker is null)
        {
            Readout.Text = "";
            return;
        }

        var x = at ?? coordinates.X;
        var position = model.XIsTime ? DateTime.FromOADate(x).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : x.ToString("0.0", CultureInfo.InvariantCulture);
        Readout.Text = $"{position} · {string.Join(" · ", parts)}" + (marker is null ? "" : $" · 이벤트: {marker}");
    }

    private void OnPlotMouseLeave(object sender, MouseEventArgs e) => Readout.Text = "";

    /// <summary>
    /// The wheel scrolls the page, never the chart. On an interactive chart, Ctrl+wheel zooms the time axis around the
    /// pointer (drag still pans). ScottPlot's own wheel zoom never runs, because this handler takes the event first.
    /// </summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Interactive && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (Data?.Model is { IsEmpty: false })
            {
                var x = Plot.Plot.GetCoordinates(Plot.GetPlotPixelPosition(e)).X;
                var limits = Plot.Plot.Axes.GetLimits();
                var factor = e.Delta > 0 ? 0.8 : 1.25;
                Plot.Plot.Axes.SetLimitsX(x - (x - limits.Left) * factor, x + (limits.Right - x) * factor);
                Plot.Refresh();
            }

            return;
        }

        WheelForwarding.ScrollOuter(this, e.Delta);
    }

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
