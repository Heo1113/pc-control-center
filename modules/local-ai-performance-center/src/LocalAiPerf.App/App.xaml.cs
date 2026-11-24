using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using LocalAiPerf.App.Services;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Presentation.ViewModels;
using LocalAiPerf.Recording;

namespace LocalAiPerf.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private FileAppLogger? _fileLog;
    private MemoryLogSink? _log;
    private MonitoringService? _service;
    private MainViewModel? _main;
    private DispatcherTimer? _timer;
    private SmokeTestRunner? _smoke;
    private bool _closing;

    internal StartupOptions Options { get; private set; } = new();

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
        catch (Exception ex)
        {
            MessageBox.Show($"데이터 폴더를 준비할 수 없습니다: {ex.Message}", "Local AI Performance Center", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        // One instance per data folder: two recorders on one database would duplicate data.
        var mutexName = "Local\\LocalAiPerformanceCenter-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataDirectory.ToUpperInvariant())))[..16];
        _singleInstance = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            if (!Options.SmokeTest)
            {
                MessageBox.Show("Local AI Performance Center가 이미 실행 중입니다 (같은 데이터 폴더).", "이미 실행 중", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(3);
            return;
        }

        _fileLog = new FileAppLogger(paths.LogDirectory);
        _log = new MemoryLogSink(500, _fileLog);
        _log.Info("app", $"시작 {AppInfo.Version}, 데이터 폴더 {(paths.IsPortable ? "(포터블)" : paths.DataDirectory)}, 관리자 권한 {Environment.IsPrivilegedProcess}");
        RegisterExceptionHandlers();

        var settingsStore = new SettingsStore(paths.SettingsPath);
        var loaded = settingsStore.Load();
        var settings = loaded.Settings;
        if (Options.StrataUrl is string url)
        {
            settings.Strata.BaseUrl = url;
            settings.Strata.Enabled = true;
        }

        settings.Normalize();
        if (loaded.WasCreated && !Options.SmokeTest)
        {
            TrySave(settingsStore, settings);
        }

        var dialogs = new WpfDialogService(() => MainWindow);
        if (Options.SmokeTest)
        {
            dialogs.Unattended = true;
        }

        _service = new MonitoringService(paths, settings, new MonitoringOptions
        {
            UseMockSensors = Options.MockSensors,
            AutoRecord = Options.NoAutoRecord ? false : null,
        }, _log);
        foreach (var note in loaded.Notes)
        {
            _log.Warn("settings", note);
        }

        try
        {
            await _service.StartAsync();
        }
        catch (Exception ex)
        {
            _log.Error("app", "모니터링 시작 실패", ex);
            MessageBox.Show($"모니터링을 시작하지 못했습니다: {ex.Message}", "Local AI Performance Center", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(4);
            return;
        }

        _main = new MainViewModel(_service, settingsStore, new WpfDispatcher(Dispatcher), dialogs, new ShellService(), _log);
        foreach (var note in loaded.Notes)
        {
            _main.Notices.Add("설정: " + note);
        }

        var window = new MainWindow { DataContext = _main };
        MainWindow = window;
        window.Closing += OnMainWindowClosing;
        window.Show();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _main?.Tick(), Dispatcher);
        _timer.Start();

        if (Options.SmokeTest)
        {
            _smoke = new SmokeTestRunner(window, _main, _service, dialogs, Options.SmokeReportDirectory ?? Path.Combine(paths.DataDirectory, "smoke"));
            _smoke.Start();
        }
    }

    private static void TrySave(SettingsStore store, AppSettings settings)
    {
        try
        {
            store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The defaults still apply for this run.
        }
    }

    private async void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        // Finish the session cleanly (end time, WAL checkpoint) before the process exits.
        // Bounded: if storage hangs, the next start recovers the open session from its last sample.
        e.Cancel = true;
        _closing = true;
        try
        {
            await Task.WhenAny(ShutdownServicesAsync(), Task.Delay(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Shutdown(_smoke?.ExitCode ?? 0);
        }
    }

    internal async Task ShutdownServicesAsync()
    {
        _timer?.Stop();
        _main?.Dispose();
        if (_service is not null)
        {
            try
            {
                await _service.DisposeAsync();
            }
            catch (Exception ex)
            {
                _log?.Error("app", "종료 처리 중 오류 (다음 실행에서 세션이 복구됩니다)", ex);
            }

            _service = null;
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows logoff/shutdown: close the session synchronously, bounded in time.
        try
        {
            _service?.StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("app", $"종료 (코드 {e.ApplicationExitCode})");
        _fileLog?.Dispose();
        if (_singleInstance is not null)
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }

        base.OnExit(e);
    }

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _log?.Error("ui", "처리되지 않은 UI 예외", args.Exception);
            _smoke?.RecordException(args.Exception);
            if (!Options.SmokeTest)
            {
                MessageBox.Show($"예상하지 못한 오류가 발생했습니다. 기록은 계속됩니다.\n\n{args.Exception.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                _log?.Error("app", "처리되지 않은 예외 (앱 종료)", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.Error("app", "관찰되지 않은 작업 예외", args.Exception);
            _smoke?.RecordException(args.Exception);
            args.SetObserved();
        };
    }
}
