using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Iron.App.Hosting;
using Iron.App.Presentation;

namespace Iron.App;

/// <summary>
/// IRON Stage 1: the real window, tray icon and tray popup over simulated readings. One instance per user session (a
/// second start brings the first window forward). No sensor, USB device, registry key or file is touched.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\IRON.Iron.App.Stage1";

    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private TrayIcon? _tray;
    private TrayPopup? _popup;
    private MainWindow? _window;
    private DispatcherTimer? _timer;

    public App()
    {
        // Stage 1 writes no files; only the CI modes (--render-screens / --smoke-test DIR) leave crash.txt in their folder.
        DispatcherUnhandledException += (_, e) =>
        {
            Report(e.Exception);
            e.Handled = true;
            Shutdown(5);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
    }

    private static void Report(Exception? ex)
    {
        var text = $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}";
        var args = Environment.GetCommandLineArgs();
        var at = Array.FindIndex(args, a => a is "--render-screens" or "--smoke-test");
        if (at >= 0 && at + 1 < args.Length)
        {
            try
            {
                System.IO.Directory.CreateDirectory(args[at + 1]);
                System.IO.File.WriteAllText(System.IO.Path.Combine(args[at + 1], "crash.txt"), text);
            }
            catch (Exception io) when (io is System.IO.IOException or UnauthorizedAccessException)
            {
            }

            return;
        }

        MessageBox.Show("IRON을 계속 실행할 수 없는 오류가 발생했습니다." + Environment.NewLine + Environment.NewLine + ex?.GetType().Name + ": " + ex?.Message,
            "IRON", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = AppOptions.Parse(e.Args);
        if (options.Errors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, options.Errors) + Environment.NewLine + Environment.NewLine + AppOptions.Usage,
                "IRON — 옵션 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }

        if (options.RenderScreensTo is not null)
        {
            try
            {
                Shutdown(ScreenRenderer.Run(options));
            }
            catch (Exception ex)
            {
                Report(ex);
                Shutdown(5);
            }

            return;
        }

        if (options.SmokeTestTo is null && !TakeInstance())
        {
            Shutdown(0);
            return;
        }

        var scheduler = new DispatcherScheduler(Dispatcher);
        var mock = new MockTelemetryProvider(jitter: !options.NoJitter);
        mock.State.Apply(options.Preset);
        var vm = new ShellViewModel(mock, scheduler) { Tab = options.Tab };
        if (options.UsbReleased)
        {
            vm.WireView.SetPortImmediately(PortState.Released);
        }

        vm.Tick();

        _window = new MainWindow(vm, options.Layout);
        MainWindow = _window;
        _popup = new TrayPopup(vm, ShowMain);
        _tray = new TrayIcon(vm, ShowMain, () => _popup.Toggle(), () => ExitApp(0));

        _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(vm.IntervalMs.Value) };
        _timer.Tick += (_, _) => vm.Tick();
        vm.IntervalChanged += interval => _timer.Interval = interval;
        _timer.Start();

        if (!options.StartInTray || options.SmokeTestTo is not null)
        {
            _window.Show();
        }

        if (options.SmokeTestTo is { } dir)
        {
            new SmokeRunner(_window, vm, _popup, dir, ExitApp).Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _tray?.Dispose();
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        if (_instance is not null)
        {
            _instance.ReleaseMutex();
            _instance.Dispose();
        }

        base.OnExit(e);
    }

    private void ShowMain() => _window?.ShowFromTray();

    private void ExitApp(int code)
    {
        _popup?.Close();
        _window?.CloseForExit();
        Shutdown(code);
    }

    /// <summary>First instance: keeps a mutex and waits for "show" signals. Later instances signal and quit.</summary>
    private bool TakeInstance()
    {
        _instance = new Mutex(initiallyOwned: true, InstanceName, out var created);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!created)
        {
            _showSignal.Set();
            _instance.Dispose();
            _instance = null;
            return false;
        }

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowMain), null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }
}
