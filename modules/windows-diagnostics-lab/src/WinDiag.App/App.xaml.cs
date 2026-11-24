using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LocalAiPerf.Core.Diagnostics;
using WinDiag.App.Services;
using WinDiag.Collection;
using WinDiag.Core;
using WinDiag.Core.Events;
using WinDiag.Core.Settings;
using WinDiag.Presentation;
using WinDiag.Presentation.ViewModels;
using WinDiag.Storage;

namespace WinDiag.App;

/// <summary>Application start and shutdown. Structure adapted from project A's App.xaml.cs (commit 5b6aae1).</summary>
public partial class App : Application, IAppHost
{
    private DataFolderLock? _singleInstance;
    private FileAppLogger? _fileLog;
    private MemoryLogSink? _log;
    private DiagnosticsEngine? _engine;
    private MainViewModel? _main;
    private DispatcherTimer? _timer;
    private TrayNotifier? _tray;
    private SmokeTestRunner? _smoke;
    private AppPaths? _paths;
    private bool _closing;

    internal StartupOptions Options { get; private set; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Options = StartupOptions.Parse(e.Args);
        try
        {
            _paths = AppPaths.Resolve(Options.DataDirectory, AppContext.BaseDirectory);
            _paths.EnsureDirectories();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"데이터 폴더를 준비할 수 없습니다: {ex.Message}", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        // One process per data folder (this app or wdl): see DataFolderLock. Released by the OS if the app is killed.
        _singleInstance = DataFolderLock.TryAcquire(_paths.DataDirectory);
        if (_singleInstance is null)
        {
            if (!Options.SmokeTest)
            {
                MessageBox.Show("같은 데이터 폴더를 쓰는 Windows Diagnostics Lab(또는 wdl 명령)이 이미 실행 중입니다.", "이미 실행 중", MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown(3);
            return;
        }

        _fileLog = new FileAppLogger(_paths.LogDirectory, filePrefix: AppInfo.ShortName);
        _log = new MemoryLogSink(500, _fileLog);
        _log.Info("app", $"시작 {AppInfo.Version}, 데이터 폴더 {(_paths.IsPortable ? "(포터블)" : _paths.DataDirectory)}, 관리자 권한 {Environment.IsPrivilegedProcess}");
        RegisterExceptionHandlers();
        WheelForwarding.Register();

        var settingsStore = new SettingsStore(_paths.SettingsPath);
        var loaded = settingsStore.Load();
        var settings = loaded.Settings;
        if (Options.HistoryDays is int days)
        {
            settings.Collection.HistoryDays = days;
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

        try
        {
            _engine = await DiagnosticsEngine.StartAsync(new EngineOptions
            {
                Paths = _paths,
                Settings = settings,
                UseMockSensors = Options.MockSensors,
                Log = _log,
            });
            if (Options.DemoData && _engine.Store.GetStats().VirtualEvents == 0)
            {
                await _engine.SeedVirtualScenarioAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Error("app", "수집 엔진 시작 실패", ex);
            MessageBox.Show($"수집을 시작하지 못했습니다: {ex.Message}", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(4);
            return;
        }

        var context = new ViewContext
        {
            Engine = _engine,
            SettingsStore = settingsStore,
            Ui = new WpfDispatcher(Dispatcher),
            Dialogs = dialogs,
            Shell = new ShellService(),
            Host = this,
            Log = _log,
        };
        _main = new MainViewModel(context);
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

        if (!Options.SmokeTest)
        {
            _tray = new TrayNotifier(() =>
            {
                window.Show();
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();
            });
            _main.NotificationRaised += n =>
            {
                if (_engine?.Settings.Notifications.TrayBalloon == true)
                {
                    _tray?.Show(n);
                }
            };
        }

        if (Options.SmokeTest)
        {
            var reportDir = Options.SmokeReportDirectory ?? Path.Combine(_paths.DataDirectory, "smoke");
            dialogs.UnattendedFolder = reportDir;
            _smoke = new SmokeTestRunner(window, _main, _engine, dialogs, reportDir);
            _smoke.Start();
        }
    }

    /// <summary>Restore: stop collection, replace the database (the current one is kept aside), start a new instance.</summary>
    public async Task RestoreAndRestartAsync(string backupPath)
    {
        if (_paths is null || _engine is null)
        {
            return;
        }

        _closing = true;
        _timer?.Stop();
        _main?.Dispose();
        await _engine.StopAsync();
        _engine = null;
        string? aside;
        try
        {
            aside = DiagnosticsStore.RestoreFrom(backupPath, _paths.DatabasePath);
        }
        catch (Exception ex)
        {
            _log?.Error("app", "복원 실패 (기존 데이터베이스를 되돌렸습니다)", ex);
            MessageBox.Show($"복원하지 못했습니다. 기존 데이터베이스는 그대로입니다.\n{ex.Message}\n\n프로그램을 다시 시작합니다.", "복원 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            aside = null;
        }

        _log?.Info("app", $"복원 완료, 이전 데이터베이스 보관: {(aside is null ? "없음" : Path.GetFileName(aside))}");
        ReleaseSingleInstance();
        if (Environment.ProcessPath is { } exe)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            {
                info.ArgumentList.Add(arg);
            }

            Process.Start(info)?.Dispose();
        }

        Shutdown(0);
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

        // Close the app run cleanly (end time, WAL checkpoint) before the process exits. Bounded: if storage hangs,
        // the next start closes the run from its last heartbeat.
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
        if (_engine is not null)
        {
            try
            {
                await _engine.StopAsync();
            }
            catch (Exception ex)
            {
                _log?.Error("app", "종료 처리 중 오류 (다음 실행에서 복구됩니다)", ex);
            }

            _engine = null;
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows logoff/shutdown: close the run synchronously, bounded in time.
        try
        {
            _engine?.StopAsync(RunEndReasons.WindowsSessionEnd).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("app", $"종료 (코드 {e.ApplicationExitCode})");
        _tray?.Dispose();
        _fileLog?.Dispose();
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    private void ReleaseSingleInstance()
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
    }

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _log?.Error("ui", "처리되지 않은 UI 예외", args.Exception);
            _smoke?.RecordException(args.Exception);
            if (!Options.SmokeTest)
            {
                MessageBox.Show($"예상하지 못한 오류가 발생했습니다. 수집은 계속됩니다.\n\n{args.Exception.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
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
