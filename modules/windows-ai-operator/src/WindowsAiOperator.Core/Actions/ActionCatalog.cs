namespace WindowsAiOperator.Core.Actions;

/// <summary>The closed set of actions a plan may contain. Anything else in model output is rejected.</summary>
public enum ActionType
{
    LaunchApp,
    FocusWindow,
    Observe,
    ReadElement,
    WaitFor,
    Click,
    SetText,
    Toggle,
    Select,
    Expand,
    Collapse,
    SelectMenu,
    Scroll,
    Hotkey,
    ClickPoint,
    ListDirectory,
    Wait,
    AskUser,
    Finish,
}

public enum ActionCategory
{
    /// <summary>Reads state only.</summary>
    Observe,

    /// <summary>Starts or activates an allowed window.</summary>
    Window,

    /// <summary>Changes UI state inside an allowed window.</summary>
    Interact,

    /// <summary>Flow control (wait, ask, finish).</summary>
    Control,

    /// <summary>Reads the file system inside a folder the user granted.</summary>
    File,
}

public sealed record ActionDescriptor(
    ActionType Type,
    string WireName,
    string DisplayName,
    ActionCategory Category,
    bool NeedsWindow,
    bool NeedsElement,
    bool ChangesState,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyList<string> OptionalFields,
    string ModelHelp);

public static class ActionCatalog
{
    private static readonly string[] Common = ["id", "action", "description"];

    public static readonly IReadOnlyList<ActionDescriptor> All =
    [
        new(ActionType.LaunchApp, "launch_app", "앱 실행", ActionCategory.Window, false, false, true,
            ["app"], ["expect"],
            "Start an allowed app that has a launch command. app = allowed app id."),
        new(ActionType.FocusWindow, "focus_window", "창 활성화", ActionCategory.Window, true, false, false,
            ["window"], ["expect"],
            "Bring an allowed window to the foreground (use this instead of Alt+Tab)."),
        new(ActionType.Observe, "observe", "화면 상태 수집", ActionCategory.Observe, false, false, false,
            [], ["window"],
            "Capture the current UI tree of the target window and plan the remaining steps from it."),
        new(ActionType.ReadElement, "read_element", "요소 읽기", ActionCategory.Observe, true, true, false,
            ["element"], ["window"],
            "Read an element's name, value, check state or selection. Use {{s<ID>.value}} or {{s<ID>.state}} in finish.message."),
        new(ActionType.WaitFor, "wait_for", "요소 대기", ActionCategory.Observe, true, false, false,
            [], ["window", "element", "timeout_ms", "expect"],
            "Wait until the window (and element, if given) exists and the optional expectation holds."),
        new(ActionType.Click, "click", "버튼 클릭", ActionCategory.Interact, true, true, true,
            ["element"], ["window", "expect"],
            "Invoke a button, link or menu item through UI Automation."),
        new(ActionType.SetText, "set_text", "텍스트 입력", ActionCategory.Interact, true, true, true,
            ["element", "text"], ["window", "mode", "expect"],
            "Put text into an edit or document. mode = replace (default) or append."),
        new(ActionType.Toggle, "toggle", "확인란 설정", ActionCategory.Interact, true, true, true,
            ["element", "checked"], ["window", "expect"],
            "Set a check box or toggle to the given state (checked = true/false). Does nothing if already in that state."),
        new(ActionType.Select, "select", "항목 선택", ActionCategory.Interact, true, true, true,
            ["element"], ["window", "expect"],
            "Select a list item, tab, radio button or combo box item."),
        new(ActionType.Expand, "expand", "펼치기", ActionCategory.Interact, true, true, true,
            ["element"], ["window", "expect"],
            "Expand a combo box, tree item or menu."),
        new(ActionType.Collapse, "collapse", "접기", ActionCategory.Interact, true, true, true,
            ["element"], ["window", "expect"],
            "Collapse a combo box, tree item or menu."),
        new(ActionType.SelectMenu, "select_menu", "메뉴 선택", ActionCategory.Interact, true, false, true,
            ["menu_path"], ["window", "expect"],
            "Open a menu path in the window's menu bar, e.g. [\"File\", \"New\"]."),
        new(ActionType.Scroll, "scroll", "스크롤", ActionCategory.Interact, true, false, true,
            ["direction"], ["window", "element", "amount"],
            "Scroll a scrollable element (or the window). direction = up/down/left/right, amount = 1..20."),
        new(ActionType.Hotkey, "hotkey", "단축키", ActionCategory.Interact, true, false, true,
            ["keys"], ["window", "expect"],
            "Press a keyboard shortcut in the target window, e.g. \"Ctrl+Z\". Windows-key combinations are not allowed."),
        new(ActionType.ClickPoint, "click_point", "좌표 클릭", ActionCategory.Interact, true, false, true,
            ["point"], ["window", "confidence", "expect"],
            "Vision fallback only: click a point given as {x,y} in 0..1000 relative to the window screenshot."),
        new(ActionType.ListDirectory, "list_directory", "폴더 목록 읽기", ActionCategory.File, false, false, false,
            ["path"], [],
            "List files in a folder the user granted (read-only, no file contents)."),
        new(ActionType.Wait, "wait", "대기", ActionCategory.Control, false, false, false,
            ["delay_ms"], [],
            "Pause for delay_ms milliseconds (max 10000)."),
        new(ActionType.AskUser, "ask_user", "사용자 확인 요청", ActionCategory.Control, false, false, false,
            ["message"], [],
            "Stop and ask the user a question when the request is ambiguous or needs a decision."),
        new(ActionType.Finish, "finish", "완료 보고", ActionCategory.Control, false, false, false,
            [], ["message"],
            "End the task with a short Korean report for the user."),
    ];

    private static readonly Dictionary<string, ActionDescriptor> ByWire =
        All.ToDictionary(a => a.WireName, StringComparer.Ordinal);

    private static readonly Dictionary<ActionType, ActionDescriptor> ByType = All.ToDictionary(a => a.Type);

    public static IReadOnlyList<string> CommonFields => Common;

    public static bool TryGet(string wireName, out ActionDescriptor descriptor) => ByWire.TryGetValue(wireName, out descriptor!);

    public static ActionDescriptor Get(ActionType type) => ByType[type];

    public static string WireName(this ActionType type) => ByType[type].WireName;

    public static string DisplayName(this ActionType type) => ByType[type].DisplayName;

    public static bool ChangesState(this ActionType type) => ByType[type].ChangesState;
}

/// <summary>UIA control type names a selector may use (programmatic names without the "ControlType." prefix).</summary>
public static class ControlTypes
{
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AppBar", "Button", "Calendar", "CheckBox", "ComboBox", "Custom", "DataGrid", "DataItem", "Document", "Edit",
        "Group", "Header", "HeaderItem", "Hyperlink", "Image", "List", "ListItem", "Menu", "MenuBar", "MenuItem", "Pane",
        "ProgressBar", "RadioButton", "ScrollBar", "SemanticZoom", "Separator", "Slider", "Spinner", "SplitButton",
        "StatusBar", "Tab", "TabItem", "Table", "Text", "Thumb", "TitleBar", "ToolBar", "ToolTip", "Tree", "TreeItem", "Window",
    };

    /// <summary>Types worth showing to a model (UFO²'s CONTROL_LIST idea, plus containers that carry names).</summary>
    public static readonly IReadOnlySet<string> Interesting = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "CheckBox", "ComboBox", "DataItem", "Document", "Edit", "Hyperlink", "List", "ListItem", "Menu", "MenuBar",
        "MenuItem", "RadioButton", "ScrollBar", "Slider", "Spinner", "SplitButton", "Tab", "TabItem", "Text", "Tree",
        "TreeItem", "Window", "Group", "Pane", "Table", "DataGrid", "ToolBar", "StatusBar", "Header", "HeaderItem",
    };

    public static string Canonical(string name) => Known.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
}
