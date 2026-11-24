using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Iron.App.Presentation;
using Iron.App.Views;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Iron.App.Hosting;

/// <summary>
/// The notification area icon (its LED dot follows the status), its tooltip, and the menu (open, popup, USB release,
/// exit). Left click toggles the popup, double click opens the window.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly ShellViewModel _vm;
    private readonly Forms.NotifyIcon _icon;
    private readonly Dictionary<StatusKind, Drawing.Icon> _icons = [];
    private readonly Forms.ToolStripMenuItem _usbItem;

    public TrayIcon(ShellViewModel vm, Action open, Action togglePopup, Action exit)
    {
        _vm = vm;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("IRON 열기", null, (_, _) => open());
        menu.Items.Add("트레이 팝업", null, (_, _) => togglePopup());
        _usbItem = new Forms.ToolStripMenuItem("WireView USB 해제 (모의)", null, (_, _) => vm.ToggleUsb());
        menu.Items.Add(_usbItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("완전 종료", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                togglePopup();
            }
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                open();
            }
        };
        vm.PropertyChanged += OnChanged;
        vm.WireView.PropertyChanged += OnChanged;
        Update();
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnChanged;
        _vm.WireView.PropertyChanged -= OnChanged;
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.Status) or nameof(ShellViewModel.TrayToolTip) or nameof(WireViewViewModel.Port))
        {
            Update();
        }
    }

    private void Update()
    {
        _icon.Icon = IconFor(_vm.Status);
        var tip = _vm.TrayToolTip;
        _icon.Text = tip.Length <= 127 ? tip : tip[..127];
        _usbItem.Text = _vm.WireView.Port == PortState.Active ? "WireView USB 해제 (모의)" : "WireView 재개 (모의)";
    }

    /// <summary>The app icon drawn at the tray's size, with the LED dot in the status color.</summary>
    private Drawing.Icon IconFor(StatusKind status)
    {
        if (_icons.TryGetValue(status, out var cached))
        {
            return cached;
        }

        var size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        using var bitmap = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            var s = size / 32f;
            using var bezel = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x1F, 0x21, 0x1F));
            g.FillRectangle(bezel, 1 * s, 1 * s, 30 * s, 30 * s);
            using var orange = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xE0, 0x6D, 0x2B));
            using var amber = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xD4, 0x9A, 0x35));
            float[] heights = [8, 12, 16, 20];
            for (var i = 0; i < heights.Length; i++)
            {
                g.FillRectangle(i < 3 ? orange : amber, (5 + i * 6) * s, (27 - heights[i]) * s, 4 * s, heights[i] * s);
            }

            var led = status switch
            {
                StatusKind.Online => Drawing.Color.FromArgb(0x6F, 0x87, 0x43),
                StatusKind.Alert => Drawing.Color.FromArgb(0xC8, 0x3E, 0x2B),
                StatusKind.Loading => Drawing.Color.FromArgb(0xD4, 0x9A, 0x35),
                _ => Drawing.Color.FromArgb(0x66, 0x69, 0x63),
            };
            using var ledBrush = new Drawing.SolidBrush(led);
            g.FillEllipse(ledBrush, 21 * s, 3 * s, 9 * s, 9 * s);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Drawing.Icon.FromHandle(handle);
            var icon = (Drawing.Icon)temporary.Clone();
            _icons[status] = icon;
            return icon;
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }
}

/// <summary>The tray popup window: shown next to the taskbar of the monitor the pointer is on; hides when it loses focus.</summary>
internal sealed class TrayPopup : Window
{
    public TrayPopup(ShellViewModel vm, Action open)
    {
        var view = new TrayView { DataContext = vm };
        view.CloseRequested += (_, _) => Hide();
        view.OpenRequested += (_, _) =>
        {
            Hide();
            open();
        };
        Content = view;
        DataContext = vm;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = "IRON";
        Left = -32000;
        Top = -32000;
        Deactivated += (_, _) => Hide();
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        ShowNearTray();
    }

    public void ShowNearTray()
    {
        Show();
        UpdateLayout();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (Native.ForCursor() is { } m && Native.WindowRect(hwnd) is { } r)
        {
            const int margin = 12;
            var work = m.WorkArea;
            var bounds = m.Bounds;
            // The taskbar is where the work area stops short of the monitor; the popup sits in that corner.
            var x = work.Left > bounds.Left ? work.Left + margin : work.Right - r.Width - margin;
            var y = work.Top > bounds.Top ? work.Top + margin : work.Bottom - r.Height - margin;
            Native.Move(hwnd, x, y, r.Width, r.Height);
        }

        Activate();
    }
}
