using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsAiOperator.App.Services;
using WindowsAiOperator.Core;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Presentation;
using WindowsAiOperator.Presentation.ViewModels;

namespace WindowsAiOperator.App;

/// <summary>
/// GUI smoke test inside the real app (same idea as the user's project B): visits every screen, saves screenshots,
/// fails on binding errors or UI exceptions, runs tasks through the real view models (plan review and approval windows
/// answered by the test), and optionally drives the real test app with real UI Automation.
/// </summary>
internal sealed class SmokeTestRunner
{
    private static readonly List<string> UiExceptions = [];
    private static readonly Lock Gate = new();

    private readonly MainViewModel _main;
    private readonly OperatorContext _context;
    private readonly MainWindow _window;
    private readonly ApprovalPresenter _presenter;
    private readonly StartupOptions _options;
    private readonly IOperatorLog _log;
    private readonly BindingErrorListener _bindings = new();
    private readonly List<string> _screenshots = [];
    private readonly List<string> _failures = [];
    private readonly List<object> _results = [];
    private readonly string _reportDirectory;
    private Func<ApprovalViewModel, bool> _approve = _ => false;

    public SmokeTestRunner(MainViewModel main, OperatorContext context, MainWindow window, ApprovalPresenter presenter, StartupOptions options, IOperatorLog log)
    {
        _main = main;
        _context = context;
        _window = window;
        _presenter = presenter;
        _options = options;
        _log = log;
        _reportDirectory = Path.GetFullPath(options.SmokeReportDirectory ?? Path.Combine(context.Paths.DataDirectory, "smoke"));
        Directory.CreateDirectory(_reportDirectory);
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(_bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        _presenter.Shown += OnDialogShown;
    }

    public static void RecordException(Exception exception)
    {
        lock (Gate)
        {
            UiExceptions.Add($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    public async Task<int> RunAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await Idle();
            await VisitAllPagesAsync("initial");
            if (_context.Desktop.IsMock)
            {
                await RunMockPipelineAsync();
            }

            if (_options.RealUiaSmoke && !_context.Desktop.IsMock)
            {
                await RunRealUiaAsync();
            }

            await CheckSettingsAsync();
            await VisitAllPagesAsync("after-tasks");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _failures.Add($"smoke runner exception: {ex.GetType().Name}: {ex.Message}");
            _log.Error("smoke", "smoke test failed", ex);
        }

        lock (Gate)
        {
            _failures.AddRange(UiExceptions.Select(e => "UI exception: " + e));
        }

        _failures.AddRange(_bindings.Errors.Select(e => "binding error: " + e));
        var status = _context.Store.GetStatus();
        var dpi = VisualTreeHelper.GetDpi(_window);
        var report = new
        {
            app = $"{AppInfo.ProductName} {AppInfo.Version}",
            desktop = _context.Desktop.Name,
            isMockDesktop = _context.Desktop.IsMock,
            realUia = _options.RealUiaSmoke && !_context.Desktop.IsMock,
            dataDirectory = _context.Paths.DataDirectory,
            durationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
            // What the screens were rendered at, so the report says which DPI and language were (and were not) covered.
            display = new
            {
                os = Environment.OSVersion.VersionString,
                uiLanguage = System.Globalization.CultureInfo.InstalledUICulture.Name,
                windowDpi = dpi.PixelsPerInchX,
                scalePercent = Math.Round(dpi.DpiScaleX * 100),
                primaryScreenDip = $"{SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0}",
                virtualScreenDip = $"{SystemParameters.VirtualScreenWidth:0}x{SystemParameters.VirtualScreenHeight:0}",
            },
            pages = _main.Pages.Select(p => p.Key).ToArray(),
            screenshots = _screenshots,
            results = _results,
            recoveredTasks = _context.Recovered.Select(r => new { r.Id, r.NewStatus, r.ChangesPerformed, r.Uncertain, r.NotExecuted }).ToArray(),
            store = new { status.Integrity, status.Tasks, status.Steps, status.Workflows, unfinished = status.UnfinishedTaskIds.Count },
            bindingErrors = _bindings.Errors,
            failures = _failures,
            ok = _failures.Count == 0,
        };
        await File.WriteAllTextAsync(Path.Combine(_reportDirectory, "smoke-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        _log.Info("smoke", $"smoke test finished: {(_failures.Count == 0 ? "ok" : string.Join(" | ", _failures))}");
        return _failures.Count == 0 ? 0 : 1;
    }

    private async Task VisitAllPagesAsync(string phase)
    {
        var index = 0;
        foreach (var page in _main.Pages)
        {
            _main.CurrentPage = page;
            await Idle();
            await Task.Delay(page is InspectorViewModel ? 1500 : 400);
            await Idle();
            Capture(_window, $"{phase}-{++index:00}-{page.Key}.png");
        }
    }

    /// <summary>Simulated desktop: the natural-language pipeline with the MOCK model, and a risky step that is denied.</summary>
    private async Task RunMockPipelineAsync()
    {
        _main.CurrentPage = _main.Runner;
        _approve = _ => false;
        var profile = _context.Settings.ActiveProfile;
        if (profile?.Kind == Core.Settings.ProviderKind.Mock)
        {
            _main.Runner.RequestText = "메모장을 열고 \"스모크 테스트 문장\"을 입력해 줘.";
            await _main.Runner.RunCommand.ExecuteAsync(null);
            Expect("mock-nl-notepad", _main.Runner.LastRun, TaskRunStatus.Completed);
        }

        var deletePlan = PlanParser.Parse("""
            {"steps":[
              {"action":"select","window":{"app":"test-target"},"element":{"control_type":"ListItem","name":"항목 1"}},
              {"action":"click","element":{"automation_id":"DeleteItemButton"}}]}
            """).Plan!;
        var denied = await _main.Runner.RunStaticPlanAsync(deletePlan, "[스모크] 삭제 버튼 — 승인 거부 경로", TaskSource.Manual);
        Expect("mock-approval-denied", denied, TaskRunStatus.PartiallyCompleted);
        if (denied?.Steps.LastOrDefault() is { } step && step.Performed)
        {
            _failures.Add("mock-approval-denied: the denied step was performed");
        }

        Capture(_window, "task-runner-after-mock-tasks.png");
    }

    /// <summary>Real Windows UI Automation on the separate test app (started from the same folder, closed afterwards).</summary>
    private async Task RunRealUiaAsync()
    {
        var before = Process.GetProcessesByName("OperatorTestTarget").Select(p => p.Id).ToHashSet();
        try
        {
            _main.CurrentPage = _main.Workflows;
            await Idle();
            var workflow = _context.Workflows.List().First(w => w.Id == "builtin-test-counter");
            _main.CurrentPage = _main.Runner;
            var run = await _main.Runner.RunWorkflowAsync(workflow, new Dictionary<string, string> { ["text"] = "실제 UIA 스모크 테스트 문장" }, []);
            Expect("real-uia-workflow", run, TaskRunStatus.Completed);
            Capture(_window, "real-uia-workflow.png");

            if (_context.Settings.ActiveProfile?.Kind == Core.Settings.ProviderKind.Mock)
            {
                _main.Runner.RequestText = "테스트 앱의 '알림 표시' 확인란 상태를 확인해 줘.";
                await _main.Runner.RunCommand.ExecuteAsync(null);
                Expect("real-uia-mock-model-read", _main.Runner.LastRun, TaskRunStatus.Completed);
            }

            _main.CurrentPage = _main.Inspector;
            await _main.Inspector.RefreshAsync();
            var target = _main.Inspector.Windows.FirstOrDefault(w => w.Window.ProcessName.Equals("OperatorTestTarget", StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                _failures.Add("real-uia-inspector: the test app window is not listed");
            }
            else
            {
                _main.Inspector.SelectedWindow = target;
                await Task.Delay(2500);
                await Idle();
                _results.Add(new { name = "real-uia-inspector", structure = _main.Inspector.StructureText, nodes = _main.Inspector.Tree.Count });
                if (_main.Inspector.Tree.Count == 0)
                {
                    _failures.Add("real-uia-inspector: no element tree");
                }

                Capture(_window, "real-uia-inspector.png");
            }
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

    private async Task CheckSettingsAsync()
    {
        _main.CurrentPage = _main.Settings;
        await Idle();
        _main.Settings.BackupNowCommand.Execute(null);
        _main.Settings.CheckIntegrityCommand.Execute(null);
        _results.Add(new { name = "settings-backup-integrity", result = _main.Settings.ResultText, backups = _main.Settings.Backups.Count });
        if (_main.Settings.Backups.Count == 0 || _main.Settings.ResultText?.Contains("ok", StringComparison.Ordinal) != true)
        {
            _failures.Add("settings: backup or integrity check failed");
        }
    }

    private void Expect(string name, TaskRun? run, TaskRunStatus expected)
    {
        _results.Add(new
        {
            name,
            status = run?.Status.ToString(),
            message = run?.StatusMessage,
            report = run?.Report,
            steps = run?.Steps.Select(s => $"{s.Sequence}. {s.Step.Action} {s.Status} {s.Method} {s.Message}").ToArray(),
        });
        if (run?.Status != expected)
        {
            _failures.Add($"{name}: expected {expected}, got {run?.Status} ({run?.StatusMessage})");
        }
    }

    private void OnDialogShown(object viewModel, Window window)
    {
        _ = window.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(400);
            await Idle();
            switch (viewModel)
            {
                case PlanReviewViewModel review:
                    Capture(window, $"dialog-plan-review-{_screenshots.Count:00}.png");
                    review.StartCommand.Execute(null);
                    break;
                case ApprovalViewModel approval:
                    Capture(window, $"dialog-approval-{_screenshots.Count:00}.png");
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

    private void Capture(Window window, string fileName)
    {
        try
        {
            if (window.Content is not FrameworkElement content || content.ActualWidth < 1 || content.ActualHeight < 1)
            {
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(content);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(_reportDirectory, fileName));
            encoder.Save(stream);
            _screenshots.Add(fileName);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            _failures.Add($"screenshot {fileName}: {ex.Message}");
        }
    }

    private static async Task Idle() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class BindingErrorListener : TraceListener
    {
        private readonly Lock _gate = new();
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
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            lock (_gate)
            {
                if (_errors.Count < 50)
                {
                    _errors.Add(message);
                }
            }
        }
    }
}
