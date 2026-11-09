using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Core.Security;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.Core.Workflows;

public sealed record WorkflowParameter
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public string? Default { get; init; }
    public bool Required { get; init; } = true;
    public int MaxLength { get; init; } = 500;

    /// <summary>"text" or "folder" (a folder is picked in a dialog and granted for that run only).</summary>
    public string Kind { get; init; } = "text";
}

/// <summary>
/// A saved, repeatable task: the same plan schema the model produces, plus parameters ({{param:name}}) and a failure
/// rule. A workflow runs only when the user starts it; there is no background or scheduled execution.
/// </summary>
public sealed record WorkflowDefinition
{
    public int Version { get; init; } = 1;
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? TargetApp { get; init; }
    public IReadOnlyList<WorkflowParameter> Parameters { get; init; } = [];

    /// <summary>"stop": end the run at the first failure (default). Workflows never re-plan with a model.</summary>
    public string OnFailure { get; init; } = "stop";
    public required JsonObject Plan { get; init; }
}

public sealed record ApprovalHint(int StepNumber, string Action, string Reason);

public static partial class WorkflowSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(WorkflowDefinition workflow) => JsonSerializer.Serialize(workflow, Options);

    public static (WorkflowDefinition? Workflow, string? Error) Parse(string json)
    {
        try
        {
            var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(json, Options);
            if (workflow is null || string.IsNullOrWhiteSpace(workflow.Name))
            {
                return (null, "워크플로 이름이 없습니다.");
            }

            if (workflow.Parameters.Any(p => !ParamName().IsMatch(p.Name)))
            {
                return (null, "매개변수 이름은 영문·숫자·밑줄 1~32자여야 합니다.");
            }

            var probe = Instantiate(workflow, workflow.Parameters.ToDictionary(p => p.Name, p => p.Default ?? "x"), new PlanParserOptions { MaxSteps = 100, AllowClickPoint = false });
            return probe.IsValid ? (workflow, null) : (null, "워크플로 단계가 스키마에 맞지 않습니다: " + string.Join("; ", probe.Errors.Take(5)));
        }
        catch (JsonException ex)
        {
            return (null, $"워크플로 JSON 오류: {ex.Message}");
        }
    }

    /// <summary>Fills parameters (user input, checked for length and control characters) and validates like model output.</summary>
    public static PlanParseResult Instantiate(WorkflowDefinition workflow, IReadOnlyDictionary<string, string> values, PlanParserOptions options)
    {
        foreach (var parameter in workflow.Parameters)
        {
            values.TryGetValue(parameter.Name, out var value);
            if (parameter.Required && string.IsNullOrEmpty(value))
            {
                return new PlanParseResult(null, [new PlanIssue(null, parameter.Name, $"'{parameter.Label}' 값을 입력하세요.")], []);
            }

            if (value is not null && (value.Length > parameter.MaxLength || value.Any(c => char.IsControl(c) && c is not '\n' and not '\t')))
            {
                return new PlanParseResult(null, [new PlanIssue(null, parameter.Name, $"'{parameter.Label}' 값이 너무 길거나 허용되지 않는 문자가 있습니다.")], []);
            }
        }

        var plan = Substitute(workflow.Plan.DeepClone(), values);
        return PlanParser.Parse(plan?.ToJsonString(), options);
    }

    /// <summary>
    /// Builds a workflow from the steps of a finished task that succeeded. Element references (e12) only exist inside one
    /// task, so they are replaced by the automation id / name the executor actually resolved.
    /// </summary>
    public static WorkflowDefinition FromRun(TaskRun run, string name)
    {
        var steps = new JsonArray();
        var id = 1;
        foreach (var record in run.Steps.Where(s => s.Status == StepRunStatus.Succeeded && s.Step.Action is not (ActionType.Observe or ActionType.Wait)))
        {
            var step = record.Step;
            var node = new JsonObject { ["id"] = id++, ["action"] = step.Action.WireName() };
            if (step.Description is not null)
            {
                node["description"] = step.Description;
            }

            if (step.App is not null)
            {
                node["app"] = step.App;
            }

            var descriptor = ActionCatalog.Get(step.Action);
            if (descriptor.NeedsWindow && record.ResolvedAppId is not null)
            {
                var window = new JsonObject { ["app"] = record.ResolvedAppId };
                if (step.Window?.TitleContains is { } title)
                {
                    window["title_contains"] = title;
                }

                node["window"] = window;
            }

            if (record.ResolvedElement is { } fingerprint)
            {
                var element = new JsonObject { ["control_type"] = fingerprint.ControlType };
                if (!string.IsNullOrEmpty(fingerprint.AutomationId))
                {
                    element["automation_id"] = fingerprint.AutomationId;
                }
                else
                {
                    element["name"] = fingerprint.Name;
                }

                node["element"] = element;
            }

            if (step.Text is not null)
            {
                node["text"] = step.Text;
                node["mode"] = step.Mode == SetTextMode.Append ? "append" : "replace";
            }

            if (step.Checked is { } isChecked)
            {
                node["checked"] = isChecked;
            }

            if (step.MenuPath is { } menu)
            {
                node["menu_path"] = new JsonArray(menu.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray());
            }

            if (step.Direction is { } direction)
            {
                node["direction"] = direction.ToString().ToLowerInvariant();
                node["amount"] = step.Amount;
            }

            if (step.Keys is not null)
            {
                node["keys"] = step.Keys.ToString();
            }

            if (step.Path is not null)
            {
                node["path"] = step.Path;
            }

            if (step.Message is not null && step.Action is ActionType.Finish or ActionType.AskUser)
            {
                node["message"] = step.Message;
            }

            steps.Add(node);
        }

        return new WorkflowDefinition
        {
            Name = name,
            Description = $"작업 '{Redactor.OneLine(run.Request, 80)}'에서 만든 워크플로",
            TargetApp = run.TargetApp,
            Plan = new JsonObject { ["summary"] = run.PlanSummary ?? name, ["steps"] = steps },
        };
    }

    /// <summary>Approvals the workflow will very likely ask for, judged from the plan alone (the live check decides).</summary>
    public static IReadOnlyList<ApprovalHint> ApprovalHints(TaskPlan plan, SafetySettings settings)
    {
        var hints = new List<ApprovalHint>();
        var number = 0;
        foreach (var step in plan.Steps)
        {
            number++;
            var categories = new List<RiskCategory>();
            switch (step.Action)
            {
                case ActionType.LaunchApp when settings.FindApp(step.App)?.RequireApprovalForLaunch == true:
                    categories.Add(RiskCategory.LaunchApp);
                    break;
                case ActionType.Hotkey when step.Keys is not null && HotkeyCatalog.Classify(step.Keys).Safety != HotkeySafety.Safe:
                    hints.Add(new ApprovalHint(number, step.Action.WireName(), HotkeyCatalog.Classify(step.Keys).Reason));
                    break;
                case ActionType.SelectMenu:
                    categories.AddRange(RiskKeywords.ClassifyText(step.MenuPath is { Count: > 0 } path ? path[^1] : null));
                    break;
                case ActionType.ClickPoint:
                    categories.Add(RiskCategory.CoordinateClick);
                    break;
                case ActionType.SetText when Redactor.ContainsSecret(step.Text):
                    categories.Add(RiskCategory.SecretInText);
                    break;
            }

            if (step.Element is { } element && step.Action.ChangesState())
            {
                categories.AddRange(RiskKeywords.Classify(element.Name ?? element.NameContains, element.AutomationId, null)
                    .Where(c => c != RiskCategory.ConfirmButton || settings.RequireApprovalForConfirmButtons));
            }

            var app = settings.FindApp(step.Window?.App ?? step.App);
            if (app?.IsSystemSettings == true && step.Action.ChangesState())
            {
                categories.Add(RiskCategory.SystemSettingChange);
            }

            foreach (var category in categories.Distinct())
            {
                var info = RiskCatalog.Get(category);
                hints.Add(new ApprovalHint(number, step.Action.WireName(), info.Denied ? $"{info.Label}(실행 거부됨)" : info.Label));
            }
        }

        return hints;
    }

    private static JsonNode? Substitute(JsonNode? node, IReadOnlyDictionary<string, string> values)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    obj[key] = Substitute(obj[key]?.DeepClone(), values);
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = Substitute(array[i]?.DeepClone(), values);
                }

                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(Placeholder().Replace(text, m => values.TryGetValue(m.Groups["name"].Value, out var v) ? v : m.Value));
            default:
                return node;
        }
    }

    [GeneratedRegex(@"\{\{param:(?<name>[A-Za-z0-9_]{1,32})\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,32}$")]
    private static partial Regex ParamName();
}

public static class BuiltInWorkflows
{
    public static IReadOnlyList<(string Id, WorkflowDefinition Workflow)> All { get; } =
    [
        ("builtin-test-counter", new WorkflowDefinition
        {
            Name = "테스트 앱: 카운트 증가와 텍스트 입력",
            Description = "테스트 앱을 새로 열고 카운트를 한 번 올린 뒤 입력 상자에 문장을 넣습니다. 테스트 앱 자신의 상태만 바뀝니다.",
            TargetApp = "test-target",
            Parameters = [new WorkflowParameter { Name = "text", Label = "입력할 문장", Default = "안녕하세요, Windows AI Operator입니다." }],
            Plan = JsonNode.Parse("""
                {"summary":"테스트 앱 카운트 증가와 입력","target_app":"test-target","steps":[
                  {"id":1,"action":"launch_app","app":"test-target","description":"테스트 앱 실행"},
                  {"id":2,"action":"wait_for","window":{"app":"test-target"},"element":{"automation_id":"IncrementButton"},"timeout_ms":10000,"description":"창 준비 확인"},
                  {"id":3,"action":"click","element":{"automation_id":"IncrementButton"},"description":"카운트 증가"},
                  {"id":4,"action":"set_text","element":{"automation_id":"InputBox"},"text":"{{param:text}}","description":"입력 상자에 문장 입력"},
                  {"id":5,"action":"read_element","element":{"automation_id":"CounterText"},"description":"카운트 읽기"},
                  {"id":6,"action":"finish","message":"완료: {{s5.value}}, 입력 상자에 문장을 넣었습니다."}]}
                """)!.AsObject(),
        }),
        ("builtin-notepad-type", new WorkflowDefinition
        {
            Name = "메모장: 새 창에 문장 입력(저장하지 않음)",
            Description = "메모장을 열고 문장을 덧붙입니다. 저장하지 않으며, 닫을 때 저장 여부는 직접 정하세요.",
            TargetApp = "notepad",
            Parameters = [new WorkflowParameter { Name = "text", Label = "입력할 문장", Default = "Windows AI Operator 테스트 문장입니다." }],
            Plan = JsonNode.Parse("""
                {"summary":"메모장에 문장 입력","target_app":"notepad","steps":[
                  {"id":1,"action":"launch_app","app":"notepad","description":"메모장 실행"},
                  {"id":2,"action":"set_text","window":{"app":"notepad"},"element":{"control_type":"Document"},"text":"{{param:text}}","mode":"append","description":"문장 덧붙이기"},
                  {"id":3,"action":"finish","message":"메모장에 문장을 입력했습니다(저장하지 않음)."}]}
                """)!.AsObject(),
        }),
        ("builtin-test-checkbox", new WorkflowDefinition
        {
            Name = "테스트 앱: 확인란 상태 확인",
            Description = "열려 있는 테스트 앱에서 '알림 표시' 확인란 상태를 읽어 보고합니다(읽기만 함).",
            TargetApp = "test-target",
            Plan = JsonNode.Parse("""
                {"summary":"확인란 상태 읽기","target_app":"test-target","steps":[
                  {"id":1,"action":"read_element","window":{"app":"test-target"},"element":{"automation_id":"NotifyCheckBox"},"description":"확인란 읽기"},
                  {"id":2,"action":"finish","message":"'알림 표시' 확인란: {{s1.state}}"}]}
                """)!.AsObject(),
        }),
        ("builtin-folder-list", new WorkflowDefinition
        {
            Name = "폴더 목록 읽기(읽기 전용)",
            Description = "선택한 폴더의 파일 이름·크기·날짜를 읽어 보여 줍니다. 파일 내용은 읽지 않고 아무것도 옮기거나 지우지 않습니다.",
            Parameters = [new WorkflowParameter { Name = "folder", Label = "폴더", Kind = "folder" }],
            Plan = JsonNode.Parse("""
                {"summary":"폴더 목록 읽기","steps":[
                  {"id":1,"action":"list_directory","path":"{{param:folder}}","description":"폴더 목록 읽기"},
                  {"id":2,"action":"finish","message":"{{s1.value}}"}]}
                """)!.AsObject(),
        }),
    ];
}
