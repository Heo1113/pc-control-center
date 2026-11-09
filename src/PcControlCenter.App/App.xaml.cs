using System.IO;
using System.Windows;
using System.Windows.Threading;
using LocalAiPerf.Core.Diagnostics;
using Microsoft.Win32;
using PcControlCenter.App.Hosting;
using PcControlCenter.Core;
using PcControlCenter.Modules;
using PcControlCenter.Presentation;

namespace PcControlCenter.App;

/// <summary>
/// Start and shutdown of the integrated app. Structure follows the original programs' App.xaml.cs (A, B, C): data
/// folder, single instance, log, engines, window, one-second timer, sleep/resume, bounded clean shutdown.
/// </summary>
public partial class App : Application
{
    private ShellRuntime? _runtime;
    private ShellViewModel? _shell;
    private DispatcherTimer? _timer;
    private SmokeTestRunner? _smoke;
    private LocalAiBinder? _localAi;
    private GpuEconomicsBinder? _economics;
    private DiagnosticsBinder? _diagnostics;
    private bool _closing;

    internal StartupOptions Options { get; private set; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Options = StartupOptions.Parse(e.Args);
        GpuProfit.App.Services.WheelForwarding.Register();
        try
        {
            _runtime = ShellRuntime.Create(new ShellOptions
            {
                DataDirectory = Options.DataDirectory,
                ExecutableDirectory = AppContext.BaseDirectory,
                MockSensors = Options.MockSensors,
                DemoData = Options.DemoData,
                Unattended = Options.SmokeTest,
            });
        }
        catch (ShellStartException ex)
        {
            if (!(Options.SmokeTest && ex.Problem == ShellStartProblem.AlreadyRunning))
            {
                MessageBox.Show(ex.Message, AppInfo.ProductName, MessageBoxButton.OK,
                    ex.Problem == ShellStartProblem.AlreadyRunning ? MessageBoxImage.Information : MessageBoxImage.Error);
            }

            Shutdown(ex.Problem == ShellStartProblem.AlreadyRunning ? 3 : 2);
            return;
        }

        var log = _runtime.Log;
        RegisterExceptionHandlers(log);
        var dialogs = new ShellDialogs(() => MainWindow);
        if (Options.SmokeTest)
        {
            dialogs.Unattended = true;
        }

        var dispatcher = new WpfShellDispatcher(Dispatcher);
        _shell = new ShellViewModel(_runtime, dispatcher, dialogs);
        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;
        window.ApplyScale(_runtime.Settings.Ui.FontScale);
        _shell.Settings.Saved += s => window.ApplyScale(s.Ui.FontScale);
        window.Closing += OnMainWindowClosing;

        var reportDirectory = Options.SmokeReportDirectory ?? _runtime.Paths.SmokeDirectory;
        _localAi = new LocalAiBinder(_shell, _runtime.LocalAi, Dispatcher, () => MainWindow, Options.SmokeTest, log);
        _economics = new GpuEconomicsBinder(_shell, _runtime.Economics, Dispatcher, () => MainWindow, Options.SmokeTest, reportDirectory, window.ApplyScale, log);
        _diagnostics = new DiagnosticsBinder(_shell, _runtime.Diagnostics, Dispatcher, () => MainWindow, Options.SmokeTest, reportDirectory, log);
        window.Show();
        if (Options.StartSection is { } section)
        {
            _shell.Navigate(section);
        }

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _shell?.Tick(), Dispatcher);
        _timer.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Modules start after the window is up: a slow or failing module never keeps the window from opening.
        await _runtime.StartAsync();

        if (Options.SmokeTest)
        {
            Directory.CreateDirectory(reportDirectory);
            _smoke = new SmokeTestRunner(window, _shell, _runtime, _localAi, _economics, _diagnostics, dialogs, reportDirectory);
            _smoke.Start();
        }
    }

    /// <summary>Windows sleep and resume: B records the time in between as a gap and never counts it as energy.</summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        try
        {
            if (e.Mode == PowerModes.Suspend)
            {
                _economics?.NotifySuspend();
                _runtime?.Log.Info("app", "Windows 절전 진입");
            }
            else if (e.Mode == PowerModes.Resume)
            {
                _economics?.NotifyResume();
                _runtime?.Log.Info("app", "Windows 절전 복귀");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runtime?.Log.Warn("app", "절전 상태 기록 실패", ex);
        }
    }

    private async void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        // Close every module cleanly (sessions paused or closed, runs ended, WAL checkpoint) before the process exits.
        // Bounded: if one module hangs, the next start recovers it from its last sample, as in each original program.
        e.Cancel = true;
        _closing = true;
        try
        {
            await Task.WhenAny(ShutdownAsync(), Task.Delay(TimeSpan.FromSeconds(20)));
        }
        finally
        {
            Shutdown(_smoke?.ExitCode ?? 0);
        }
    }

    private async Task ShutdownAsync()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer?.Stop();
        _localAi?.Dispose();
        _economics?.Dispose();
        _diagnostics?.Dispose();
        if (_runtime is not null)
        {
            try
            {
                await _runtime.DisposeAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _runtime.Log.Error("app", "종료 처리 중 오류 (다음 실행에서 복구됩니다)", ex);
            }

            _runtime = null;
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows logoff/shutdown: stop the modules synchronously, bounded in time.
        try
        {
            _runtime?.StopAsync().Wait(TimeSpan.FromSeconds(8));
        }
        catch (AggregateException)
        {
        }

        base.OnSessionEnding(e);
    }

    private void RegisterExceptionHandlers(IAppLogger log)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            log.Error("ui", "처리되지 않은 UI 예외", args.Exception);
            _smoke?.RecordException(args.Exception);
            if (!Options.SmokeTest)
            {
                MessageBox.Show($"예상하지 못한 오류가 발생했습니다. 기록은 계속됩니다.\n\n{SecretRedactor.Redact(args.Exception.Message)}", "오류", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                log.Error("app", "처리되지 않은 예외 (앱 종료)", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.Error("app", "관찰되지 않은 작업 예외", args.Exception);
            _smoke?.RecordException(args.Exception);
            args.SetObserved();
        };
    }
}

internal sealed class WpfShellDispatcher(Dispatcher dispatcher) : IShellDispatcher
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
