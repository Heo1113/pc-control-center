using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Files;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Core.Security;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.Core.Execution;

/// <summary>
/// Runs a task: plan → (plan review) → for each step: resolve the live target, ask the policy, ask the user when
/// needed, re-validate after approval, act through <see cref="IDesktopAutomation"/>, verify, report.
/// Model output is only ever data: a validated <see cref="TaskPlan"/> of predefined actions.
/// </summary>
public sealed partial class TaskExecutor
{
    private const int ScreenChangeThresholdBits = 12;

    private readonly IDesktopAutomation _desktop;
    private readonly PolicyEngine _policy;
    private readonly IApprovalHandler _approvals;
    private readonly ITaskObserver _observer;
    private readonly IOperatorLog _log;
    private readonly ExecutorOptions _options;
    private readonly WindowResolver _windows;
    private readonly ElementResolver _elements;
    private readonly Verifier _verifier;

    public TaskExecutor(IDesktopAutomation desktop, PolicyEngine policy, IApprovalHandler approvals, ITaskObserver? observer = null, IOperatorLog? log = null, ExecutorOptions? options = null)
    {
        _desktop = desktop;
        _policy = policy;
        _approvals = approvals;
        _observer = observer ?? NullTaskObserver.Instance;
        _log = log ?? NullOperatorLog.Instance;
        _options = options ?? new ExecutorOptions();
        _windows = new WindowResolver(desktop);
        _elements = new ElementResolver(desktop);
        _verifier = new Verifier(desktop, _elements, _options);
    }

    public async Task<TaskRun> RunAsync(TaskRun run, IPlanSource planSource, SafetySettings liveSettings, ExecutionControl control, CancellationToken cancellationToken = default)
    {
        var settings = Clone(liveSettings);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, control.StopToken);
        var token = linked.Token;
        var state = new RunState(settings);
        run.StartedAt = DateTimeOffset.Now;
        run.ModelLabel ??= planSource.Label;
        control.StateChanged += OnControlChanged;
        state.Active.Start();

        try
        {
            SetStatus(run, TaskRunStatus.Planning, "계획을 만드는 중입니다.");
            var initialContext = BuildContext(run, planSource, state, failure: null, observeHandle: run.InitialWindowHandle ?? ForegroundAllowedWindow(settings));
            var outcome = await planSource.CreatePlanAsync(initialContext, token).ConfigureAwait(false);
            Account(run, outcome);
            if (!await AcceptPlanAsync(run, planSource, outcome, state, settings, token).ConfigureAwait(false))
            {
                return run;
            }

            SetStatus(run, TaskRunStatus.Running, "실행 중입니다.");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var record = run.Steps.Where(s => s.Status == StepRunStatus.Pending).OrderBy(s => s.Sequence).FirstOrDefault();
                if (record is null)
                {
                    Finish(run, TaskRunStatus.Completed, null, run.Report ?? "계획한 단계를 모두 실행했습니다.");
                    return run;
                }

                if (state.Active.Elapsed > TimeSpan.FromSeconds(settings.TaskTimeoutSeconds))
                {
                    EndWithLimit(run, $"작업 제한 시간({settings.TaskTimeoutSeconds}초)을 넘었습니다.");
                    return run;
                }

                if (state.ExecutedSteps >= settings.MaxStepsPerTask)
                {
                    EndWithLimit(run, $"최대 실행 단계({settings.MaxStepsPerTask}개)에 도달했습니다.");
                    return run;
                }

                await WaitWhilePausedAsync(run, control, state, token).ConfigureAwait(false);
                state.ExecutedSteps++;
                var result = await ExecuteStepAsync(run, record, planSource, state, control, token).ConfigureAwait(false);
                UpdateLastKnownState(run, record);

                switch (result.Kind)
                {
                    case StepResultKind.Succeeded:
                        state.ConsecutiveFailures = 0;
                        continue;

                    case StepResultKind.Finish:
                        SkipPending(run, "완료 보고 뒤의 단계라 실행하지 않았습니다.");
                        Finish(run, TaskRunStatus.Completed, null, result.Message);
                        return run;

                    case StepResultKind.AskUser:
                        run.UserInterventions++;
                        SkipPending(run, "사용자 확인이 필요해 실행하지 않았습니다.");
                        Finish(run, TaskRunStatus.NeedsUserInput, null, result.Message);
                        return run;

                    case StepResultKind.Observe:
                        if (!CanReplan(planSource, state, run, settings))
                        {
                            Event(run, TaskEventLevel.Info, "화면 상태를 수집했습니다(고정 계획이라 다시 계획하지 않음).");
                            continue;
                        }

                        SkipPending(run, "새 화면 정보로 다시 계획해 대체되었습니다.");
                        if (!await ReplanAsync(run, planSource, state, settings, null, result.WindowHandle, token).ConfigureAwait(false))
                        {
                            return run;
                        }

                        continue;

                    case StepResultKind.Denied:
                        SkipPending(run, "앞 단계가 차단·거부되어 실행하지 않았습니다.");
                        Finish(run, TaskRunStatus.Denied, result.Error, result.Message);
                        return run;

                    case StepResultKind.Failed:
                        state.ConsecutiveFailures++;
                        if (state.ConsecutiveFailures >= settings.MaxConsecutiveFailures)
                        {
                            SkipPending(run, "연속 실패로 자동 중단되어 실행하지 않았습니다.");
                            Finish(run, TaskRunStatus.Failed, ExecutionErrorKind.LimitReached,
                                $"연속 {state.ConsecutiveFailures}번 실패해 자동으로 중단했습니다. 마지막 오류: {result.Message}");
                            return run;
                        }

                        if (result.Retryable && !record.Performed && record.Attempts <= _options.MaxResolveRetries)
                        {
                            Event(run, TaskEventLevel.Warning, $"{record.Sequence}단계를 다시 시도합니다(화면과 대상을 다시 탐색): {result.Message}");
                            record.Status = StepRunStatus.Pending;
                            record.Message = "재시도 대기";
                            SafeNotify(() => _observer.StepChanged(run, record));
                            continue;
                        }

                        if (result.Error is not (ExecutionErrorKind.WindowClosed or ExecutionErrorKind.Timeout or ExecutionErrorKind.AccessDenied)
                            && CanReplan(planSource, state, run, settings))
                        {
                            SkipPending(run, "실패 후 다시 계획해 대체되었습니다.");
                            var failure = $"{record.Sequence}단계({record.Step.Action.WireName()}) 실패: {result.Message}" +
                                          (record.Performed ? " (동작은 실행되었으나 결과가 기대와 다름 — 같은 동작을 그대로 반복하지 말 것)" : string.Empty);
                            if (!await ReplanAsync(run, planSource, state, settings, failure, state.CurrentWindow, token).ConfigureAwait(false))
                            {
                                return run;
                            }

                            continue;
                        }

                        SkipPending(run, "앞 단계가 실패해 실행하지 않았습니다.");
                        Finish(run, TaskRunStatus.Failed, result.Error, result.Message);
                        return run;
                }
            }
        }
        catch (OperationCanceledException) when (control.IsStopRequested || cancellationToken.IsCancellationRequested)
        {
            foreach (var step in run.Steps.Where(s => s.Status is StepRunStatus.Running or StepRunStatus.AwaitingApproval))
            {
                step.Status = StepRunStatus.Cancelled;
                step.Message = step.Performed ? "동작은 실행되었고 결과 확인 전에 중단되었습니다." : "사용자가 중단했습니다.";
                step.EndedAt = DateTimeOffset.Now;
                SafeNotify(() => _observer.StepChanged(run, step));
            }

            SkipPending(run, "작업이 중단되어 실행하지 않았습니다.", StepRunStatus.Cancelled);
            Finish(run, TaskRunStatus.Cancelled, ExecutionErrorKind.Cancelled, control.StopReason ?? "작업을 중단했습니다.");
            return run;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("executor", "작업 실행 중 예외", ex);
            SkipPending(run, "내부 오류로 실행하지 않았습니다.");
            Finish(run, TaskRunStatus.Failed, ExecutionErrorKind.Internal, $"내부 오류: {Redactor.OneLine(ex.Message, 200)}");
            return run;
        }
        finally
        {
            control.StateChanged -= OnControlChanged;
            state.Active.Stop();
        }

        void OnControlChanged()
        {
            if (TaskStatusText.IsFinal(run.Status))
            {
                return;
            }

            if (control.IsPaused && run.Status != TaskRunStatus.Paused)
            {
                state.StatusBeforePause = run.Status;
                SetStatus(run, TaskRunStatus.Paused, control.PauseReason ?? "일시정지");
            }
            else if (!control.IsPaused && run.Status == TaskRunStatus.Paused)
            {
                SetStatus(run, state.StatusBeforePause ?? TaskRunStatus.Running, "다시 실행합니다.");
            }
        }
    }

    private async Task<bool> AcceptPlanAsync(TaskRun run, IPlanSource planSource, PlanOutcome outcome, RunState state, SafetySettings settings, CancellationToken token)
    {
        if (outcome.Plan is null)
        {
            Finish(run, TaskRunStatus.Failed, outcome.ErrorKind ?? ExecutionErrorKind.ModelError, outcome.Error ?? "계획을 만들지 못했습니다.");
            return false;
        }

        var plan = outcome.Plan;
        foreach (var note in outcome.Notes ?? [])
        {
            Event(run, TaskEventLevel.Info, note);
        }

        foreach (var warning in plan.Warnings)
        {
            Event(run, TaskEventLevel.Warning, "계획 경고: " + warning);
        }

        if (plan.Status != PlanStatus.Ok)
        {
            run.PlanSummary ??= plan.Summary;
            var status = plan.Status == PlanStatus.NeedUserInput ? TaskRunStatus.NeedsUserInput : TaskRunStatus.Failed;
            if (status == TaskRunStatus.NeedsUserInput)
            {
                run.UserInterventions++;
            }

            Finish(run, status, status == TaskRunStatus.Failed ? ExecutionErrorKind.NotSupported : null, plan.Message ?? "모델이 이 요청을 수행할 수 없다고 답했습니다.");
            return false;
        }

        state.Round++;
        run.Rounds = state.Round;
        run.PlanSummary ??= plan.Summary;
        run.TargetApp ??= plan.TargetApp;
        foreach (var step in plan.Steps)
        {
            run.Steps.Add(new StepRecord { Sequence = run.Steps.Count + 1, Round = state.Round, Step = step });
            state.NextStepId = Math.Max(state.NextStepId, step.Id + 1);
        }

        SafeNotify(() => _observer.StepsChanged(run));
        if (settings.RequirePlanReview && planSource.UsesModel)
        {
            SetStatus(run, TaskRunStatus.AwaitingPlanReview, state.Round == 1 ? "계획을 확인하고 [실행]을 누르세요." : "다시 만든 계획을 확인하세요.");
            state.Active.Stop();
            bool approved;
            try
            {
                approved = await _approvals.ReviewPlanAsync(new PlanReviewRequest(run.Id, run.Request, plan, planSource.Label, plan.Warnings), token).ConfigureAwait(false);
            }
            finally
            {
                state.Active.Start();
            }

            if (!approved)
            {
                SkipPending(run, "사용자가 계획을 실행하지 않았습니다.", StepRunStatus.Cancelled);
                Finish(run, TaskRunStatus.Cancelled, ExecutionErrorKind.ApprovalDenied, "계획을 실행하지 않았습니다(사용자 취소).");
                return false;
            }

            SetStatus(run, TaskRunStatus.Running, "실행 중입니다.");
        }

        return true;
    }

    private async Task<bool> ReplanAsync(TaskRun run, IPlanSource planSource, RunState state, SafetySettings settings, string? failure, long? observeHandle, CancellationToken token)
    {
        state.Replans++;
        SetStatus(run, TaskRunStatus.Planning, failure is null ? "새 화면 정보로 남은 단계를 계획합니다." : "실패 원인을 반영해 다시 계획합니다.");
        var context = BuildContext(run, planSource, state, failure, observeHandle);
        var outcome = await planSource.ReplanAsync(context, token).ConfigureAwait(false);
        Account(run, outcome);
        if (!await AcceptPlanAsync(run, planSource, outcome, state, settings, token).ConfigureAwait(false))
        {
            return false;
        }

        SetStatus(run, TaskRunStatus.Running, "실행 중입니다.");
        return true;
    }

    private async Task<StepResult> ExecuteStepAsync(TaskRun run, StepRecord record, IPlanSource planSource, RunState state, ExecutionControl control, CancellationToken token)
    {
        var step = record.Step;
        var settings = state.Settings;
        record.Status = StepRunStatus.Running;
        record.Attempts++;
        record.StartedAt = DateTimeOffset.Now;
        SafeNotify(() => _observer.StepChanged(run, record));
        var stepTimeout = TimeSpan.FromSeconds(settings.StepTimeoutSeconds);

        switch (step.Action)
        {
            case ActionType.Wait:
                await Task.Delay(step.DelayMs ?? 0, token).ConfigureAwait(false);
                return Succeed(run, record, $"{step.DelayMs} ms 대기");

            case ActionType.Finish:
                var report = FillPlaceholders(step.Message, state) ?? "작업을 마쳤습니다.";
                run.Report = report;
                return Succeed(run, record, "완료 보고", StepResultKind.Finish, report);

            case ActionType.AskUser:
                var question = FillPlaceholders(step.Message, state) ?? "확인이 필요합니다.";
                run.Report = question;
                return Succeed(run, record, "사용자 확인 요청", StepResultKind.AskUser, question);

            case ActionType.ListDirectory:
                return ListDirectory(run, record, state);

            case ActionType.LaunchApp:
                return await LaunchAsync(run, record, state, token).ConfigureAwait(false);
        }

        // Window-targeted actions.
        var windowResolution = await ResolveWindowAsync(step, state, settings, stepTimeout, token).ConfigureAwait(false);
        if (!windowResolution.Found)
        {
            var kind = windowResolution.Error ?? ExecutionErrorKind.WindowNotFound;
            return kind == ExecutionErrorKind.PolicyDenied
                ? Deny(run, record, kind, windowResolution.Message)
                : Fail(run, record, kind, windowResolution.Message, retryable: false);
        }

        var window = windowResolution.Window!;
        var access = _policy.CheckWindow(window, settings, _desktop.OwnProcessId);
        record.TargetApp = access.App?.DisplayName ?? window.ProcessName;
        record.TargetWindow = Redactor.OneLine(window.Title, 120);
        run.TargetApp ??= access.App?.Id ?? window.ProcessName;
        if (!access.CanRead)
        {
            record.Decision = PolicyDecision.Deny(access.Reason);
            return Deny(run, record, ExecutionErrorKind.PolicyDenied, access.Reason);
        }

        state.CurrentWindow = window.Handle;
        record.ResolvedAppId = access.App?.Id;
        SafeNotify(() => _observer.StepChanged(run, record));

        if (step.Action == ActionType.Observe)
        {
            return Succeed(run, record, "화면 상태 수집", StepResultKind.Observe, windowHandle: window.Handle);
        }

        ElementInfo? element = null;
        if (step.Element is not null)
        {
            var elementResolution = await ResolveElementAsync(window.Handle, step.Element, state, step.Action == ActionType.WaitFor ? TimeSpan.FromMilliseconds(step.TimeoutMs ?? (int)stepTimeout.TotalMilliseconds) : stepTimeout, token, step.Action).ConfigureAwait(false);
            if (!elementResolution.Found)
            {
                var stillThere = _desktop.GetWindow(window.Handle) is not null;
                return Fail(run, record, stillThere ? elementResolution.Error ?? ExecutionErrorKind.ElementNotFound : ExecutionErrorKind.WindowClosed,
                    stillThere ? elementResolution.Message : "대상 창이 닫혔습니다(앱이 종료되었을 수 있음).", retryable: false);
            }

            element = elementResolution.Element!;
            record.TargetElement = Redactor.OneLine(element.Describe(), 120);
            record.ResolvedElement = ElementFingerprint.From(element);
            SafeNotify(() => _observer.StepChanged(run, record));
        }
        else if (ActionCatalog.Get(step.Action).NeedsElement)
        {
            return Fail(run, record, ExecutionErrorKind.InvalidPlan, "이 동작에는 대상 요소가 필요합니다.", retryable: false);
        }

        if (step.Action == ActionType.WaitFor)
        {
            var waited = await _verifier.VerifyAsync(step, window, element, token).ConfigureAwait(false);
            return waited.State == VerificationState.Failed
                ? Fail(run, record, ExecutionErrorKind.Timeout, waited.Message, retryable: false)
                : Succeed(run, record, element is null ? "창을 확인했습니다." : "요소를 확인했습니다.");
        }

        if (step.Action == ActionType.ReadElement)
        {
            var (value, stateText) = ReadElement(element!);
            state.Values[step.Id] = value;
            state.States[step.Id] = stateText;
            state.Names[step.Id] = element!.Name;
            record.ReadValue = $"{value}{(string.IsNullOrEmpty(stateText) ? string.Empty : $" ({stateText})")}";
            return Succeed(run, record, "요소를 읽었습니다.");
        }

        // State-changing actions.
        PixelPoint? screenPoint = null;
        ScreenImage? shot = null;
        if (step.Action == ActionType.ClickPoint)
        {
            if (!state.Screenshots.TryGetValue(window.Handle, out shot) || step.Point is null)
            {
                return Fail(run, record, ExecutionErrorKind.InvalidPlan, "좌표의 기준이 되는 이 창의 스크린샷이 없습니다.", retryable: false);
            }

            screenPoint = CoordinateMapper.ToScreen(step.Point.Value, window.Bounds);
            var problem = CoordinateMapper.ValidateClick(shot, window, screenPoint.Value, DateTimeOffset.UtcNow);
            if (problem is not null)
            {
                return Fail(run, record, ExecutionErrorKind.TargetChangedAfterApproval, problem, retryable: false);
            }

            var hit = _desktop.ElementFromPoint(screenPoint.Value);
            if (hit is not null && hit.ProcessId != 0 && hit.ProcessId != window.ProcessId)
            {
                return Deny(run, record, ExecutionErrorKind.PolicyDenied, $"클릭 위치에 다른 프로그램의 요소가 있습니다({Redactor.OneLine(hit.Describe(), 60)}).");
            }

            record.TargetElement = hit is null ? $"좌표 {screenPoint}" : $"좌표 {screenPoint} › {Redactor.OneLine(hit.Describe(), 80)}";
        }

        var decision = _policy.Evaluate(new PolicyInput
        {
            Step = step,
            Settings = settings,
            App = access.App,
            Window = window,
            Element = element,
            OwnProcessId = _desktop.OwnProcessId,
            InjectionSuspected = run.InjectionSuspected,
            TaskGrantedFolders = run.GrantedFolders,
            VisionActive = planSource.VisionAvailable,
            CurrentValue = element?.Value,
        });
        record.Decision = decision;
        if (decision.Verdict == PolicyVerdict.Deny)
        {
            return Deny(run, record, ExecutionErrorKind.PolicyDenied, decision.ReasonText);
        }

        if (decision.Verdict == PolicyVerdict.RequireApproval)
        {
            var previewPoint = shot is not null && step.Point is { } p ? CoordinateMapper.ToImage(p, shot.ImageWidth, shot.ImageHeight) : (PixelPoint?)null;
            var request = ApprovalTextBuilder.Build(run, record, decision, access.App, window, element, TimeSpan.FromSeconds(settings.ApprovalTimeoutSeconds), shot, previewPoint);
            var answer = await AskApprovalAsync(run, record, request, state, token).ConfigureAwait(false);
            if (answer != ApprovalResult.Approved)
            {
                var timedOut = answer == ApprovalResult.TimedOut;
                return Deny(run, record, timedOut ? ExecutionErrorKind.ApprovalTimeout : ExecutionErrorKind.ApprovalDenied,
                    timedOut ? "승인 시간이 지나 실행하지 않았습니다." : "사용자가 거부해 실행하지 않았습니다.");
            }

            // Re-validate everything after the wait: the user may have taken a while.
            var revalidation = Revalidate(step, window, element, shot, screenPoint, settings, run, planSource, decision);
            if (revalidation.Problem is not null)
            {
                return Fail(run, record, revalidation.Kind, revalidation.Problem, retryable: false);
            }

            window = revalidation.Window!;
            element = revalidation.Element;
        }

        if (element is not null && !element.IsEnabled)
        {
            return Fail(run, record, ExecutionErrorKind.ElementDisabled, $"{element.Describe()}이(가) 비활성 상태입니다.", retryable: true);
        }

        await WaitWhilePausedAsync(run, control, state, token).ConfigureAwait(false);
        await RespectMinimumIntervalAsync(state, settings, token).ConfigureAwait(false);

        if (step.Action is ActionType.Hotkey or ActionType.ClickPoint or ActionType.FocusWindow)
        {
            if (!await EnsureForegroundAsync(window.Handle, token).ConfigureAwait(false))
            {
                return Fail(run, record, ExecutionErrorKind.FocusFailed, "대상 창을 앞으로 가져오지 못했습니다(다른 창이 포커스를 잡고 있을 수 있음).", retryable: true);
            }

            if (step.Action == ActionType.FocusWindow)
            {
                record.Performed = true;
                record.Method = "SetForegroundWindow";
                return await VerifyAndFinishAsync(run, record, window, element, state, control, token).ConfigureAwait(false);
            }
        }

        var actionRequest = BuildRequest(step, window, element, screenPoint);
        record.Method = "실행 중";
        SafeNotify(() => _observer.StepChanged(run, record));
        ActionOutcome outcome;
        try
        {
            outcome = await _desktop.PerformAsync(actionRequest, token).WaitAsync(stepTimeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            record.Performed = true;
            return Fail(run, record, ExecutionErrorKind.Timeout, $"동작이 {stepTimeout.TotalSeconds:0}초 안에 끝나지 않았습니다(실행 여부 불확실, 대상 앱이 응답하지 않을 수 있음).", retryable: false);
        }

        state.LastActionAt = DateTimeOffset.UtcNow;
        record.Performed = outcome.Performed;
        record.Method = outcome.Method;
        if (!outcome.Success)
        {
            var gone = _desktop.GetWindow(window.Handle) is null;
            return Fail(run, record, gone ? ExecutionErrorKind.WindowClosed : outcome.Error ?? ExecutionErrorKind.ActionFailed,
                gone ? "실행 중 대상 창이 닫혔습니다." : outcome.Message, retryable: !outcome.Performed && outcome.Error is ExecutionErrorKind.FocusFailed or ExecutionErrorKind.ElementDisabled);
        }

        return await VerifyAndFinishAsync(run, record, window, element, state, control, token).ConfigureAwait(false);
    }

    private async Task<StepResult> VerifyAndFinishAsync(TaskRun run, StepRecord record, WindowInfo window, ElementInfo? element, RunState state, ExecutionControl control, CancellationToken token)
    {
        var verification = await _verifier.VerifyAsync(record.Step, window, element, token).ConfigureAwait(false);
        record.Verification = verification.State;
        if (verification.State == VerificationState.Failed)
        {
            return Fail(run, record, ExecutionErrorKind.VerificationFailed, verification.Message, retryable: false);
        }

        CheckForegroundAfterAction(run, window, state.Settings, control);
        return Succeed(run, record, verification.Message);
    }

    private (string? Problem, ExecutionErrorKind Kind, WindowInfo? Window, ElementInfo? Element) Revalidate(
        PlanStep step, WindowInfo window, ElementInfo? element, ScreenImage? shot, PixelPoint? screenPoint, SafetySettings settings, TaskRun run, IPlanSource planSource, PolicyDecision approvedDecision)
    {
        const ExecutionErrorKind changed = ExecutionErrorKind.TargetChangedAfterApproval;
        var currentWindow = _desktop.GetWindow(window.Handle);
        if (currentWindow is null)
        {
            return ("승인을 기다리는 동안 대상 창이 닫혀 실행하지 않았습니다.", ExecutionErrorKind.WindowClosed, null, null);
        }

        if (currentWindow.ProcessId != window.ProcessId)
        {
            return ("승인 후 창의 프로세스가 바뀌어 실행하지 않았습니다.", changed, null, null);
        }

        var access = _policy.CheckWindow(currentWindow, settings, _desktop.OwnProcessId);
        if (!access.CanInteract)
        {
            return ($"승인 후 다시 확인한 결과 조작할 수 없습니다: {access.Reason}", ExecutionErrorKind.PolicyDenied, null, null);
        }

        ElementInfo? currentElement = null;
        if (element is not null)
        {
            currentElement = _elements.Refresh(element, CancellationToken.None);
            if (currentElement is null)
            {
                return ("승인을 기다리는 동안 대상 요소가 사라져 실행하지 않았습니다.", changed, null, null);
            }

            if (!string.Equals(currentElement.Name, element.Name, StringComparison.Ordinal) && step.Action != ActionType.SetText)
            {
                return ($"승인 후 대상 요소의 이름이 바뀌었습니다('{Redactor.OneLine(element.Name, 40)}' → '{Redactor.OneLine(currentElement.Name, 40)}').", changed, null, null);
            }

            if (step.Action == ActionType.SetText && !string.Equals(currentElement.Value, element.Value, StringComparison.Ordinal) && step.Mode == SetTextMode.Replace)
            {
                return ("승인을 기다리는 동안 입력란의 내용이 바뀌어 덮어쓰지 않았습니다.", changed, null, null);
            }
        }

        if (shot is not null && screenPoint is { } point)
        {
            var problem = CoordinateMapper.ValidateClick(shot, currentWindow, point, DateTimeOffset.UtcNow);
            if (problem is not null)
            {
                return (problem, changed, null, null);
            }

            var fresh = _desktop.CaptureWindowImage(currentWindow.Handle, shot.ImageWidth);
            if (fresh is null || CoordinateMapper.SignatureDistance(fresh.Signature, shot.Signature) > ScreenChangeThresholdBits)
            {
                return ("승인 후 화면 내용이 바뀌어 좌표 클릭을 하지 않았습니다(화면을 다시 분석해야 함).", changed, null, null);
            }
        }

        var again = _policy.Evaluate(new PolicyInput
        {
            Step = step,
            Settings = settings,
            App = access.App,
            Window = currentWindow,
            Element = currentElement,
            OwnProcessId = _desktop.OwnProcessId,
            InjectionSuspected = run.InjectionSuspected,
            TaskGrantedFolders = run.GrantedFolders,
            VisionActive = planSource.VisionAvailable,
            CurrentValue = currentElement?.Value,
        });
        if (again.Verdict == PolicyVerdict.Deny)
        {
            return ($"승인 후 다시 확인한 결과 차단되었습니다: {again.ReasonText}", ExecutionErrorKind.PolicyDenied, null, null);
        }

        if (again.Categories.Except(approvedDecision.Categories).Any())
        {
            return ("승인 후 위험 분류가 바뀌어 실행하지 않았습니다. 다시 요청하세요.", changed, null, null);
        }

        return (null, ExecutionErrorKind.Internal, currentWindow, currentElement);
    }

    private async Task<ApprovalResult> AskApprovalAsync(TaskRun run, StepRecord record, ApprovalRequest request, RunState state, CancellationToken token)
    {
        record.Status = StepRunStatus.AwaitingApproval;
        record.Approval = ApprovalState.Pending;
        run.ApprovalsRequested++;
        run.UserInterventions++;
        SafeNotify(() => _observer.StepChanged(run, record));
        SetStatus(run, TaskRunStatus.AwaitingApproval, request.Title);
        Event(run, TaskEventLevel.Warning, $"승인 요청: {request.Change}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(request.Timeout);
        ApprovalResult result;
        state.Active.Stop();
        try
        {
            result = await _approvals.RequestStepApprovalAsync(request, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            result = ApprovalResult.TimedOut;
        }
        finally
        {
            state.Active.Start();
        }

        record.Approval = result switch
        {
            ApprovalResult.Approved => ApprovalState.Approved,
            ApprovalResult.TimedOut => ApprovalState.TimedOut,
            _ => ApprovalState.Denied,
        };
        if (result == ApprovalResult.Approved)
        {
            run.ApprovalsGranted++;
            record.Status = StepRunStatus.Running;
            SetStatus(run, TaskRunStatus.Running, "승인되어 대상을 다시 확인한 뒤 실행합니다.");
        }

        Event(run, result == ApprovalResult.Approved ? TaskEventLevel.Info : TaskEventLevel.Warning,
            result switch
            {
                ApprovalResult.Approved => "사용자가 승인했습니다.",
                ApprovalResult.TimedOut => "승인 시간이 지나 실행하지 않습니다.",
                _ => "사용자가 거부했습니다.",
            });
        SafeNotify(() => _observer.StepChanged(run, record));
        return result;
    }

    private async Task<StepResult> LaunchAsync(TaskRun run, StepRecord record, RunState state, CancellationToken token)
    {
        var step = record.Step;
        var settings = state.Settings;
        var app = settings.FindApp(step.App);
        record.TargetApp = app?.DisplayName ?? step.App;
        var decision = _policy.Evaluate(new PolicyInput { Step = step, Settings = settings, App = app, OwnProcessId = _desktop.OwnProcessId });
        record.Decision = decision;
        if (decision.Verdict == PolicyVerdict.Deny)
        {
            return Deny(run, record, ExecutionErrorKind.PolicyDenied, decision.ReasonText);
        }

        if (decision.Verdict == PolicyVerdict.RequireApproval)
        {
            var request = ApprovalTextBuilder.Build(run, record, decision, app, null, null, TimeSpan.FromSeconds(settings.ApprovalTimeoutSeconds));
            var answer = await AskApprovalAsync(run, record, request, state, token).ConfigureAwait(false);
            if (answer != ApprovalResult.Approved)
            {
                return Deny(run, record, answer == ApprovalResult.TimedOut ? ExecutionErrorKind.ApprovalTimeout : ExecutionErrorKind.ApprovalDenied,
                    answer == ApprovalResult.TimedOut ? "승인 시간이 지나 실행하지 않았습니다." : "사용자가 거부해 실행하지 않았습니다.");
            }
        }

        record.Method = "실행 중";
        SafeNotify(() => _observer.StepChanged(run, record));
        var result = await _desktop.LaunchAsync(app!.LaunchPath!, app.ProcessNames, _options.LaunchTimeout, token).ConfigureAwait(false);
        record.Performed = result.Success || result.ProcessId != 0;
        record.Method = "Process.Start";
        if (!result.Success || result.WindowHandle == 0)
        {
            return Fail(run, record, ExecutionErrorKind.WindowNotFound, result.Message, retryable: false);
        }

        state.CurrentWindow = result.WindowHandle;
        state.Launched.Add(result.WindowHandle);
        record.ResolvedAppId = app.Id;
        var window = _desktop.GetWindow(result.WindowHandle);
        record.TargetWindow = window is null ? null : Redactor.OneLine(window.Title, 120);
        run.TargetApp ??= app.Id;
        record.Verification = VerificationState.Passed;
        return Succeed(run, record, result.Message);
    }

    private StepResult ListDirectory(TaskRun run, StepRecord record, RunState state)
    {
        var decision = _policy.Evaluate(new PolicyInput { Step = record.Step, Settings = state.Settings, TaskGrantedFolders = run.GrantedFolders, OwnProcessId = _desktop.OwnProcessId });
        record.Decision = decision;
        if (decision.Verdict != PolicyVerdict.Allow)
        {
            return Deny(run, record, ExecutionErrorKind.PolicyDenied, decision.ReasonText);
        }

        FolderAccess.CheckAccess(record.Step.Path, state.Settings.GrantedFolders.Concat(run.GrantedFolders), out var fullPath);
        try
        {
            var listing = FolderAccess.List(fullPath!);
            var builder = new StringBuilder();
            builder.AppendLine(CultureInfo.InvariantCulture, $"{listing.Path} — 항목 {listing.TotalCount}개{(listing.Truncated ? $" (앞 {listing.Entries.Count}개만)" : string.Empty)}");
            foreach (var entry in listing.Entries)
            {
                builder.AppendLine(entry.IsDirectory
                    ? $"[폴더] {entry.Name}  {entry.LastModified:yyyy-MM-dd}"
                    : string.Create(CultureInfo.InvariantCulture, $"{entry.Name}  {FormatSize(entry.SizeBytes ?? 0)}  {entry.LastModified:yyyy-MM-dd}"));
            }

            var text = builder.ToString();
            state.Values[record.Step.Id] = text;
            record.ReadValue = text;
            record.TargetApp = "파일 시스템(읽기 전용)";
            return Succeed(run, record, $"항목 {listing.TotalCount}개를 읽었습니다.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(run, record, ExecutionErrorKind.AccessDenied, $"폴더를 읽지 못했습니다: {Redactor.OneLine(ex.Message, 120)}", retryable: false);
        }
    }

    private async Task<WindowResolution> ResolveWindowAsync(PlanStep step, RunState state, SafetySettings settings, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var resolution = _windows.Resolve(step.Window, step.App, state.CurrentWindow, state.Launched, settings);
            if (resolution.Found || resolution.Error is ExecutionErrorKind.PolicyDenied or ExecutionErrorKind.WindowClosed || DateTimeOffset.UtcNow >= deadline)
            {
                return resolution;
            }

            await Task.Delay(_options.PollInterval, token).ConfigureAwait(false);
        }
    }

    private async Task<ElementResolution> ResolveElementAsync(long windowHandle, ElementSelector selector, RunState state, TimeSpan timeout, CancellationToken token, ActionType action = ActionType.ReadElement)
    {
        var preferred = action switch
        {
            ActionType.Click => UiPatterns.Invoke | UiPatterns.Toggle | UiPatterns.SelectionItem | UiPatterns.ExpandCollapse,
            ActionType.SetText => UiPatterns.Value,
            ActionType.Toggle => UiPatterns.Toggle,
            ActionType.Select => UiPatterns.SelectionItem,
            ActionType.Expand or ActionType.Collapse => UiPatterns.ExpandCollapse,
            ActionType.Scroll => UiPatterns.Scroll,
            _ => UiPatterns.None,
        };
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var resolution = _elements.Resolve(windowHandle, selector, state.Registry, token, preferred);
            if (resolution.Found || resolution.Error == ExecutionErrorKind.ElementAmbiguous || DateTimeOffset.UtcNow >= deadline)
            {
                return resolution;
            }

            if (_desktop.GetWindow(windowHandle) is null)
            {
                return new ElementResolution(null, ExecutionErrorKind.WindowClosed, "대상 창이 닫혔습니다.");
            }

            await Task.Delay(_options.PollInterval, token).ConfigureAwait(false);
        }
    }

    private async Task<bool> EnsureForegroundAsync(long handle, CancellationToken token)
    {
        if (_desktop.GetForegroundWindow()?.Handle == handle)
        {
            return true;
        }

        _desktop.FocusWindow(handle);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_desktop.GetForegroundWindow()?.Handle == handle)
            {
                return true;
            }

            await Task.Delay(_options.PollInterval, token).ConfigureAwait(false);
        }

        return false;
    }

    private void CheckForegroundAfterAction(TaskRun run, WindowInfo target, SafetySettings settings, ExecutionControl control)
    {
        var foreground = _desktop.GetForegroundWindow();
        if (foreground is null || foreground.Handle == target.Handle || foreground.ProcessId == _desktop.OwnProcessId)
        {
            return;
        }

        if (foreground.ProcessId == target.ProcessId)
        {
            Event(run, TaskEventLevel.Info, $"같은 앱의 새 창이 앞에 나타났습니다: '{Redactor.OneLine(foreground.Title, 80)}'");
            return;
        }

        if (_policy.CheckWindow(foreground, settings, _desktop.OwnProcessId).CanRead)
        {
            Event(run, TaskEventLevel.Info, $"다른 허용 앱의 창이 앞에 나타났습니다: {Redactor.OneLine(foreground.Describe(), 120)}");
            return;
        }

        Event(run, TaskEventLevel.Warning, $"예상하지 못한 창이 앞에 나타났습니다: {Redactor.OneLine(foreground.Describe(), 120)}");
        run.LastKnownState = $"예상하지 못한 창: {Redactor.OneLine(foreground.Describe(), 120)}";
        if (_options.PauseOnUnexpectedWindow)
        {
            run.UserInterventions++;
            control.Pause("예상하지 못한 창 전환을 감지해 일시정지했습니다. 화면을 확인한 뒤 재개하거나 중단하세요.");
        }
    }

    private async Task WaitWhilePausedAsync(TaskRun run, ExecutionControl control, RunState state, CancellationToken token)
    {
        if (!control.IsPaused)
        {
            return;
        }

        state.Active.Stop();
        try
        {
            Event(run, TaskEventLevel.Info, "일시정지 중입니다. 재개를 기다립니다.");
            await control.WaitIfPausedAsync(token).ConfigureAwait(false);
        }
        finally
        {
            state.Active.Start();
        }
    }

    private async Task RespectMinimumIntervalAsync(RunState state, SafetySettings settings, CancellationToken token)
    {
        if (state.LastActionAt is { } last)
        {
            var wait = TimeSpan.FromMilliseconds(settings.MinActionIntervalMs) - (DateTimeOffset.UtcNow - last);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, token).ConfigureAwait(false);
            }
        }
    }

    private PlanningContext BuildContext(TaskRun run, IPlanSource planSource, RunState state, string? failure, long? observeHandle)
    {
        var settings = state.Settings;
        IReadOnlyList<WindowInfo> allowedWindows = [];
        UiSnapshot? snapshot = null;
        ScreenImage? screenshot = null;
        if (planSource.UsesModel)
        {
            allowedWindows = _desktop.ListWindows()
                .Where(w => _policy.CheckWindow(w, settings, _desktop.OwnProcessId).CanRead)
                .Take(20)
                .ToList();
            if (observeHandle is { } handle && _desktop.GetWindow(handle) is { } window && _policy.CheckWindow(window, settings, _desktop.OwnProcessId).CanRead)
            {
                snapshot = _desktop.CaptureTree(handle, new TreeCaptureOptions());
                state.CurrentWindow = handle;
                var texts = new List<string?> { window.Title };
                texts.AddRange(snapshot.Flatten().SelectMany(e => new[] { e.Name, e.Value }));
                foreach (var text in texts)
                {
                    if (!run.InjectionSuspected && UntrustedText.LooksLikeInstruction(text))
                    {
                        run.InjectionSuspected = true;
                        Event(run, TaskEventLevel.Warning, $"화면 텍스트에 AI에게 지시하려는 듯한 문구가 있어, 이 작업의 모든 변경 동작은 승인을 받습니다: \"{Redactor.OneLine(text, 80)}\"");
                    }
                }

                if (planSource.VisionAvailable && settings.AllowVision && snapshot.Status is StructureStatus.Limited or StructureStatus.Unavailable)
                {
                    screenshot = _desktop.CaptureWindowImage(handle, 1280);
                    if (screenshot is not null)
                    {
                        state.Screenshots[handle] = screenshot;
                        SafeNotify(() => _options.ScreenshotSink?.Invoke(run, screenshot));
                        Event(run, TaskEventLevel.Info, "UI 구조 정보가 부족해 창 스크린샷을 모델에 보냅니다(비전 지원 모델).");
                    }
                }
            }
        }

        return new PlanningContext
        {
            Request = run.Request,
            Run = run,
            Settings = settings,
            AllowedWindows = allowedWindows,
            Snapshot = snapshot,
            Registry = state.Registry,
            History = run.Steps.Where(s => s.Status != StepRunStatus.Pending).ToList(),
            FailureReason = failure,
            NextStepId = state.NextStepId,
            VisionAvailable = planSource.VisionAvailable && settings.AllowVision,
            Screenshot = screenshot,
        };
    }

    private long? ForegroundAllowedWindow(SafetySettings settings)
    {
        var foreground = _desktop.GetForegroundWindow();
        return foreground is not null && _policy.CheckWindow(foreground, settings, _desktop.OwnProcessId).CanRead ? foreground.Handle : null;
    }

    private static bool CanReplan(IPlanSource source, RunState state, TaskRun run, SafetySettings settings) =>
        source.CanReplan && state.Replans < settings.MaxReplans && run.ModelCalls < settings.MaxModelCallsPerTask;

    private static ActionRequest BuildRequest(PlanStep step, WindowInfo window, ElementInfo? element, PixelPoint? point) => step.Action switch
    {
        ActionType.Click => new ActionRequest { Action = PrimitiveAction.Invoke, WindowHandle = window.Handle, RuntimeId = element?.RuntimeId },
        ActionType.SetText => new ActionRequest
        {
            Action = step.Mode == SetTextMode.Append ? PrimitiveAction.AppendText : PrimitiveAction.SetValue,
            WindowHandle = window.Handle,
            RuntimeId = element?.RuntimeId,
            Text = step.Text ?? string.Empty,
        },
        ActionType.Toggle => new ActionRequest { Action = PrimitiveAction.Toggle, WindowHandle = window.Handle, RuntimeId = element?.RuntimeId, DesiredToggle = step.Checked },
        ActionType.Select => new ActionRequest { Action = PrimitiveAction.Select, WindowHandle = window.Handle, RuntimeId = element?.RuntimeId },
        ActionType.Expand => new ActionRequest { Action = PrimitiveAction.Expand, WindowHandle = window.Handle, RuntimeId = element?.RuntimeId },
        ActionType.Collapse => new ActionRequest { Action = PrimitiveAction.Collapse, WindowHandle = window.Handle, RuntimeId = element?.RuntimeId },
        ActionType.SelectMenu => new ActionRequest { Action = PrimitiveAction.SelectMenuPath, WindowHandle = window.Handle, MenuPath = step.MenuPath },
        ActionType.Scroll => new ActionRequest
        {
            Action = PrimitiveAction.Scroll,
            WindowHandle = window.Handle,
            RuntimeId = element?.RuntimeId,
            Direction = step.Direction ?? ScrollDirection.Down,
            Amount = step.Amount,
        },
        ActionType.Hotkey => new ActionRequest { Action = PrimitiveAction.SendHotkey, WindowHandle = window.Handle, Hotkey = step.Keys },
        ActionType.ClickPoint => new ActionRequest { Action = PrimitiveAction.ClickPoint, WindowHandle = window.Handle, Point = point },
        _ => throw new InvalidOperationException($"실행 요청으로 바꿀 수 없는 동작: {step.Action}"),
    };

    private static (string Value, string State) ReadElement(ElementInfo element)
    {
        var value = element.IsPassword ? "(비밀번호 입력란 — 읽지 않음)" : element.Value ?? element.Name;
        var parts = new List<string>();
        if (element.Toggle is { } toggle)
        {
            parts.Add(toggle switch { ToggleValue.On => "선택됨", ToggleValue.Off => "해제됨", _ => "부분 선택" });
        }

        if (element.IsSelected is { } selected)
        {
            parts.Add(selected ? "선택된 항목" : "선택 안 됨");
        }

        if (element.Expand is ExpandValue.Expanded)
        {
            parts.Add("펼쳐짐");
        }

        if (!element.IsEnabled)
        {
            parts.Add("비활성");
        }

        return (value, string.Join(", ", parts));
    }

    private static string? FillPlaceholders(string? message, RunState state)
    {
        if (message is null)
        {
            return null;
        }

        return Placeholder().Replace(message, m =>
        {
            var id = int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture);
            var map = m.Groups["kind"].Value switch
            {
                "state" => state.States,
                "name" => state.Names,
                _ => state.Values,
            };
            return map.TryGetValue(id, out var value) ? (string.IsNullOrEmpty(value) ? "(비어 있음)" : value) : "(값 없음)";
        });
    }

    private void Account(TaskRun run, PlanOutcome outcome)
    {
        run.ModelCalls += outcome.ModelCalls;
        run.ModelErrors += outcome.ModelErrors;
        SafeNotify(() => _observer.TaskChanged(run));
    }

    private void SkipPending(TaskRun run, string reason, StepRunStatus status = StepRunStatus.Skipped)
    {
        foreach (var step in run.Steps.Where(s => s.Status == StepRunStatus.Pending))
        {
            step.Status = status;
            step.Message = reason;
            SafeNotify(() => _observer.StepChanged(run, step));
        }
    }

    private void EndWithLimit(TaskRun run, string message)
    {
        SkipPending(run, "실행 한도에 도달해 실행하지 않았습니다.");
        Finish(run, TaskRunStatus.Failed, ExecutionErrorKind.LimitReached, message);
    }

    private void Finish(TaskRun run, TaskRunStatus status, ExecutionErrorKind? error, string? message)
    {
        if (status is TaskRunStatus.Failed or TaskRunStatus.Cancelled or TaskRunStatus.Denied && run.AnyChangePerformed)
        {
            message = $"{TaskStatusText.Korean(status)}: {message} — 이미 실행된 변경 {run.Steps.Count(s => s.Performed && s.ChangesState)}개, 실행하지 않은 단계 {run.Steps.Count(s => s.Status is StepRunStatus.Skipped or StepRunStatus.Cancelled or StepRunStatus.Pending)}개";
            status = TaskRunStatus.PartiallyCompleted;
        }

        run.Error = error;
        run.EndedAt = DateTimeOffset.Now;
        if (status is TaskRunStatus.Completed or TaskRunStatus.NeedsUserInput)
        {
            run.Report = message;
        }

        SetStatus(run, status, message);
        Event(run, status == TaskRunStatus.Completed ? TaskEventLevel.Info : TaskEventLevel.Warning, $"작업 종료: {TaskStatusText.Korean(status)} — {message}");
        _log.Info("executor", $"task {run.Id} ended: {status} ({error}) steps={run.Steps.Count} model_calls={run.ModelCalls}");
    }

    private StepResult Succeed(TaskRun run, StepRecord record, string message, StepResultKind kind = StepResultKind.Succeeded, string? payload = null, long? windowHandle = null)
    {
        record.Status = StepRunStatus.Succeeded;
        record.Message = message;
        record.EndedAt = DateTimeOffset.Now;
        SafeNotify(() => _observer.StepChanged(run, record));
        return new StepResult(kind, payload ?? message, null, false, windowHandle);
    }

    private StepResult Fail(TaskRun run, StepRecord record, ExecutionErrorKind error, string message, bool retryable)
    {
        record.Status = StepRunStatus.Failed;
        record.Error = error;
        record.Message = message;
        record.EndedAt = DateTimeOffset.Now;
        SafeNotify(() => _observer.StepChanged(run, record));
        Event(run, TaskEventLevel.Warning, $"{record.Sequence}단계 실패({ExecutionErrorText.Korean(error)}): {message}");
        return new StepResult(StepResultKind.Failed, message, error, retryable, null);
    }

    private StepResult Deny(TaskRun run, StepRecord record, ExecutionErrorKind error, string message)
    {
        record.Status = StepRunStatus.Denied;
        record.Error = error;
        record.Message = message;
        record.EndedAt = DateTimeOffset.Now;
        SafeNotify(() => _observer.StepChanged(run, record));
        Event(run, TaskEventLevel.Warning, $"{record.Sequence}단계 실행 안 함({ExecutionErrorText.Korean(error)}): {message}");
        return new StepResult(StepResultKind.Denied, message, error, false, null);
    }

    private void SetStatus(TaskRun run, TaskRunStatus status, string? message)
    {
        run.Status = status;
        run.StatusMessage = message;
        SafeNotify(() => _observer.TaskChanged(run));
    }

    private void Event(TaskRun run, TaskEventLevel level, string message) => SafeNotify(() => _observer.Event(run, level, message));

    private void UpdateLastKnownState(TaskRun run, StepRecord record)
    {
        var foreground = _desktop.GetForegroundWindow();
        run.LastKnownState = $"마지막 단계 {record.Sequence}({record.Step.Action.WireName()}): {TaskStatusText.Korean(record.Status)}; " +
                             $"앞 창: {(foreground is null ? "-" : Redactor.OneLine(foreground.Describe(), 100))}";
    }

    private void SafeNotify(Action notify)
    {
        try
        {
            notify();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("executor", "관찰자 알림 실패", ex);
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };

    private static SafetySettings Clone(SafetySettings settings) =>
        JsonSerializer.Deserialize<SafetySettings>(JsonSerializer.Serialize(settings, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;

    [GeneratedRegex(@"\{\{\s*s(?<id>\d{1,4})\.(?<kind>value|state|name)\s*\}\}")]
    private static partial Regex Placeholder();

    private enum StepResultKind
    {
        Succeeded,
        Failed,
        Denied,
        Finish,
        AskUser,
        Observe,
    }

    private sealed record StepResult(StepResultKind Kind, string Message, ExecutionErrorKind? Error, bool Retryable, long? WindowHandle);

    private sealed class RunState(SafetySettings settings)
    {
        public SafetySettings Settings { get; } = settings;
        public ElementRegistry Registry { get; } = new();
        public Stopwatch Active { get; } = new();
        public long? CurrentWindow { get; set; }
        public List<long> Launched { get; } = [];
        public Dictionary<long, ScreenImage> Screenshots { get; } = [];
        public Dictionary<int, string> Values { get; } = [];
        public Dictionary<int, string> States { get; } = [];
        public Dictionary<int, string> Names { get; } = [];
        public int Round { get; set; }
        public int ExecutedSteps { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int Replans { get; set; }
        public int NextStepId { get; set; } = 1;
        public DateTimeOffset? LastActionAt { get; set; }
        public TaskRunStatus? StatusBeforePause { get; set; }
    }
}
