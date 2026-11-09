using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsAiOperator.Core.Automation;

namespace WindowsAiOperator.Core.Actions;

/// <summary>
/// Turns model output (or a stored workflow) into a validated <see cref="TaskPlan"/>. The model never produces code:
/// it produces a JSON object of predefined actions, and anything outside the schema is rejected with a reason.
/// Formatting noise (reasoning blocks, code fences, comments) is tolerated; semantic deviations are not.
/// </summary>
public static partial class PlanParser
{
    private static readonly HashSet<string> RootFields = new(StringComparer.Ordinal) { "summary", "target_app", "status", "message", "steps" };

    private static readonly HashSet<string> StepFields = new(StringComparer.Ordinal)
    {
        "id", "action", "description", "app", "window", "element", "text", "mode", "checked", "menu_path", "direction", "amount",
        "keys", "point", "confidence", "path", "timeout_ms", "delay_ms", "message", "expect",
    };

    private static readonly HashSet<string> WindowFields = new(StringComparer.Ordinal) { "app", "title_contains", "handle" };

    private static readonly HashSet<string> ElementFields = new(StringComparer.Ordinal)
    {
        "ref", "automation_id", "name", "name_contains", "control_type", "class_name", "index",
    };

    private static readonly HashSet<string> ExpectFields = new(StringComparer.Ordinal)
    {
        "value_equals", "value_contains", "name_contains", "checked", "selected", "exists", "gone", "window_title_contains",
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 16,
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static PlanParseResult Parse(string? rawOutput, PlanParserOptions? options = null)
    {
        options ??= new PlanParserOptions();
        var errors = new List<PlanIssue>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return Fail("모델 응답이 비어 있습니다.");
        }

        if (rawOutput.Length > options.MaxInputLength)
        {
            return Fail($"모델 응답이 너무 깁니다({rawOutput.Length}자, 상한 {options.MaxInputLength}자).");
        }

        var json = ExtractJsonObject(rawOutput, warnings, out var extractError);
        if (json is null)
        {
            return Fail(extractError ?? "응답에서 JSON 객체를 찾지 못했습니다.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            return Fail($"JSON 형식 오류: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fail("최상위 값은 JSON 객체여야 합니다.");
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!RootFields.Contains(property.Name))
                {
                    errors.Add(new PlanIssue(null, property.Name, "스키마에 없는 최상위 필드입니다."));
                }
            }

            var summary = OptionalString(root, "summary", 300, null, errors);
            var targetApp = OptionalAppId(root, "target_app", null, errors);
            var message = OptionalString(root, "message", 2000, null, errors);
            var status = PlanStatus.Ok;
            var statusText = OptionalString(root, "status", 40, null, errors);
            if (statusText is not null)
            {
                status = statusText switch
                {
                    "ok" => PlanStatus.Ok,
                    "need_user_input" => PlanStatus.NeedUserInput,
                    "cannot_do" => PlanStatus.CannotDo,
                    _ => AddError(errors, new PlanIssue(null, "status", $"알 수 없는 상태 '{statusText}' (ok, need_user_input, cannot_do 중 하나)"), PlanStatus.Ok),
                };
            }

            var steps = new List<PlanStep>();
            if (!root.TryGetProperty("steps", out var stepsElement))
            {
                if (status == PlanStatus.Ok)
                {
                    errors.Add(new PlanIssue(null, "steps", "필수 필드가 없습니다."));
                }
            }
            else if (stepsElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new PlanIssue(null, "steps", "배열이어야 합니다."));
            }
            else
            {
                var count = stepsElement.GetArrayLength();
                if (count > options.MaxSteps)
                {
                    errors.Add(new PlanIssue(null, "steps", $"단계가 너무 많습니다({count}개, 상한 {options.MaxSteps}개)."));
                }
                else if (count == 0 && status == PlanStatus.Ok)
                {
                    errors.Add(new PlanIssue(null, "steps", "실행할 단계가 없습니다."));
                }
                else
                {
                    var number = 0;
                    var usedIds = new HashSet<int>();
                    foreach (var stepElement in stepsElement.EnumerateArray())
                    {
                        number++;
                        var step = ParseStep(stepElement, number, options, errors, warnings);
                        if (step is null)
                        {
                            continue;
                        }

                        if (!usedIds.Add(step.Id))
                        {
                            errors.Add(new PlanIssue(number, "id", $"단계 번호 {step.Id}가 중복됩니다."));
                            continue;
                        }

                        steps.Add(step);
                    }

                    var finishIndex = steps.FindIndex(s => s.Action is ActionType.Finish or ActionType.AskUser);
                    if (finishIndex >= 0 && finishIndex < steps.Count - 1)
                    {
                        warnings.Add("finish/ask_user 뒤의 단계는 실행되지 않습니다.");
                    }
                }
            }

            if ((status != PlanStatus.Ok) && string.IsNullOrWhiteSpace(message))
            {
                errors.Add(new PlanIssue(null, "message", "status가 ok가 아니면 사용자에게 보여 줄 message가 필요합니다."));
            }

            if (errors.Count > 0)
            {
                return new PlanParseResult(null, errors, warnings);
            }

            return new PlanParseResult(
                new TaskPlan
                {
                    Summary = summary,
                    TargetApp = targetApp,
                    Status = status,
                    Message = message,
                    Steps = steps,
                    Warnings = warnings,
                },
                errors,
                warnings);
        }

        PlanParseResult Fail(string reason)
        {
            errors.Add(new PlanIssue(null, null, reason));
            return new PlanParseResult(null, errors, warnings);
        }
    }

    /// <summary>
    /// Removes reasoning blocks and code fences and returns the first balanced JSON object, or null.
    /// </summary>
    public static string? ExtractJsonObject(string raw, List<string> warnings, out string? error)
    {
        error = null;
        var text = ThinkBlock().Replace(raw, string.Empty);
        var openThink = text.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
        if (openThink >= 0)
        {
            var before = text[..openThink];
            if (before.Contains('{'))
            {
                text = before;
            }
            else
            {
                error = "모델이 추론 블록(<think>)만 출력하고 답을 끝내지 않았습니다. 최대 토큰을 늘리거나 추론을 끄세요.";
                return null;
            }
        }

        if (text.Length != raw.Length)
        {
            warnings.Add("모델 추론 블록을 제거했습니다.");
        }

        var fence = CodeFence().Match(text);
        if (fence.Success)
        {
            var inner = fence.Groups["body"].Value;
            if (inner.Contains('{'))
            {
                text = inner;
            }
        }

        var start = text.IndexOf('{');
        if (start < 0)
        {
            error = "응답에 JSON 객체가 없습니다.";
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        var rest = text[(i + 1)..];
                        if (rest.Contains('{'))
                        {
                            warnings.Add("응답에 JSON 객체가 여러 개 있어 첫 번째만 사용했습니다.");
                        }

                        return text[start..(i + 1)];
                    }

                    break;
            }
        }

        error = "JSON 객체가 끝나지 않았습니다(응답이 잘렸을 수 있습니다).";
        return null;
    }

    private static PlanStep? ParseStep(JsonElement element, int number, PlanParserOptions options, List<PlanIssue> errors, List<string> warnings)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new PlanIssue(number, null, "단계는 JSON 객체여야 합니다."));
            return null;
        }

        var before = errors.Count;
        foreach (var property in element.EnumerateObject())
        {
            if (!StepFields.Contains(property.Name))
            {
                errors.Add(new PlanIssue(number, property.Name, "스키마에 없는 필드입니다."));
            }
        }

        var actionName = OptionalString(element, "action", 40, number, errors);
        if (actionName is null)
        {
            if (!element.TryGetProperty("action", out _))
            {
                errors.Add(new PlanIssue(number, "action", "필수 필드가 없습니다."));
            }

            return null;
        }

        if (!ActionCatalog.TryGet(actionName, out var descriptor))
        {
            errors.Add(new PlanIssue(number, "action", $"정의되지 않은 동작 '{Security.Redactor.OneLine(actionName, 40)}'입니다. 허용 동작: {string.Join(", ", ActionCatalog.All.Select(a => a.WireName))}"));
            return null;
        }

        if (descriptor.Type == ActionType.ClickPoint && !options.AllowClickPoint)
        {
            errors.Add(new PlanIssue(number, "action", "화면 인식(비전)이 꺼져 있거나 모델이 이미지를 지원하지 않아 click_point를 쓸 수 없습니다."));
            return null;
        }

        var applicable = new HashSet<string>(ActionCatalog.CommonFields.Concat(descriptor.RequiredFields).Concat(descriptor.OptionalFields), StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (StepFields.Contains(property.Name) && !applicable.Contains(property.Name))
            {
                warnings.Add($"{number}번째 단계: '{property.Name}'는 {descriptor.WireName}에 쓰이지 않아 무시했습니다.");
            }
        }

        foreach (var required in descriptor.RequiredFields)
        {
            if (!element.TryGetProperty(required, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                errors.Add(new PlanIssue(number, required, $"{descriptor.WireName}에 필요한 필드가 없습니다."));
            }
        }

        var id = number;
        if (element.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null)
        {
            if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt32(out var parsedId) && parsedId is >= 1 and <= 9999)
            {
                id = parsedId;
            }
            else
            {
                errors.Add(new PlanIssue(number, "id", "1~9999 사이의 정수여야 합니다."));
            }
        }

        bool Uses(string field) => applicable.Contains(field);

        var description = OptionalString(element, "description", 200, number, errors);
        var app = Uses("app") ? OptionalAppId(element, "app", number, errors) : null;
        var window = Uses("window") ? ParseWindow(element, number, errors) : null;
        var target = Uses("element") ? ParseElement(element, number, errors) : null;
        var text = Uses("text") ? OptionalString(element, "text", options.MaxTextLength, number, errors, allowNewlines: true) : null;
        var mode = SetTextMode.Replace;
        if (Uses("mode"))
        {
            var modeText = OptionalString(element, "mode", 20, number, errors);
            if (modeText is not null)
            {
                mode = modeText switch
                {
                    "replace" => SetTextMode.Replace,
                    "append" => SetTextMode.Append,
                    _ => AddError(errors, new PlanIssue(number, "mode", "replace 또는 append여야 합니다."), SetTextMode.Replace),
                };
            }
        }

        var isChecked = Uses("checked") ? OptionalBool(element, "checked", number, errors) : null;
        var menuPath = Uses("menu_path") ? ParseMenuPath(element, number, errors) : null;
        ScrollDirection? direction = null;
        if (Uses("direction"))
        {
            var directionText = OptionalString(element, "direction", 10, number, errors);
            if (directionText is not null)
            {
                direction = directionText switch
                {
                    "up" => ScrollDirection.Up,
                    "down" => ScrollDirection.Down,
                    "left" => ScrollDirection.Left,
                    "right" => ScrollDirection.Right,
                    _ => AddError<ScrollDirection?>(errors, new PlanIssue(number, "direction", "up, down, left, right 중 하나여야 합니다."), null),
                };
            }
        }

        var amount = Uses("amount") ? OptionalInt(element, "amount", 1, 20, number, errors) ?? 1 : 1;
        Hotkey? keys = null;
        if (Uses("keys"))
        {
            var keysText = OptionalString(element, "keys", 40, number, errors);
            if (keysText is not null)
            {
                if (HotkeyCatalog.TryParse(keysText, out var hotkey, out var keyError))
                {
                    keys = hotkey;
                    if (HotkeyCatalog.Classify(hotkey!).Safety == HotkeySafety.Blocked)
                    {
                        errors.Add(new PlanIssue(number, "keys", HotkeyCatalog.Classify(hotkey!).Reason));
                    }
                }
                else
                {
                    errors.Add(new PlanIssue(number, "keys", keyError!));
                }
            }
        }

        var point = Uses("point") ? ParsePoint(element, number, errors) : null;
        double? confidence = null;
        if (Uses("confidence") && element.TryGetProperty("confidence", out var confidenceElement) && confidenceElement.ValueKind != JsonValueKind.Null)
        {
            if (confidenceElement.ValueKind == JsonValueKind.Number && confidenceElement.TryGetDouble(out var c) && c is >= 0 and <= 1)
            {
                confidence = c;
            }
            else
            {
                errors.Add(new PlanIssue(number, "confidence", "0~1 사이의 숫자여야 합니다."));
            }
        }

        var path = Uses("path") ? OptionalString(element, "path", 260, number, errors) : null;
        var timeout = Uses("timeout_ms") ? OptionalInt(element, "timeout_ms", 100, 60000, number, errors) : null;
        var delay = Uses("delay_ms") ? OptionalInt(element, "delay_ms", 0, 10000, number, errors) : null;
        var message = Uses("message") ? OptionalString(element, "message", 2000, number, errors, allowNewlines: true) : null;
        var expect = Uses("expect") ? ParseExpect(element, options, number, errors) : null;

        if (descriptor.Type == ActionType.FocusWindow && window is not null && window.IsEmpty)
        {
            errors.Add(new PlanIssue(number, "window", "활성화할 창을 지정해야 합니다."));
        }

        if (descriptor.NeedsElement && target is not null && target.IsEmpty)
        {
            errors.Add(new PlanIssue(number, "element", "요소 선택자에 값이 하나 이상 있어야 합니다."));
        }

        if (errors.Count > before)
        {
            return null;
        }

        return new PlanStep
        {
            Id = id,
            Action = descriptor.Type,
            Description = description,
            App = app,
            Window = window,
            Element = target,
            Text = text,
            Mode = mode,
            Checked = isChecked,
            MenuPath = menuPath,
            Direction = direction,
            Amount = amount,
            Keys = keys,
            Point = point,
            Confidence = confidence,
            Path = path,
            TimeoutMs = timeout,
            DelayMs = delay,
            Message = message,
            Expect = expect,
        };
    }

    private static WindowSelector? ParseWindow(JsonElement step, int number, List<PlanIssue> errors)
    {
        if (!step.TryGetProperty("window", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new PlanIssue(number, "window", "객체여야 합니다(app, title_contains, handle)."));
            return null;
        }

        CheckFields(element, WindowFields, number, "window", errors);
        long? handle = null;
        if (element.TryGetProperty("handle", out var handleElement) && handleElement.ValueKind != JsonValueKind.Null)
        {
            if (handleElement.ValueKind == JsonValueKind.Number && handleElement.TryGetInt64(out var h) && h > 0)
            {
                handle = h;
            }
            else if (handleElement.ValueKind == JsonValueKind.String && long.TryParse(handleElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hs) && hs > 0)
            {
                handle = hs;
            }
            else
            {
                errors.Add(new PlanIssue(number, "window.handle", "양의 정수여야 합니다."));
            }
        }

        return new WindowSelector
        {
            App = OptionalAppId(element, "app", number, errors, "window.app"),
            TitleContains = OptionalString(element, "title_contains", 200, number, errors, field: "window.title_contains"),
            Handle = handle,
        };
    }

    private static ElementSelector? ParseElement(JsonElement step, int number, List<PlanIssue> errors)
    {
        if (!step.TryGetProperty("element", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new PlanIssue(number, "element", "객체여야 합니다(ref, automation_id, name, name_contains, control_type, class_name, index)."));
            return null;
        }

        CheckFields(element, ElementFields, number, "element", errors);
        var reference = OptionalString(element, "ref", 12, number, errors, field: "element.ref");
        if (reference is not null && !RefPattern().IsMatch(reference))
        {
            errors.Add(new PlanIssue(number, "element.ref", "e 뒤에 숫자가 오는 형식이어야 합니다(예: e12)."));
        }

        var controlType = OptionalString(element, "control_type", 40, number, errors, field: "element.control_type");
        if (controlType is not null)
        {
            if (!ControlTypes.Known.Contains(controlType))
            {
                errors.Add(new PlanIssue(number, "element.control_type", $"알 수 없는 컨트롤 형식 '{Security.Redactor.OneLine(controlType, 40)}'"));
            }
            else
            {
                controlType = ControlTypes.Canonical(controlType);
            }
        }

        return new ElementSelector
        {
            Ref = reference,
            AutomationId = OptionalString(element, "automation_id", 200, number, errors, field: "element.automation_id"),
            Name = OptionalString(element, "name", 200, number, errors, field: "element.name"),
            NameContains = OptionalString(element, "name_contains", 200, number, errors, field: "element.name_contains"),
            ControlType = controlType,
            ClassName = OptionalString(element, "class_name", 200, number, errors, field: "element.class_name"),
            Index = OptionalInt(element, "index", 0, 50, number, errors, "element.index"),
        };
    }

    private static StepExpectation? ParseExpect(JsonElement step, PlanParserOptions options, int number, List<PlanIssue> errors)
    {
        if (!step.TryGetProperty("expect", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new PlanIssue(number, "expect", "객체여야 합니다."));
            return null;
        }

        CheckFields(element, ExpectFields, number, "expect", errors);
        return new StepExpectation
        {
            ValueEquals = OptionalString(element, "value_equals", options.MaxTextLength, number, errors, allowNewlines: true, field: "expect.value_equals"),
            ValueContains = OptionalString(element, "value_contains", options.MaxTextLength, number, errors, allowNewlines: true, field: "expect.value_contains"),
            NameContains = OptionalString(element, "name_contains", 200, number, errors, field: "expect.name_contains"),
            Checked = OptionalBool(element, "checked", number, errors, "expect.checked"),
            Selected = OptionalBool(element, "selected", number, errors, "expect.selected"),
            Exists = OptionalBool(element, "exists", number, errors, "expect.exists"),
            Gone = OptionalBool(element, "gone", number, errors, "expect.gone"),
            WindowTitleContains = OptionalString(element, "window_title_contains", 200, number, errors, field: "expect.window_title_contains"),
        };
    }

    private static IReadOnlyList<string>? ParseMenuPath(JsonElement step, int number, List<PlanIssue> errors)
    {
        if (!step.TryGetProperty("menu_path", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 1 or > 6)
        {
            errors.Add(new PlanIssue(number, "menu_path", "1~6개의 메뉴 이름 배열이어야 합니다."));
            return null;
        }

        var items = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) || item.GetString()!.Length > 100)
            {
                errors.Add(new PlanIssue(number, "menu_path", "메뉴 이름은 1~100자의 문자열이어야 합니다."));
                return null;
            }

            items.Add(item.GetString()!.Trim());
        }

        return items;
    }

    private static NormalizedPoint? ParsePoint(JsonElement step, int number, List<PlanIssue> errors)
    {
        if (!step.TryGetProperty("point", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("x", out var x) || !element.TryGetProperty("y", out var y)
            || x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number)
        {
            errors.Add(new PlanIssue(number, "point", "{\"x\": 0~1000, \"y\": 0~1000} 형식이어야 합니다."));
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is not ("x" or "y"))
            {
                errors.Add(new PlanIssue(number, "point." + property.Name, "스키마에 없는 필드입니다."));
            }
        }

        var px = x.GetDouble();
        var py = y.GetDouble();
        if (double.IsNaN(px) || double.IsNaN(py) || px is < 0 or > 1000 || py is < 0 or > 1000)
        {
            errors.Add(new PlanIssue(number, "point", "좌표는 0~1000 범위여야 합니다."));
            return null;
        }

        return new NormalizedPoint((int)Math.Round(px), (int)Math.Round(py));
    }

    private static void CheckFields(JsonElement element, HashSet<string> allowed, int number, string prefix, List<PlanIssue> errors)
    {
        var any = false;
        foreach (var property in element.EnumerateObject())
        {
            any = true;
            if (!allowed.Contains(property.Name))
            {
                errors.Add(new PlanIssue(number, $"{prefix}.{property.Name}", "스키마에 없는 필드입니다."));
            }
        }

        if (!any && prefix != "expect")
        {
            errors.Add(new PlanIssue(number, prefix, "빈 객체입니다."));
        }
    }

    private static string? OptionalString(JsonElement element, string name, int maxLength, int? number, List<PlanIssue> errors, bool allowNewlines = false, string? field = null)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        field ??= name;
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new PlanIssue(number, field, "문자열이어야 합니다."));
            return null;
        }

        var text = value.GetString() ?? string.Empty;
        if (text.Length > maxLength)
        {
            errors.Add(new PlanIssue(number, field, $"너무 깁니다({text.Length}자, 상한 {maxLength}자)."));
            return null;
        }

        foreach (var ch in text)
        {
            if (ch == '\0' || (char.IsControl(ch) && !(allowNewlines && ch is '\n' or '\r' or '\t')))
            {
                errors.Add(new PlanIssue(number, field, "허용되지 않는 제어 문자가 있습니다."));
                return null;
            }
        }

        return text;
    }

    private static string? OptionalAppId(JsonElement element, string name, int? number, List<PlanIssue> errors, string? field = null)
    {
        var text = OptionalString(element, name, 64, number, errors, field: field);
        if (text is not null && !AppIdPattern().IsMatch(text))
        {
            errors.Add(new PlanIssue(number, field ?? name, "앱 ID는 영문·숫자·.-_ 1~64자여야 합니다."));
            return null;
        }

        return text;
    }

    private static bool? OptionalBool(JsonElement element, string name, int? number, List<PlanIssue> errors, string? field = null)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        errors.Add(new PlanIssue(number, field ?? name, "true 또는 false여야 합니다."));
        return null;
    }

    private static int? OptionalInt(JsonElement element, string name, int min, int max, int? number, List<PlanIssue> errors, string? field = null)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) && result >= min && result <= max)
        {
            return result;
        }

        errors.Add(new PlanIssue(number, field ?? name, $"{min}~{max} 사이의 정수여야 합니다."));
        return null;
    }

    private static T AddError<T>(List<PlanIssue> errors, PlanIssue issue, T fallback)
    {
        errors.Add(issue);
        return fallback;
    }

    [GeneratedRegex(@"<think(?:ing)?>[\s\S]*?</think(?:ing)?>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"```(?:json|JSON)?\s*(?<body>[\s\S]*?)```")]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"^e\d{1,6}$")]
    private static partial Regex RefPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,64}$")]
    private static partial Regex AppIdPattern();
}
