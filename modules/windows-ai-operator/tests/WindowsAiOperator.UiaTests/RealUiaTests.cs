using System.Diagnostics;
using WindowsAiOperator.Automation.Windows;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Policy;

namespace WindowsAiOperator.UiaTests;

/// <summary>
/// Real Windows UI Automation against the separate test app (and Notepad for one scenario). Nothing here touches user
/// files or settings: the test app only changes its own state, Notepad text is never saved and only processes started
/// by the test itself are closed.
/// </summary>
public sealed class RealUiaTests : IDisposable
{
    private readonly FlaUiDesktop _desktop = new();

    public RealUiaTests() => Assert.SkipUnless(OperatingSystem.IsWindows(), "실제 UI Automation 테스트는 Windows에서만 실행합니다.");

    public void Dispose() => _desktop.Dispose();

    [Fact]
    public void Lists_the_window_and_reads_its_structure()
    {
        using var app = TestTargetApp.Start(_desktop);

        var window = _desktop.GetWindow(app.Window.Handle)!;
        Assert.Equal("OperatorTestTarget", window.ProcessName, ignoreCase: true);
        Assert.False(window.Bounds.IsEmpty);
        Assert.True(window.Dpi >= 96);
        Assert.False(window.IsElevatedOrProtected);

        var snapshot = _desktop.CaptureTree(app.Window.Handle, new TreeCaptureOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(StructureStatus.Available, snapshot.Status);
        var elements = snapshot.Flatten().ToList();
        var button = Assert.Single(elements, e => e.AutomationId == "IncrementButton");
        Assert.Equal("Button", button.ControlType);
        Assert.True(button.Supports(UiPatterns.Invoke));
        Assert.Contains(elements, e => e.AutomationId == "NotifyCheckBox" && e.Toggle == ToggleValue.Off);
        var password = Assert.Single(elements, e => e.AutomationId == "PasswordInput");
        Assert.True(password.IsPassword);
        Assert.Null(password.Value);
    }

    [Fact]
    public async Task Clicks_types_toggles_selects_and_verifies()
    {
        using var app = TestTargetApp.Start(_desktop);
        var run = await Uia.Run(_desktop, "{\"steps\":[" +
            $"{{\"action\":\"click\",\"window\":{app.Selector},\"element\":{{\"automation_id\":\"IncrementButton\"}}}}," +
            "{\"action\":\"read_element\",\"element\":{\"automation_id\":\"CounterText\"}}," +
            "{\"action\":\"set_text\",\"element\":{\"automation_id\":\"InputBox\"},\"text\":\"안녕하세요 UIA 테스트\"}," +
            "{\"action\":\"set_text\",\"element\":{\"automation_id\":\"NotesBox\"},\"text\":\"첫 줄\\n둘째 줄\",\"mode\":\"append\"}," +
            "{\"action\":\"toggle\",\"element\":{\"automation_id\":\"NotifyCheckBox\"},\"checked\":true}," +
            "{\"action\":\"select\",\"element\":{\"automation_id\":\"ModeAdvanced\"}}," +
            "{\"action\":\"select\",\"element\":{\"control_type\":\"ListItem\",\"name\":\"항목 3\"}}," +
            "{\"action\":\"scroll\",\"element\":{\"automation_id\":\"LongList\"},\"direction\":\"down\",\"amount\":3}," +
            "{\"action\":\"finish\",\"message\":\"{{s2.value}}\"}]}");

        Assert.True(run.Status == TaskRunStatus.Completed, Uia.Describe(run));
        Assert.Equal("카운트: 1", run.Report);
        Assert.Equal("안녕하세요 UIA 테스트", Uia.Find(_desktop, app, "InputBox").Value);
        Assert.Contains("둘째 줄", Uia.Find(_desktop, app, "NotesBox").Value, StringComparison.Ordinal);
        Assert.Equal(ToggleValue.On, Uia.Find(_desktop, app, "NotifyCheckBox").Toggle);
        Assert.True(Uia.Find(_desktop, app, "ModeAdvanced").IsSelected);
        Assert.True(Uia.Find(_desktop, app, "LongList").ScrollVerticalPercent > 0);
    }

    [Fact]
    public async Task Combo_box_expand_and_select()
    {
        using var app = TestTargetApp.Start(_desktop);
        var run = await Uia.Run(_desktop, "{\"steps\":[" +
            $"{{\"action\":\"expand\",\"window\":{app.Selector},\"element\":{{\"automation_id\":\"ColorCombo\"}}}}," +
            "{\"action\":\"select\",\"element\":{\"control_type\":\"ListItem\",\"name\":\"파랑\"}}," +
            "{\"action\":\"read_element\",\"element\":{\"automation_id\":\"StatusText\"}}]}");

        Assert.True(run.Status == TaskRunStatus.Completed, Uia.Describe(run));
        Assert.Equal("상태: 색상 파랑", run.Steps[2].ReadValue);
    }

    [Fact]
    public async Task Menu_opens_a_dialog_that_is_handled_with_approval()
    {
        using var app = TestTargetApp.Start(_desktop);
        var approvals = ScriptedApprovalHandler.ApproveAll();
        var run = await Uia.Run(_desktop, "{\"steps\":[" +
            $"{{\"action\":\"select_menu\",\"window\":{app.Selector},\"menu_path\":[\"도구\",\"대화상자 열기\"]}}," +
            "{\"action\":\"wait_for\",\"window\":{\"app\":\"test-target\",\"title_contains\":\"테스트 대화상자\"},\"element\":{\"automation_id\":\"DialogOkButton\"},\"timeout_ms\":5000}," +
            "{\"action\":\"toggle\",\"element\":{\"automation_id\":\"DialogOptionCheckBox\"},\"checked\":true}," +
            "{\"action\":\"click\",\"element\":{\"automation_id\":\"DialogOkButton\"}}," +
            $"{{\"action\":\"read_element\",\"window\":{app.Selector},\"element\":{{\"automation_id\":\"StatusText\"}}}}]}}", approvals);

        Assert.True(run.Status == TaskRunStatus.Completed, Uia.Describe(run));
        Assert.Contains(approvals.Requests, r => r.Categories.Contains(RiskCategory.ConfirmButton));
        Assert.Equal("상태: 대화상자 확인(옵션 사용)", run.Steps[4].ReadValue);
    }

    [Fact]
    public async Task Denied_delete_leaves_the_list_untouched()
    {
        using var app = TestTargetApp.Start(_desktop);
        var run = await Uia.Run(_desktop, "{\"steps\":[" +
            $"{{\"action\":\"select\",\"window\":{app.Selector},\"element\":{{\"control_type\":\"ListItem\",\"name\":\"항목 1\"}}}}," +
            "{\"action\":\"click\",\"element\":{\"automation_id\":\"DeleteItemButton\"}}]}", ScriptedApprovalHandler.DenyAll());

        Assert.Equal(TaskRunStatus.PartiallyCompleted, run.Status);
        Assert.Equal(ExecutionErrorKind.ApprovalDenied, run.Error);
        var items = _desktop.FindElements(app.Window.Handle, new ElementQuery { ControlType = "ListItem", NameContains = "항목 " }, TestContext.Current.CancellationToken);
        Assert.Equal(5, items.Count(i => !i.Name.StartsWith("긴", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Vanished_element_and_closed_app_are_reported()
    {
        using var app = TestTargetApp.Start(_desktop);
        var vanished = await Uia.Run(_desktop, "{\"steps\":[" +
            $"{{\"action\":\"click\",\"window\":{app.Selector},\"element\":{{\"automation_id\":\"VanishingButton\"}}}}," +
            "{\"action\":\"click\",\"element\":{\"automation_id\":\"VanishingButton\"}}]}", settings: Uia.Settings(s => s.StepTimeoutSeconds = 2));
        Assert.Equal(ExecutionErrorKind.ElementNotFound, vanished.Steps[1].Error);

        var closed = await Uia.Run(_desktop, "{\"steps\":[" +
            $"{{\"action\":\"click\",\"window\":{app.Selector},\"element\":{{\"automation_id\":\"CloseSoonButton\"}}}}," +
            "{\"action\":\"wait\",\"delay_ms\":2500}," +
            "{\"action\":\"set_text\",\"element\":{\"automation_id\":\"InputBox\"},\"text\":\"늦은 입력\"}]}", settings: Uia.Settings(s => s.StepTimeoutSeconds = 3));
        Assert.True(closed.Error is ExecutionErrorKind.WindowClosed, Uia.Describe(closed));
        Assert.True(app.HasExited);
    }

    [Fact]
    public async Task Programs_outside_the_allow_list_and_password_fields_are_refused()
    {
        using var app = TestTargetApp.Start(_desktop);
        var notAllowed = await Uia.Run(_desktop, "{\"steps\":[" +
            $"{{\"action\":\"click\",\"window\":{{\"title_contains\":\"[{app.Suffix}]\"}},\"element\":{{\"automation_id\":\"IncrementButton\"}}}}]}}",
            ScriptedApprovalHandler.ApproveAll(), Uia.Settings(s => s.AllowedApps.RemoveAll(a => a.Id == "test-target")));
        Assert.Equal(TaskRunStatus.Denied, notAllowed.Status);
        Assert.Equal("카운트: 0", Uia.Find(_desktop, app, "CounterText").Name);

        var password = await Uia.Run(_desktop, "{\"steps\":[" +
            $"{{\"action\":\"set_text\",\"window\":{app.Selector},\"element\":{{\"automation_id\":\"PasswordInput\"}},\"text\":\"secret\"}}]}}", ScriptedApprovalHandler.ApproveAll());
        Assert.Equal(TaskRunStatus.Denied, password.Status);
    }

    [Fact]
    public async Task Injected_screen_text_forces_approval()
    {
        using var app = TestTargetApp.Start(_desktop, "--injection-text");
        var approvals = ScriptedApprovalHandler.DenyAll();
        var source = new FixedModelLikeSource("{\"steps\":[" + $"{{\"action\":\"click\",\"window\":{app.Selector},\"element\":{{\"automation_id\":\"IncrementButton\"}}}}]}}");
        var run = await Uia.Run(_desktop, "{\"steps\":[{\"action\":\"finish\"}]}", approvals, source: source, initialWindow: app.Window.Handle);

        Assert.True(run.InjectionSuspected, Uia.Describe(run));
        Assert.Contains(RiskCategory.InjectionSuspected, Assert.Single(approvals.Requests).Categories);
        Assert.Equal("카운트: 0", Uia.Find(_desktop, app, "CounterText").Name);
    }

    [Fact]
    public async Task Safe_hotkey_reaches_the_focused_window()
    {
        using var app = TestTargetApp.Start(_desktop);
        var run = await Uia.Run(_desktop, "{\"steps\":[" +
            $"{{\"action\":\"set_text\",\"window\":{app.Selector},\"element\":{{\"automation_id\":\"InputBox\"}},\"text\":\"되돌릴 문장\"}}," +
            "{\"action\":\"focus_window\",\"window\":" + app.Selector + "}," +
            "{\"action\":\"hotkey\",\"keys\":\"Ctrl+A\"}]}");

        Assert.True(run.Status == TaskRunStatus.Completed, Uia.Describe(run));
        Assert.Equal("SendInput(키보드)", run.Steps[2].Method);
    }

    [Fact]
    public void Window_capture_returns_a_png_with_bounds_and_dpi()
    {
        using var app = TestTargetApp.Start(_desktop);
        _desktop.FocusWindow(app.Window.Handle);
        var image = _desktop.CaptureWindowImage(app.Window.Handle, 800);

        Assert.NotNull(image);
        Assert.True(image.Png.Length > 1000);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, image.Png[..4]);
        Assert.Equal(_desktop.GetWindow(app.Window.Handle)!.Bounds, image.SourceBounds);
        Assert.True(image.ImageWidth <= 800);
    }

    [Fact]
    public async Task Notepad_launch_and_type_without_saving()
    {
        var before = Process.GetProcessesByName("notepad").Select(p => p.Id).ToHashSet();
        try
        {
            var run = await Uia.Run(_desktop, """
                {"steps":[
                  {"action":"launch_app","app":"notepad"},
                  {"action":"set_text","window":{"app":"notepad"},"element":{"control_type":"Document"},"text":"Windows AI Operator 테스트 문장","mode":"append"},
                  {"action":"finish","message":"입력 완료"}]}
                """, settings: Uia.Settings(s => s.StepTimeoutSeconds = 15));

            Assert.True(run.Status == TaskRunStatus.Completed, Uia.Describe(run));
            Assert.NotEqual(VerificationState.Failed, run.Steps[1].Verification);
        }
        finally
        {
            foreach (var process in Process.GetProcessesByName("notepad").Where(p => !before.Contains(p.Id)))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }
}
