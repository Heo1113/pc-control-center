using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcControlCenter.App.Hosting;
using PcControlCenter.Operator;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Presentation.ViewModels;
using PcControlCenter.Core;
using PcControlCenter.GpuMemory;
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
    OperatorBinder? operatorBinder,
    ShellDialogs dialogs,
    string reportDirectory,
    StartupOptions options)
{
    private readonly List<string> _exceptions = [];
    private readonly List<string> _screens = [];
    private readonly BindingErrorListener _bindings = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, bool> _legacyExistedAtStart = LegacyDataLocations.Defaults().ToDictionary(l => l.Directory, l => Directory.Exists(l.Directory));

    public int? ExitCode { get; private set; }

    private Func<ApprovalViewModel, bool> _approve = _ => false;
    private readonly List<object> _operatorRuns = [];

    public void Start()
    {
        if (operatorBinder is not null)
        {
            operatorBinder.Presenter.Shown += OnOperatorDialogShown;
        }

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

            // GPU memory (Windows counters, or MOCK): a reading arrived, or the page says why there is none. Never fails the
            // app when the counters are missing (a runner without a GPU driver), but the reason must be given.
            checks["gpu-memory-reader"] = await WaitUntil(() => runtime.GpuMemory.Latest is not null
                                                               || runtime.GpuMemory.Health.State is GpuMemoryMonitorState.Unavailable
                                                                   or GpuMemoryMonitorState.Disabled,
                TimeSpan.FromSeconds(20));
            details["gpu-memory"] = runtime.GpuMemory.Latest is { } gpuMemory
                ? $"{gpuMemory.Source}: adapters {gpuMemory.Adapters.Count}, processes {gpuMemory.Processes.Count}, variant {gpuMemory.Variant}, {gpuMemory.CollectionTime.TotalMilliseconds:0} ms"
                : runtime.GpuMemory.Health.UnavailableReason ?? "-";
            if (runtime.Hub.IsMock)
            {
                checks["gpu-memory-mock-rows"] = shell.GpuMemory.Processes.Count > 0 || runtime.GpuMemory.Latest?.Processes.Count > 0;
            }

            // The page open for longer: engine activity and process details are read only then (recorded, not required: a
            // runner may lack the engine counters). Counts only; no process, library or file names go into the report.
            if (runtime.GpuMemory.Health.State is not (GpuMemoryMonitorState.Unavailable or GpuMemoryMonitorState.Disabled))
            {
                shell.Navigate(SectionKeys.Hardware, "hardware/gpu-memory");
                await WaitUntil(() => runtime.GpuMemory.Latest is { } s && (s.EnginesRead || s.Details.Count > 0), TimeSpan.FromSeconds(15));
                await Task.Delay(2500); // a further reading: engine rates need two collections
                if (runtime.GpuMemory.Latest is { } open)
                {
                    details["gpu-memory-open"] = $"engines {(open.EnginesRead ? "read" : "not read")}, details {open.Details.Count} "
                                                 + $"(refused {open.Details.Values.Count(d => d.AccessDenied)}, with known runtimes {open.Details.Values.Count(d => d.Runtimes.Count > 0)}, "
                                                 + $"model files {open.Details.Values.Sum(d => d.Models.Count)}), {open.CollectionTime.TotalMilliseconds:0} ms"
                                                 + (open.Issues.Count > 0 ? "; issue scopes: " + string.Join(", ", open.Issues.Select(i => i.Scope).Distinct()) : "");
                }

                await Capture("hardware-gpu-memory-open");

                // A kind picked from the legend and the help panel, as a user would open them.
                if (shell.GpuMemory.Adapters.FirstOrDefault()?.View.Legend.FirstOrDefault(l => l.Kind is not null)?.Kind is { } kind)
                {
                    shell.GpuMemory.FilterKindCommand.Execute(kind);
                }

                shell.GpuMemory.HelpOpen = true;
                await Task.Delay(1500);
                await Capture("hardware-gpu-memory-filter-help");
                shell.GpuMemory.ClearKindFilterCommand.Execute(null);
                shell.GpuMemory.HelpOpen = false;

                // MOCK: the Ollama runner with what its server reported (sizes, context), shown apart from the measured values.
                if (runtime.Hub.IsMock && shell.GpuMemory.Processes.FirstOrDefault(r => r.Name.StartsWith("ollama", StringComparison.OrdinalIgnoreCase)) is { } runner)
                {
                    shell.GpuMemory.SelectedProcess = runner;
                    checks["gpu-memory-runtime-report"] = await WaitUntil(() => shell.GpuMemory.Detail?.Reports.Count > 0, TimeSpan.FromSeconds(15));
                    await Task.Delay(800);
                    BringIntoView("런타임 보고"); // the detail panel scrolls in a small window
                    await Task.Delay(300);
                    await Capture("hardware-gpu-memory-runtime");
                }
            }

            shell.Navigate(SectionKeys.Dashboard);
            checks["dashboard-live"] = await WaitUntil(() => shell.Dashboard.CpuUtil != Text.NA, TimeSpan.FromSeconds(10));
            if (runtime.Hub.IsMock)
            {
                // The dashboard's GPU memory card: the largest processes from the reader (MOCK here).
                checks["dashboard-gpu-memory"] = await WaitUntil(() => shell.Dashboard.GpuMemoryLargest != Text.NA, TimeSpan.FromSeconds(15));
                await Task.Delay(800);
                await Capture("dashboard-gpu-memory");
            }

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

            await RunOperatorAsync(checks, details);
            await RunMigrationAsync(checks, details);

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
            operatorHost = runtime.Operator?.Host is { } host ? new { state = host.State.ToString(), pid = host.ProcessId, shellPid = Environment.ProcessId, shellElevated = Environment.IsPrivilegedProcess, deElevated = host.HostDeElevated, host.Restarts, mock = host.ExpectMock, reason = host.UnavailableReason } : null,
            operatorRuns = _operatorRuns,
            strata = runtime.LocalAi.Service?.LatestRuntime?.State.ToString(),
            gpuMemory = GpuMemoryReport(runtime.GpuMemory),
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

    /// <summary>
    /// The GPU memory reader for the report: provider, state, adapters (name, vendor, LUID, usage) and counts only — no
    /// process names, so a report from a user's PC does not list their programs.
    /// </summary>
    private static object GpuMemoryReport(GpuMemoryMonitor monitor)
    {
        var health = monitor.Health;
        var latest = monitor.Latest;
        return new
        {
            state = health.State.ToString(),
            provider = health.ProviderName,
            isMock = health.IsMock,
            reason = health.UnavailableReason,
            readings = health.Readings,
            failures = health.Failures,
            intervalMs = health.Interval.TotalMilliseconds,
            lastCollectionMs = latest?.CollectionTime.TotalMilliseconds,
            timestamp = latest?.Timestamp,
            variant = latest?.Variant.ToString(),
            committedAvailable = latest?.CommittedAvailable,
            adapterCountersAvailable = latest?.AdapterCountersAvailable,
            adapters = latest?.Adapters.Select(a => new
            {
                luid = GpuLuid.Format(a.Luid),
                name = a.Info?.Name,
                vendor = a.Info?.VendorName,
                software = a.Info?.IsSoftware,
                dedicatedCapacity = a.Info?.DedicatedVideoMemoryBytes,
                dedicatedUsage = a.DedicatedUsageBytes,
                sharedUsage = a.SharedUsageBytes,
                processes = latest.Processes.Count(p => p.Luid == a.Luid),
            }),
            processes = latest?.Processes.Count,
            listedProcesses = latest?.Processes.Count(GpuMemoryRules.IsListed),
            identities = latest?.ProcessTable.Values.GroupBy(p => p.State).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            issues = latest?.Issues.Select(i => $"{i.Source} [{i.Scope}] {i.Status}: {i.Message}"),
        };
    }

    /// <summary>
    /// E through the separate host process: with --mock-desktop the natural-language pipeline (MOCK model) and a denied
    /// approval on E's simulated desktop; without it, E's built-in workflow on the real desktop against the safe test app
    /// (OperatorTestTarget.exe, packaged next to the EXE). Then the emergency stop latches and resume releases it.
    /// </summary>
    private async Task RunOperatorAsync(Dictionary<string, bool> checks, Dictionary<string, string> details)
    {
        if (runtime.Operator is not { } module || operatorBinder?.ViewModel is not { } e || module.Host is not { } host || module.Context is not { } context)
        {
            if (runtime.Operator is not null)
            {
                checks["operator-ready"] = false;
            }

            return;
        }

        checks["operator-host-process"] = host.State == HostState.Connected && host.ProcessId is int pid && pid != Environment.ProcessId;
        details["operator-host-process"] = $"shell {Environment.ProcessId}, host {host.ProcessId}, desktop {context.Desktop.Name}";
        shell.Navigate(SectionKeys.Operator, "operator/run");
        if (options.MockDesktop)
        {
            if (options.MockModel)
            {
                e.Runner.RequestText = "메모장을 열고 \"스모크 테스트 문장\"을 입력해 줘.";
                await e.Runner.RunCommand.ExecuteAsync(null);
                checks["operator-mock-nl-task"] = Expect("mock-nl-notepad", e.Runner.LastRun, TaskRunStatus.Completed);
            }

            _approve = _ => false;
            var deletePlan = PlanParser.Parse("""
                {"steps":[
                  {"action":"select","window":{"app":"test-target"},"element":{"control_type":"ListItem","name":"항목 1"}},
                  {"action":"click","element":{"automation_id":"DeleteItemButton"}}]}
                """).Plan!;
            var denied = await e.Runner.RunStaticPlanAsync(deletePlan, "[스모크] 삭제 버튼 — 승인 거부 경로", TaskSource.Manual);
            checks["operator-approval-denied"] = Expect("mock-approval-denied", denied, TaskRunStatus.PartiallyCompleted) && denied?.Steps.LastOrDefault()?.Performed == false;
        }
        else
        {
            var before = Process.GetProcessesByName("OperatorTestTarget").Select(p => p.Id).ToHashSet();
            try
            {
                var workflow = context.Workflows.List().First(w => w.Id == "builtin-test-counter");
                var run = await e.Runner.RunWorkflowAsync(workflow, new Dictionary<string, string> { ["text"] = "실제 UIA 스모크 테스트 문장" }, []);
                checks["operator-real-uia-workflow"] = Expect("real-uia-workflow", run, TaskRunStatus.Completed);
                await Capture("operator-real-uia-workflow");
            }
            finally
            {
                foreach (var process in Process.GetProcessesByName("OperatorTestTarget").Where(p => !before.Contains(p.Id)))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }

        // Emergency stop from the shell: latched until resume, the desktop refuses changes in the meantime.
        if (shell.Operator is { } controls)
        {
            await controls.EmergencyStopCommand.ExecuteAsync(null);
            await WaitUntil(() => host.State == HostState.Connected, TimeSpan.FromSeconds(30));
            var refused = await context.Desktop.PerformAsync(new ActionRequest { Action = PrimitiveAction.Focus, WindowHandle = 1 }, CancellationToken.None);
            checks["operator-emergency-latch"] = host.EmergencyLatched && !refused.Performed;
            await Capture("operator-emergency-stopped");
            await controls.ResumeCommand.ExecuteAsync(null);
            checks["operator-resume"] = !host.EmergencyLatched;
        }
    }

    /// <summary>
    /// --smoke-migrate-from: the data page rehearses an original program's folder (byte copy, hashes, checks on the copy)
    /// and applies it after the (unattended) confirmation; the folder must be byte-identical afterwards.
    /// </summary>
    private async Task RunMigrationAsync(Dictionary<string, bool> checks, Dictionary<string, string> details)
    {
        if (options.SmokeMigrateFrom is not { } folder)
        {
            return;
        }

        var location = PcControlCenter.Migration.LegacyMigrator.FromFolder(folder);
        if (location is null)
        {
            checks["migration-source"] = false;
            details["migration-source"] = "no database in " + folder;
            return;
        }

        static Dictionary<string, string> Hashes(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

        var before = Hashes(location.Directory);
        shell.Navigate(SectionKeys.Settings, "settings/data");
        var row = new LegacySourceRow(location);
        shell.Data.Sources.Add(row);
        shell.Data.RefreshCommand.Execute(null);
        await shell.Data.RehearseCommand.ExecuteAsync(row);
        await WaitUntil(() => row.Rehearsal is not null, TimeSpan.FromSeconds(60));
        checks["migration-rehearsal"] = row.Rehearsal?.Passed == true;
        details["migration-rehearsal"] = string.Join(" | ", shell.Data.Checks.Select(c => $"{c.State} {c.Name}: {c.Detail}"));
        await Capture("settings-data-rehearsal");
        if (row.Rehearsal?.Passed == true)
        {
            await shell.Data.ApplyCommand.ExecuteAsync(row);
            await WaitUntil(() => row.State == "가져옴" || row.State.StartsWith("적용 실패", StringComparison.Ordinal), TimeSpan.FromSeconds(120));
            checks["migration-apply"] = row.State == "가져옴";
            details["migration-apply"] = shell.Data.StatusText;
            var module = runtime.Supervisor.Find(location.ModuleId);
            checks["migration-module-restarted"] = await WaitUntil(() => module?.Status.IsUsable == true, TimeSpan.FromSeconds(60));
            await Capture("settings-data-applied");
        }

        checks["migration-original-unchanged"] = Hashes(location.Directory).SequenceEqual(before);
    }

    private bool Expect(string name, TaskRun? run, TaskRunStatus expected)
    {
        _operatorRuns.Add(new
        {
            name,
            status = run?.Status.ToString(),
            message = run?.StatusMessage,
            steps = run?.Steps.Select(s => $"{s.Sequence}. {s.Step.Action} {s.Status} {s.Method} {s.Message}").ToArray(),
        });
        return run?.Status == expected;
    }

    private void OnOperatorDialogShown(object viewModel, Window dialog)
    {
        _ = dialog.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(400);
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            switch (viewModel)
            {
                case PlanReviewViewModel review:
                    review.StartCommand.Execute(null);
                    break;
                case ApprovalViewModel approval:
                    if (_approve(approval))
                    {
                        approval.ApproveCommand.Execute(null);
                    }
                    else
                    {
                        approval.DenyCommand.Execute(null);
                    }

                    break;
            }
        });
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

    /// <summary>Scrolls the first visible element with this automation name into view.</summary>
    private void BringIntoView(string automationName)
    {
        static FrameworkElement? Find(DependencyObject node, string name)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement { IsVisible: true } element && AutomationProperties.GetName(element) == name)
                {
                    return element;
                }

                if (Find(child, name) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        Find(window, automationName)?.BringIntoView();
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
