using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Security;

namespace WindowsAiOperator.Presentation.ViewModels;

public sealed partial class WindowItemViewModel(WindowInfo window, string access, Tone tone, bool canInteract) : ObservableObject
{
    public WindowInfo Window { get; } = window;
    public string Title => string.IsNullOrWhiteSpace(Window.Title) ? "(제목 없음)" : Redactor.OneLine(Window.Title, 90);
    public string Process => $"{Window.ProcessName}.exe · PID {Window.ProcessId}";
    public string Access { get; } = access;
    public Tone AccessTone { get; } = tone;
    public bool CanInteract { get; } = canInteract;
}

public sealed partial class ElementNodeViewModel(ElementInfo element, IReadOnlyList<ElementNodeViewModel> children) : ObservableObject
{
    public ElementInfo Element { get; } = element;
    public IReadOnlyList<ElementNodeViewModel> Children { get; } = children;

    public string Text
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Element.Name) ? string.Empty : $" \"{Redactor.OneLine(Element.Name, 60)}\"";
            var id = string.IsNullOrEmpty(Element.AutomationId) ? string.Empty : $" #{Redactor.OneLine(Element.AutomationId, 40)}";
            var state = !Element.IsEnabled ? " (비활성)" : Element.IsOffscreen ? " (화면 밖)" : string.Empty;
            return $"{Element.ControlType}{name}{id}{state}";
        }
    }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

public sealed record PropertyRow(string Name, string Value);

/// <summary>
/// Windows and their UI Automation trees, element properties, and direct single-step actions on the selected element
/// (the "AI 없이도 안전한 테스트 동작" path). Direct actions go through the same policy, approval and journal.
/// </summary>
public sealed partial class InspectorViewModel : PageViewModel
{
    private readonly OperatorContext _context;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IHighlighter _highlighter;
    private readonly TaskRunnerViewModel _runner;
    private readonly Action<long> _openInRunner;

    public InspectorViewModel(OperatorContext context, IUiDispatcher ui, IDialogService dialogs, IHighlighter highlighter, TaskRunnerViewModel runner, Action<long> openInRunner)
        : base("창 검사기", "", "inspector")
    {
        _context = context;
        _ui = ui;
        _dialogs = dialogs;
        _highlighter = highlighter;
        _runner = runner;
        _openInRunner = openInRunner;
    }

    public ObservableCollection<WindowItemViewModel> Windows { get; } = [];
    public ObservableCollection<ElementNodeViewModel> Tree { get; } = [];
    public ObservableCollection<PropertyRow> WindowProperties { get; } = [];
    public ObservableCollection<PropertyRow> ElementProperties { get; } = [];

    [ObservableProperty]
    public partial WindowItemViewModel? SelectedWindow { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClickCommand), nameof(SetTextCommand), nameof(ToggleOnCommand), nameof(ToggleOffCommand), nameof(SelectCommand), nameof(ExpandCommand), nameof(HighlightCommand), nameof(ReadCommand))]
    public partial ElementNodeViewModel? SelectedElement { get; set; }

    [ObservableProperty]
    public partial string StructureText { get; set; } = "창을 고르면 UI Automation 구조를 읽습니다.";

    [ObservableProperty]
    public partial Tone StructureTone { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string ManualText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ActionResult { get; set; }

    [ObservableProperty]
    public partial bool ShowAllWindows { get; set; }

    public override void OnNavigatedTo()
    {
        if (Windows.Count == 0)
        {
            _ = RefreshAsync();
        }
    }

    partial void OnShowAllWindowsChanged(bool value) => _ = RefreshAsync();

    partial void OnSelectedWindowChanged(WindowItemViewModel? value)
    {
        Tree.Clear();
        ElementProperties.Clear();
        WindowProperties.Clear();
        SelectedElement = null;
        if (value is null)
        {
            return;
        }

        var w = value.Window;
        var scale = w.Dpi / 96.0;
        WindowProperties.Add(new("제목", Redactor.OneLine(w.Title, 200)));
        WindowProperties.Add(new("프로세스", $"{w.ProcessName}.exe (PID {w.ProcessId})"));
        WindowProperties.Add(new("실행 파일", w.ExecutablePath ?? "(확인 불가)"));
        WindowProperties.Add(new("창 핸들 / 클래스", $"{w.Handle} / {w.ClassName}"));
        WindowProperties.Add(new("위치·크기(물리 픽셀)", w.Bounds.ToString()));
        WindowProperties.Add(new("DPI / 배율", string.Create(CultureInfo.InvariantCulture, $"{w.Dpi} / {scale * 100:0}% (논리 크기 {w.Bounds.Width / scale:0}x{w.Bounds.Height / scale:0})")));
        WindowProperties.Add(new("상태", $"{(w.IsForeground ? "활성 창" : "비활성")}{(w.IsMinimized ? ", 최소화" : string.Empty)}{(w.IsElevatedOrProtected ? ", 관리자 권한/보호됨" : string.Empty)}"));
        WindowProperties.Add(new("자동화 허용", value.Access));
        _ = LoadTreeAsync(w);
    }

    partial void OnSelectedElementChanged(ElementNodeViewModel? value)
    {
        ElementProperties.Clear();
        if (value is null)
        {
            return;
        }

        var e = value.Element;
        ElementProperties.Add(new("이름(Name)", Redactor.OneLine(e.Name, 200)));
        ElementProperties.Add(new("역할(ControlType)", e.ControlType));
        ElementProperties.Add(new("AutomationId", e.AutomationId ?? "-"));
        ElementProperties.Add(new("ClassName / Framework", $"{e.ClassName ?? "-"} / {e.FrameworkId ?? "-"}"));
        ElementProperties.Add(new("지원 패턴", e.Patterns == UiPatterns.None ? "없음" : e.Patterns.ToString()));
        ElementProperties.Add(new("활성 / 포커스", $"{(e.IsEnabled ? "활성" : "비활성")} / {(e.HasKeyboardFocus ? "포커스 있음" : e.IsKeyboardFocusable ? "포커스 가능" : "포커스 불가")}"));
        ElementProperties.Add(new("화면 표시", e.IsOffscreen ? "화면 밖" : "화면 안"));
        ElementProperties.Add(new("위치(물리 픽셀)", e.Bounds.ToString()));
        if (e.IsPassword)
        {
            ElementProperties.Add(new("값", "(비밀번호 입력란 — 읽지 않음)"));
        }
        else if (e.Value is not null)
        {
            ElementProperties.Add(new("값(민감정보 가림)", Redactor.OneLine(e.Value, 300)));
        }

        if (e.Toggle is { } toggle)
        {
            ElementProperties.Add(new("확인란 상태", toggle switch { ToggleValue.On => "선택됨", ToggleValue.Off => "해제됨", _ => "부분 선택" }));
        }

        if (e.IsSelected is { } selected)
        {
            ElementProperties.Add(new("선택 여부", selected ? "선택됨" : "선택 안 됨"));
        }

        if (e.Expand is { } expand)
        {
            ElementProperties.Add(new("펼침 상태", expand.ToString()));
        }

        ElementProperties.Add(new("RuntimeId / 경로", $"{e.RuntimeId} / {e.Path}"));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var settings = _context.Settings.Safety;
            var own = _context.Desktop.OwnProcessId;
            var windows = await Task.Run(() => _context.Desktop.ListWindows()).ConfigureAwait(true);
            var selected = SelectedWindow?.Window.Handle;
            Windows.Clear();
            foreach (var window in windows)
            {
                var access = _context.Policy.CheckWindow(window, settings, own);
                if (!ShowAllWindows && !access.CanRead)
                {
                    continue;
                }

                var label = access.CanInteract ? "조작 가능" : access.CanRead ? "읽기 전용" : "차단: " + access.Reason;
                Windows.Add(new WindowItemViewModel(window, label, access.CanInteract ? Tone.Good : access.CanRead ? Tone.Info : Tone.Bad, access.CanInteract));
            }

            SelectedWindow = Windows.FirstOrDefault(w => w.Window.Handle == selected) ?? Windows.FirstOrDefault();
            Notice = Windows.Count == 0 ? (ShowAllWindows ? "열린 창이 없습니다." : "허용 앱의 창이 없습니다. 허용 앱을 실행하거나 '모든 창 보기'를 켜세요(차단된 창은 읽지 않습니다).") : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Notice = "창 목록을 읽지 못했습니다: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadTreeAsync(WindowInfo window)
    {
        var access = _context.Policy.CheckWindow(window, _context.Settings.Safety, _context.Desktop.OwnProcessId);
        if (!access.CanRead)
        {
            StructureText = "이 창은 안전 정책상 읽지 않습니다: " + access.Reason;
            StructureTone = Tone.Bad;
            return;
        }

        IsBusy = true;
        StructureText = "UI Automation 트리를 읽는 중…";
        StructureTone = Tone.Info;
        try
        {
            var snapshot = await Task.Run(() => _context.Desktop.CaptureTree(window.Handle, new TreeCaptureOptions { MaxNodes = 2000, MaxDepth = 30, IncludeOffscreen = true })).ConfigureAwait(true);
            Tree.Clear();
            if (snapshot.Root is not null)
            {
                var root = Build(snapshot.Root, 0);
                root.IsExpanded = true;
                Tree.Add(root);
            }

            (StructureText, StructureTone) = snapshot.Status switch
            {
                StructureStatus.Available => ($"요소 {snapshot.NodeCount}개{(snapshot.Truncated ? " (일부만 표시)" : string.Empty)} — 구조 인식 가능", Tone.Good),
                StructureStatus.Limited => ($"요소 {snapshot.NodeCount}개 — {snapshot.StatusReason}", Tone.Warning),
                StructureStatus.AccessDenied => ($"접근 거부 — {snapshot.StatusReason}", Tone.Bad),
                _ => ($"구조적 인식 불가 — {snapshot.StatusReason} 화면 인식(비전) 지원 모델이 필요합니다.", Tone.Bad),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or TimeoutException)
        {
            StructureText = "트리를 읽지 못했습니다(창이 닫혔을 수 있음): " + ex.Message;
            StructureTone = Tone.Bad;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static ElementNodeViewModel Build(UiNode node, int depth) =>
        new(node.Element, node.Children.Select(c => Build(c, depth + 1)).ToList()) { IsExpanded = depth < 2 };

    private bool HasElement() => SelectedElement is not null;

    [RelayCommand(CanExecute = nameof(HasElement))]
    private void Highlight()
    {
        if (SelectedElement is { } element && !element.Element.Bounds.IsEmpty)
        {
            _highlighter.Highlight(element.Element.Bounds);
        }
    }

    [RelayCommand(CanExecute = nameof(HasElement))]
    private Task ClickAsync() => RunManual(ActionType.Click, "누르기");

    [RelayCommand(CanExecute = nameof(HasElement))]
    private Task SetTextAsync() => RunManual(ActionType.SetText, "텍스트 입력");

    [RelayCommand(CanExecute = nameof(HasElement))]
    private Task ToggleOnAsync() => RunManual(ActionType.Toggle, "확인란 켜기", isChecked: true);

    [RelayCommand(CanExecute = nameof(HasElement))]
    private Task ToggleOffAsync() => RunManual(ActionType.Toggle, "확인란 끄기", isChecked: false);

    [RelayCommand(CanExecute = nameof(HasElement))]
    private Task SelectAsync() => RunManual(ActionType.Select, "선택");

    [RelayCommand(CanExecute = nameof(HasElement))]
    private Task ExpandAsync() => RunManual(ActionType.Expand, "펼치기");

    [RelayCommand(CanExecute = nameof(HasElement))]
    private Task ReadAsync() => RunManual(ActionType.ReadElement, "읽기");

    [RelayCommand]
    private void StartTaskHere()
    {
        if (SelectedWindow is { } window)
        {
            _openInRunner(window.Window.Handle);
        }
    }

    private async Task RunManual(ActionType action, string label, bool? isChecked = null)
    {
        if (SelectedWindow is not { } window || SelectedElement is not { } node)
        {
            return;
        }

        if (action == ActionType.SetText && string.IsNullOrEmpty(ManualText))
        {
            _dialogs.Error("텍스트 입력", "입력할 텍스트를 먼저 적으세요.");
            return;
        }

        var e = node.Element;
        var selector = new ElementSelector
        {
            AutomationId = string.IsNullOrEmpty(e.AutomationId) ? null : e.AutomationId,
            Name = string.IsNullOrEmpty(e.AutomationId) && !string.IsNullOrEmpty(e.Name) ? e.Name : null,
            ControlType = e.ControlType,
        };
        var step = new PlanStep
        {
            Id = 1,
            Action = action,
            Description = $"창 검사기에서 직접 {label}",
            Window = new WindowSelector { Handle = window.Window.Handle },
            Element = selector,
            Text = action == ActionType.SetText ? ManualText : null,
            Checked = isChecked,
        };
        ActionResult = $"{label} 실행 중…";
        var run = await _runner.RunManualStepAsync(step, $"{label}: {e.Describe()}").ConfigureAwait(true);
        if (run is not null)
        {
            var record = run.Steps.FirstOrDefault();
            ActionResult = $"{label}: {Core.Execution.TaskStatusText.Korean(run.Status)} — {record?.Message ?? run.StatusMessage}" +
                           (record?.ReadValue is { } read ? $" (값: {Redactor.OneLine(read, 120)})" : string.Empty);
            _context.Log.Info("inspector", $"manual {action} → {run.Status}");
            await LoadTreeAsync(window.Window).ConfigureAwait(true);
        }
    }
}
