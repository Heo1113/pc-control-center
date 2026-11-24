using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Iron.App.Hosting;
using Iron.App.Presentation;

namespace Iron.App;

/// <summary>
/// The IRON window. Two placements, both taken from the work area of the monitor the window is on at that moment (so
/// a taskbar, a second monitor or another DPI are followed, and no screen size is assumed): <see cref="WindowLayout.Full"/>
/// = maximized to exactly the work area (4K 175 %: 2194×1186 DIP), <see cref="WindowLayout.Half"/> = the left or right half
/// (1097×1186 DIP). Closing hides the window to the tray when the setting says so.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _vm;
    private WindowLayout _layout;
    private bool _halfOnRight = true;
    private bool _exiting;

    public MainWindow(ShellViewModel vm, WindowLayout layout)
    {
        InitializeComponent();
        _vm = vm;
        _layout = layout;
        DataContext = vm;
        Shell.DataContext = vm;
        Shell.TrayRequested += (_, _) => HideToTray();
        Shell.LayoutToggleRequested += (_, _) => ToggleLayout();
        Shell.CloseRequested += (_, _) => Close();
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(Handle)?.AddHook(WndProc);
            ApplyLayout();
        };
    }

    /// <summary>Raised when the window is hidden to the tray (the app may show a hint once).</summary>
    public event EventHandler? HiddenToTray;

    public nint Handle => new WindowInteropHelper(this).Handle;

    public WindowLayout Layout => _layout;

    /// <summary>Closes for real (tray menu "완전 종료"), skipping the close-to-tray rule.</summary>
    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        ApplyLayout();
        Activate();
    }

    public void HideToTray()
    {
        Hide();
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleLayout()
    {
        if (_layout == WindowLayout.Full && Native.ForWindow(Handle) is { } m && Native.WindowRect(Handle) is { } r)
        {
            // Half on the side the window is on now (right by default).
            _halfOnRight = (r.Left + r.Right) / 2 >= (m.WorkArea.Left + m.WorkArea.Right) / 2 || WindowState == WindowState.Maximized;
        }

        SetLayout(_layout == WindowLayout.Full ? WindowLayout.Half : WindowLayout.Full);
    }

    public void SetLayout(WindowLayout layout, bool? rightHalf = null)
    {
        _layout = layout;
        if (rightHalf is bool right)
        {
            _halfOnRight = right;
        }

        ApplyLayout();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _vm.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    private void ApplyLayout()
    {
        _vm.IsHalfWidth = _layout == WindowLayout.Half;
        if (Handle == 0)
        {
            return;
        }

        if (_layout == WindowLayout.Full)
        {
            // WM_GETMINMAXINFO (below) makes "maximized" exactly the work area of this window's monitor.
            WindowState = WindowState.Maximized;
            return;
        }

        if (Native.ForWindow(Handle) is not { } monitor)
        {
            return;
        }

        WindowState = WindowState.Normal;
        var work = monitor.WorkArea;
        var half = work.Width / 2;
        var x = _halfOnRight ? work.Left + work.Width - half : work.Left;
        Native.Move(Handle, x, work.Top, half, work.Height);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_GETMINMAXINFO:
                Native.FitMaximizedToWorkArea(hwnd, lParam);
                break;
            case Native.WM_NCLBUTTONDBLCLK when wParam == Native.HTCAPTION:
                // Double-clicking the title bar switches full ↔ half instead of maximize ↔ restore.
                ToggleLayout();
                handled = true;
                break;
            case Native.WM_DPICHANGED:
            case Native.WM_DISPLAYCHANGE:
            case Native.WM_SETTINGCHANGE when wParam == Native.SPI_SETWORKAREA:
                // Another monitor, DPI or taskbar: place the window again once Windows and WPF are done.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyLayout);
                break;
        }

        return 0;
    }
}
