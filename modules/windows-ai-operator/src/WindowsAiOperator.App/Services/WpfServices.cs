using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Settings;
using WindowsAiOperator.Presentation;
using WindowsAiOperator.Presentation.ViewModels;

namespace WindowsAiOperator.App.Services;

public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
    }
}

public sealed class WpfDialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } main ? main : null;

    public bool Confirm(string title, string message) =>
        MessageBox.Show(OwnerOrDesktop(), message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void Info(string title, string message) => MessageBox.Show(OwnerOrDesktop(), message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string title, string message) => MessageBox.Show(OwnerOrDesktop(), message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? OpenFile(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    private static Window OwnerOrDesktop() => Owner ?? new Window { Visibility = Visibility.Hidden };
}

/// <summary>Approval and plan-review windows. They stay on top of the target app, so the user always sees them.</summary>
public sealed class ApprovalPresenter : IApprovalPresenter
{
    private readonly Dictionary<object, ApprovalWindow> _windows = [];

    /// <summary>Raised when a window is shown (the smoke test answers through it).</summary>
    public event Action<object, Window>? Shown;

    public void ShowApproval(ApprovalViewModel approval) => Show(approval);

    public void ShowPlanReview(PlanReviewViewModel review) => Show(review);

    public void Close(object viewModel)
    {
        if (_windows.Remove(viewModel, out var window))
        {
            window.CloseFromCode();
        }
    }

    private void Show(object viewModel)
    {
        if (_windows.ContainsKey(viewModel))
        {
            return;
        }

        var window = new ApprovalWindow(viewModel);
        _windows[viewModel] = window;
        window.Show();
        window.Activate();
        Shown?.Invoke(viewModel, window);
    }
}

/// <summary>Red rectangle drawn over a UI element for two seconds (inspector). Click-through, never activated.</summary>
public sealed class OverlayHighlighter : IHighlighter
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    public void Highlight(PixelRect rectangle)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            IsHitTestVisible = false,
            Width = 10,
            Height = 10,
            Content = new System.Windows.Controls.Border { BorderBrush = Brushes.Red, BorderThickness = new Thickness(3) },
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            SetWindowLongPtr(hwnd, GwlExStyle, (IntPtr)(GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() | WsExTransparent | WsExToolWindow | WsExNoActivate));
            SetWindowPos(hwnd, HwndTopmost, rectangle.Left - 3, rectangle.Top - 3, rectangle.Width + 6, rectangle.Height + 6, SwpNoActivate);
        };
        window.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.Close();
        };
        timer.Start();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}

/// <summary>Global emergency-stop hotkey (Ctrl+Alt+F12) through RegisterHotKey on the main window.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int Id = 0x5741;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF12 = 0x7B;
    private HwndSource? _source;
    private Action? _callback;

    public bool Register(Window window, Action callback)
    {
        _callback = callback;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(Hook);
        return RegisterHotKey(handle, Id, ModControl | ModAlt | ModNoRepeat, VkF12);
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            UnregisterHotKey(_source.Handle, Id);
            _source.RemoveHook(Hook);
            _source = null;
        }
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id)
        {
            _callback?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>
/// API keys encrypted with Windows DPAPI for the current user (secrets.dat). Only this Windows account on this PC can
/// decrypt them; the values never appear in settings.json, the database, logs or prompts.
/// </summary>
public sealed class DpapiSecretStore(string path, IOperatorLog log) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsAiOperator/secrets/v1");
    private readonly Lock _gate = new();

    public bool IsAvailable => OperatingSystem.IsWindows();

    public string? Get(string key)
    {
        lock (_gate)
        {
            var map = Load();
            if (!map.TryGetValue(key, out var protectedValue))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                log.Warn("secrets", "stored key could not be decrypted (another user or PC?)");
                return null;
            }
        }
    }

    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            var map = Load();
            if (string.IsNullOrEmpty(value))
            {
                map.Remove(key);
            }
            else
            {
                map[key] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(map));
            File.Move(temp, path, overwrite: true);
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            log.Warn("secrets", "secrets file unreadable", ex);
            return [];
        }
    }
}

public sealed class AppHost(IOperatorLog log) : IAppHost
{
    public void ApplyFontScale(double scale)
    {
        if (Application.Current?.MainWindow is MainWindow main)
        {
            main.ApplyScale(scale);
        }
    }

    public void ApplyTheme(string theme)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "System" => ThemeMode.System,
            _ => ThemeMode.Dark,
        };
    }

    public void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Warn("ui", "open folder failed", ex);
        }
    }

    public void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null)
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        }

        Application.Current.Shutdown();
    }
}
