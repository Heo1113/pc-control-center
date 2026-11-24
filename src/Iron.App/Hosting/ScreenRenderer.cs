using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iron.App.Presentation;
using Iron.App.Views;

namespace Iron.App.Hosting;

/// <summary>
/// --render-screens: draws every screen and state offscreen at the given window sizes (DIP) and scale (1.75 = 175 %),
/// exactly as the window draws it, into PNG files with a layout report (layout-report.json). Used by CI and for the
/// screenshots of the Stage 1 report; the window itself never uses fixed sizes.
/// </summary>
internal static class ScreenRenderer
{
    private sealed record Scenario(string Name, IronTab Tab, MockPreset Preset, bool UsbReleased = false, Action<ShellViewModel>? Extra = null);

    private static readonly Scenario[] Scenarios =
    [
        new("overview-normal", IronTab.Overview, MockPreset.Normal),
        new("overview-alert", IronTab.Overview, MockPreset.Alert),
        new("overview-alert-usb-released", IronTab.Overview, MockPreset.Alert, UsbReleased: true),
        new("overview-disconnected", IronTab.Overview, MockPreset.Disconnected),
        new("overview-loading", IronTab.Overview, MockPreset.Loading),
        new("overview-flux-conflict", IronTab.Overview, MockPreset.Normal, Extra: vm => vm.Mock.IUnityRunning = true),
        new("processes-normal", IronTab.Processes, MockPreset.Normal),
        new("power-normal", IronTab.Power, MockPreset.Normal),
        new("power-alert", IronTab.Power, MockPreset.Alert),
        new("power-usb-released", IronTab.Power, MockPreset.Normal, UsbReleased: true),
        new("devices-normal", IronTab.Devices, MockPreset.Normal),
        new("devices-conflict", IronTab.Devices, MockPreset.Normal, Extra: vm => vm.Mock.IUnityRunning = true),
        new("settings-normal", IronTab.Settings, MockPreset.Normal),
        new("settings-invalid-input", IronTab.Settings, MockPreset.Normal, Extra: vm =>
        {
            vm.GpuTemperatureLimit.Text = "120";
            vm.PinCurrentLimit.Text = "abc";
        }),
    ];

    public static int Run(AppOptions options)
    {
        var dir = Path.GetFullPath(options.RenderScreensTo!);
        Directory.CreateDirectory(dir);
        var errors = BindingErrorListener.Attach();
        var screens = new List<object>();
        var total = 0;
        foreach (var (width, height) in options.RenderSizes)
        {
            var sizeName = $"{width:0}x{height:0}";
            foreach (var scenario in Scenarios)
            {
                var vm = CreateViewModel(scenario);
                var view = new ShellView { DataContext = vm };
                var file = Path.Combine(dir, $"{sizeName}-{scenario.Name}.png");
                Render(view, width, height, options.RenderScale, file);
                var issues = LayoutAudit.Check(view);
                total += issues.Count;
                screens.Add(new { file = Path.GetFileName(file), width, height, scale = options.RenderScale, layout = vm.Layout.ToString(), status = vm.StatusText, issues });
            }

            // the tray popup (its own window, fixed 320 DIP wide)
            var trayVm = CreateViewModel(new Scenario("tray-alert", IronTab.Overview, MockPreset.Alert, Extra: vm => vm.Mock.IUnityRunning = true));
            var tray = new TrayView { DataContext = trayVm };
            var trayFile = Path.Combine(dir, $"{sizeName}-tray-alert-flux-conflict.png");
            Render(tray, 320, double.NaN, options.RenderScale, trayFile);
            var trayIssues = LayoutAudit.Check(tray);
            total += trayIssues.Count;
            screens.Add(new { file = Path.GetFileName(trayFile), width = 320.0, height = tray.ActualHeight, scale = options.RenderScale, layout = "Tray", status = trayVm.StatusText, issues = trayIssues });
        }

        var bindingErrors = errors.Messages.Where(m => m.Contains("Error", StringComparison.Ordinal)).Distinct().ToList();
        var report = new { generated = DateTimeOffset.Now, totalIssues = total, bindingErrors, screens };
        File.WriteAllText(Path.Combine(dir, "layout-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Console.WriteLine($"rendered {screens.Count} screens into {dir}: {total} layout issue(s), {bindingErrors.Count} binding error(s)");
        return total == 0 && bindingErrors.Count == 0 ? 0 : 3;
    }

    private static ShellViewModel CreateViewModel(Scenario scenario)
    {
        var clock = new ManualScheduler(new DateTimeOffset(2026, 11, 24, 14, 30, 0, TimeSpan.FromHours(9)));
        var mock = new MockTelemetryProvider(jitter: false);
        var vm = new ShellViewModel(mock, clock) { Tab = scenario.Tab };
        vm.Mock.Apply(scenario.Preset);
        scenario.Extra?.Invoke(vm);
        // One minute of readings at 1 Hz: the trend and sparkline are full, the pin warning (three in a row) is settled.
        for (var i = 0; i < 60; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            vm.Tick();
        }

        if (scenario.UsbReleased)
        {
            vm.WireView.Release();
            clock.Advance(TimeSpan.FromSeconds(1));
            vm.Tick();
        }

        return vm;
    }

    /// <summary>Lays the element out at <paramref name="widthDip"/> × <paramref name="heightDip"/> (NaN = natural height) and saves it at the scale.</summary>
    public static void Render(FrameworkElement element, double widthDip, double heightDip, double scale, string file)
    {
        VisualTreeHelper.SetRootDpi(element, new DpiScale(scale, scale));
        element.Width = widthDip;
        if (!double.IsNaN(heightDip))
        {
            element.Height = heightDip;
        }

        for (var pass = 0; pass < 3; pass++)
        {
            element.Measure(new Size(widthDip, double.IsNaN(heightDip) ? double.PositiveInfinity : heightDip));
            element.Arrange(new Rect(0, 0, widthDip, double.IsNaN(heightDip) ? element.DesiredSize.Height : heightDip));
            element.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }

        var h = double.IsNaN(heightDip) ? element.ActualHeight : heightDip;
        var bitmap = new RenderTargetBitmap((int)Math.Round(widthDip * scale), (int)Math.Round(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    public static string Describe(double w, double h) => string.Create(CultureInfo.InvariantCulture, $"{w:0}x{h:0}");
}
