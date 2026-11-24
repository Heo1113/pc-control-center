using System.Runtime.InteropServices;

namespace Iron.App.Hosting;

/// <summary>Monitor work areas in physical pixels (the window follows the monitor it is on; no screen size is assumed).</summary>
internal static partial class Native
{
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_NCLBUTTONDBLCLK = 0x00A3;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_DPICHANGED = 0x02E0;
    public const int SPI_SETWORKAREA = 0x002F;
    public const int HTCAPTION = 2;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public readonly record struct Monitor(RECT Bounds, RECT WorkArea);

    public static Monitor? ForWindow(nint hwnd) => Info(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    public static Monitor? ForCursor() => GetCursorPos(out var p) ? Info(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST)) : null;

    public static bool Move(nint hwnd, int x, int y, int width, int height) => SetWindowPos(hwnd, 0, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    public static RECT? WindowRect(nint hwnd) => GetWindowRect(hwnd, out var r) ? r : null;

    /// <summary>Maximized = exactly the work area of the window's monitor (not the monitor, not past the taskbar).</summary>
    public static void FitMaximizedToWorkArea(nint hwnd, nint lParam)
    {
        if (ForWindow(hwnd) is not { } m)
        {
            return;
        }

        var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        info.ptMaxPosition.X = m.WorkArea.Left - m.Bounds.Left;
        info.ptMaxPosition.Y = m.WorkArea.Top - m.Bounds.Top;
        info.ptMaxSize.X = m.WorkArea.Width;
        info.ptMaxSize.Y = m.WorkArea.Height;
        Marshal.StructureToPtr(info, lParam, false);
    }

    private static Monitor? Info(nint monitor)
    {
        if (monitor == 0)
        {
            return null;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfoW(monitor, ref info) ? new Monitor(info.rcMonitor, info.rcWork) : null;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(POINT pt, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);
}
