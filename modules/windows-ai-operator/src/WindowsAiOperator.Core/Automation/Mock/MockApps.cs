namespace WindowsAiOperator.Core.Automation.Mock;

/// <summary>
/// Simulated versions of the real test app (src/WindowsAiOperator.TestTarget) and of Notepad. Automation ids, names and
/// behaviour match the real test app so the same workflows run against both.
/// </summary>
public static class MockApps
{
    public const string TestTargetTitle = "Operator Test Target";
    public const string TestTargetProcess = "OperatorTestTarget";

    public static MockWindow CreateTestTarget(MockDesktop desktop, int? processId = null)
    {
        var pid = processId ?? desktop.NewProcessId();
        var y = 140;
        PixelRect Row(int height = 30) => new(120, (y += height + 10) - height - 10, 360, height);

        MockElement status = new("Text", "상태: 준비", "StatusText") { Bounds = new PixelRect(120, 740, 400, 24) };
        var counter = 0;
        MockElement counterText = new("Text", "카운트: 0", "CounterText");
        MockElement input = new("Edit", "입력 상자", "InputBox") { Patterns = UiPatterns.Value | UiPatterns.Text, Value = string.Empty, ClassName = "TextBox", Bounds = Row() };
        MockElement notes = new("Edit", "메모", "NotesBox") { Patterns = UiPatterns.Value | UiPatterns.Text, Value = string.Empty, ClassName = "TextBox", Bounds = Row(60) };
        MockElement increment = new("Button", "카운트 증가", "IncrementButton") { Patterns = UiPatterns.Invoke, ClassName = "Button", Bounds = Row() };
        counterText.Bounds = Row(24);
        increment.OnInvoke = (_, _, _) =>
        {
            counter++;
            counterText.Name = $"카운트: {counter}";
            status.Name = "상태: 카운트 증가";
        };

        MockElement notify = new("CheckBox", "알림 표시", "NotifyCheckBox") { Patterns = UiPatterns.Toggle, Toggle = ToggleValue.Off, ClassName = "CheckBox", Bounds = Row() };
        MockElement basic = new("RadioButton", "기본 모드", "ModeBasic") { Patterns = UiPatterns.SelectionItem, IsSelected = true, Bounds = Row() };
        MockElement advanced = new("RadioButton", "고급 모드", "ModeAdvanced") { Patterns = UiPatterns.SelectionItem, IsSelected = false, Bounds = Row() };
        var radioGroup = new MockElement("Group", "모드", "ModeGroup") { Bounds = new PixelRect(110, basic.Bounds.Top - 4, 380, 80) };
        radioGroup.Add(basic).Add(advanced);

        MockElement combo = new("ComboBox", "색상", "ColorCombo")
        {
            Patterns = UiPatterns.ExpandCollapse | UiPatterns.Value | UiPatterns.Selection,
            Value = "빨강",
            IsReadOnly = true,
            Expand = ExpandValue.Collapsed,
            Bounds = Row(),
        };
        foreach (var color in new[] { "빨강", "초록", "파랑" })
        {
            combo.Add(new MockElement("ListItem", color) { Patterns = UiPatterns.SelectionItem, IsSelected = color == "빨강", Bounds = combo.Bounds });
        }

        MockElement list = new("List", "항목 목록", "ItemList") { Patterns = UiPatterns.Selection, Bounds = Row(110) };
        for (var i = 1; i <= 5; i++)
        {
            list.Add(new MockElement("ListItem", $"항목 {i}") { Patterns = UiPatterns.SelectionItem, IsSelected = false, Bounds = new PixelRect(130, list.Bounds.Top + (i - 1) * 20, 300, 20) });
        }

        MockElement delete = new("Button", "선택 항목 삭제", "DeleteItemButton") { Patterns = UiPatterns.Invoke, Bounds = Row() };
        delete.OnInvoke = (_, _, _) =>
        {
            var selected = list.Children.FirstOrDefault(c => c.IsSelected == true);
            if (selected is not null)
            {
                selected.Remove();
                list.Value = null;
                status.Name = $"상태: {selected.Name} 삭제";
            }
        };

        MockElement password = new("Edit", "비밀번호", "PasswordInput") { Patterns = UiPatterns.Value, IsPassword = true, Value = string.Empty, Bounds = Row() };
        MockElement removeMe = new("Button", "한 번 누르면 사라지는 버튼", "VanishingButton") { Patterns = UiPatterns.Invoke, Bounds = Row() };
        removeMe.OnInvoke = (_, _, element) =>
        {
            element.Remove();
            status.Name = "상태: 버튼 사라짐";
        };

        MockElement openDialog = new("Button", "대화상자 열기", "OpenDialogButton") { Patterns = UiPatterns.Invoke, Bounds = Row() };
        MockElement closeSoon = new("Button", "1초 뒤 종료", "CloseSoonButton") { Patterns = UiPatterns.Invoke, Bounds = Row() };
        MockElement longList = new("List", "긴 목록", "LongList") { Patterns = UiPatterns.Scroll | UiPatterns.Selection, ScrollPercent = 0, Bounds = new PixelRect(520, 160, 300, 400) };
        for (var i = 1; i <= 50; i++)
        {
            longList.Add(new MockElement("ListItem", $"긴 목록 {i}") { Patterns = UiPatterns.SelectionItem, IsSelected = false, IsOffscreen = i > 15, Bounds = new PixelRect(530, 160 + (i - 1) * 26, 280, 24) });
        }

        MockElement menuBar = new("MenuBar", "메뉴", "MainMenu") { Bounds = new PixelRect(100, 110, 900, 24) };
        MockElement fileMenu = new("MenuItem", "파일", "FileMenu") { Patterns = UiPatterns.ExpandCollapse, Expand = ExpandValue.Collapsed, Bounds = new PixelRect(104, 110, 50, 24) };
        MockElement newItem = new("MenuItem", "새로 만들기", "NewMenuItem") { Patterns = UiPatterns.Invoke, IsOffscreen = true };
        MockElement exitItem = new("MenuItem", "끝내기", "ExitMenuItem") { Patterns = UiPatterns.Invoke, IsOffscreen = true };
        MockElement toolsMenu = new("MenuItem", "도구", "ToolsMenu") { Patterns = UiPatterns.ExpandCollapse, Expand = ExpandValue.Collapsed, Bounds = new PixelRect(160, 110, 50, 24) };
        MockElement dialogItem = new("MenuItem", "대화상자 열기", "OpenDialogMenuItem") { Patterns = UiPatterns.Invoke, IsOffscreen = true };
        fileMenu.Add(newItem).Add(exitItem);
        toolsMenu.Add(dialogItem);
        menuBar.Add(fileMenu).Add(toolsMenu);

        var root = new MockElement("Window", TestTargetTitle, "TestTargetWindow") { Patterns = UiPatterns.Window, Bounds = new PixelRect(100, 100, 900, 700) };
        root.Add(menuBar).Add(input).Add(notes).Add(increment).Add(counterText).Add(notify).Add(radioGroup).Add(combo).Add(list)
            .Add(delete).Add(password).Add(removeMe).Add(openDialog).Add(closeSoon).Add(longList).Add(status);

        var window = new MockWindow
        {
            ProcessId = pid,
            ProcessName = TestTargetProcess,
            Title = TestTargetTitle,
            ClassName = "HwndWrapper[OperatorTestTarget]",
            Bounds = new PixelRect(100, 100, 900, 700),
            Root = root,
        };

        void OpenDialog(MockDesktop d)
        {
            status.Name = "상태: 대화상자 열림";
            d.AddWindow(CreateDialog(d, pid, status));
        }

        openDialog.OnInvoke = (d, _, _) => OpenDialog(d);
        dialogItem.OnInvoke = (d, _, _) => OpenDialog(d);
        newItem.OnInvoke = (_, _, _) =>
        {
            input.Value = string.Empty;
            notes.Value = string.Empty;
            status.Name = "상태: 새로 만들기";
        };
        exitItem.OnInvoke = (d, w, _) => d.CloseWindow(w.Handle);
        closeSoon.OnInvoke = (d, w, _) => d.CloseWindow(w.Handle);
        return window;
    }

    public static MockWindow CreateDialog(MockDesktop desktop, int processId, MockElement? status = null)
    {
        MockElement option = new("CheckBox", "옵션 사용", "DialogOptionCheckBox") { Patterns = UiPatterns.Toggle, Toggle = ToggleValue.Off, Bounds = new PixelRect(330, 300, 200, 24) };
        MockElement ok = new("Button", "확인", "DialogOkButton") { Patterns = UiPatterns.Invoke, Bounds = new PixelRect(420, 440, 80, 30) };
        MockElement cancel = new("Button", "취소", "DialogCancelButton") { Patterns = UiPatterns.Invoke, Bounds = new PixelRect(510, 440, 80, 30) };
        var root = new MockElement("Window", "테스트 대화상자", "TestDialog") { Patterns = UiPatterns.Window, Bounds = new PixelRect(300, 250, 400, 250) };
        root.Add(new MockElement("Text", "테스트용 대화상자입니다.", "DialogMessage") { Bounds = new PixelRect(330, 270, 300, 24) }).Add(option).Add(ok).Add(cancel);
        var dialog = new MockWindow
        {
            ProcessId = processId,
            ProcessName = TestTargetProcess,
            Title = "테스트 대화상자",
            ClassName = "HwndWrapper[OperatorTestTarget]",
            Bounds = new PixelRect(300, 250, 400, 250),
            Root = root,
        };
        ok.OnInvoke = (d, w, _) =>
        {
            if (status is not null)
            {
                status.Name = option.Toggle == ToggleValue.On ? "상태: 대화상자 확인(옵션 사용)" : "상태: 대화상자 확인";
            }

            d.CloseWindow(w.Handle);
        };
        cancel.OnInvoke = (d, w, _) =>
        {
            if (status is not null)
            {
                status.Name = "상태: 대화상자 취소";
            }

            d.CloseWindow(w.Handle);
        };
        return dialog;
    }

    public static MockWindow CreateNotepad(MockDesktop desktop)
    {
        MockElement document = new("Document", "텍스트 편집기", "RichEditD2DPT")
        {
            Patterns = UiPatterns.Value | UiPatterns.Text,
            Value = string.Empty,
            ClassName = "RichEditD2DPT",
            Bounds = new PixelRect(210, 260, 780, 500),
        };
        MockElement menuBar = new("MenuBar", "애플리케이션", "MenuBar") { Bounds = new PixelRect(200, 220, 800, 30) };
        MockElement file = new("MenuItem", "파일", "File") { Patterns = UiPatterns.ExpandCollapse, Expand = ExpandValue.Collapsed };
        file.Add(new MockElement("MenuItem", "새 탭", "NewTab") { Patterns = UiPatterns.Invoke, IsOffscreen = true })
            .Add(new MockElement("MenuItem", "저장", "Save") { Patterns = UiPatterns.Invoke, IsOffscreen = true })
            .Add(new MockElement("MenuItem", "끝내기", "Exit") { Patterns = UiPatterns.Invoke, IsOffscreen = true });
        MockElement edit = new("MenuItem", "편집", "Edit") { Patterns = UiPatterns.ExpandCollapse, Expand = ExpandValue.Collapsed };
        edit.Add(new MockElement("MenuItem", "모두 선택", "SelectAll") { Patterns = UiPatterns.Invoke, IsOffscreen = true });
        menuBar.Add(file).Add(edit);
        var root = new MockElement("Window", "제목 없음 - Windows 메모장", "NotepadWindow") { Patterns = UiPatterns.Window, Bounds = new PixelRect(200, 200, 800, 600) };
        root.Add(menuBar).Add(document);
        return new MockWindow
        {
            ProcessId = desktop.NewProcessId(),
            ProcessName = "Notepad",
            Title = "제목 없음 - Windows 메모장",
            ClassName = "Notepad",
            Bounds = new PixelRect(200, 200, 800, 600),
            Root = root,
        };
    }

    /// <summary>A custom-drawn window without UIA structure (vision fallback tests).</summary>
    public static MockWindow CreateCanvasApp(MockDesktop desktop, string processName = "CanvasApp")
    {
        var root = new MockElement("Window", "Canvas App", "CanvasRoot") { Patterns = UiPatterns.Window, Bounds = new PixelRect(0, 0, 1000, 800) };
        return new MockWindow
        {
            ProcessId = desktop.NewProcessId(),
            ProcessName = processName,
            Title = "Canvas App",
            Bounds = new PixelRect(0, 0, 1000, 800),
            Root = root,
            NoStructure = true,
        };
    }
}
