using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using static WindowsAiOperator.Automation.Windows.NativeMethods;

namespace WindowsAiOperator.Automation.Windows;

/// <summary>Process name, path and integrity level by process id, cached briefly.</summary>
internal sealed class ProcessInfoCache
{
    private readonly ConcurrentDictionary<uint, (string Name, string? Path, bool HigherIntegrity, DateTime At)> _cache = new();
    private readonly int? _ownIntegrity = IntegrityLevel(GetCurrentProcess());

    public (string Name, string? Path, bool HigherIntegrity) Get(uint processId)
    {
        if (_cache.TryGetValue(processId, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(10))
        {
            return (cached.Name, cached.Path, cached.HigherIntegrity);
        }

        string name = "unknown";
        string? path = null;
        var higher = true;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var builder = new StringBuilder(1024);
                var size = builder.Capacity;
                if (QueryFullProcessImageName(handle, 0, builder, ref size))
                {
                    path = builder.ToString();
                    name = Path.GetFileNameWithoutExtension(path);
                }

                var integrity = IntegrityLevel(handle);
                higher = integrity is null || (_ownIntegrity is { } own && integrity.Value > own);
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        if (name == "unknown")
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                name = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        _cache[processId] = (name, path, higher, DateTime.UtcNow);
        return (name, path, higher);
    }
}

/// <summary>Top-level windows through Win32: titles, owners, bounds (DWM frame), DPI, cloaking, UWP host resolution.</summary>
internal static class WindowEnumerator
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "IME", "MSCTFIME UI",
        "tooltips_class32", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow",
    };

    public static List<WindowInfo> List(ProcessInfoCache processes)
    {
        var handles = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            handles.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        var foreground = GetForegroundWindow();
        var result = new List<WindowInfo>();
        foreach (var handle in handles)
        {
            var info = Describe(handle, processes, foreground, requireListable: true);
            if (info is not null)
            {
                result.Add(info);
            }
        }

        return result;
    }

    public static WindowInfo? Describe(IntPtr handle, ProcessInfoCache processes, IntPtr? foreground = null, bool requireListable = false)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            return null;
        }

        var className = ClassName(handle);
        if (requireListable)
        {
            if (!IsWindowVisible(handle) || ShellClasses.Contains(className) || IsCloaked(handle))
            {
                return null;
            }

            var exStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            if ((exStyle & WsExToolWindow) != 0)
            {
                return null;
            }

            if (GetAncestor(handle, GaRootOwner) != handle && WindowText(handle).Length == 0)
            {
                return null;
            }
        }

        var title = WindowText(handle);
        if (requireListable && title.Length == 0)
        {
            return null;
        }

        GetWindowThreadProcessId(handle, out var pid);
        if (className == "ApplicationFrameWindow")
        {
            pid = HostedAppProcess(handle, pid);
        }

        var (name, path, higher) = processes.Get(pid);
        var bounds = Bounds(handle);
        var fg = foreground ?? GetForegroundWindow();
        return new WindowInfo
        {
            Handle = handle.ToInt64(),
            ProcessId = (int)pid,
            ProcessName = name,
            ExecutablePath = path,
            Title = title,
            ClassName = className,
            Bounds = bounds,
            Dpi = (int)Math.Max(96, GetDpiForWindow(handle)),
            IsVisible = IsWindowVisible(handle),
            IsMinimized = IsIconic(handle),
            IsForeground = fg == handle,
            IsElevatedOrProtected = higher,
        };
    }

    public static PixelRect Bounds(IntPtr handle)
    {
        if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out Rect frame, Marshal.SizeOf<Rect>()) == 0 && frame.Right > frame.Left)
        {
            return new PixelRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        }

        return GetWindowRect(handle, out var rect)
            ? new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : default;
    }

    private static bool IsCloaked(IntPtr handle) =>
        DwmGetWindowAttribute(handle, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>UWP apps are framed by ApplicationFrameHost; the app itself owns the CoreWindow child.</summary>
    private static uint HostedAppProcess(IntPtr frame, uint framePid)
    {
        var hosted = framePid;
        EnumChildWindows(frame, (child, _) =>
        {
            if (ClassName(child) == "Windows.UI.Core.CoreWindow")
            {
                GetWindowThreadProcessId(child, out var childPid);
                if (childPid != framePid)
                {
                    hosted = childPid;
                    return false;
                }
            }

            return true;
        }, IntPtr.Zero);
        return hosted;
    }
}

/// <summary>Keyboard and mouse through SendInput. Text is typed as Unicode characters (IME independent).</summary>
internal static class InputSender
{
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkReturn = 0x0D;
    private const ushort VkTab = 0x09;

    private static readonly Dictionary<string, ushort> Keys = new(StringComparer.Ordinal)
    {
        ["Enter"] = VkReturn, ["Escape"] = 0x1B, ["Tab"] = VkTab, ["Space"] = 0x20, ["Backspace"] = 0x08, ["Delete"] = 0x2E,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Up"] = 0x26, ["Down"] = 0x28,
        ["Left"] = 0x25, ["Right"] = 0x27, ["Insert"] = 0x2D,
    };

    private static readonly HashSet<ushort> Extended = [0x2E, 0x24, 0x23, 0x21, 0x22, 0x26, 0x28, 0x25, 0x27, 0x2D];

    public static void Hotkey(Hotkey hotkey)
    {
        var key = VirtualKey(hotkey.Key);
        var inputs = new List<Input>();
        var modifiers = new List<ushort>();
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Ctrl))
        {
            modifiers.Add(VkControl);
        }

        if (hotkey.Modifiers.HasFlag(KeyModifiers.Shift))
        {
            modifiers.Add(VkShift);
        }

        if (hotkey.Modifiers.HasFlag(KeyModifiers.Alt))
        {
            modifiers.Add(VkMenu);
        }

        inputs.AddRange(modifiers.Select(m => KeyInput(m, false)));
        inputs.Add(KeyInput(key, false));
        inputs.Add(KeyInput(key, true));
        inputs.AddRange(modifiers.AsEnumerable().Reverse().Select(m => KeyInput(m, true)));
        Send(inputs);
    }

    public static void TypeText(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var batch = new List<Input>();
        foreach (var ch in normalized)
        {
            if (ch == '\n')
            {
                batch.Add(KeyInput(VkReturn, false));
                batch.Add(KeyInput(VkReturn, true));
            }
            else if (ch == '\t')
            {
                batch.Add(KeyInput(VkTab, false));
                batch.Add(KeyInput(VkTab, true));
            }
            else
            {
                batch.Add(UnicodeInput(ch, false));
                batch.Add(UnicodeInput(ch, true));
            }

            if (batch.Count >= 64)
            {
                Send(batch);
                batch.Clear();
                Thread.Sleep(5);
            }
        }

        if (batch.Count > 0)
        {
            Send(batch);
        }
    }

    public static void ClickAt(PixelPoint point)
    {
        SetCursorPos(point.X, point.Y);
        Thread.Sleep(30);
        Send([MouseInput(MouseeventfLeftDown, 0), MouseInput(MouseeventfLeftUp, 0)]);
    }

    public static void Wheel(PixelPoint point, int notches, bool horizontal)
    {
        SetCursorPos(point.X, point.Y);
        Thread.Sleep(20);
        Send([MouseInput(horizontal ? MouseeventfHWheel : MouseeventfWheel, notches * 120)]);
    }

    /// <summary>A bare Alt tap: Windows then lets this process move the foreground (documented SetForegroundWindow rule).</summary>
    public static void TapAlt() => Send([KeyInput(VkMenu, false), KeyInput(VkMenu, true)]);

    private static ushort VirtualKey(string key)
    {
        if (Keys.TryGetValue(key, out var vk))
        {
            return vk;
        }

        if (key.Length == 1 && (char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0])))
        {
            return key[0];
        }

        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 12)
        {
            return (ushort)(0x70 + f - 1);
        }

        throw new ArgumentException($"지원하지 않는 키: {key}");
    }

    private static Input KeyInput(ushort vk, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = vk,
                Flags = (up ? KeyeventfKeyUp : 0) | (Extended.Contains(vk) ? KeyeventfExtendedKey : 0),
            },
        },
    };

    private static Input UnicodeInput(char ch, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = ch, Flags = KeyeventfUnicode | (up ? KeyeventfKeyUp : 0) } },
    };

    private static Input MouseInput(uint flags, int data) => new()
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new NativeMethods.MouseInput { Flags = flags, MouseData = data } },
    };

    private static void Send(IList<Input> inputs)
    {
        var array = inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, Marshal.SizeOf<Input>());
        if (sent != array.Length)
        {
            throw new InvalidOperationException($"SendInput이 입력 {array.Length}개 중 {sent}개만 보냈습니다(다른 프로그램이 입력을 막았을 수 있음, 오류 {Marshal.GetLastWin32Error()}).");
        }
    }
}
