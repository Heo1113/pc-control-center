using System.Diagnostics;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Automation.Mock;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.ModelProviders;

public sealed record VisionTestResult(bool Passed, bool ImagesRejected, string Message, TimeSpan Elapsed);

/// <summary>
/// "Does this model really read images?" Two synthetic pictures (a red and a blue square — no screen content) are sent;
/// both colours must be named correctly. Guessing both is unlikely, so a pass is real evidence of image input.
/// </summary>
public static class VisionTester
{
    public static async Task<VisionTestResult> RunAsync(IModelClient client, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var cases = new (string Expected, string Korean, (byte, byte, byte) Color)[]
        {
            ("red", "빨", (220, 30, 30)),
            ("blue", "파", (30, 60, 220)),
        };
        foreach (var (expected, korean, color) in cases)
        {
            var png = PngWriter.SolidWithRectangle(256, 256, (255, 255, 255), new PixelRect(64, 64, 128, 128), color);
            var response = await client.CompleteAsync(new ModelRequest
            {
                Messages =
                [
                    new ChatMessage(ChatRole.User, "What is the color of the large square in the center of this image? Answer with one lowercase English color word only.", [new ChatImage(png)]),
                ],
                MaxTokens = 512,
                Temperature = 0,
            }, cancellationToken).ConfigureAwait(false);

            if (!response.Success)
            {
                var rejected = response.Error is ModelErrorKind.BadRequest or ModelErrorKind.ServerError or ModelErrorKind.InvalidResponse;
                return new VisionTestResult(false, rejected,
                    rejected ? $"서버가 이미지 요청을 처리하지 못했습니다(텍스트 전용 모델로 보임): {response.ErrorMessage}" : $"테스트 요청 실패: {response.ErrorMessage}",
                    stopwatch.Elapsed);
            }

            var answer = StripThinking(response.Content ?? string.Empty).ToLowerInvariant();
            if (!answer.Contains(expected, StringComparison.Ordinal) && !answer.Contains(korean, StringComparison.Ordinal))
            {
                return new VisionTestResult(false, false, $"이미지의 색({expected})을 맞히지 못했습니다(답: \"{Core.Security.Redactor.OneLine(answer, 60)}\"). 이미지를 실제로 읽지 못하는 모델로 판단합니다.", stopwatch.Elapsed);
            }
        }

        return new VisionTestResult(true, false, "빨강·파랑 두 이미지를 모두 맞혔습니다. 이미지 입력이 실제로 동작합니다.", stopwatch.Elapsed);
    }

    private static string StripThinking(string text)
    {
        var end = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return end >= 0 ? text[(end + 8)..] : text;
    }
}

public sealed record PlanFormatTestResult(bool Valid, bool TargetsExpectedElement, string Message, TimeSpan Elapsed, int ModelCalls, TaskPlan? Plan);

/// <summary>
/// Checks the model's real answer format with the real prompt: a simulated test-app snapshot and the request
/// "카운트 증가 버튼을 한 번 눌러 줘". Nothing on the desktop is touched.
/// </summary>
public static class PlanFormatTester
{
    public const string Request = "카운트 증가 버튼을 한 번 눌러 줘";

    public static async Task<PlanFormatTestResult> RunAsync(IModelClient client, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var desktop = MockDesktop.CreateDemo();
        var window = desktop.ListWindows()[0];
        var settings = new SafetySettings { RequirePlanReview = false };
        settings.AllowedApps.AddRange(AllowedApp.Defaults());
        var registry = new ElementRegistry();
        var context = new PlanningContext
        {
            Request = Request,
            Run = new TaskRun { Request = Request, Source = TaskSource.Evaluation },
            Settings = settings,
            AllowedWindows = desktop.ListWindows(),
            Snapshot = desktop.CaptureTree(window.Handle, new TreeCaptureOptions()),
            Registry = registry,
        };
        var planner = new ModelPlanner(client, visionAllowedBySettings: false);
        var outcome = await planner.CreatePlanAsync(context, cancellationToken).ConfigureAwait(false);
        if (outcome.Plan is null)
        {
            return new PlanFormatTestResult(false, false, outcome.Error ?? "계획 없음", stopwatch.Elapsed, outcome.ModelCalls, null);
        }

        var targets = outcome.Plan.Steps.Any(s => s.Action == ActionType.Click && s.Element is { } e
            && (e.AutomationId == "IncrementButton" || e.Name == "카운트 증가" || (e.Ref is not null && registry.Get(e.Ref)?.AutomationId == "IncrementButton")));
        return new PlanFormatTestResult(true, targets,
            targets ? $"유효한 계획({outcome.Plan.Steps.Count}단계), 올바른 버튼을 선택했습니다." : $"유효한 계획({outcome.Plan.Steps.Count}단계)이지만 기대한 버튼을 누르지 않습니다.",
            stopwatch.Elapsed, outcome.ModelCalls, outcome.Plan);
    }
}
