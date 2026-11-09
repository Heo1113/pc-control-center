using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using GpuProfit.App.Services;
using GpuProfit.Core;
using GpuProfit.Core.Settings;
using GpuProfit.Presentation;
using GpuProfit.Presentation.ViewModels;
using GpuProfit.Recording;
using GpuProfit.Storage;
using LocalAiPerf.Core.Diagnostics;
using Microsoft.Win32;

namespace GpuProfit.App;

/// <summary>Application start and shutdown. Structure adapted from project C's App.xaml.cs (commit 06cc9e5), itself from project A.</summary>
public partial class App : Application, IAppHost
{
    private DataFolderLock? _singleInstance;
    private FileAppLogger? _fileLog;
    private MemoryLogSink? _log;
    private ProfitEngine? _engine;
    private MainViewModel? _main;
    private DispatcherTimer? _timer;
    private SmokeTestRunner? _smoke;
    private AppPaths? _paths;
    private bool _closing;

    internal StartupOptions Options { get; private set; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WheelForwarding.Register();
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

        // One process per data folder (this app or gpc): see DataFolderLock. Released by the OS if the app is killed.
        _singleInstance = DataFolderLock.TryAcquire(_paths.DataDirectory);
        if (_singleInstance is null)
        {
            if (!Options.SmokeTest)
            {
                MessageBox.Show("같은 데이터 폴더를 쓰는 GPU Profit & Compute Center(또는 gpc 명령)가 이미 실행 중입니다.", "이미 실행 중", MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown(3);
            return;
        }

        _fileLog = new FileAppLogger(_paths.LogDirectory, filePrefix: AppInfo.ShortName);
        _log = new MemoryLogSink(500, _fileLog);
        _log.Info("app", $"시작 {AppInfo.Version}, 데이터 폴더 {(_paths.IsPortable ? "(포터블)" : "사용자 폴더")}, 관리자 권한 {Environment.IsPrivilegedProcess}");
        RegisterExceptionHandlers();

        var settingsStore = new SettingsStore(_paths.SettingsPath);
        var loaded = settingsStore.Load();
        var settings = loaded.Settings;
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
            _engine = await ProfitEngine.StartAsync(new EngineOptions
            {
                Paths = _paths,
                Settings = settings,
                UseMockSensors = Options.MockSensors,
                Log = _log,
            });
            if (Options.DemoData && !DemoDataSeeder.HasDemoData(_engine.Store))
            {
                var engine = _engine;
                await Task.Run(() => DemoDataSeeder.Seed(engine.Store, engine.Sessions, DateTimeOffset.UtcNow));
            }

            if (Options.StartTestSession)
            {
                var engine = _engine;
                await Task.Run(() =>
                {
                    const string name = "[테스트] 비정상 종료 복구 시험";
                    var workload = engine.Store.ListWorkloads().FirstOrDefault(w => w.Name == name)?.Id
                                   ?? engine.Store.AddWorkload(new GpuProfit.Core.Domain.Workload { Name = name, Type = GpuProfit.Core.Domain.WorkloadType.Other });
                    engine.Sessions.Start(workload, "[테스트] 강제 종료 전 기록", "--start-test-session으로 시작한 시험 세션입니다.");
                });
            }
        }
        catch (Exception ex)
        {
            _log.Error("app", "시작 실패", ex);
            MessageBox.Show($"시작하지 못했습니다: {SecretRedactor.Redact(ex.Message)}\n\n데이터는 변경하지 않았습니다.", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            ReleaseSingleInstance();
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
        ApplyFontScale(settings.Ui.FontScale);
        window.Closing += OnMainWindowClosing;
        window.Show();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _main?.Tick(), Dispatcher);
        _timer.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (Options.SmokeTest)
        {
            var reportDir = Options.SmokeReportDirectory ?? Path.Combine(_paths.DataDirectory, "smoke");
            Directory.CreateDirectory(reportDir);
            dialogs.UnattendedFolder = reportDir;
            _smoke = new SmokeTestRunner(window, _main, _engine, dialogs, reportDir);
            _smoke.Start();
        }
    }

    public void ApplyFontScale(double scale)
    {
        if (MainWindow is MainWindow window)
        {
            window.ApplyScale(scale);
        }
    }

    /// <summary>Windows sleep and resume: the time in between is recorded as a gap and never counted as energy.</summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        try
        {
            if (e.Mode == PowerModes.Suspend)
            {
                _engine?.NotifySuspend();
                _log?.Info("app", "Windows 절전 진입");
            }
            else if (e.Mode == PowerModes.Resume)
            {
                _engine?.NotifyResume();
                _log?.Info("app", "Windows 절전 복귀");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Warn("app", "절전 상태 기록 실패", ex);
        }
    }

    /// <summary>Restore: stop recording, replace the database (the current one is kept aside), start a new instance.</summary>
    public async Task RestoreAndRestartAsync(string backupPath)
    {
        if (_paths is null || _engine is null)
        {
            return;
        }

        _closing = true;
        _timer?.Stop();
        _main?.Dispose();
        await _engine.StopAsync(RunEndReasons.Restore);
        _engine = null;
        string? aside;
        try
        {
            aside = ProfitStore.RestoreFrom(backupPath, _paths.DatabasePath);
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

        // Pause running sessions and close the app run cleanly (end time, WAL checkpoint) before the process exits.
        // Bounded: if storage hangs, the next start closes the run from its last sample.
        e.Cancel = true;
        _closing = true;
        try
        {
            await Task.WhenAny(ShutdownServicesAsync(RunEndReasons.Normal), Task.Delay(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Shutdown(_smoke?.ExitCode ?? 0);
        }
    }

    internal async Task ShutdownServicesAsync(string reason)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer?.Stop();
        _main?.Dispose();
        if (_engine is not null)
        {
            try
            {
                await _engine.StopAsync(reason);
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
        // Windows logoff/shutdown: pause sessions and close the run synchronously, bounded in time.
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
                MessageBox.Show($"예상하지 못한 오류가 발생했습니다. 기록은 계속됩니다.\n\n{SecretRedactor.Redact(args.Exception.Message)}", "오류", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
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
