using System.Diagnostics;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.Core.Evaluation;

/// <summary>A fixed request against a freshly started test app, with an objective success check on the live UI.</summary>
public sealed record EvaluationScenario(string Id, string Name, string Request, Func<IDesktopAutomation, long, TaskRun, (bool Success, string Note)> Check);

public sealed record EvaluationResult
{
    public required string ProfileName { get; init; }
    public required string ModelId { get; init; }
    public required string ScenarioId { get; init; }
    public required string ScenarioName { get; init; }
    public bool IsMock { get; init; }

    /// <summary>The model produced a schema-valid plan (independent of what happened on screen).</summary>
    public bool PlanValid { get; init; }

    /// <summary>The live UI ended in the expected state.</summary>
    public bool GuiSuccess { get; init; }
    public int Steps { get; init; }
    public long DurationMs { get; init; }
    public int ModelCalls { get; init; }
    public int ModelErrors { get; init; }
    public int WrongTargets { get; init; }
    public int Interventions { get; init; }
    public required string Status { get; init; }
    public string? Note { get; init; }
}

/// <summary>A test-app window prepared for one run, and how to clean it up.</summary>
public sealed record EvaluationTarget(long WindowHandle, Func<Task> Cleanup);

/// <summary>
/// Runs the same scenarios with several models in a fixed environment (a fresh test app per run). Approvals are
/// answered "no" automatically and counted as interventions, so runs never change anything risky unattended.
/// </summary>
public sealed class ModelEvaluator(IDesktopAutomation desktop, ExecutorOptions? options = null)
{
    public static IReadOnlyList<EvaluationScenario> DefaultScenarios { get; } =
    [
        new("counter-twice", "카운트 두 번 증가", "테스트 앱에서 '카운트 증가' 버튼을 두 번 눌러 줘.", (d, h, _) =>
        {
            var counter = d.FindElements(h, new ElementQuery { AutomationId = "CounterText" }).FirstOrDefault();
            return (counter?.Name == "카운트: 2", $"카운트 표시: {counter?.Name ?? "(없음)"}");
        }),
        new("input-text", "입력 상자에 문장 입력", "테스트 앱의 입력 상자에 '평가 문장입니다'를 입력해 줘.", (d, h, _) =>
        {
            var input = d.FindElements(h, new ElementQuery { AutomationId = "InputBox" }).FirstOrDefault();
            return (input?.Value == "평가 문장입니다", $"입력 상자 값: {Security.Redactor.OneLine(input?.Value, 40)}");
        }),
        new("read-checkbox", "확인란 상태 보고", "테스트 앱의 '알림 표시' 확인란이 켜져 있는지 알려 줘.", (_, _, run) =>
        {
            var reported = run.Report ?? string.Empty;
            var ok = run.Status == TaskRunStatus.Completed && (reported.Contains("해제", StringComparison.Ordinal) || reported.Contains("꺼", StringComparison.Ordinal)
                                                                || reported.Contains("off", StringComparison.OrdinalIgnoreCase) || reported.Contains("않", StringComparison.Ordinal));
            return (ok, $"보고: {Security.Redactor.OneLine(reported, 80)}");
        }),
        new("select-combo", "색상 선택", "테스트 앱의 색상 목록에서 '파랑'을 선택해 줘.", (d, h, _) =>
        {
            var combo = d.FindElements(h, new ElementQuery { AutomationId = "ColorCombo" }).FirstOrDefault();
            return (combo?.Value == "파랑", $"색상 값: {combo?.Value ?? "(없음)"}");
        }),
    ];

    public async Task<IReadOnlyList<EvaluationResult>> RunAsync(
        IReadOnlyList<(string ProfileName, IModelClient Client)> models,
        IReadOnlyList<EvaluationScenario> scenarios,
        SafetySettings baseSettings,
        Func<CancellationToken, Task<EvaluationTarget>> prepareTarget,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<EvaluationResult>();
        foreach (var (profileName, client) in models)
        {
            foreach (var scenario in scenarios)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"{profileName}: {scenario.Name} 실행 중…");
                EvaluationTarget target;
                try
                {
                    target = await prepareTarget(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
                {
                    results.Add(new EvaluationResult
                    {
                        ProfileName = profileName,
                        ModelId = client.Descriptor.ModelId,
                        ScenarioId = scenario.Id,
                        ScenarioName = scenario.Name,
                        IsMock = client.Descriptor.IsMock || desktop.IsMock,
                        Status = "준비 실패",
                        Note = ex.Message,
                    });
                    continue;
                }

                try
                {
                    results.Add(await RunOneAsync(profileName, client, scenario, baseSettings, target, cancellationToken).ConfigureAwait(false));
                }
                finally
                {
                    await target.Cleanup().ConfigureAwait(false);
                }
            }
        }

        return results;
    }

    private async Task<EvaluationResult> RunOneAsync(string profileName, IModelClient client, EvaluationScenario scenario, SafetySettings baseSettings, EvaluationTarget target, CancellationToken cancellationToken)
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<SafetySettings>(System.Text.Json.JsonSerializer.Serialize(baseSettings))!;
        settings.RequirePlanReview = false;
        var approvals = ScriptedApprovalHandler.DenyAll();
        var executor = new TaskExecutor(desktop, new PolicyEngine(), approvals, options: options);
        using var control = new ExecutionControl();
        var run = new TaskRun { Request = scenario.Request, Source = TaskSource.Evaluation, InitialWindowHandle = target.WindowHandle, IsMockDesktop = desktop.IsMock };
        var planner = new ModelPlanner(client, settings.AllowVision);
        var stopwatch = Stopwatch.StartNew();
        await executor.RunAsync(run, planner, settings, control, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        var (success, note) = desktop.GetWindow(target.WindowHandle) is null
            ? (false, "테스트 앱 창이 닫혔습니다.")
            : scenario.Check(desktop, target.WindowHandle, run);
        var planValid = run.Rounds > 0;
        return new EvaluationResult
        {
            ProfileName = profileName,
            ModelId = client.Descriptor.ModelId,
            ScenarioId = scenario.Id,
            ScenarioName = scenario.Name,
            IsMock = client.Descriptor.IsMock || desktop.IsMock,
            PlanValid = planValid,
            GuiSuccess = success,
            Steps = run.Steps.Count(s => s.Status is not (StepRunStatus.Pending or StepRunStatus.Skipped)),
            DurationMs = stopwatch.ElapsedMilliseconds,
            ModelCalls = run.ModelCalls,
            ModelErrors = run.ModelErrors,
            WrongTargets = run.Steps.Count(s => s.Error is ExecutionErrorKind.ElementNotFound or ExecutionErrorKind.ElementAmbiguous or ExecutionErrorKind.WindowNotFound
                                                || (s.Error == ExecutionErrorKind.PolicyDenied && s.Decision?.Verdict == PolicyVerdict.Deny)),
            Interventions = run.UserInterventions,
            Status = TaskStatusText.Korean(run.Status),
            Note = note + (run.StatusMessage is null ? string.Empty : $" | {Security.Redactor.OneLine(run.StatusMessage, 120)}"),
        };
    }
}
