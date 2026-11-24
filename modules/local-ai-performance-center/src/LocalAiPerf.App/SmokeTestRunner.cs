using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalAiPerf.App.Services;
using LocalAiPerf.Presentation.ViewModels;
using LocalAiPerf.Recording;

namespace LocalAiPerf.App;

/// <summary>
/// --smoke-test: drives every screen of the real window (record two sessions, history, compare, settings),
/// saves a screenshot per screen and a JSON report, then exits with 0 (pass) or 1 (fail).
/// Used by Windows CI; it verifies the GUI starts and binds correctly, not hardware accuracy.
/// </summary>
internal sealed class SmokeTestRunner(Window window, MainViewModel main, MonitoringService service, WpfDialogService dialogs, string reportDirectory)
{
    private readonly List<string> _exceptions = [];
    private readonly List<string> _screens = [];
    private readonly BindingErrorListener _bindings = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public int? ExitCode { get; private set; }

    public void Start()
    {
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(_bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        _ = RunAsync();
    }

    public void RecordException(Exception ex)
    {
        lock (_exceptions)
        {
            _exceptions.Add($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task RunAsync()
    {
        var checks = new Dictionary<string, bool>();
        try
        {
            Directory.CreateDirectory(reportDirectory);
            checks["frames"] = await WaitUntil(() => main.FramesReceived >= 3, TimeSpan.FromSeconds(30));
            await service.StartRecordingAsync("Smoke A", "smoke test");
            foreach (var page in main.Pages)
            {
                main.CurrentPage = page;
                await Task.Delay(1500);
                await Capture(page.GetType().Name.Replace("ViewModel", "", StringComparison.Ordinal));
            }

            await service.StopRecordingAsync();
            await service.StartRecordingAsync("Smoke B", "smoke test");
            await Task.Delay(3000);
            await service.StopRecordingAsync();

            main.CurrentPage = main.History;
            checks["history"] = await WaitUntil(() => main.History.Sessions.Count >= 2 && !main.History.IsBusy && main.History.Stats.Count > 0, TimeSpan.FromSeconds(30));
            await Capture("History-loaded");

            main.CurrentPage = main.Compare;
            await WaitUntil(() => main.Compare.Sessions.Count >= 2, TimeSpan.FromSeconds(20));
            foreach (var item in main.Compare.Sessions.Take(2))
            {
                item.IsSelected = true;
            }

            await main.Compare.CompareCommand.ExecuteAsync(null);
            checks["compare"] = main.Compare.HasResult && main.Compare.Rows.Count > 0;
            await Task.Delay(1000);
            await Capture("Compare-result");

            main.CurrentPage = main.Settings;
            await Task.Delay(1500);
            checks["settings"] = main.Settings.DatabaseInfo.Count > 0;
            await Capture("Settings-loaded");
            main.CurrentPage = main.Dashboard;
            await Task.Delay(1000);
        }
        catch (Exception ex)
        {
            RecordException(ex);
        }

        var pathErrors = _bindings.Errors.Where(e => e.Contains("BindingExpression path error", StringComparison.Ordinal)).ToList();
        checks["no-exceptions"] = _exceptions.Count == 0;
        checks["no-binding-path-errors"] = pathErrors.Count == 0;
        var ok = checks.Values.All(v => v);
        ExitCode = ok ? 0 : 1;
        var report = new
        {
            ok,
            checks,
            elapsedSeconds = _clock.Elapsed.TotalSeconds,
            framesReceived = main.FramesReceived,
            isMock = service.IsMock,
            gpuProvider = service.Hardware?.Gpu.Name,
            gpuAvailable = service.Hardware?.Gpu.IsAvailable,
            gpuUnavailableReason = service.Hardware?.Gpu.UnavailableReason,
            cpu = service.HardwareInfo?.CpuName,
            runtimeState = service.LatestRuntime?.State.ToString(),
            runtimeError = service.LatestRuntime?.Error?.Message,
            screenshots = _screens,
            exceptions = _exceptions,
            bindingPathErrors = pathErrors,
            otherBindingMessages = _bindings.Errors.Except(pathErrors).Take(50),
            notices = service.Notices,
            dialogs = dialogs.Log,
        };
        File.WriteAllText(Path.Combine(reportDirectory, "smoke-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        window.Close();
    }

    private async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var end = _clock.Elapsed + timeout;
        while (_clock.Elapsed < end)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return condition();
    }

    private async Task Capture(string name)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var width = Math.Max(1, (int)(window.ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)(window.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var file = Path.Combine(reportDirectory, $"{_screens.Count + 1:00}-{name}.png");
            await using (var stream = File.Create(file))
            {
                encoder.Save(stream);
            }

            _screens.Add(Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            RecordException(ex);
        }
    }

    private sealed class BindingErrorListener : TraceListener
    {
        private readonly object _gate = new();
        private readonly List<string> _errors = [];

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (_gate)
                {
                    return [.. _errors];
                }
            }
        }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_errors.Count < 500)
                {
                    _errors.Add(message);
                }
            }
        }
    }
}
