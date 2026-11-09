using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.Core.Planning;

/// <summary>
/// The AI planner: prompt → model → <see cref="PlanParser"/>. An invalid answer gets one correction round with the
/// validator's errors; a second invalid answer is rejected. A model failure never throws: it becomes a failed outcome.
/// </summary>
public sealed class ModelPlanner : IPlanSource
{
    private readonly IModelClient _client;
    private readonly IOperatorLog _log;
    private readonly bool _visionAllowedBySettings;

    public ModelPlanner(IModelClient client, bool visionAllowedBySettings, IOperatorLog? log = null)
    {
        _client = client;
        _visionAllowedBySettings = visionAllowedBySettings;
        _log = log ?? NullOperatorLog.Instance;
    }

    public string Label => _client.Descriptor.Label;
    public bool UsesModel => true;
    public bool CanReplan => true;
    public bool VisionAvailable => _client.Descriptor.CanSendImages && _visionAllowedBySettings;

    /// <summary>Latest model output that was rejected (for the evaluation report).</summary>
    public string? LastRejectedOutput { get; private set; }

    public Task<PlanOutcome> CreatePlanAsync(PlanningContext context, CancellationToken cancellationToken) => PlanAsync(context, cancellationToken);

    public Task<PlanOutcome> ReplanAsync(PlanningContext context, CancellationToken cancellationToken) => PlanAsync(context, cancellationToken);

    private async Task<PlanOutcome> PlanAsync(PlanningContext context, CancellationToken cancellationToken)
    {
        var descriptor = _client.Descriptor;
        var vision = VisionAvailable && context.Screenshot is not null;
        var formatted = context.Snapshot is null ? null : SnapshotFormatter.Format(context.Snapshot, context.Registry);
        var system = PromptBuilder.SystemPrompt(context.Settings, VisionAvailable);
        var user = PromptBuilder.ContextMessage(context, formatted);
        var images = vision ? new[] { new ChatImage(context.Screenshot!.Png) } : null;
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, user, images),
        };
        var notes = new List<string>();
        if (formatted is not null)
        {
            notes.Add($"모델에 UI 요소 {formatted.ElementCount}개의 구조 정보를 보냈습니다{(vision ? "(스크린샷 포함)" : string.Empty)}.");
        }

        var calls = 0;
        var errors = 0;
        var parserOptions = new PlanParserOptions
        {
            MaxSteps = context.Settings.MaxStepsPerTask,
            MaxTextLength = context.Settings.MaxTextLength,
            AllowClickPoint = VisionAvailable,
        };

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (context.Run.ModelCalls + calls >= context.Settings.MaxModelCallsPerTask)
            {
                return PlanOutcome.Failed($"모델 호출 한도({context.Settings.MaxModelCallsPerTask}회)에 도달했습니다.", ExecutionErrorKind.LimitReached, calls, errors);
            }

            var response = await _client.CompleteAsync(new ModelRequest
            {
                Messages = messages,
                ResponseFormat = descriptor.StructuredOutput switch
                {
                    StructuredOutputMode.PromptOnly => ResponseFormatKind.None,
                    StructuredOutputMode.JsonObject => ResponseFormatKind.JsonObject,
                    _ => ResponseFormatKind.JsonSchema,
                },
                JsonSchema = PlanJsonSchema.Build(VisionAvailable, context.Settings.MaxStepsPerTask),
                SchemaName = PlanJsonSchema.SchemaName,
                MaxTokens = descriptor.MaxTokens,
                Temperature = descriptor.Temperature,
            }, cancellationToken).ConfigureAwait(false);
            calls += response.Calls;

            if (!response.Success)
            {
                errors++;
                var kind = response.Error is ModelErrorKind.Connection or ModelErrorKind.Timeout or ModelErrorKind.Authentication or ModelErrorKind.NotFound or ModelErrorKind.RemoteNotAllowed or ModelErrorKind.Configuration
                    ? ExecutionErrorKind.ModelUnavailable
                    : ExecutionErrorKind.ModelError;
                var message = $"모델 호출 실패 — {ModelErrorText.Korean(response.Error ?? ModelErrorKind.InvalidResponse)}: {response.ErrorMessage}";
                _log.Warn("planner", message);
                return PlanOutcome.Failed(message, kind, calls, errors);
            }

            var parsed = PlanParser.Parse(response.Content, parserOptions);
            if (parsed.IsValid)
            {
                if (response.FinishReason == "length")
                {
                    notes.Add("모델 응답이 최대 토큰에서 끊겼지만 계획은 유효했습니다.");
                }

                notes.Add($"모델 응답 {response.Elapsed.TotalSeconds:0.0}초, 형식 {response.UsedFormat}.");
                return new PlanOutcome(parsed.Plan, null, null, calls, errors, notes);
            }

            errors++;
            LastRejectedOutput = response.Content;
            var reasons = string.Join("; ", parsed.Errors.Take(6));
            _log.Warn("planner", $"model output rejected (attempt {attempt}): {reasons}");
            if (attempt == 2)
            {
                return PlanOutcome.Failed($"모델 출력이 동작 스키마에 맞지 않아 거부했습니다: {reasons}", ExecutionErrorKind.ModelOutputRejected, calls, errors);
            }

            notes.Add($"모델 출력이 스키마에 맞지 않아 수정을 한 번 요청했습니다: {reasons}");
            messages.Add(new ChatMessage(ChatRole.Assistant, Security.Redactor.Sanitize(response.Content, 8000)));
            messages.Add(new ChatMessage(ChatRole.User, PromptBuilder.CorrectionMessage(parsed.Errors)));
        }

        return PlanOutcome.Failed("계획을 만들지 못했습니다.", ExecutionErrorKind.ModelError, calls, errors);
    }
}
