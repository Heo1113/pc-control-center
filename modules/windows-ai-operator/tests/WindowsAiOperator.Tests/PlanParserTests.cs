using WindowsAiOperator.Core.Actions;

namespace WindowsAiOperator.Tests;

public sealed class PlanParserTests
{
    [Fact]
    public void Valid_plan_with_reasoning_and_code_fence_is_accepted()
    {
        var raw = """
            <think>사용자는 메모장을 원한다...{ 이런 것도 있음 }</think>
            물론입니다. 계획입니다:
            ```json
            {"summary":"메모장","target_app":"notepad","steps":[{"id":1,"action":"launch_app","app":"notepad"},{"id":2,"action":"observe","window":{"app":"notepad"}}]}
            ```
            """;
        var result = PlanParser.Parse(raw);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal([ActionType.LaunchApp, ActionType.Observe], result.Plan!.Steps.Select(s => s.Action));
        Assert.Contains(result.Warnings, w => w.Contains("추론", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"steps":[{"action":"run_shell","command":"del C:\\"}]}""", "정의되지 않은 동작")]
    [InlineData("""{"steps":[{"action":"click","element":{"automation_id":"x"},"script":"rm"}]}""", "스키마에 없는 필드")]
    [InlineData("""{"steps":[{"action":"set_text","element":{"automation_id":"x"}}]}""", "필요한 필드가 없습니다")]
    [InlineData("""{"steps":[{"action":"toggle","element":{"automation_id":"x"},"checked":"yes"}]}""", "true 또는 false")]
    [InlineData("""{"steps":[{"action":"hotkey","keys":"Win+R"}]}""", "Windows 키")]
    [InlineData("""{"steps":[{"action":"hotkey","keys":"Alt+Tab"}]}""", "시스템 전체 단축키")]
    [InlineData("""{"steps":[{"action":"click_point","point":{"x":10,"y":10}}]}""", "비전")]
    [InlineData("""{"steps":[{"action":"click","element":{"ref":"button-1"}}]}""", "e 뒤에 숫자")]
    [InlineData("""{"steps":[{"action":"click","element":{"control_type":"Rocket"}}]}""", "알 수 없는 컨트롤")]
    [InlineData("""{"steps":[{"id":1,"action":"wait","delay_ms":10},{"id":1,"action":"wait","delay_ms":10}]}""", "중복")]
    [InlineData("""{"status":"need_user_input","steps":[]}""", "message")]
    [InlineData("""{"steps":[{"action":"wait","delay_ms":99999}]}""", "0~10000")]
    [InlineData("""{"steps":[{"action":"click","element":{"automation_id":"x"}}""", "끝나지 않았습니다")]
    [InlineData("안녕하세요, 무엇을 도와드릴까요?", "JSON 객체가 없습니다")]
    [InlineData("""{"steps":[],"evil":true}""", "최상위 필드")]
    public void Invalid_output_is_rejected_with_a_reason(string raw, string expected)
    {
        var result = PlanParser.Parse(raw);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ToString().Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Too_many_steps_and_too_long_text_are_rejected()
    {
        var many = "{\"steps\":[" + string.Join(",", Enumerable.Range(1, 31).Select(i => "{\"action\":\"wait\",\"delay_ms\":1}")) + "]}";
        Assert.False(PlanParser.Parse(many).IsValid);

        var longText = "{\"steps\":[{\"action\":\"set_text\",\"element\":{\"automation_id\":\"x\"},\"text\":\"" + new string('가', 2001) + "\"}]}";
        Assert.Contains(PlanParser.Parse(longText).Errors, e => e.Message.Contains("너무 깁니다", StringComparison.Ordinal));
    }

    [Fact]
    public void Irrelevant_known_fields_are_ignored_with_a_warning()
    {
        var result = PlanParser.Parse("""{"steps":[{"action":"click","element":{"automation_id":"OkButton"},"text":"무시됨"}]}""");

        Assert.True(result.IsValid);
        Assert.Null(result.Plan!.Steps[0].Text);
        Assert.Contains(result.Warnings, w => w.Contains("'text'", StringComparison.Ordinal));
    }

    [Fact]
    public void Click_point_is_accepted_only_with_vision()
    {
        const string json = """{"steps":[{"action":"click_point","window":{"app":"x"},"point":{"x":500.4,"y":20},"confidence":0.9}]}""";
        var result = PlanParser.Parse(json, new PlanParserOptions { AllowClickPoint = true });

        Assert.True(result.IsValid);
        Assert.Equal(new NormalizedPoint(500, 20), result.Plan!.Steps[0].Point);
    }

    [Fact]
    public void Json_schema_lists_exactly_the_catalog_actions()
    {
        var schema = PlanJsonSchema.Build(allowClickPoint: false, maxSteps: 30).ToJsonString();

        foreach (var action in ActionCatalog.All.Where(a => a.Type != ActionType.ClickPoint))
        {
            Assert.Contains($"\"{action.WireName}\"", schema, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("click_point", schema, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\":false", schema, StringComparison.Ordinal);
    }
}

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("ctrl+z", "Ctrl+Z", HotkeySafety.Safe)]
    [InlineData("Ctrl + Shift + Tab", "Ctrl+Shift+Tab", HotkeySafety.Safe)]
    [InlineData("Esc", "Escape", HotkeySafety.Safe)]
    [InlineData("Enter", "Enter", HotkeySafety.NeedsApproval)]
    [InlineData("Ctrl+S", "Ctrl+S", HotkeySafety.NeedsApproval)]
    [InlineData("Delete", "Delete", HotkeySafety.NeedsApproval)]
    [InlineData("Alt+F4", "Alt+F4", HotkeySafety.NeedsApproval)]
    [InlineData("Ctrl+Q", "Ctrl+Q", HotkeySafety.NeedsApproval)]
    [InlineData("Ctrl+Alt+Del", "Ctrl+Alt+Delete", HotkeySafety.Blocked)]
    [InlineData("Alt+Tab", "Alt+Tab", HotkeySafety.Blocked)]
    public void Hotkeys_are_parsed_and_classified(string text, string canonical, HotkeySafety expected)
    {
        Assert.True(HotkeyCatalog.TryParse(text, out var hotkey, out var error), error);
        Assert.Equal(canonical, hotkey!.ToString());
        Assert.Equal(expected, HotkeyCatalog.Classify(hotkey).Safety);
    }

    [Theory]
    [InlineData("Win+R")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+F13")]
    [InlineData("Ctrl+한")]
    public void Unsupported_hotkeys_do_not_parse(string text) =>
        Assert.False(HotkeyCatalog.TryParse(text, out _, out _));
}
