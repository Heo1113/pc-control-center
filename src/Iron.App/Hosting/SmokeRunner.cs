using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Iron.App.Presentation;

namespace Iron.App.Hosting;

/// <summary>
/// --smoke-test DIR: drives the real window (not an offscreen copy) through every tab, the four states, the USB
/// handover, the 1.5 s flash, both placements, the tray popup and close-to-tray; saves what the window shows at the
/// monitor's own DPI, the window and work-area rectangles, layout issues and binding errors; then exits.
/// </summary>
internal sealed class SmokeRunner
{
    private readonly MainWindow _window;
    private readonly ShellViewModel _vm;
    private readonly TrayPopup _popup;
    private readonly string _dir;
    private readonly Action<int> _exit;
    private readonly BindingErrorListener _errors = BindingErrorListener.Attach();
    private readonly List<object> _steps = [];
    private readonly Queue<(string Name, TimeSpan Wait, Action Act, bool Capture)> _plan = new();
    private readonly DispatcherTimer _timer;
    private int _issues;

    public SmokeRunner(MainWindow window, ShellViewModel vm, TrayPopup popup, string dir, Action<int> exit)
    {
        _window = window;
        _vm = vm;
        _popup = popup;
        _dir = Path.GetFullPath(dir);
        _exit = exit;
        Directory.CreateDirectory(_dir);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Step();
        Plan();
    }

    public void Start() => _timer.Start();

    private void Plan()
    {
        var quick = TimeSpan.FromMilliseconds(900);
        Add("full-overview-normal", quick, () => { _window.SetLayout(WindowLayout.Full); _vm.ApplyPreset("normal"); _vm.SelectTab("overview"); });
        foreach (var tab in new[] { "processes", "power", "devices", "settings" })
        {
            Add($"full-{tab}", quick, () => _vm.SelectTab(tab));
        }

        Add("full-overview-alert", TimeSpan.FromSeconds(4), () => { _vm.SelectTab("overview"); _vm.ApplyPreset("alert"); });
        Add("full-overview-alert-usb-released", TimeSpan.FromSeconds(1.5), () => _vm.ToggleUsb());
        Add("full-power-usb-released", quick, () => _vm.SelectTab("power"));
        Add("full-overview-usb-resumed", TimeSpan.FromSeconds(1.5), () => { _vm.ToggleUsb(); _vm.SelectTab("overview"); });
        Add("full-overview-disconnected", quick, () => _vm.ApplyPreset("disconnected"));
        Add("full-overview-loading", quick, () => _vm.ApplyPreset("loading"));
        Add("full-overview-flash", TimeSpan.FromMilliseconds(500), () => { _vm.ApplyPreset("normal"); _vm.FlashImbalance(); });
        Add("half-overview-normal", TimeSpan.FromSeconds(2), () => { _window.SetLayout(WindowLayout.Half, rightHalf: true); _vm.ApplyPreset("normal"); });
        foreach (var tab in new[] { "processes", "power", "devices", "settings" })
        {
            Add($"half-{tab}", quick, () => _vm.SelectTab(tab));
        }

        Add("half-overview-alert", TimeSpan.FromSeconds(4), () => { _vm.SelectTab("overview"); _vm.ApplyPreset("alert"); });
        Add("tray-popup", quick, () => _popup.ShowNearTray(), capture: false);
        Add("close-to-tray", quick, () => { _popup.Hide(); _window.Close(); }, capture: false);
        Add("show-from-tray", quick, () => _window.ShowFromTray());
        Add("done", TimeSpan.Zero, Finish, capture: false);
    }

    private void Add(string name, TimeSpan wait, Action act, bool capture = true) => _plan.Enqueue((name, wait, act, capture));

    private DateTimeOffset _due = DateTimeOffset.MinValue;
    private (string Name, TimeSpan Wait, Action Act, bool Capture)? _current;

    private void Step()
    {
        if (_current is { } current)
        {
            if (DateTimeOffset.Now < _due)
            {
                return;
            }

            Record(current.Name, current.Capture);
            _current = null;
        }

        if (_plan.Count == 0)
        {
            return;
        }

        var next = _plan.Dequeue();
        try
        {
            next.Act();
        }
        catch (Exception ex)
        {
            _steps.Add(new { step = next.Name, error = ex.ToString() });
            _issues++;
        }

        _current = next;
        _due = DateTimeOffset.Now + next.Wait;
    }

    private void Record(string name, bool capture)
    {
        var hwnd = _window.Handle;
        var rect = Native.WindowRect(hwnd);
        var monitor = Native.ForWindow(hwnd);
        var dpi = VisualTreeHelper.GetDpi(_window).PixelsPerDip;
        object? popupRect = null;
        if (name == "tray-popup")
        {
            var r = Native.WindowRect(new System.Windows.Interop.WindowInteropHelper(_popup).Handle);
            popupRect = r is { } p ? new { p.Left, p.Top, p.Right, p.Bottom } : null;
            Save(_popup.Content as FrameworkElement, Path.Combine(_dir, "live-tray-popup.png"));
        }

        List<LayoutIssue> issues = [];
        string? file = null;
        if (capture && _window.IsVisible && _window.Content is FrameworkElement content && content.ActualWidth > 0)
        {
            file = $"live-{name}.png";
            Save(content, Path.Combine(_dir, file));
            issues = LayoutAudit.Check(content);
            _issues += issues.Count;
        }

        if (name == "close-to-tray" && (_window.IsVisible || Application.Current.MainWindow is null))
        {
            _issues++;
            issues.Add(new LayoutIssue("close-to-tray", "창이 숨겨지지 않았거나 앱이 끝났습니다", 0, 0, ""));
        }

        _steps.Add(new
        {
            step = name,
            file,
            windowVisible = _window.IsVisible,
            windowState = _window.WindowState.ToString(),
            layout = _vm.Layout.ToString(),
            status = _vm.StatusText,
            pixelsPerDip = dpi,
            windowPx = rect is { } w ? new { w.Left, w.Top, w.Right, w.Bottom, w.Width, w.Height } : null,
            workAreaPx = monitor is { } m ? new { m.WorkArea.Left, m.WorkArea.Top, m.WorkArea.Right, m.WorkArea.Bottom, m.WorkArea.Width, m.WorkArea.Height } : null,
            contentDip = new { width = (_window.Content as FrameworkElement)?.ActualWidth, height = (_window.Content as FrameworkElement)?.ActualHeight },
            popupPx = popupRect,
            issues,
        });
    }

    private void Finish()
    {
        _timer.Stop();
        var bindingErrors = _errors.Messages.Where(m => m.Contains("Error", StringComparison.Ordinal)).Distinct().ToList();
        var report = new { generated = DateTimeOffset.Now, issues = _issues, bindingErrors, steps = _steps };
        File.WriteAllText(Path.Combine(_dir, "smoke-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Console.WriteLine($"smoke test: {_steps.Count} steps, {_issues} issue(s), {bindingErrors.Count} binding error(s)");
        _exit(_issues == 0 && bindingErrors.Count == 0 ? 0 : 4);
    }

    private static void Save(FrameworkElement? element, string file)
    {
        if (element is null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return;
        }

        var scale = VisualTreeHelper.GetDpi(element).PixelsPerDip;
        var bitmap = new RenderTargetBitmap((int)Math.Round(element.ActualWidth * scale), (int)Math.Round(element.ActualHeight * scale), 96 * scale, 96 * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
