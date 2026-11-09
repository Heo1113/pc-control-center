using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsAiOperator.Core.Planning;

namespace WindowsAiOperator.ModelProviders;

public enum MockModelBehavior
{
    /// <summary>Plans a few known example requests correctly.</summary>
    Examples,

    /// <summary>Answers with text that is not JSON.</summary>
    Garbage,

    /// <summary>Answers with an action outside the schema (e.g. running a shell command).</summary>
    ForbiddenAction,

    /// <summary>Always fails like an unreachable server.</summary>
    Offline,
}

/// <summary>
/// A deterministic stand-in for a model (tests, CI smoke runs, demo without a server). It is always labelled MOCK and
/// its results are never presented as a real model's. It plans only a handful of example requests.
/// </summary>
public sealed partial class MockModelClient(MockModelBehavior behavior = MockModelBehavior.Examples) : IModelClient
{
    public ModelDescriptor Descriptor { get; } = new()
    {
        ProfileName = "MOCK",
        ModelId = behavior == MockModelBehavior.Examples ? "mock-planner" : $"mock-{behavior.ToString().ToLowerInvariant()}",
        BaseUrl = "mock://local",
        IsMock = true,
        VisionReason = "MOCK 모델은 이미지를 읽지 않습니다.",
    };

    public int Calls { get; private set; }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        var elapsed = TimeSpan.FromMilliseconds(5);
        switch (behavior)
        {
            case MockModelBehavior.Offline:
                return Task.FromResult(ModelResponse.Fail(ModelErrorKind.Connection, "MOCK: 서버에 연결할 수 없습니다.", elapsed));
            case MockModelBehavior.Garbage:
                return Task.FromResult(Ok("물론이죠! 메모장을 열어서 입력하겠습니다. 잠시만요.", elapsed));
            case MockModelBehavior.ForbiddenAction:
                return Task.FromResult(Ok("""{"steps":[{"action":"run_shell","command":"del /s C:\\Users"}]}""", elapsed));
        }

        var user = request.Messages.Last(m => m.Role == ChatRole.User).Text;
        var firstUser = request.Messages.First(m => m.Role == ChatRole.User).Text;
        var requestText = ExtractRequest(firstUser);
        var nextId = NextId().Match(firstUser) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
        return Task.FromResult(Ok(Plan(requestText, firstUser, nextId, user), elapsed));
    }

    private static ModelResponse Ok(string content, TimeSpan elapsed) =>
        new() { Success = true, Content = content, Elapsed = elapsed, StatusCode = 200, UsedFormat = ResponseFormatKind.None };

    private static string Plan(string request, string context, int id, string lastUser)
    {
        if (lastUser.Contains("rejected by the validator", StringComparison.Ordinal))
        {
            return Json(new { status = "cannot_do", message = "MOCK: 수정할 수 없습니다.", steps = Array.Empty<object>() });
        }

        if (request.Contains("메모장", StringComparison.Ordinal))
        {
            var text = Quoted().Match(request) is { Success: true } q ? q.Groups["t"].Value : "테스트 문장입니다.";
            var documentRef = RefFor(context, "Document");
            if (documentRef is null)
            {
                return Json(new
                {
                    summary = "메모장 실행 후 화면 확인",
                    target_app = "notepad",
                    status = "ok",
                    steps = new object[]
                    {
                        new { id, action = "launch_app", app = "notepad", description = "메모장 실행" },
                        new { id = id + 1, action = "observe", window = new { app = "notepad" }, description = "메모장 화면 확인" },
                    },
                });
            }

            return Json(new
            {
                summary = "메모장에 텍스트 입력",
                target_app = "notepad",
                status = "ok",
                steps = new object[]
                {
                    new { id, action = "set_text", window = new { app = "notepad" }, element = new { @ref = documentRef }, text, mode = "append", description = "문장 입력" },
                    new { id = id + 1, action = "finish", message = "메모장에 문장을 입력했습니다(저장하지 않음)." },
                },
            });
        }

        if (request.Contains("카운트", StringComparison.Ordinal))
        {
            return Json(new
            {
                summary = "테스트 앱 카운트 증가",
                target_app = "test-target",
                status = "ok",
                steps = new object[]
                {
                    new { id, action = "click", window = new { app = "test-target" }, element = new { automation_id = "IncrementButton" }, description = "카운트 증가 버튼 누르기" },
                    new { id = id + 1, action = "read_element", element = new { automation_id = "CounterText" }, description = "카운트 읽기" },
                    new { id = id + 2, action = "finish", message = $"현재 {{{{s{id + 1}.value}}}}입니다." },
                },
            });
        }

        if (request.Contains("체크", StringComparison.Ordinal) || request.Contains("확인란", StringComparison.Ordinal) || request.Contains("알림", StringComparison.Ordinal))
        {
            return Json(new
            {
                summary = "확인란 상태 확인",
                target_app = "test-target",
                status = "ok",
                steps = new object[]
                {
                    new { id, action = "read_element", window = new { app = "test-target" }, element = new { automation_id = "NotifyCheckBox" }, description = "알림 표시 확인란 읽기" },
                    new { id = id + 1, action = "finish", message = $"'알림 표시' 확인란은 {{{{s{id}.state}}}} 상태입니다." },
                },
            });
        }

        return Json(new
        {
            status = "need_user_input",
            message = "MOCK 모델은 예시 요청(메모장 입력, 카운트 증가, 확인란 확인)만 처리합니다. 실제 모델을 연결하세요.",
            steps = Array.Empty<object>(),
        });
    }

    private static string ExtractRequest(string context)
    {
        var lines = context.Split('\n');
        var index = Array.FindIndex(lines, l => l.StartsWith("USER REQUEST", StringComparison.Ordinal));
        return index >= 0 && index + 1 < lines.Length ? lines[index + 1].Trim() : string.Empty;
    }

    private static string? RefFor(string context, string controlType)
    {
        var match = Regex.Match(context, @"\[(e\d+)\] " + controlType + @"\b");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [GeneratedRegex(@"[""“'‘](?<t>[^""”'’]{1,200})[""”'’]")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"Number new step ids from (\d+)")]
    private static partial Regex NextId();
}
