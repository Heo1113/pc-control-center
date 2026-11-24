using WindowsAiOperator.Core.Actions;

namespace WindowsAiOperator.Core.Automation.Mock;

/// <summary>An element of the simulated desktop. Behaviours are plain callbacks.</summary>
public sealed class MockElement
{
    public MockElement(string controlType, string name = "", string? automationId = null)
    {
        ControlType = controlType;
        Name = name;
        AutomationId = automationId;
    }

    public string RuntimeId { get; internal set; } = string.Empty;
    public string? AutomationId { get; set; }
    public string Name { get; set; }
    public string ControlType { get; set; }
    public string? ClassName { get; set; }
    public string? HelpText { get; set; }
    public UiPatterns Patterns { get; set; }
    public string? Value { get; set; }
    public bool IsReadOnly { get; set; }
    public ToggleValue? Toggle { get; set; }
    public bool? IsSelected { get; set; }
    public ExpandValue? Expand { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsOffscreen { get; set; }
    public bool IsPassword { get; set; }
    public bool HasFocus { get; set; }
    public double? ScrollPercent { get; set; }
    public PixelRect Bounds { get; set; }
    public MockElement? Parent { get; private set; }
    public List<MockElement> Children { get; } = [];
    public Action<MockDesktop, MockWindow, MockElement>? OnInvoke { get; set; }
    public Action<MockDesktop, MockWindow, MockElement>? OnSelect { get; set; }

    public MockElement Add(MockElement child)
    {
        child.Parent = this;
        Children.Add(child);
        return this;
    }

    public void Remove()
    {
        Parent?.Children.Remove(this);
        Parent = null;
    }

    public IEnumerable<MockElement> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var nested in child.Descendants())
            {
                yield return nested;
            }
        }
    }
}

public sealed class MockWindow
{
    public long Handle { get; internal set; }
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string Title { get; set; }
    public string ClassName { get; init; } = "Window";
    public PixelRect Bounds { get; set; } = new(100, 100, 800, 600);
    public int Dpi { get; set; } = 96;
    public bool IsMinimized { get; set; }
    public bool IsElevated { get; set; }

    /// <summary>Simulates a custom-drawn window with no usable UIA structure.</summary>
    public bool NoStructure { get; set; }
    public bool IsClosed { get; internal set; }
    public required MockElement Root { get; init; }

    public MockElement? Find(string automationId) => Root.Descendants().FirstOrDefault(e => e.AutomationId == automationId);
}

/// <summary>
/// A simulated desktop for tests, CI and demo mode. It mirrors the real test app (same automation ids and behaviour)
/// so workflows run unchanged against both. Everything it does is labelled MOCK and never counts as real GUI automation.
/// </summary>
public sealed class MockDesktop : IDesktopAutomation
{
    private readonly Lock _gate = new();
    private readonly List<MockWindow> _windows = [];
    private readonly List<string> _actionLog = [];
    private readonly Dictionary<string, Func<MockDesktop, MockWindow>> _launchers = new(StringComparer.OrdinalIgnoreCase);
    private long _nextHandle = 0x10010;
    private int _nextRuntime = 1;
    private int _nextPid = 5000;
    private long? _foreground;

    public MockDesktop(int ownProcessId = 4242) => OwnProcessId = ownProcessId;

    public string Name => "MOCK 데스크톱(가상)";
    public bool IsMock => true;
    public DesktopStatus Status => new(true, "MOCK: 실제 Windows 화면이 아닌 가상 데스크톱입니다.");
    public int OwnProcessId { get; }

    /// <summary>Every primitive actually performed, in order. Tests assert on it ("nothing was executed").</summary>
    public IReadOnlyList<string> ActionLog
    {
        get
        {
            lock (_gate)
            {
                return _actionLog.ToArray();
            }
        }
    }

    /// <summary>Called before each primitive; return an outcome to fail it (fault injection).</summary>
    public Func<ActionRequest, ActionOutcome?>? Interceptor { get; set; }

    /// <summary>Called after each primitive (e.g. to close a window mid-task).</summary>
    public Action<ActionRequest>? AfterAction { get; set; }

    public IReadOnlyList<MockWindow> Windows
    {
        get
        {
            lock (_gate)
            {
                return _windows.Where(w => !w.IsClosed).ToArray();
            }
        }
    }

    public static MockDesktop CreateDemo(bool openTestTarget = true)
    {
        var desktop = new MockDesktop();
        desktop.RegisterLauncher("OperatorTestTarget.exe", d => MockApps.CreateTestTarget(d));
        desktop.RegisterLauncher("notepad.exe", d => MockApps.CreateNotepad(d));
        if (openTestTarget)
        {
            desktop.AddWindow(MockApps.CreateTestTarget(desktop));
        }

        return desktop;
    }

    public void RegisterLauncher(string executable, Func<MockDesktop, MockWindow> factory) => _launchers[executable] = factory;

    public int NewProcessId() => Interlocked.Increment(ref _nextPid);

    public MockWindow AddWindow(MockWindow window, bool foreground = true)
    {
        lock (_gate)
        {
            window.Handle = _nextHandle;
            _nextHandle += 0x10;
            AssignRuntimeIds(window.Root);
            _windows.Insert(0, window);
            if (foreground)
            {
                _foreground = window.Handle;
            }
        }

        return window;
    }

    public void CloseWindow(long handle)
    {
        lock (_gate)
        {
            var window = _windows.FirstOrDefault(w => w.Handle == handle);
            if (window is null)
            {
                return;
            }

            window.IsClosed = true;
            _windows.Remove(window);
            if (_foreground == handle)
            {
                _foreground = _windows.FirstOrDefault()?.Handle;
            }
        }
    }

    public void BringToFront(long handle)
    {
        lock (_gate)
        {
            var window = _windows.FirstOrDefault(w => w.Handle == handle);
            if (window is null)
            {
                return;
            }

            _windows.Remove(window);
            _windows.Insert(0, window);
            _foreground = handle;
        }
    }

    /// <summary>Re-numbers runtime ids under an element (call after adding elements at run time).</summary>
    public void AssignRuntimeIds(MockElement root)
    {
        if (string.IsNullOrEmpty(root.RuntimeId))
        {
            root.RuntimeId = "mock-" + Interlocked.Increment(ref _nextRuntime);
        }

        foreach (var element in root.Descendants())
        {
            if (string.IsNullOrEmpty(element.RuntimeId))
            {
                element.RuntimeId = "mock-" + Interlocked.Increment(ref _nextRuntime);
            }
        }
    }

    public MockWindow? FindWindow(string titleContains) => Windows.FirstOrDefault(w => w.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<WindowInfo> ListWindows()
    {
        lock (_gate)
        {
            return _windows.Where(w => !w.IsClosed).Select(ToInfo).ToArray();
        }
    }

    public WindowInfo? GetWindow(long handle)
    {
        lock (_gate)
        {
            var window = _windows.FirstOrDefault(w => w.Handle == handle && !w.IsClosed);
            return window is null ? null : ToInfo(window);
        }
    }

    public WindowInfo? GetForegroundWindow() => _foreground is { } handle ? GetWindow(handle) : null;

    public UiSnapshot CaptureTree(long windowHandle, TreeCaptureOptions options, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var window = _windows.FirstOrDefault(w => w.Handle == windowHandle && !w.IsClosed)
                         ?? throw new InvalidOperationException("창이 없습니다.");
            var info = ToInfo(window);
            if (window.NoStructure)
            {
                return new UiSnapshot
                {
                    Window = info,
                    Root = new UiNode(ToInfo(window, window.Root, "0"), []),
                    NodeCount = 1,
                    Status = StructureStatus.Unavailable,
                    StatusReason = "UI Automation 요소가 제공되지 않는 화면입니다(구조적 인식 불가).",
                };
            }

            var count = 0;
            var truncated = false;
            UiNode Build(MockElement element, string path, int depth)
            {
                count++;
                var children = new List<UiNode>();
                if (depth < options.MaxDepth)
                {
                    var index = 0;
                    foreach (var child in element.Children)
                    {
                        if (count >= options.MaxNodes)
                        {
                            truncated = true;
                            break;
                        }

                        if (child.IsOffscreen && !options.IncludeOffscreen)
                        {
                            index++;
                            continue;
                        }

                        children.Add(Build(child, $"{path}/{index++}", depth + 1));
                    }
                }

                return new UiNode(ToInfo(window, element, path), children);
            }

            var root = Build(window.Root, "0", 0);
            return new UiSnapshot { Window = info, Root = root, NodeCount = count, Truncated = truncated, Status = StructureStatus.Available };
        }
    }

    public IReadOnlyList<ElementInfo> FindElements(long windowHandle, ElementQuery query, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var window = _windows.FirstOrDefault(w => w.Handle == windowHandle && !w.IsClosed);
            if (window is null || window.NoStructure)
            {
                return [];
            }

            return Enumerate(window)
                .Select(pair => ToInfo(window, pair.Element, pair.Path))
                .Where(query.Matches)
                .Take(query.MaxResults)
                .ToArray();
        }
    }

    public ElementInfo? ElementFromPoint(PixelPoint point)
    {
        lock (_gate)
        {
            foreach (var window in _windows.Where(w => !w.IsClosed && w.Bounds.Contains(point)))
            {
                var hit = Enumerate(window).Where(p => p.Element.Bounds.Contains(point)).LastOrDefault();
                return hit.Element is null ? ToInfo(window, window.Root, "0") : ToInfo(window, hit.Element, hit.Path);
            }

            return null;
        }
    }

    public bool FocusWindow(long windowHandle)
    {
        if (GetWindow(windowHandle) is null)
        {
            return false;
        }

        BringToFront(windowHandle);
        return true;
    }

    public Task<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var intercepted = Interceptor?.Invoke(request);
        if (intercepted is not null)
        {
            if (intercepted.Performed)
            {
                Log($"{request.Action} (fault injected)");
            }

            return Task.FromResult(intercepted);
        }

        ActionOutcome outcome;
        lock (_gate)
        {
            outcome = Perform(request);
        }

        AfterAction?.Invoke(request);
        return Task.FromResult(outcome);
    }

    public Task<LaunchResult> LaunchAsync(string executable, IReadOnlyCollection<string> expectedProcessNames, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var key = Path.GetFileName(executable);
        if (!_launchers.TryGetValue(key, out var factory))
        {
            return Task.FromResult(new LaunchResult(false, 0, 0, $"MOCK: '{executable}'을(를) 실행할 수 없습니다."));
        }

        var window = AddWindow(factory(this));
        Log($"Launch {key}");
        return Task.FromResult(new LaunchResult(true, window.ProcessId, window.Handle, $"MOCK: {window.Title} 실행"));
    }

    public ScreenImage? CaptureWindowImage(long windowHandle, int maxWidth)
    {
        lock (_gate)
        {
            var window = _windows.FirstOrDefault(w => w.Handle == windowHandle && !w.IsClosed);
            if (window is null)
            {
                return null;
            }

            const int width = 160;
            const int height = 120;
            var state = string.Join('|', window.Root.Descendants().Select(e => e.Name + e.Value + e.Toggle));
            var shade = (byte)(Math.Abs(state.GetHashCode(StringComparison.Ordinal)) % 200 + 20);
            var rgb = new byte[width * height * 3];
            for (var i = 0; i < rgb.Length; i += 3)
            {
                var pixel = i / 3;
                var x = pixel % width;
                var y = pixel / width;
                var on = (x / 20 + y / 20 + shade / 10) % 2 == 0;
                rgb[i] = on ? shade : (byte)30;
                rgb[i + 1] = on ? (byte)(255 - shade) : (byte)30;
                rgb[i + 2] = 60;
            }

            return new ScreenImage
            {
                Png = PngWriter.Encode(width, height, rgb),
                ImageWidth = width,
                ImageHeight = height,
                SourceBounds = window.Bounds,
                Dpi = window.Dpi,
                WindowHandle = window.Handle,
                Signature = PngWriter.Signature(width, height, rgb),
            };
        }
    }

    public void Dispose()
    {
    }

    internal void Log(string entry)
    {
        lock (_gate)
        {
            _actionLog.Add(entry);
        }
    }

    private ActionOutcome Perform(ActionRequest request)
    {
        var window = _windows.FirstOrDefault(w => w.Handle == request.WindowHandle && !w.IsClosed);
        if (window is null)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.WindowClosed, "창이 닫혔습니다.");
        }

        if (request.Action == PrimitiveAction.SelectMenuPath)
        {
            return SelectMenu(window, request.MenuPath ?? []);
        }

        if (request.Action == PrimitiveAction.SendHotkey)
        {
            Log($"Hotkey {request.Hotkey} → {window.Title}");
            return ActionOutcome.Ok("MOCK.Keyboard");
        }

        if (request.Action == PrimitiveAction.ClickPoint)
        {
            if (request.Point is not { } point || !window.Bounds.Contains(point))
            {
                return ActionOutcome.NotPerformed(ExecutionErrorKind.ActionFailed, "좌표가 창 밖입니다.");
            }

            var hit = Enumerate(window).Where(p => p.Element.Bounds.Contains(point)).LastOrDefault().Element;
            Log($"ClickPoint {point} → {hit?.Name ?? window.Title}");
            if (hit is not null)
            {
                Invoke(window, hit);
            }

            return ActionOutcome.Ok("MOCK.Mouse");
        }

        var element = request.RuntimeId is null
            ? (request.Action == PrimitiveAction.Scroll ? window.Root : null)
            : Enumerate(window).FirstOrDefault(p => p.Element.RuntimeId == request.RuntimeId).Element;
        if (element is null)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.ElementNotFound, "요소가 사라졌습니다.");
        }

        if (!element.IsEnabled)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.ElementDisabled, "요소가 비활성 상태입니다.");
        }

        switch (request.Action)
        {
            case PrimitiveAction.Invoke:
                if ((element.Patterns & (UiPatterns.Invoke | UiPatterns.Toggle | UiPatterns.SelectionItem | UiPatterns.ExpandCollapse)) == 0)
                {
                    return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, "누를 수 없는 요소입니다.");
                }

                Log($"Invoke {element.Name} ({element.AutomationId}) in {window.Title}");
                Invoke(window, element);
                return ActionOutcome.Ok("MOCK.InvokePattern");

            case PrimitiveAction.SetValue or PrimitiveAction.AppendText:
                if (!element.Patterns.HasFlag(UiPatterns.Value) || element.IsReadOnly)
                {
                    return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, "값을 입력할 수 없는 요소입니다.");
                }

                element.Value = request.Action == PrimitiveAction.AppendText ? (element.Value ?? string.Empty) + request.Text : request.Text;
                Log($"{request.Action} {element.Name} ← {request.Text?.Length}자");
                return ActionOutcome.Ok("MOCK.ValuePattern");

            case PrimitiveAction.Toggle:
                if (!element.Patterns.HasFlag(UiPatterns.Toggle))
                {
                    return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, "확인란이 아닙니다.");
                }

                var desired = request.DesiredToggle ?? element.Toggle != ToggleValue.On;
                var target = desired ? ToggleValue.On : ToggleValue.Off;
                if (element.Toggle == target)
                {
                    return new ActionOutcome(true, false, "MOCK.TogglePattern", "이미 원하는 상태입니다.");
                }

                element.Toggle = target;
                Log($"Toggle {element.Name} → {target}");
                return ActionOutcome.Ok("MOCK.TogglePattern");

            case PrimitiveAction.Select:
                if (!element.Patterns.HasFlag(UiPatterns.SelectionItem))
                {
                    return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, "선택할 수 없는 요소입니다.");
                }

                Select(window, element);
                Log($"Select {element.Name}");
                return ActionOutcome.Ok("MOCK.SelectionItemPattern");

            case PrimitiveAction.Expand or PrimitiveAction.Collapse:
                if (!element.Patterns.HasFlag(UiPatterns.ExpandCollapse))
                {
                    return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, "펼칠 수 없는 요소입니다.");
                }

                element.Expand = request.Action == PrimitiveAction.Expand ? ExpandValue.Expanded : ExpandValue.Collapsed;
                Log($"{request.Action} {element.Name}");
                return ActionOutcome.Ok("MOCK.ExpandCollapsePattern");

            case PrimitiveAction.Scroll:
                var scrollable = element.Patterns.HasFlag(UiPatterns.Scroll) ? element : element.Descendants().FirstOrDefault(e => e.Patterns.HasFlag(UiPatterns.Scroll));
                if (scrollable is null)
                {
                    return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, "스크롤할 수 있는 영역이 없습니다.");
                }

                var delta = request.Direction is ScrollDirection.Up or ScrollDirection.Left ? -10 : 10;
                scrollable.ScrollPercent = Math.Clamp((scrollable.ScrollPercent ?? 0) + delta * request.Amount, 0, 100);
                Log($"Scroll {request.Direction} x{request.Amount}");
                return ActionOutcome.Ok("MOCK.ScrollPattern");

            case PrimitiveAction.Focus:
                element.HasFocus = true;
                return ActionOutcome.Ok("MOCK.SetFocus");

            default:
                return ActionOutcome.NotPerformed(ExecutionErrorKind.NotSupported, $"MOCK에서 지원하지 않는 동작: {request.Action}");
        }
    }

    private ActionOutcome SelectMenu(MockWindow window, IReadOnlyList<string> path)
    {
        MockElement? current = null;
        IEnumerable<MockElement> scope = window.Root.Descendants().Where(e => e.ControlType == "MenuItem" && e.Parent?.ControlType == "MenuBar");
        foreach (var name in path)
        {
            current = scope.FirstOrDefault(e => Matches(e.Name, name));
            if (current is null)
            {
                return ActionOutcome.NotPerformed(ExecutionErrorKind.ElementNotFound, $"메뉴 '{name}'을(를) 찾지 못했습니다.");
            }

            scope = current.Children.Where(c => c.ControlType == "MenuItem");
        }

        Log($"Menu {string.Join(" > ", path)}");
        Invoke(window, current!);
        return ActionOutcome.Ok("MOCK.Menu");

        static bool Matches(string actual, string wanted) =>
            Policy.RiskKeywords.Normalize(actual) == Policy.RiskKeywords.Normalize(wanted)
            || Policy.RiskKeywords.Normalize(actual).StartsWith(Policy.RiskKeywords.Normalize(wanted), StringComparison.Ordinal);
    }

    private void Invoke(MockWindow window, MockElement element)
    {
        if (element.OnInvoke is not null)
        {
            element.OnInvoke(this, window, element);
        }
        else if (element.Patterns.HasFlag(UiPatterns.Toggle))
        {
            element.Toggle = element.Toggle == ToggleValue.On ? ToggleValue.Off : ToggleValue.On;
        }
        else if (element.Patterns.HasFlag(UiPatterns.SelectionItem))
        {
            Select(window, element);
        }
    }

    private void Select(MockWindow window, MockElement element)
    {
        if (element.Parent is { } parent)
        {
            foreach (var sibling in parent.Children.Where(c => c.IsSelected is not null))
            {
                sibling.IsSelected = false;
            }

            if (parent.ControlType == "ComboBox")
            {
                parent.Value = element.Name;
            }
            else if (parent.Patterns.HasFlag(UiPatterns.Selection))
            {
                parent.Value = element.Name;
            }
        }

        element.IsSelected = true;
        element.OnSelect?.Invoke(this, window, element);
    }

    private static IEnumerable<(MockElement Element, string Path)> Enumerate(MockWindow window)
    {
        var stack = new Stack<(MockElement, string)>();
        stack.Push((window.Root, "0"));
        while (stack.Count > 0)
        {
            var (element, path) = stack.Pop();
            yield return (element, path);
            for (var i = element.Children.Count - 1; i >= 0; i--)
            {
                stack.Push((element.Children[i], $"{path}/{i}"));
            }
        }
    }

    private WindowInfo ToInfo(MockWindow window) => new()
    {
        Handle = window.Handle,
        ProcessId = window.ProcessId,
        ProcessName = window.ProcessName,
        ExecutablePath = $@"C:\MOCK\{window.ProcessName}.exe",
        Title = window.Title,
        ClassName = window.ClassName,
        Bounds = window.Bounds,
        Dpi = window.Dpi,
        IsMinimized = window.IsMinimized,
        IsForeground = _foreground == window.Handle,
        IsElevatedOrProtected = window.IsElevated,
    };

    private static ElementInfo ToInfo(MockWindow window, MockElement element, string path) => new()
    {
        RuntimeId = element.RuntimeId,
        WindowHandle = window.Handle,
        ProcessId = window.ProcessId,
        AutomationId = element.AutomationId,
        Name = element.Name,
        ControlType = element.ControlType,
        ClassName = element.ClassName,
        HelpText = element.HelpText,
        Bounds = element.Bounds,
        IsEnabled = element.IsEnabled,
        IsOffscreen = element.IsOffscreen,
        HasKeyboardFocus = element.HasFocus,
        IsKeyboardFocusable = element.Patterns != UiPatterns.None,
        IsPassword = element.IsPassword,
        Patterns = element.Patterns,
        Value = element.IsPassword ? null : element.Value,
        IsReadOnly = element.Patterns.HasFlag(UiPatterns.Value) ? element.IsReadOnly : null,
        Toggle = element.Toggle,
        IsSelected = element.IsSelected,
        Expand = element.Expand,
        ScrollVerticalPercent = element.ScrollPercent,
        Path = path,
    };
}
