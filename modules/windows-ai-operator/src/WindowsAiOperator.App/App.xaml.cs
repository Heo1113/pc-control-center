using System.IO;
using System.Windows;
using System.Windows.Threading;
using WindowsAiOperator.App.Services;
using WindowsAiOperator.Automation.Windows;
using WindowsAiOperator.Core;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Automation.Mock;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Presentation;
using WindowsAiOperator.Presentation.ViewModels;

namespace WindowsAiOperator.App;

public partial class App : Application
{
    private DataFolderLock? _lock;
    private FileOperatorLog? _log;
    private OperatorContext? _context;
    private MainViewModel? _main;
    private HotkeyService? _hotkey;

    internal static StartupOptions Options { get; private set; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Options = StartupOptions.Parse(e.Args);
        AppPaths paths;
        try
        {
            paths = AppPaths.Resolve(Options.DataDirectory, AppContext.BaseDirectory);
            paths.EnsureDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"데이터 폴더를 만들 수 없습니다: {ex.Message}", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        _lock = DataFolderLock.TryAcquire(paths.DataDirectory);
        if (_lock is null)
        {
            if (!Options.SmokeTest)
            {
                MessageBox.Show("이 데이터 폴더를 쓰는 Windows AI Operator가 이미 실행 중입니다.", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown(3);
            return;
        }

        _log = new FileOperatorLog(paths.LogDirectory);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log?.Error("app", "unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.Warn("app", "unobserved task exception", args.Exception);
            args.SetObserved();
        };

        IDesktopAutomation desktop;
        try
        {
            // UI Automation is created on a thread-pool (MTA) thread, as Microsoft recommends for UIA clients, so tree reads
            // and actions from background threads never route through the WPF UI thread.
            var log = _log;
            desktop = Options.MockDesktop ? MockDesktop.CreateDemo() : await Task.Run(() => new FlaUiDesktop(log));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or TypeInitializationException)
        {
            _log.Error("app", "UI Automation could not be started", ex);
            MessageBox.Show($"Windows UI Automation을 시작하지 못했습니다: {ex.Message}", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(4);
            return;
        }

        _context = new OperatorContext(paths, desktop, new DpapiSecretStore(paths.SecretsPath, _log), _log, Options.MockModel);
        var dialogs = new WpfDialogService();
        var presenter = new ApprovalPresenter();
        var host = new AppHost(_log);
        _main = new MainViewModel(_context, new WpfDispatcher(Dispatcher), dialogs, presenter, new OverlayHighlighter(), host);
        var window = new MainWindow(_main);
        MainWindow = window;
        window.Closing += OnMainWindowClosing;
        window.Closed += (_, _) =>
        {
            if (!Options.SmokeTest)
            {
                Shutdown(0);
            }
        };
        host.ApplyTheme(_context.Settings.General.Theme);
        window.ApplyScale(_context.Settings.General.FontScale);
        window.Show();

        _hotkey = new HotkeyService();
        if (!_hotkey.Register(window, () => _main.EmergencyStop()))
        {
            _log.Warn("app", "emergency hotkey could not be registered");
            _main.Dashboard.Warnings.Add("긴급 중단 단축키 Ctrl+Alt+F12를 등록하지 못했습니다(다른 프로그램이 사용 중일 수 있음). 화면 아래 [긴급 중단] 버튼을 사용하세요.");
        }

        if (Options.StartTestTask)
        {
            _ = StartTestTaskAsync();
        }

        if (Options.SmokeTest)
        {
            var smoke = new SmokeTestRunner(_main, _context, window, presenter, Options, _log);
            var code = await smoke.RunAsync();
            window.Closing -= OnMainWindowClosing;
            window.Close();
            Shutdown(code);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Runner.StopAll("프로그램 종료");
        _hotkey?.Dispose();
        _log?.Info("app", $"exit code {e.ApplicationExitCode}");
        _context?.Dispose();
        _log?.Dispose();
        _lock?.Dispose();
        base.OnExit(e);
    }

    /// <summary>A long task on the simulated desktop, started at once (for the "killed while running" recovery test).</summary>
    private async Task StartTestTaskAsync()
    {
        await Task.Delay(500);
        var plan = PlanParser.Parse("""
            {"summary":"복구 시험용 작업","steps":[
              {"id":1,"action":"click","window":{"app":"test-target"},"element":{"automation_id":"IncrementButton"},"description":"카운트 증가(변경)"},
              {"id":2,"action":"wait","delay_ms":10000,"description":"긴 대기"},
              {"id":3,"action":"wait","delay_ms":10000,"description":"긴 대기"},
              {"id":4,"action":"wait","delay_ms":10000,"description":"긴 대기"},
              {"id":5,"action":"wait","delay_ms":10000,"description":"긴 대기"},
              {"id":6,"action":"wait","delay_ms":10000,"description":"긴 대기"},
              {"id":7,"action":"click","element":{"automation_id":"IncrementButton"},"description":"실행되지 않을 단계"}]}
            """).Plan!;
        await _main!.Runner.RunStaticPlanAsync(plan, "[복구 시험] 실행 중 강제 종료될 작업", TaskSource.Manual);
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_main is not { IsRunning: true })
        {
            return;
        }

        var answer = MessageBox.Show("작업이 실행 중입니다. 중단하고 종료할까요? (이미 실행된 변경은 기록에 '부분 완료'로 남습니다)", AppInfo.ProductName,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _main.Runner.StopAll("프로그램 종료");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (_main.IsRunning && DateTime.UtcNow < deadline)
        {
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(50);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("ui", "unhandled UI exception", e.Exception);
        if (Options.SmokeTest)
        {
            SmokeTestRunner.RecordException(e.Exception);
        }
        else
        {
            MessageBox.Show($"예상하지 못한 오류가 났지만 프로그램은 계속 실행됩니다: {Core.Security.Redactor.OneLine(e.Exception.Message, 200)}", AppInfo.ProductName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        e.Handled = true;
    }
}
