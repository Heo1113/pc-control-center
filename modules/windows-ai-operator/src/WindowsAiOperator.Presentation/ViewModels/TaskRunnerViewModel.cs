using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Security;
using WindowsAiOperator.Core.Workflows;
using WindowsAiOperator.Storage;

namespace WindowsAiOperator.Presentation.ViewModels;

/// <summary>
/// Natural-language request → plan → live execution with approval, pause and stop. It is also the approval handler
/// and an observer of the executor, so everything the executor does is visible here as it happens.
/// </summary>
public sealed partial class TaskRunnerViewModel : PageViewModel, IApprovalHandler, ITaskObserver
{
    public static readonly IReadOnlyList<string> Examples =
    [
        "메모장을 열고 \"테스트 문장입니다.\"를 입력해 줘.",
        "설정 창에서 현재 선택된 항목을 확인해 줘.",
        "테스트 앱의 '알림 표시' 확인란 상태를 확인해 줘.",
        "테스트 앱의 설정 화면(도구 › 대화상자 열기)에서 '옵션 사용' 옵션을 찾아줘.",
        "선택한 폴더의 파일 목록을 읽고 정리할 항목을 제안해 줘.",
    ];

    private readonly OperatorContext _context;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IApprovalPresenter _presenter;
    private ExecutionControl? _control;
    private CancellationTokenSource? _cts;

    public TaskRunnerViewModel(OperatorContext context, IUiDispatcher ui, IDialogService dialogs, IApprovalPresenter presenter)
        : base("작업 실행", "", "task-runner")
    {
        _context = context;
        _ui = ui;
        _dialogs = dialogs;
        _presenter = presenter;
        RefreshWindows();
        UpdateModelLabel();
        context.SettingsChanged += () => _ui.Post(UpdateModelLabel);
        context.Models.Changed += () => _ui.Post(UpdateModelLabel);
    }

    public event Action<TaskRun>? TaskFinished;

    public ObservableCollection<Option<long?>> TargetWindows { get; } = [];
    public ObservableCollection<string> GrantedFolders { get; } = [];
    public ObservableCollection<StepItemViewModel> Steps { get; } = [];
    public ObservableCollection<string> Events { get; } = [];
    public IReadOnlyList<string> ExampleRequests => Examples;
    public bool IsMockDesktop => _context.Desktop.IsMock;
    public TaskRun? LastRun { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string RequestText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Option<long?>? SelectedTargetWindow { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(PauseCommand), nameof(ResumeCommand), nameof(StopCommand), nameof(SaveAsWorkflowCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand), nameof(ResumeCommand))]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "요청을 입력하고 [계획 및 실행]을 누르세요.";

    [ObservableProperty]
    public partial Tone StatusTone { get; set; }

    [ObservableProperty]
    public partial string ModelLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ModelProblem { get; set; }

    [ObservableProperty]
    public partial string? Report { get; set; }

    [ObservableProperty]
    public partial string? TargetApp { get; set; }

    [ObservableProperty]
    public partial string Progress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial StepItemViewModel? CurrentStep { get; set; }

    [ObservableProperty]
    public partial ApprovalViewModel? PendingApproval { get; set; }

    [ObservableProperty]
    public partial PlanReviewViewModel? PendingPlanReview { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAsWorkflowCommand))]
    public partial bool CanSaveWorkflow { get; set; }

    public override void OnNavigatedTo()
    {
        RefreshWindows();
        UpdateModelLabel();
    }

    [RelayCommand]
    public void RefreshWindows()
    {
        var selected = SelectedTargetWindow?.Value;
        TargetWindows.Clear();
        TargetWindows.Add(new Option<long?>("자동 (앞에 있는 허용 앱 창 또는 실행 계획에 따름)", null));
        try
        {
            foreach (var window in _context.Desktop.ListWindows())
            {
                var access = _context.Policy.CheckWindow(window, _context.Settings.Safety, _context.Desktop.OwnProcessId);
                if (access.CanRead)
                {
                    TargetWindows.Add(new Option<long?>($"{access.App!.DisplayName} — {Redactor.OneLine(window.Title, 60)}", window.Handle));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            _context.Log.Warn("ui", "window list failed", ex);
        }

        SelectedTargetWindow = TargetWindows.FirstOrDefault(o => o.Value == selected) ?? TargetWindows[0];
    }

    public void SelectWindow(long handle)
    {
        RefreshWindows();
        SelectedTargetWindow = TargetWindows.FirstOrDefault(o => o.Value == handle) ?? SelectedTargetWindow;
    }

    [RelayCommand]
    private void UseExample(string example) => RequestText = example;

    [RelayCommand]
    private void AddFolder()
    {
        var folder = _dialogs.PickFolder("이 작업에서 읽을 수 있는 폴더 선택(목록만 읽음)");
        if (folder is not null && !GrantedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            GrantedFolders.Add(folder);
        }
    }

    [RelayCommand]
    private void RemoveFolder(string folder) => GrantedFolders.Remove(folder);

    private bool CanRun() => !IsRunning && !string.IsNullOrWhiteSpace(RequestText);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var (source, profile, client, problem) = _context.Tasks.CreatePlanner();
        if (source is null)
        {
            ModelProblem = problem;
            _dialogs.Error("모델 연결 필요", $"{problem}\n\nAI 모델 없이 실행하려면 '워크플로' 화면의 워크플로나 '창 검사기'의 직접 조작을 사용하세요.");
            return;
        }

        var run = new TaskRun
        {
            Request = RequestText.Trim(),
            Source = TaskSource.User,
            InitialWindowHandle = SelectedTargetWindow?.Value,
            GrantedFolders = GrantedFolders.ToList(),
            IsMockDesktop = _context.Desktop.IsMock,
            ModelLabel = source.Label,
        };
        try
        {
            await ExecuteAsync(run, source).ConfigureAwait(true);
        }
        finally
        {
            if (profile is not null && client is not null)
            {
                _context.Models.Remember(profile, client);
            }

            (client as IDisposable)?.Dispose();
        }
    }

    /// <summary>Runs a stored workflow (no model). Parameters were filled and validated by the caller.</summary>
    public async Task<TaskRun?> RunWorkflowAsync(WorkflowRecord record, IReadOnlyDictionary<string, string> values, IReadOnlyList<string> grants)
    {
        if (IsRunning)
        {
            _dialogs.Error("실행 중", "다른 작업이 실행 중입니다. 끝나거나 중단한 뒤 다시 시도하세요.");
            return null;
        }

        var (workflow, error) = WorkflowSerializer.Parse(record.DefinitionJson);
        if (workflow is null)
        {
            _dialogs.Error("워크플로 오류", error ?? "워크플로를 읽지 못했습니다.");
            return null;
        }

        var parsed = WorkflowSerializer.Instantiate(workflow, values, new PlanParserOptions
        {
            MaxSteps = _context.Settings.Safety.MaxStepsPerTask,
            MaxTextLength = _context.Settings.Safety.MaxTextLength,
        });
        if (!parsed.IsValid)
        {
            _dialogs.Error("워크플로 매개변수", string.Join("\n", parsed.Errors));
            return null;
        }

        RequestText = $"[워크플로] {workflow.Name}";
        var run = new TaskRun
        {
            Request = RequestText,
            Source = TaskSource.Workflow,
            WorkflowId = record.Id,
            GrantedFolders = grants.ToList(),
            IsMockDesktop = _context.Desktop.IsMock,
            ModelLabel = "워크플로(모델 없음)",
        };
        await ExecuteAsync(run, new StaticPlanSource(parsed.Plan!, $"워크플로: {workflow.Name}")).ConfigureAwait(true);
        return run;
    }

    /// <summary>One step chosen by hand in the inspector, through the same policy, approval and journal.</summary>
    public Task<TaskRun?> RunManualStepAsync(PlanStep step, string description) =>
        RunStaticPlanAsync(TaskService.SingleStep(step), $"[직접 조작] {description}", TaskSource.Manual);

    /// <summary>A fixed plan (no model) through the same executor, policy, approval and journal.</summary>
    public async Task<TaskRun?> RunStaticPlanAsync(TaskPlan plan, string request, TaskSource source)
    {
        if (IsRunning)
        {
            _dialogs.Error("실행 중", "다른 작업이 실행 중입니다.");
            return null;
        }

        RequestText = request;
        var run = new TaskRun { Request = request, Source = source, IsMockDesktop = _context.Desktop.IsMock, ModelLabel = "고정 계획(모델 없음)" };
        await ExecuteAsync(run, new StaticPlanSource(plan, "고정 계획")).ConfigureAwait(true);
        return run;
    }

    private bool CanPause() => IsRunning && !IsPaused;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => _control?.Pause();

    private bool CanResume() => IsRunning && IsPaused;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() => _control?.Resume();

    [RelayCommand(CanExecute = nameof(IsRunning))]
    public void Stop() => StopAll("사용자 중단");

    /// <summary>Emergency stop: ends the run, answers any open approval with "no" and closes review windows.</summary>
    public void StopAll(string reason)
    {
        _control?.Stop(reason);
        PendingApproval?.Expire();
        PendingPlanReview?.Expire();
        _cts?.Cancel();
    }

    private bool CanSave() => !IsRunning && CanSaveWorkflow;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveAsWorkflow()
    {
        if (LastRun is null)
        {
            return;
        }

        var name = Redactor.OneLine(LastRun.Request.Replace("[워크플로] ", string.Empty, StringComparison.Ordinal), 60);
        var workflow = WorkflowSerializer.FromRun(LastRun, name);
        _context.Workflows.Save(workflow);
        _dialogs.Info("워크플로 저장", $"'{name}' 워크플로로 저장했습니다. 워크플로 화면에서 이름과 매개변수를 바꿀 수 있습니다.");
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsRunning)
        {
            return;
        }

        Steps.Clear();
        Events.Clear();
        Report = null;
        TargetApp = null;
        Progress = string.Empty;
        StatusText = "요청을 입력하고 [계획 및 실행]을 누르세요.";
        StatusTone = Tone.Neutral;
    }

    private async Task ExecuteAsync(TaskRun run, IPlanSource source)
    {
        IsRunning = true;
        IsPaused = false;
        CanSaveWorkflow = false;
        Steps.Clear();
        Events.Clear();
        Report = null;
        TargetApp = null;
        StatusText = "시작합니다…";
        StatusTone = Tone.Info;
        _control = new ExecutionControl();
        _cts = new CancellationTokenSource();
        var control = _control;
        control.StateChanged += () => _ui.Post(() => IsPaused = control.IsPaused);
        try
        {
            var finished = await Task.Run(() => _context.Tasks.RunAsync(run, source, this, this, control, _cts.Token)).ConfigureAwait(true);
            LastRun = finished;
            CanSaveWorkflow = finished.Status == TaskRunStatus.Completed && finished.Steps.Any(s => s.Status == StepRunStatus.Succeeded && s.Step.Action != ActionType.Finish);
            TaskFinished?.Invoke(finished);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Error("ui", "task failed unexpectedly", ex);
            StatusText = $"예상하지 못한 오류: {Redactor.OneLine(ex.Message, 160)}";
            StatusTone = Tone.Bad;
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            PendingApproval = null;
            PendingPlanReview = null;
            control.Dispose();
            _cts.Dispose();
            _cts = null;
            _control = null;
        }
    }

    private void UpdateModelLabel()
    {
        var profile = _context.Settings.ActiveProfile;
        if (profile is null)
        {
            ModelLabel = "모델 없음";
            return;
        }

        var (vision, reason) = _context.Models.Vision(profile);
        ModelLabel = $"{profile.Name} · {profile.Model ?? "(모델 미선택)"}" + (vision ? " · 이미지 입력 사용" : " · 텍스트 전용");
        ModelProblem = _context.Models.CreateClient(profile) is (null, var problem) ? problem : null;
        _ = reason;
    }

    // ---- IApprovalHandler ----

    public async Task<ApprovalResult> RequestStepApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var approval = new ApprovalViewModel(request, _ui);
        _ui.Post(() =>
        {
            PendingApproval = approval;
            _presenter.ShowApproval(approval);
        });
        using var registration = cancellationToken.Register(approval.Expire);
        try
        {
            return await approval.Result.ConfigureAwait(false);
        }
        finally
        {
            approval.Dispose();
            _ui.Post(() =>
            {
                if (ReferenceEquals(PendingApproval, approval))
                {
                    PendingApproval = null;
                }

                _presenter.Close(approval);
            });
        }
    }

    public async Task<bool> ReviewPlanAsync(PlanReviewRequest request, CancellationToken cancellationToken)
    {
        var review = new PlanReviewViewModel(request, _context.Settings.Safety);
        _ui.Post(() =>
        {
            PendingPlanReview = review;
            _presenter.ShowPlanReview(review);
        });
        using var registration = cancellationToken.Register(review.Expire);
        try
        {
            return await review.Result.ConfigureAwait(false);
        }
        finally
        {
            _ui.Post(() =>
            {
                if (ReferenceEquals(PendingPlanReview, review))
                {
                    PendingPlanReview = null;
                }

                _presenter.Close(review);
            });
        }
    }

    // ---- ITaskObserver (executor thread) ----

    public void TaskChanged(TaskRun run)
    {
        var status = run.Status;
        var message = run.StatusMessage;
        var report = run.Report;
        var target = run.TargetApp;
        var done = run.Steps.Count(s => s.Status == StepRunStatus.Succeeded);
        var total = run.Steps.Count;
        var model = run.ModelLabel;
        _ui.Post(() =>
        {
            StatusText = $"{TaskStatusText.Korean(status)}{(string.IsNullOrEmpty(message) ? string.Empty : " — " + message)}";
            StatusTone = status switch
            {
                TaskRunStatus.Completed => Tone.Good,
                TaskRunStatus.Failed or TaskRunStatus.Denied or TaskRunStatus.Interrupted => Tone.Bad,
                TaskRunStatus.PartiallyCompleted or TaskRunStatus.Cancelled or TaskRunStatus.NeedsUserInput or TaskRunStatus.Paused => Tone.Warning,
                _ => Tone.Info,
            };
            Report = report;
            TargetApp = target;
            Progress = total == 0 ? string.Empty : $"{done}/{total}단계 성공";
            if (model is not null && !IsMockDesktop)
            {
                ModelLabel = model;
            }
        });
    }

    public void StepsChanged(TaskRun run)
    {
        var snapshots = run.Steps.Select(StepSnapshot.From).ToList();
        _ui.Post(() => Sync(snapshots));
    }

    public void StepChanged(TaskRun run, StepRecord step)
    {
        var snapshot = StepSnapshot.From(step);
        _ui.Post(() =>
        {
            var item = Steps.FirstOrDefault(s => s.Sequence == snapshot.Sequence);
            if (item is null)
            {
                item = new StepItemViewModel(snapshot.Sequence, snapshot.Round);
                Steps.Add(item);
            }

            item.Update(snapshot);
            if (snapshot.IsActive)
            {
                if (CurrentStep is not null)
                {
                    CurrentStep.IsCurrent = false;
                }

                item.IsCurrent = true;
                CurrentStep = item;
            }
            else if (ReferenceEquals(CurrentStep, item))
            {
                item.IsCurrent = false;
            }
        });
    }

    public void Event(TaskRun run, TaskEventLevel level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {(level == TaskEventLevel.Info ? "·" : level == TaskEventLevel.Warning ? "!" : "×")} {Redactor.OneLine(message, 300)}";
        _ui.Post(() =>
        {
            Events.Add(line);
            if (Events.Count > 500)
            {
                Events.RemoveAt(0);
            }
        });
    }

    private void Sync(List<StepSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            var item = Steps.FirstOrDefault(s => s.Sequence == snapshot.Sequence);
            if (item is null)
            {
                item = new StepItemViewModel(snapshot.Sequence, snapshot.Round);
                var index = Steps.TakeWhile(s => s.Sequence < snapshot.Sequence).Count();
                Steps.Insert(index, item);
            }

            item.Update(snapshot);
        }
    }
}
