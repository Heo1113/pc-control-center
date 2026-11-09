using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Identifiers;
using FlaUI.UIA3;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Policy;
using static WindowsAiOperator.Automation.Windows.NativeMethods;
using Drawing = System.Drawing;

namespace WindowsAiOperator.Automation.Windows;

/// <summary>
/// The real desktop: FlaUI (UIA3) for element trees and control patterns, Win32 for windows, focus, DPI, input and
/// capture. Patterns are always tried first; keyboard or mouse input is a fallback that is only used after checking
/// that the target window is in front and the target element has focus or is under the click point.
/// </summary>
public sealed class FlaUiDesktop : IDesktopAutomation
{
    private const int ElementDisabledHResult = unchecked((int)0x80040200);
    private const int UiaTimeoutHResult = unchecked((int)0x80131505);

    private readonly UIA3Automation _automation;
    private readonly ProcessInfoCache _processes = new();
    private readonly CacheRequest _cache;
    private readonly ITreeWalker _walker;
    private readonly IOperatorLog _log;
    private readonly PatternId[] _patterns;
    private readonly (PatternId Pattern, UiPatterns Flag)[] _patternFlags;

    public FlaUiDesktop(IOperatorLog? log = null)
    {
        _log = log ?? NullOperatorLog.Instance;
        _automation = new UIA3Automation();
        try
        {
            _automation.ConnectionTimeout = TimeSpan.FromSeconds(3);
            _automation.TransactionTimeout = TimeSpan.FromSeconds(10);
        }
        catch (Exception ex) when (ex is COMException or System.NotSupportedException or FlaUIException or InvalidCastException)
        {
            _log.Warn("uia", "UIA timeouts could not be set", ex);
        }

        _walker = _automation.TreeWalkerFactory.GetControlViewWalker();
        var library = _automation.PatternLibrary;
        _patternFlags =
        [
            (library.InvokePattern, UiPatterns.Invoke), (library.ValuePattern, UiPatterns.Value), (library.TogglePattern, UiPatterns.Toggle),
            (library.SelectionItemPattern, UiPatterns.SelectionItem), (library.ExpandCollapsePattern, UiPatterns.ExpandCollapse),
            (library.ScrollPattern, UiPatterns.Scroll), (library.TextPattern, UiPatterns.Text), (library.SelectionPattern, UiPatterns.Selection),
            (library.RangeValuePattern, UiPatterns.RangeValue), (library.WindowPattern, UiPatterns.Window),
            (library.ScrollItemPattern, UiPatterns.ScrollItem), (library.LegacyIAccessiblePattern, UiPatterns.LegacyIAccessible),
        ];
        _patterns = _patternFlags.Select(p => p.Pattern).ToArray();
        _cache = BuildCacheRequest();
        OwnProcessId = Environment.ProcessId;
    }

    public string Name => "Windows UI Automation (FlaUI UIA3)";
    public bool IsMock => false;
    public DesktopStatus Status => new(true, "실제 Windows 데스크톱 (UIA3)");
    public int OwnProcessId { get; }

    public IReadOnlyList<WindowInfo> ListWindows() => WindowEnumerator.List(_processes);

    public WindowInfo? GetWindow(long handle) => WindowEnumerator.Describe(new IntPtr(handle), _processes);

    public WindowInfo? GetForegroundWindow()
    {
        var handle = NativeMethods.GetForegroundWindow();
        return handle == IntPtr.Zero ? null : WindowEnumerator.Describe(handle, _processes, handle);
    }

    public UiSnapshot CaptureTree(long windowHandle, TreeCaptureOptions options, CancellationToken cancellationToken = default)
    {
        var hwnd = new IntPtr(windowHandle);
        var window = WindowEnumerator.Describe(hwnd, _processes) ?? throw new InvalidOperationException("창이 없습니다.");
        if (window.IsElevatedOrProtected)
        {
            return new UiSnapshot { Window = window, Status = StructureStatus.AccessDenied, StatusReason = "관리자 권한(또는 보호된) 프로세스라 일반 권한으로는 읽거나 조작할 수 없습니다." };
        }

        var count = 0;
        var truncated = false;
        try
        {
            using (_cache.Activate())
            {
                var root = _automation.FromHandle(hwnd);

                UiNode Walk(AutomationElement element, string path, int depth)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    count++;
                    var children = new List<UiNode>();
                    if (depth < options.MaxDepth)
                    {
                        var child = SafeWalk(() => _walker.GetFirstChild(element));
                        var index = 0;
                        while (child is not null)
                        {
                            if (count >= options.MaxNodes)
                            {
                                truncated = true;
                                break;
                            }

                            if (options.IncludeOffscreen || !Read<bool>(child, _automation.PropertyLibrary.Element.IsOffscreen))
                            {
                                children.Add(Walk(child, $"{path}/{index}", depth + 1));
                            }

                            index++;
                            var current = child;
                            child = SafeWalk(() => _walker.GetNextSibling(current));
                        }
                    }

                    return new UiNode(ToInfo(element, windowHandle, path, options.ReadValues ? options.MaxValueLength : 0), children);
                }

                var tree = Walk(root, "0", 0);
                var (status, reason) = Classify(tree, window);
                return new UiSnapshot { Window = window, Root = tree, NodeCount = count, Truncated = truncated, Status = status, StatusReason = reason };
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or UnauthorizedAccessException or TimeoutException)
        {
            _log.Warn("uia", "tree capture failed", ex);
            return new UiSnapshot
            {
                Window = window,
                Status = ex is UnauthorizedAccessException ? StructureStatus.AccessDenied : StructureStatus.Unavailable,
                StatusReason = $"UI Automation 트리를 읽지 못했습니다: {ex.GetType().Name}",
            };
        }
    }

    public IReadOnlyList<ElementInfo> FindElements(long windowHandle, ElementQuery query, CancellationToken cancellationToken = default)
    {
        var hwnd = new IntPtr(windowHandle);
        if (!IsWindow(hwnd))
        {
            return [];
        }

        try
        {
            using (_cache.Activate())
            {
                var root = _automation.FromHandle(hwnd);
                var processId = Read<int>(root, _automation.PropertyLibrary.Element.ProcessId);
                if (query.RuntimeId is not null)
                {
                    var found = FindByRuntimeId(root, query.RuntimeId, processId);
                    return found is null ? [] : new[] { ToInfo(found, windowHandle, string.Empty, int.MaxValue) }.Where(query.Matches).ToArray();
                }

                var condition = BuildCondition(query);
                var results = root.FindAll(TreeScope.Descendants, condition)
                    .Select(e => ToInfo(e, windowHandle, string.Empty, int.MaxValue))
                    .Where(query.Matches)
                    .Take(query.MaxResults)
                    .ToList();
                if (results.Count == 0 && processId != 0)
                {
                    foreach (var popup in ProcessPopups(processId, windowHandle))
                    {
                        results.AddRange(popup.FindAll(TreeScope.Subtree, condition)
                            .Select(e => ToInfo(e, windowHandle, string.Empty, int.MaxValue))
                            .Where(query.Matches)
                            .Take(query.MaxResults - results.Count));
                    }
                }

                return results;
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or UnauthorizedAccessException or TimeoutException)
        {
            _log.Warn("uia", "element search failed", ex);
            return [];
        }
    }

    public ElementInfo? ElementFromPoint(PixelPoint point)
    {
        try
        {
            var windowHandle = GetAncestor(WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), 2).ToInt64();
            using (_cache.Activate())
            {
                var element = _automation.FromPoint(new Drawing.Point(point.X, point.Y));
                return element is null ? null : ToInfo(element, windowHandle, string.Empty, 300);
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool FocusWindow(long windowHandle)
    {
        var hwnd = new IntPtr(windowHandle);
        if (!IsWindow(hwnd))
        {
            return false;
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }

        if (!SetForegroundWindow(hwnd))
        {
            InputSender.TapAlt();
            SetForegroundWindow(hwnd);
        }

        BringWindowToTop(hwnd);
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    public Task<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken) =>
        Task.Run(() => PerformSafe(request), cancellationToken);

    public async Task<LaunchResult> LaunchAsync(string executable, IReadOnlyCollection<string> expectedProcessNames, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var before = ListWindows().Where(w => Matches(w)).Select(w => w.Handle).ToHashSet();
        var target = ResolveLaunchTarget(executable);
        int startedPid;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            startedPid = process?.Id ?? 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new LaunchResult(false, 0, 0, $"실행하지 못했습니다({executable}): {ex.Message}");
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var fresh = ListWindows().Where(w => Matches(w) && !before.Contains(w.Handle)).ToList();
            var chosen = fresh.FirstOrDefault(w => w.ProcessId == startedPid) ?? fresh.FirstOrDefault();
            if (chosen is not null)
            {
                return new LaunchResult(true, chosen.ProcessId, chosen.Handle, $"'{chosen.Title}' 창이 열렸습니다.");
            }
        }

        var existing = GetForegroundWindow();
        if (existing is not null && Matches(existing))
        {
            return new LaunchResult(true, existing.ProcessId, existing.Handle, "새 창 대신 기존 창이 앞에 나왔습니다(새 탭으로 열렸을 수 있음).");
        }

        return new LaunchResult(false, startedPid, 0, $"{timeout.TotalSeconds:0}초 안에 새 창이 나타나지 않았습니다.");

        bool Matches(WindowInfo window) => expectedProcessNames.Any(n => string.Equals(n, window.ProcessName, StringComparison.OrdinalIgnoreCase));
    }

    public ScreenImage? CaptureWindowImage(long windowHandle, int maxWidth)
    {
        try
        {
            return ScreenCapturer.Capture(new IntPtr(windowHandle), maxWidth);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
        {
            _log.Warn("capture", "window capture failed", ex);
            return null;
        }
    }

    public void Dispose() => _automation.Dispose();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    private ActionOutcome PerformSafe(ActionRequest request)
    {
        try
        {
            return Perform(request);
        }
        catch (ElementNotAvailableException)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.ElementNotFound, "요소가 사라졌습니다.");
        }
        catch (PatternNotSupportedException ex)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, $"요소가 이 조작을 지원하지 않습니다: {ex.Message}");
        }
        catch (COMException ex) when (ex.HResult == ElementDisabledHResult)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.ElementDisabled, "요소가 비활성 상태입니다.");
        }
        catch (COMException ex) when (ex.HResult == UiaTimeoutHResult)
        {
            return ActionOutcome.PerformedButFailed("UIA", ExecutionErrorKind.Timeout, "대상 앱이 응답하지 않습니다(실행 여부 불확실).");
        }
        catch (UnauthorizedAccessException)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.AccessDenied, "권한이 없어 조작할 수 없습니다.");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
        {
            _log.Warn("uia", $"action {request.Action} failed", ex);
            return ActionOutcome.PerformedButFailed("UIA", ExecutionErrorKind.ActionFailed, $"조작 중 오류: {ex.GetType().Name}");
        }
    }

    private ActionOutcome Perform(ActionRequest request)
    {
        var hwnd = new IntPtr(request.WindowHandle);
        if (!IsWindow(hwnd))
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.WindowClosed, "창이 닫혔습니다.");
        }

        switch (request.Action)
        {
            case PrimitiveAction.SendHotkey:
                if (request.Hotkey is null)
                {
                    return ActionOutcome.NotPerformed(ExecutionErrorKind.ActionFailed, "단축키가 없습니다.");
                }

                if (NativeMethods.GetForegroundWindow() != hwnd)
                {
                    return ActionOutcome.NotPerformed(ExecutionErrorKind.FocusFailed, "대상 창이 앞에 있지 않아 키를 보내지 않았습니다.");
                }

                InputSender.Hotkey(request.Hotkey);
                return ActionOutcome.Ok("SendInput(키보드)");

            case PrimitiveAction.ClickPoint:
                if (request.Point is not { } point)
                {
                    return ActionOutcome.NotPerformed(ExecutionErrorKind.ActionFailed, "좌표가 없습니다.");
                }

                if (NativeMethods.GetForegroundWindow() != hwnd || !WindowEnumerator.Bounds(hwnd).Contains(point))
                {
                    return ActionOutcome.NotPerformed(ExecutionErrorKind.FocusFailed, "대상 창이 앞에 없거나 좌표가 창 밖이라 클릭하지 않았습니다.");
                }

                InputSender.ClickAt(point);
                return ActionOutcome.Ok("SendInput(마우스 좌표)");

            case PrimitiveAction.SelectMenuPath:
                return SelectMenu(hwnd, request.MenuPath ?? []);
        }

        AutomationElement? element;
        using (_cache.Activate())
        {
            var root = _automation.FromHandle(hwnd);
            var processId = Read<int>(root, _automation.PropertyLibrary.Element.ProcessId);
            element = request.RuntimeId is null ? root : FindByRuntimeId(root, request.RuntimeId, processId);
        }

        if (element is null)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.ElementNotFound, "요소를 다시 찾지 못했습니다(사라졌거나 바뀜).");
        }

        if (!element.Properties.IsEnabled.ValueOrDefault)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.ElementDisabled, "요소가 비활성 상태입니다.");
        }

        return request.Action switch
        {
            PrimitiveAction.Invoke => Invoke(element, hwnd),
            PrimitiveAction.SetValue => SetText(element, hwnd, request.Text ?? string.Empty, append: false),
            PrimitiveAction.AppendText => SetText(element, hwnd, request.Text ?? string.Empty, append: true),
            PrimitiveAction.Toggle => Toggle(element, request.DesiredToggle),
            PrimitiveAction.Select => Select(element),
            PrimitiveAction.Expand or PrimitiveAction.Collapse => ExpandCollapse(element, request.Action == PrimitiveAction.Expand),
            PrimitiveAction.Scroll => Scroll(element, hwnd, request.Direction, Math.Clamp(request.Amount, 1, 20)),
            PrimitiveAction.Focus => FocusElement(element),
            _ => ActionOutcome.NotPerformed(ExecutionErrorKind.NotSupported, $"지원하지 않는 동작: {request.Action}"),
        };
    }

    private ActionOutcome Invoke(AutomationElement element, IntPtr hwnd)
    {
        if (element.Patterns.Invoke.TryGetPattern(out var invoke))
        {
            invoke.Invoke();
            return ActionOutcome.Ok("InvokePattern");
        }

        if (element.Patterns.Toggle.TryGetPattern(out var toggle))
        {
            toggle.Toggle();
            return ActionOutcome.Ok("TogglePattern");
        }

        if (element.Patterns.SelectionItem.TryGetPattern(out var selection))
        {
            selection.Select();
            return ActionOutcome.Ok("SelectionItemPattern");
        }

        if (element.Patterns.ExpandCollapse.TryGetPattern(out var expand))
        {
            if (expand.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Expanded)
            {
                expand.Collapse();
            }
            else
            {
                expand.Expand();
            }

            return ActionOutcome.Ok("ExpandCollapsePattern");
        }

        if (element.Patterns.LegacyIAccessible.TryGetPattern(out var legacy) && !string.IsNullOrEmpty(legacy.DefaultAction.ValueOrDefault))
        {
            legacy.DoDefaultAction();
            return ActionOutcome.Ok("LegacyIAccessible.DoDefaultAction");
        }

        return ClickElementCenter(element, hwnd);
    }

    /// <summary>Mouse fallback: only when the window is in front and a hit test at the point returns the same element.</summary>
    private ActionOutcome ClickElementCenter(AutomationElement element, IntPtr hwnd)
    {
        if (!element.TryGetClickablePoint(out var clickable))
        {
            var bounds = element.Properties.BoundingRectangle.ValueOrDefault;
            if (bounds.IsEmpty)
            {
                return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, "누를 수 있는 UIA 패턴도, 클릭 가능한 위치도 없습니다.");
            }

            clickable = new Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }

        if (!FocusWindow(hwnd.ToInt64()))
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.FocusFailed, "대상 창을 앞으로 가져오지 못해 마우스로 누르지 않았습니다.");
        }

        var target = RuntimeId(element);
        var hit = _automation.FromPoint(clickable);
        if (hit is null || !IsSameOrDescendant(hit, target))
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.ActionFailed, "클릭 위치에 다른 요소가 있어 마우스로 누르지 않았습니다.");
        }

        InputSender.ClickAt(new PixelPoint(clickable.X, clickable.Y));
        return ActionOutcome.Ok("마우스 클릭(패턴 없음, 위치 확인 후)");
    }

    private ActionOutcome SetText(AutomationElement element, IntPtr hwnd, string text, bool append)
    {
        if (element.Properties.IsPassword.ValueOrDefault)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.PolicyDenied, "비밀번호 입력란에는 입력하지 않습니다.");
        }

        if (element.Patterns.Value.TryGetPattern(out var value) && !value.IsReadOnly.ValueOrDefault)
        {
            var current = append ? value.Value.ValueOrDefault ?? string.Empty : string.Empty;
            if (!append || current.Length < 200_000)
            {
                value.SetValue(append ? current + text : text);
                return ActionOutcome.Ok(append ? "ValuePattern(기존 값 + 덧붙임)" : "ValuePattern.SetValue");
            }
        }

        // Keyboard fallback: only into the focused target inside the foreground target window.
        if (!FocusWindow(hwnd.ToInt64()))
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.FocusFailed, "대상 창을 앞으로 가져오지 못해 키보드로 입력하지 않았습니다.");
        }

        element.Focus();
        Thread.Sleep(80);
        var focused = _automation.FocusedElement();
        if (focused is null || !IsSameOrDescendant(focused, RuntimeId(element)) && !IsSameOrDescendant(element, RuntimeId(focused)))
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.FocusFailed, "입력란에 포커스를 두지 못해 키보드로 입력하지 않았습니다.");
        }

        InputSender.Hotkey(new Core.Actions.Hotkey(Core.Actions.KeyModifiers.Ctrl, append ? "End" : "A"));
        Thread.Sleep(30);
        InputSender.TypeText(text);
        return ActionOutcome.Ok(append ? "키보드(Ctrl+End 후 입력)" : "키보드(Ctrl+A 후 입력)");
    }

    private static ActionOutcome Toggle(AutomationElement element, bool? desired)
    {
        if (!element.Patterns.Toggle.TryGetPattern(out var toggle))
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, "확인란(TogglePattern)이 아닙니다.");
        }

        var want = desired ?? toggle.ToggleState.ValueOrDefault != ToggleState.On;
        var target = want ? ToggleState.On : ToggleState.Off;
        if (toggle.ToggleState.ValueOrDefault == target)
        {
            return new ActionOutcome(true, false, "TogglePattern", "이미 원하는 상태입니다.");
        }

        for (var i = 0; i < 3 && toggle.ToggleState.ValueOrDefault != target; i++)
        {
            toggle.Toggle();
            Thread.Sleep(50);
        }

        return ActionOutcome.Ok("TogglePattern");
    }

    private static ActionOutcome Select(AutomationElement element)
    {
        if (element.Patterns.SelectionItem.TryGetPattern(out var selection))
        {
            if (selection.IsSelected.ValueOrDefault)
            {
                return new ActionOutcome(true, false, "SelectionItemPattern", "이미 선택되어 있습니다.");
            }

            selection.Select();
            return ActionOutcome.Ok("SelectionItemPattern");
        }

        if (element.Patterns.Invoke.TryGetPattern(out var invoke))
        {
            invoke.Invoke();
            return ActionOutcome.Ok("InvokePattern(선택)");
        }

        return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, "선택할 수 있는 항목(SelectionItemPattern)이 아닙니다.");
    }

    private static ActionOutcome ExpandCollapse(AutomationElement element, bool expand)
    {
        if (!element.Patterns.ExpandCollapse.TryGetPattern(out var pattern))
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, "펼치거나 접을 수 없는 요소입니다.");
        }

        if (expand)
        {
            pattern.Expand();
        }
        else
        {
            pattern.Collapse();
        }

        return ActionOutcome.Ok("ExpandCollapsePattern");
    }

    private ActionOutcome Scroll(AutomationElement element, IntPtr hwnd, ScrollDirection direction, int amount)
    {
        var scrollable = element.Patterns.Scroll.IsSupported
            ? element
            : element.FindFirstDescendant(_automation.ConditionFactory.ByControlType(ControlType.List).Or(_automation.ConditionFactory.ByControlType(ControlType.Pane)).Or(_automation.ConditionFactory.ByControlType(ControlType.Document)).Or(_automation.ConditionFactory.ByControlType(ControlType.Tree)).Or(_automation.ConditionFactory.ByControlType(ControlType.DataGrid)))
              is { } candidate && candidate.Patterns.Scroll.IsSupported ? candidate : null;
        if (scrollable is not null && scrollable.Patterns.Scroll.TryGetPattern(out var scroll))
        {
            var vertical = direction is ScrollDirection.Up or ScrollDirection.Down;
            var amountValue = direction is ScrollDirection.Up or ScrollDirection.Left ? ScrollAmount.LargeDecrement : ScrollAmount.LargeIncrement;
            for (var i = 0; i < amount; i++)
            {
                scroll.Scroll(vertical ? ScrollAmount.NoAmount : amountValue, vertical ? amountValue : ScrollAmount.NoAmount);
            }

            return ActionOutcome.Ok("ScrollPattern");
        }

        var bounds = element.Properties.BoundingRectangle.ValueOrDefault;
        if (bounds.IsEmpty || !FocusWindow(hwnd.ToInt64()))
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, "스크롤할 수 있는 영역을 찾지 못했습니다.");
        }

        var center = new PixelPoint(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var notches = direction is ScrollDirection.Up or ScrollDirection.Right ? 3 * amount : -3 * amount;
        InputSender.Wheel(center, notches, horizontal: direction is ScrollDirection.Left or ScrollDirection.Right);
        return ActionOutcome.Ok("마우스 휠(ScrollPattern 없음)");
    }

    private static ActionOutcome FocusElement(AutomationElement element)
    {
        element.Focus();
        return ActionOutcome.Ok("SetFocus");
    }

    private ActionOutcome SelectMenu(IntPtr hwnd, IReadOnlyList<string> path)
    {
        if (path.Count == 0)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.ActionFailed, "메뉴 경로가 비어 있습니다.");
        }

        var root = _automation.FromHandle(hwnd);
        var processId = root.Properties.ProcessId.ValueOrDefault;
        var first = FindMenuItem([root], path[0]);
        if (first is null)
        {
            return ActionOutcome.NotPerformed(ExecutionErrorKind.ElementNotFound, $"메뉴 '{path[0]}'을(를) 찾지 못했습니다.");
        }

        var current = first;
        var opened = new List<AutomationElement>();
        for (var i = 1; i < path.Count; i++)
        {
            Open(current);
            opened.Add(current);
            AutomationElement? next = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (next is null && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(120);
                next = FindMenuItem([current, .. ProcessPopups(processId, hwnd.ToInt64())], path[i]);
            }

            if (next is null)
            {
                CloseMenus(opened);
                return ActionOutcome.NotPerformed(ExecutionErrorKind.ElementNotFound, $"하위 메뉴 '{path[i]}'을(를) 찾지 못했습니다.");
            }

            current = next;
        }

        if (!current.Properties.IsEnabled.ValueOrDefault)
        {
            CloseMenus(opened);
            return ActionOutcome.NotPerformed(ExecutionErrorKind.ElementDisabled, $"메뉴 '{path[^1]}'이(가) 비활성 상태입니다.");
        }

        if (current.Patterns.Invoke.TryGetPattern(out var invoke))
        {
            invoke.Invoke();
        }
        else if (current.Patterns.Toggle.TryGetPattern(out var toggle))
        {
            toggle.Toggle();
        }
        else if (current.Patterns.ExpandCollapse.TryGetPattern(out var expand))
        {
            expand.Expand();
        }
        else if (current.Patterns.LegacyIAccessible.TryGetPattern(out var legacy))
        {
            legacy.DoDefaultAction();
        }
        else
        {
            CloseMenus(opened);
            return ActionOutcome.NotPerformed(ExecutionErrorKind.PatternNotSupported, "메뉴 항목을 실행할 수 없습니다.");
        }

        return ActionOutcome.Ok("메뉴(ExpandCollapse/Invoke 패턴)");

        static void Open(AutomationElement item)
        {
            if (item.Patterns.ExpandCollapse.TryGetPattern(out var expand))
            {
                expand.Expand();
            }
            else if (item.Patterns.Invoke.TryGetPattern(out var invoke))
            {
                invoke.Invoke();
            }
            else if (item.Patterns.LegacyIAccessible.TryGetPattern(out var legacy))
            {
                legacy.DoDefaultAction();
            }
        }
    }

    private static void CloseMenus(List<AutomationElement> opened)
    {
        foreach (var item in Enumerable.Reverse(opened))
        {
            try
            {
                if (item.Patterns.ExpandCollapse.TryGetPattern(out var expand))
                {
                    expand.Collapse();
                }
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
            {
            }
        }
    }

    private AutomationElement? FindMenuItem(IEnumerable<AutomationElement> scopes, string name)
    {
        var wanted = MenuKey(name);
        foreach (var scope in scopes)
        {
            AutomationElement[] items;
            try
            {
                items = scope.FindAllDescendants(_automation.ConditionFactory.ByControlType(ControlType.MenuItem));
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
            {
                continue;
            }

            var exact = items.FirstOrDefault(i => MenuKey(i.Properties.Name.ValueOrDefault) == wanted);
            if (exact is not null)
            {
                return exact;
            }

            var prefix = items.FirstOrDefault(i => MenuKey(i.Properties.Name.ValueOrDefault).StartsWith(wanted, StringComparison.Ordinal));
            if (prefix is not null)
            {
                return prefix;
            }
        }

        return null;

        static string MenuKey(string? text) => RiskKeywords.Normalize((text ?? string.Empty).Split('\t')[0]);
    }

    /// <summary>Top-level popup/menu windows of the same process (Win32 menus and context menus live there).</summary>
    private IEnumerable<AutomationElement> ProcessPopups(int processId, long ownerHandle)
    {
        AutomationElement[] candidates;
        try
        {
            candidates = _automation.GetDesktop().FindAllChildren(_automation.ConditionFactory.ByProcessId(processId));
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
        {
            yield break;
        }

        foreach (var candidate in candidates)
        {
            var type = candidate.Properties.ControlType.ValueOrDefault;
            var handle = candidate.Properties.NativeWindowHandle.ValueOrDefault;
            if (handle.ToInt64() == ownerHandle)
            {
                continue;
            }

            if (type is ControlType.Menu or ControlType.Window or ControlType.Pane)
            {
                yield return candidate;
            }
        }
    }

    private AutomationElement? FindByRuntimeId(AutomationElement root, string runtimeId, int processId)
    {
        var ids = ParseRuntimeId(runtimeId);
        if (ids.Length == 0)
        {
            return null;
        }

        try
        {
            var found = root.FindFirst(TreeScope.Subtree, new PropertyCondition(_automation.PropertyLibrary.Element.RuntimeId, ids));
            if (found is not null)
            {
                return found;
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            _log.Debug("uia", "runtime id condition not supported; walking the tree");
            return WalkFind(root, e => RuntimeId(e) == runtimeId, 4000);
        }

        foreach (var popup in processId == 0 ? [] : ProcessPopups(processId, 0))
        {
            var inPopup = WalkFind(popup, e => RuntimeId(e) == runtimeId, 1000);
            if (inPopup is not null)
            {
                return inPopup;
            }
        }

        return null;
    }

    private AutomationElement? WalkFind(AutomationElement root, Func<AutomationElement, bool> predicate, int maxNodes)
    {
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        var seen = 0;
        while (queue.Count > 0 && seen++ < maxNodes)
        {
            var element = queue.Dequeue();
            if (predicate(element))
            {
                return element;
            }

            var child = SafeWalk(() => _walker.GetFirstChild(element));
            while (child is not null)
            {
                queue.Enqueue(child);
                var current = child;
                child = SafeWalk(() => _walker.GetNextSibling(current));
            }
        }

        return null;
    }

    private bool IsSameOrDescendant(AutomationElement element, string ancestorRuntimeId)
    {
        var current = element;
        for (var depth = 0; current is not null && depth < 30; depth++)
        {
            if (RuntimeId(current) == ancestorRuntimeId)
            {
                return true;
            }

            current = SafeWalk(() => _walker.GetParent(current));
        }

        return false;
    }

    private ConditionBase BuildCondition(ElementQuery query)
    {
        var factory = _automation.ConditionFactory;
        var conditions = new List<ConditionBase>
        {
            new PropertyCondition(_automation.PropertyLibrary.Element.IsControlElement, true),
        };
        if (query.AutomationId is not null)
        {
            conditions.Add(factory.ByAutomationId(query.AutomationId));
        }

        if (query.Name is not null)
        {
            conditions.Add(factory.ByName(query.Name.Trim(), PropertyConditionFlags.IgnoreCase));
        }

        if (query.ControlType is not null && Enum.TryParse<ControlType>(query.ControlType, ignoreCase: true, out var controlType))
        {
            conditions.Add(factory.ByControlType(controlType));
        }

        if (query.ClassName is not null)
        {
            conditions.Add(factory.ByClassName(query.ClassName));
        }

        return conditions.Count == 1 ? conditions[0] : new AndCondition(conditions.ToArray());
    }

    private CacheRequest BuildCacheRequest()
    {
        var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        var e = _automation.PropertyLibrary.Element;
        foreach (var property in new[]
                 {
                     e.Name, e.AutomationId, e.ControlType, e.ClassName, e.FrameworkId, e.BoundingRectangle, e.IsEnabled, e.IsOffscreen,
                     e.HasKeyboardFocus, e.IsKeyboardFocusable, e.IsPassword, e.HelpText, e.RuntimeId, e.ProcessId, e.NativeWindowHandle,
                 })
        {
            request.Add(property);
        }

        foreach (var pattern in _patterns)
        {
            if (pattern.AvailabilityProperty is { } availability)
            {
                request.Add(availability);
            }
        }

        var library = _automation.PropertyLibrary;
        request.Add(library.Value.Value);
        request.Add(library.Value.IsReadOnly);
        request.Add(library.Toggle.ToggleState);
        request.Add(library.SelectionItem.IsSelected);
        request.Add(library.ExpandCollapse.ExpandCollapseState);
        request.Add(library.Scroll.VerticalScrollPercent);
        return request;
    }

    private ElementInfo ToInfo(AutomationElement element, long windowHandle, string path, int maxValueLength)
    {
        var e = _automation.PropertyLibrary.Element;
        var patterns = UiPatterns.None;
        foreach (var (pattern, flag) in _patternFlags)
        {
            if (pattern.AvailabilityProperty is { } availability && Read<bool>(element, availability))
            {
                patterns |= flag;
            }
        }

        var isPassword = Read<bool>(element, e.IsPassword);
        var controlType = Read<ControlType>(element, e.ControlType).ToString();
        string? value = null;
        bool? readOnly = null;
        if (!isPassword && maxValueLength > 0)
        {
            if (patterns.HasFlag(UiPatterns.Value))
            {
                value = Read<string>(element, _automation.PropertyLibrary.Value.Value);
                readOnly = Read<bool>(element, _automation.PropertyLibrary.Value.IsReadOnly);
            }

            if (value is null && patterns.HasFlag(UiPatterns.Selection))
            {
                value = SelectedNames(element);
            }

            if (value is not null && value.Length > maxValueLength)
            {
                value = value[..maxValueLength];
            }
        }
        else if (patterns.HasFlag(UiPatterns.Value))
        {
            readOnly = Read<bool>(element, _automation.PropertyLibrary.Value.IsReadOnly);
        }

        var rect = Read<Drawing.Rectangle>(element, e.BoundingRectangle);
        return new ElementInfo
        {
            RuntimeId = RuntimeId(element),
            WindowHandle = windowHandle,
            ProcessId = Read<int>(element, e.ProcessId),
            AutomationId = NullIfEmpty(Read<string>(element, e.AutomationId)),
            Name = Read<string>(element, e.Name) ?? string.Empty,
            ControlType = controlType,
            ClassName = NullIfEmpty(Read<string>(element, e.ClassName)),
            FrameworkId = NullIfEmpty(Read<string>(element, e.FrameworkId)),
            HelpText = NullIfEmpty(Read<string>(element, e.HelpText)),
            Bounds = new PixelRect(rect.X, rect.Y, rect.Width, rect.Height),
            IsEnabled = Read<bool>(element, e.IsEnabled),
            IsOffscreen = Read<bool>(element, e.IsOffscreen),
            HasKeyboardFocus = Read<bool>(element, e.HasKeyboardFocus),
            IsKeyboardFocusable = Read<bool>(element, e.IsKeyboardFocusable),
            IsPassword = isPassword,
            Patterns = patterns,
            Value = value,
            IsReadOnly = readOnly,
            Toggle = patterns.HasFlag(UiPatterns.Toggle) ? Read<ToggleState>(element, _automation.PropertyLibrary.Toggle.ToggleState) switch
            {
                ToggleState.On => ToggleValue.On,
                ToggleState.Indeterminate => ToggleValue.Indeterminate,
                _ => ToggleValue.Off,
            } : null,
            IsSelected = patterns.HasFlag(UiPatterns.SelectionItem) ? Read<bool>(element, _automation.PropertyLibrary.SelectionItem.IsSelected) : null,
            Expand = patterns.HasFlag(UiPatterns.ExpandCollapse) ? Read<ExpandCollapseState>(element, _automation.PropertyLibrary.ExpandCollapse.ExpandCollapseState) switch
            {
                ExpandCollapseState.Expanded => ExpandValue.Expanded,
                ExpandCollapseState.PartiallyExpanded => ExpandValue.PartiallyExpanded,
                ExpandCollapseState.LeafNode => ExpandValue.LeafNode,
                _ => ExpandValue.Collapsed,
            } : null,
            ScrollVerticalPercent = patterns.HasFlag(UiPatterns.Scroll) ? Read<double>(element, _automation.PropertyLibrary.Scroll.VerticalScrollPercent) : null,
            Path = path,
        };
    }

    private static string? SelectedNames(AutomationElement element)
    {
        try
        {
            using (CacheRequest.ForceNoCache())
            {
                var selected = element.Patterns.Selection.PatternOrDefault?.Selection.ValueOrDefault;
                return selected is null || selected.Length == 0 ? null : string.Join(", ", selected.Take(10).Select(s => s.Properties.Name.ValueOrDefault ?? string.Empty));
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or System.NotSupportedException or FlaUIException)
        {
            return null;
        }
    }

    private static (StructureStatus, string?) Classify(UiNode root, WindowInfo window)
    {
        var meaningful = 0;
        var stack = new Stack<UiNode>(root.Children);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            var e = node.Element;
            if (e.ControlType is not ("TitleBar" or "ScrollBar" or "Thumb") && (!string.IsNullOrWhiteSpace(e.Name) || !string.IsNullOrEmpty(e.AutomationId)
                || (e.Patterns & ~(UiPatterns.Window | UiPatterns.LegacyIAccessible)) != UiPatterns.None))
            {
                meaningful++;
            }

            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        var chromium = root.Element.FrameworkId is "Chrome" || window.ClassName.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal);
        if (meaningful == 0)
        {
            return (StructureStatus.Unavailable, chromium
                ? "구조적 인식 불가: Chromium/Electron 앱의 접근성 트리가 꺼져 있을 수 있습니다."
                : "구조적 인식 불가: 이 창은 UI Automation 요소를 제공하지 않습니다(그림으로 그린 화면일 수 있음).");
        }

        return meaningful < 3
            ? (StructureStatus.Limited, "UI 요소가 매우 적습니다. 일부 컨트롤은 구조 정보 없이 그려졌을 수 있습니다.")
            : (StructureStatus.Available, null);
    }

    private static T? Read<T>(AutomationElement element, PropertyId property)
    {
        try
        {
            return element.FrameworkAutomationElement.TryGetPropertyValue<T>(property, out var value) ? value : default;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or System.NotSupportedException or FlaUIException or InvalidCastException or PropertyNotCachedException)
        {
            return default;
        }
    }

    private static string RuntimeId(AutomationElement element)
    {
        try
        {
            var ids = element.FrameworkAutomationElement.TryGetPropertyValue<int[]>(element.Automation.PropertyLibrary.Element.RuntimeId, out var value) ? value : null;
            return ids is null ? string.Empty : string.Join('.', ids);
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or System.NotSupportedException or FlaUIException or PropertyNotCachedException)
        {
            return string.Empty;
        }
    }

    private static int[] ParseRuntimeId(string runtimeId)
    {
        var parts = runtimeId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var ids = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out ids[i]))
            {
                return [];
            }
        }

        return ids;
    }

    private static T? SafeWalk<T>(Func<T?> walk)
        where T : class
    {
        try
        {
            return walk();
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or TimeoutException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string ResolveLaunchTarget(string executable)
    {
        if (executable.Contains(':', StringComparison.Ordinal) && !Path.IsPathRooted(executable))
        {
            return executable;
        }

        if (Path.IsPathRooted(executable))
        {
            return executable;
        }

        var local = Path.Combine(AppContext.BaseDirectory, executable);
        return File.Exists(local) ? local : executable;
    }
}
