using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinDiag.App.Services;
using WinDiag.Collection;
using WinDiag.Core.Events;
using WinDiag.Presentation.ViewModels;
using WinDiag.Storage;

namespace WinDiag.App;

/// <summary>
/// --smoke-test: drives every screen of the real window against this machine's real event logs (and the virtual
/// scenario when --demo-data is given), uses the main commands (search, details, sensors around an event, timeline
/// record, diagnosis, comparison, exports, backup), saves a screenshot per screen and a JSON report, then exits with
/// 0 (pass) or 1 (fail). It checks that the GUI starts, binds and works; it does not check hardware accuracy.
/// Structure adapted from project A's SmokeTestRunner.
/// </summary>
internal sealed class SmokeTestRunner(Window window, MainViewModel main, DiagnosticsEngine engine, WpfDialogService dialogs, string reportDirectory)
{
    private readonly List<string> _exceptions = [];
    private readonly List<string> _screens = [];
    private readonly BindingErrorListener _bindings = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, bool> _checks = [];
    private readonly List<string> _wheel = [];

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

    private async Task Check(string name, Func<Task<bool>> step)
    {
        try
        {
            _checks[name] = await step();
        }
        catch (Exception ex)
        {
            RecordException(ex);
            _checks[name] = false;
        }
    }

    private async Task RunAsync()
    {
        Directory.CreateDirectory(reportDirectory);
        var hasDemo = engine.Store.GetStats().VirtualEvents > 0;
        await Check("events-collected", async () => await WaitUntil(() => engine.LastPoll is not null || engine.Collector is null, TimeSpan.FromSeconds(120)));
        await Check("sensor-samples", async () => await WaitUntil(() => engine.Recorder?.Live.Latest is not null, TimeSpan.FromSeconds(30)));

        foreach (var page in main.Pages)
        {
            main.CurrentPage = page;
            await Task.Delay(1500);
            await WaitUntil(() => !page.IsBusy, TimeSpan.FromSeconds(60));
            await Capture(page.GetType().Name.Replace("ViewModel", "", StringComparison.Ordinal));
        }

        await Check("dashboard", async () =>
        {
            main.CurrentPage = main.Dashboard;
            await main.Dashboard.RefreshAsync();
            return main.Dashboard.Warnings.Count > 0 && main.Dashboard.Trend is not null;
        });

        await Check("event-search-and-detail", async () =>
        {
            main.CurrentPage = main.Events;
            main.Events.SelectedRange = main.Events.Ranges.First(r => r.Value == 0);
            main.Events.SelectedSeverity = main.Events.Severities[0];
            await main.Events.SearchCommand.ExecuteAsync(null);
            if (main.Events.Events.Count == 0)
            {
                return !hasDemo; // a clean CI runner may have no matching events at all
            }

            main.Events.SelectedEvent = main.Events.Events[0];
            await WaitUntil(() => main.Events.HasDetail, TimeSpan.FromSeconds(15));
            await Capture("Events-detail");
            main.Events.GroupMode = true;
            await WaitUntil(() => !main.Events.IsBusy && main.Events.Groups.Count > 0, TimeSpan.FromSeconds(15));
            await Capture("Events-groups");
            var ok = main.Events.HasDetail && main.Events.Groups.Count > 0;
            main.Events.GroupMode = false;
            return ok;
        });

        await Check("hardware-around-event", async () =>
        {
            var target = engine.Store.QueryEvents(new EventQuery { Providers = ["Display"], Limit = 1 }).FirstOrDefault()
                         ?? engine.Store.QueryEvents(new EventQuery { MinSeverity = DiagSeverity.Warning, Limit = 1 }).FirstOrDefault();
            if (target is null)
            {
                main.CurrentPage = main.Hardware;
                await Task.Delay(1500);
                return main.Hardware.PowerChart is not null;
            }

            main.ShowHardwareAt(target.TimeCreated, target.Title);
            await Task.Delay(2500);
            await WaitUntil(() => !main.Hardware.IsBusy, TimeSpan.FromSeconds(30));
            await Capture("Hardware-focus");
            return main.Hardware.PowerChart is not null && main.Hardware.IsFocused;
        });

        await Check("wheel-over-chart-and-table-scrolls-page", async () =>
        {
            main.CurrentPage = main.Hardware;
            await Task.Delay(1000);
            var chart = Descendants<Controls.ChartView>(window).FirstOrDefault(c => c.IsVisible);
            var chartOk = WheelScrollsPage("hardware chart", chart);
            main.CurrentPage = main.Dashboard;
            await Task.Delay(1500);
            await WaitUntil(() => !main.Dashboard.IsBusy, TimeSpan.FromSeconds(30));
            // A table with more rows scrolls itself first; at its end the page must take over.
            var grid = Descendants<DataGrid>(window).FirstOrDefault(g => g.IsVisible);
            if (grid is not null && Descendants<ScrollViewer>(grid).FirstOrDefault() is { } inner)
            {
                inner.ScrollToEnd();
                window.UpdateLayout();
            }

            var row = grid is null ? null : Descendants<DataGridRow>(grid).LastOrDefault(r => r.IsVisible);
            var tableOk = WheelScrollsPage("dashboard table row (table at its end)", row);
            return chartOk && tableOk;
        });

        await Check("timeline-and-user-record", async () =>
        {
            main.CurrentPage = main.Timeline;
            var timeline = main.Timeline;
            timeline.NewTitle = "스모크 테스트 사용자 기록";
            timeline.NewValueName = "VDD";
            timeline.NewValueBefore = "1.35";
            timeline.NewValueAfter = "1.35";
            timeline.NewValueUnit = "V";
            timeline.AddValueCommand.Execute(null);
            await timeline.SaveRecordCommand.ExecuteAsync(null);
            await WaitUntil(() => !timeline.IsBusy, TimeSpan.FromSeconds(30));
            await Capture("Timeline-after-record");
            return engine.Store.ListUserRecords().Any(r => r.Title == "스모크 테스트 사용자 기록") && timeline.Items.Count > 0;
        });

        await Check("diagnostics", async () =>
        {
            main.CurrentPage = main.Diagnostics;
            await main.Diagnostics.RunAsync();
            await Capture("Diagnostics-report");
            await main.Diagnostics.ExportHtmlCommand.ExecuteAsync(null);
            var report = Directory.GetFiles(reportDirectory, "wdl-report-*.html").Length > 0;
            return main.Diagnostics.HasReport && report && (!hasDemo || main.Diagnostics.Hypotheses.Count > 0);
        });

        await Check("compare", async () =>
        {
            main.CurrentPage = main.Compare;
            await Task.Delay(1500);
            await WaitUntil(() => !main.Compare.IsBusy, TimeSpan.FromSeconds(30));
            if (main.Compare.Baselines.Count == 0)
            {
                await main.Compare.AddBaselineNowCommand.ExecuteAsync(null);
            }

            main.Compare.SelectedBaseline ??= main.Compare.Baselines.FirstOrDefault();
            await main.Compare.CompareCommand.ExecuteAsync(null);
            await main.Compare.ExportResultCommand.ExecuteAsync("html");
            await Capture("Compare-result");
            return main.Compare.HasResult && Directory.GetFiles(reportDirectory, "wdl-compare-*.html").Length > 0;
        });

        await Check("settings-save-and-backup", async () =>
        {
            main.CurrentPage = main.Settings;
            await Task.Delay(1000);
            await main.Settings.SaveCommand.ExecuteAsync(null);
            await main.Settings.BackupCommand.ExecuteAsync(null);
            await Capture("Settings-after-backup");
            var backup = Directory.GetFiles(reportDirectory, "wdl-backup-*.db").FirstOrDefault();
            return main.Settings.DatabaseInfo.Count > 0 && backup is not null && DiagnosticsStore.ValidateBackup(backup).IsValid;
        });

        main.CurrentPage = main.Dashboard;
        await Task.Delay(1000);
        await Capture("Dashboard-final");

        var pathErrors = _bindings.Errors.Where(e => e.Contains("BindingExpression path error", StringComparison.Ordinal)).ToList();
        _checks["no-exceptions"] = _exceptions.Count == 0;
        _checks["no-binding-path-errors"] = pathErrors.Count == 0;
        var ok = _checks.Values.All(v => v);
        ExitCode = ok ? 0 : 1;
        var stats = engine.Store.GetStats();
        var report = new
        {
            ok,
            checks = _checks,
            elapsedSeconds = _clock.Elapsed.TotalSeconds,
            isMockSensors = engine.IsMock,
            demoData = hasDemo,
            gpuProvider = engine.Hardware.Gpu.Name,
            gpuAvailable = engine.Hardware.Gpu.IsAvailable,
            gpuUnavailableReason = engine.Hardware.Gpu.UnavailableReason,
            cpu = engine.Hardware.Info.CpuName,
            elevated = Environment.IsPrivilegedProcess,
            channels = engine.Store.GetChannelStates().Select(c => new { c.Channel, status = c.Status.ToString(), c.LastRecordId, oldest = c.OldestEventTime }),
            lastCollection = engine.LastReport?.Channels.Select(c => new { c.Channel, status = c.Status.ToString(), c.Read, c.Inserted, c.Duplicates }),
            database = new { stats.Events, stats.VirtualEvents, stats.SensorSamples, stats.Snapshots, stats.Gaps },
            integrity = engine.Store.IntegrityCheck(),
            recoveredRuns = engine.RecoveredRuns.Select(r => new { r.Id, r.Started, r.Ended, r.EndReason }),
            gapKinds = engine.Store.ListGaps(limit: 100_000).GroupBy(g => g.Kind).ToDictionary(g => g.Key, g => g.Count()),
            screenshots = _screens,
            exceptions = _exceptions,
            bindingPathErrors = pathErrors,
            otherBindingMessages = _bindings.Errors.Except(pathErrors).Take(50),
            notices = main.Notices,
            dialogs = dialogs.Log,
            wheel = _wheel,
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

    /// <summary>
    /// Sends a wheel-down the way Windows does (preview event from the window down, then the bubbling event if nobody
    /// handled it) to an element inside a table or chart, and checks that the page scroll viewer moved.
    /// A page that has nothing to scroll is recorded as not applicable.
    /// </summary>
    private bool WheelScrollsPage(string what, FrameworkElement? target)
    {
        if (target is null)
        {
            _wheel.Add($"{what}: 대상 없음");
            return false;
        }

        ScrollViewer? page = null;
        for (DependencyObject? node = target; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer viewer)
            {
                page = viewer; // the outermost one wins
            }
        }

        window.UpdateLayout();
        if (page is null || page.ScrollableHeight <= 0)
        {
            _wheel.Add($"{what}: 페이지에 스크롤할 내용 없음 (해당 없음)");
            return true;
        }

        page.ScrollToTop();
        window.UpdateLayout();
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = target };
        target.RaiseEvent(preview);
        if (!preview.Handled)
        {
            target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.MouseWheelEvent, Source = target });
        }

        window.UpdateLayout();
        var moved = page.VerticalOffset > 0;
        _wheel.Add($"{what}: 페이지 위치 {page.VerticalOffset:0.#} / {page.ScrollableHeight:0.#} ({(moved ? "스크롤됨" : "움직이지 않음")})");
        page.ScrollToTop();
        return moved;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
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
