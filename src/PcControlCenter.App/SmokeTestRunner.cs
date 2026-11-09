using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcControlCenter.App.Hosting;
using PcControlCenter.Core;
using PcControlCenter.Modules;
using PcControlCenter.Presentation;

namespace PcControlCenter.App;

/// <summary>
/// --smoke-test: drives the real integrated window: waits for the shared sensor reader and the modules, opens every
/// section and page (one screenshot each), records an A session, a B session and a C event poll, checks that every
/// module samples through the shared reader and that no original program's data folder was touched, then writes
/// smoke-report.json and exits with 0 (pass) or 1 (fail). It verifies startup, binding and wiring, not hardware accuracy.
/// Adapted from the smoke test runners of A, B and C.
/// </summary>
internal sealed class SmokeTestRunner(
    Window window,
    ShellViewModel shell,
    ShellRuntime runtime,
    LocalAiBinder localAi,
    GpuEconomicsBinder economics,
    DiagnosticsBinder diagnostics,
    ShellDialogs dialogs,
    string reportDirectory)
{
    private readonly List<string> _exceptions = [];
    private readonly List<string> _screens = [];
    private readonly BindingErrorListener _bindings = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, bool> _legacyExistedAtStart = LegacyDataLocations.Defaults().ToDictionary(l => l.Directory, l => Directory.Exists(l.Directory));

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
        var details = new Dictionary<string, string>();
        try
        {
            Directory.CreateDirectory(reportDirectory);
            checks["sensor-readings"] = await WaitUntil(() => runtime.Hub.Health.Readings >= 3, TimeSpan.FromSeconds(30));
            checks["modules-settled"] = await WaitUntil(() => runtime.Supervisor.Modules.All(m => m.Status.State is not (ModuleState.Starting or ModuleState.NotStarted)),
                TimeSpan.FromSeconds(120));
            var usable = runtime.Supervisor.Modules.Where(m => m.Status.IsUsable).Select(m => m.Id).ToList();
            checks["modules-usable"] = usable.Count == runtime.Supervisor.Modules.Count(m => m.Status.State != ModuleState.Disabled);
            checks["pages-bound"] = await WaitUntil(() => usable.All(id => shell.TabsOf(id).All(t => t.Content is not ModuleUnavailableViewModel)), TimeSpan.FromSeconds(30));

            // Every module samples through the shared reader; none opened a sensor itself.
            checks["single-sensor-reader"] = runtime.LocalAi.Service?.Hardware?.IsSharedView != false
                                             && runtime.Economics.Engine?.Hardware?.IsSharedView != false
                                             && runtime.Diagnostics.Engine?.Hardware.IsSharedView != false;

            // Visit every section and page of the one main window.
            foreach (var section in shell.Sections)
            {
                shell.CurrentSection = section;
                foreach (var tab in section.Tabs)
                {
                    section.SelectedTab = tab;
                    await Task.Delay(1200);
                    await Capture($"{section.Key}-{tab.Key.Replace('/', '-')}");
                }
            }

            shell.Navigate(SectionKeys.Dashboard);
            checks["dashboard-live"] = await WaitUntil(() => shell.Dashboard.CpuUtil != Text.NA, TimeSpan.FromSeconds(10));

            // A: a manual inference session through A's own recorder.
            if (runtime.LocalAi.Service is { } service && service.Recorder is not null)
            {
                await service.StartRecordingAsync("[smoke] PC Control Center", "smoke test");
                await Task.Delay(3000);
                await service.StopRecordingAsync();
                checks["local-ai-session"] = service.Store?.ListSessions().Count > 0;
            }

            // B: a session with energy accounting through B's own session service.
            if (runtime.Economics.Engine is { } engine)
            {
                var result = await Task.Run(() =>
                {
                    const string name = "[smoke] PC Control Center";
                    var workload = engine.Store.ListWorkloads().FirstOrDefault(w => w.Name == name)?.Id
                                   ?? engine.Store.AddWorkload(new GpuProfit.Core.Domain.Workload { Name = name, Type = GpuProfit.Core.Domain.WorkloadType.Other });
                    var session = engine.Sessions.Start(workload, name, "smoke test");
                    Thread.Sleep(3000);
                    engine.Sessions.Stop(session.Id);
                    return engine.Sessions.Compute(session.Id);
                });
                checks["economics-session"] = result.RecordedSeconds > 0;
                details["economics-session"] = $"recorded {result.RecordedSeconds:0.0} s, energy {result.Energy.TotalKwh}";
            }

            // C: one event-log poll (Windows) through C's own collector.
            if (runtime.Diagnostics.Engine is { } diag)
            {
                var poll = await diag.PollNowAsync();
                checks["diagnostics-poll"] = poll is not null || diag.EventCollectionUnavailableReason is not null;
                details["diagnostics-poll"] = poll is null ? diag.EventCollectionUnavailableReason ?? diag.LastPollError ?? "-" : $"read {poll.Read}, inserted {poll.Inserted} events";
            }

            shell.Navigate(SectionKeys.Dashboard);
            shell.Dashboard.Invalidate();
            await Task.Delay(3000);
            await Capture("dashboard-final");

            // The original programs' data folders were not created or written by the integrated app.
            checks["legacy-folders-untouched"] = LegacyDataLocations.Defaults().All(l => _legacyExistedAtStart[l.Directory] || !Directory.Exists(l.Directory));
            checks["own-databases"] = usable.All(id => File.Exists(runtime.Paths.ModuleDatabasePath(id)));
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
        var health = runtime.Hub.Health;
        var report = new
        {
            ok,
            checks,
            details,
            elapsedSeconds = _clock.Elapsed.TotalSeconds,
            dataRoot = runtime.Paths.Root,
            sensor = new
            {
                isMock = runtime.Hub.IsMock,
                gpuProvider = runtime.Hub.GpuProviderName,
                gpuAvailable = runtime.Hub.GpuAvailable,
                gpuUnavailableReason = runtime.Hub.GpuUnavailableReason,
                readings = health.Readings,
                skippedTicks = health.SkippedTicks,
                intervalMs = health.Interval.TotalMilliseconds,
                consumers = health.Consumers,
                requests = runtime.Intervals.Requests.ToDictionary(r => r.Key, r => r.Value.TotalSeconds),
            },
            modules = runtime.Supervisor.Modules.Select(m => new { m.Id, state = m.Status.State.ToString(), m.Status.Summary, m.Status.Detail }),
            strata = runtime.LocalAi.Service?.LatestRuntime?.State.ToString(),
            screenshots = _screens,
            exceptions = _exceptions,
            bindingPathErrors = pathErrors,
            otherBindingMessages = _bindings.Errors.Except(pathErrors).Take(50),
            shellNotices = shell.Notices,
            dialogs = dialogs.Log,
            moduleDialogs = new { localAi = localAi.Dialogs.Log, economics = economics.Dialogs.Log, diagnostics = diagnostics.Dialogs.Log },
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
