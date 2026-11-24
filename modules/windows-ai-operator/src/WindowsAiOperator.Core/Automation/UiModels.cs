namespace WindowsAiOperator.Core.Automation;

/// <summary>A rectangle in physical screen pixels (the process is per-monitor DPI aware, so UIA and Win32 agree).</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PixelPoint Center => new(Left + Width / 2, Top + Height / 2);

    public bool Contains(PixelPoint point) =>
        !IsEmpty && point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    public override string ToString() => $"({Left},{Top}) {Width}x{Height}";
}

public readonly record struct PixelPoint(int X, int Y)
{
    public override string ToString() => $"({X},{Y})";
}

/// <summary>Top-level window as seen at one moment. Handles are plain numbers so the core stays platform-neutral.</summary>
public sealed record WindowInfo
{
    public required long Handle { get; init; }
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public string? ExecutablePath { get; init; }
    public required string Title { get; init; }
    public string ClassName { get; init; } = string.Empty;
    public PixelRect Bounds { get; init; }
    public int Dpi { get; init; } = 96;
    public bool IsVisible { get; init; } = true;
    public bool IsMinimized { get; init; }
    public bool IsForeground { get; init; }

    /// <summary>The process runs with higher integrity (e.g. as administrator) or cannot be opened: UIA input is blocked by Windows (UIPI).</summary>
    public bool IsElevatedOrProtected { get; init; }

    public double Scale => Dpi / 96.0;

    public string Describe() => $"{ProcessName}.exe (PID {ProcessId}) '{Title}'";
}

[Flags]
public enum UiPatterns
{
    None = 0,
    Invoke = 1 << 0,
    Value = 1 << 1,
    Toggle = 1 << 2,
    SelectionItem = 1 << 3,
    ExpandCollapse = 1 << 4,
    Scroll = 1 << 5,
    Text = 1 << 6,
    Selection = 1 << 7,
    RangeValue = 1 << 8,
    Window = 1 << 9,
    ScrollItem = 1 << 10,
    LegacyIAccessible = 1 << 11,
}

public enum ToggleValue
{
    Off,
    On,
    Indeterminate,
}

public enum ExpandValue
{
    Collapsed,
    Expanded,
    PartiallyExpanded,
    LeafNode,
}

/// <summary>One UI Automation element, read at one moment. Values of password fields are never read.</summary>
public sealed record ElementInfo
{
    public required string RuntimeId { get; init; }
    public required long WindowHandle { get; init; }
    public int ProcessId { get; init; }
    public string? AutomationId { get; init; }
    public string Name { get; init; } = string.Empty;
    public required string ControlType { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public string? HelpText { get; init; }
    public PixelRect Bounds { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsOffscreen { get; init; }
    public bool HasKeyboardFocus { get; init; }
    public bool IsKeyboardFocusable { get; init; }
    public bool IsPassword { get; init; }
    public UiPatterns Patterns { get; init; }
    public string? Value { get; init; }
    public bool? IsReadOnly { get; init; }
    public ToggleValue? Toggle { get; init; }
    public bool? IsSelected { get; init; }
    public ExpandValue? Expand { get; init; }
    public double? ScrollVerticalPercent { get; init; }

    /// <summary>Index path from the window root ("0/2/1"), for display and as a weak identity hint.</summary>
    public string Path { get; init; } = string.Empty;

    public bool Supports(UiPatterns pattern) => (Patterns & pattern) == pattern;

    public string Describe()
    {
        var name = string.IsNullOrEmpty(Name) ? "(이름 없음)" : $"'{Name}'";
        var id = string.IsNullOrEmpty(AutomationId) ? string.Empty : $" #{AutomationId}";
        return $"{ControlType} {name}{id}";
    }
}

public sealed record UiNode(ElementInfo Element, IReadOnlyList<UiNode> Children);

public enum StructureStatus
{
    /// <summary>The window exposes a usable UIA tree.</summary>
    Available,

    /// <summary>Only a few elements: some controls may be drawn without UIA support.</summary>
    Limited,

    /// <summary>No usable structure (games, custom-drawn or inaccessible UI): "구조적 인식 불가".</summary>
    Unavailable,

    /// <summary>Windows blocks access (higher-integrity process).</summary>
    AccessDenied,
}

public sealed record UiSnapshot
{
    public required WindowInfo Window { get; init; }
    public UiNode? Root { get; init; }
    public int NodeCount { get; init; }
    public bool Truncated { get; init; }
    public StructureStatus Status { get; init; }
    public string? StatusReason { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    public IEnumerable<ElementInfo> Flatten()
    {
        if (Root is null)
        {
            yield break;
        }

        var stack = new Stack<UiNode>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node.Element;
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }
    }
}

public sealed record TreeCaptureOptions
{
    public int MaxDepth { get; init; } = 25;
    public int MaxNodes { get; init; } = 600;
    public bool IncludeOffscreen { get; init; }
    public bool ReadValues { get; init; } = true;
    public int MaxValueLength { get; init; } = 300;
}

/// <summary>A query for live elements inside one window. All set properties must match.</summary>
public sealed record ElementQuery
{
    public string? RuntimeId { get; init; }
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? NameContains { get; init; }
    public string? ControlType { get; init; }
    public string? ClassName { get; init; }
    public int MaxResults { get; init; } = 20;

    public bool IsEmpty => RuntimeId is null && AutomationId is null && Name is null && NameContains is null && ControlType is null && ClassName is null;

    public bool Matches(ElementInfo element)
    {
        if (RuntimeId is not null && !string.Equals(element.RuntimeId, RuntimeId, StringComparison.Ordinal))
        {
            return false;
        }

        if (AutomationId is not null && !string.Equals(element.AutomationId, AutomationId, StringComparison.Ordinal))
        {
            return false;
        }

        if (Name is not null && !string.Equals(element.Name.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (NameContains is not null && !element.Name.Contains(NameContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ControlType is not null && !string.Equals(element.ControlType, ControlType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ClassName is not null && !string.Equals(element.ClassName, ClassName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}

public sealed record ScreenImage
{
    public required byte[] Png { get; init; }
    public required int ImageWidth { get; init; }
    public required int ImageHeight { get; init; }

    /// <summary>The captured window rectangle in physical pixels at capture time.</summary>
    public required PixelRect SourceBounds { get; init; }
    public required int Dpi { get; init; }
    public required long WindowHandle { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Coarse 8x8 luminance signature of the image, used to notice that the screen changed.</summary>
    public ulong Signature { get; init; }
}

public sealed record LaunchResult(bool Success, int ProcessId, long WindowHandle, string Message);

public sealed record DesktopStatus(bool Available, string Description);
