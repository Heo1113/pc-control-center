using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Storage;

namespace WindowsAiOperator.Presentation.ViewModels;

public sealed record HistoryRow(TaskSummary Summary)
{
    public string When => Summary.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public string Request => Summary.Request;
    public string StatusText => Enum.TryParse<TaskRunStatus>(Summary.Status, out var status) ? TaskStatusText.Korean(status) : Summary.Status;

    public Tone Tone => Summary.Status switch
    {
        nameof(TaskRunStatus.Completed) => Tone.Good,
        nameof(TaskRunStatus.Failed) or nameof(TaskRunStatus.Denied) or nameof(TaskRunStatus.Interrupted) => Tone.Bad,
        nameof(TaskRunStatus.PartiallyCompleted) or nameof(TaskRunStatus.Cancelled) or nameof(TaskRunStatus.NeedsUserInput) => Tone.Warning,
        _ => Tone.Info,
    };

    public string Counts => $"단계 {Summary.StepsSucceeded}/{Summary.StepCount} · 변경 {Summary.ChangesPerformed} · 승인 요청 {Summary.ApprovalsRequested} · 모델 호출 {Summary.ModelCalls}";
    public string Source => (Summary.IsMock ? "MOCK · " : string.Empty) + Summary.Source switch
    {
        nameof(TaskSource.User) => "자연어 요청",
        nameof(TaskSource.Workflow) => "워크플로",
        nameof(TaskSource.Manual) => "직접 조작",
        nameof(TaskSource.Evaluation) => "모델 비교",
        _ => Summary.Source,
    };
}

public sealed record HistoryStepRow(StepRow Step)
{
    public string Title => $"{Step.Sequence}. {Step.Summary}";
    public string StatusText => Enum.TryParse<StepRunStatus>(Step.Status, out var status) ? TaskStatusText.Korean(status) : Step.Status;

    public Tone Tone => Step.Status switch
    {
        nameof(StepRunStatus.Succeeded) => Tone.Good,
        nameof(StepRunStatus.Failed) or nameof(StepRunStatus.Denied) or nameof(StepRunStatus.Unknown) => Tone.Bad,
        nameof(StepRunStatus.Skipped) or nameof(StepRunStatus.Cancelled) => Tone.Warning,
        _ => Tone.Neutral,
    };

    public string Detail => string.Join(" · ", new[]
    {
        Step.TargetApp, Step.TargetElement, Step.Decision is null ? null : $"정책: {Step.Decision}", Step.Approval == "NotNeeded" ? null : $"승인: {Step.Approval}",
        Step.Performed && Step.ChangesState ? "변경 실행됨" : null, Step.Method, Step.Message, Step.ReadValue is null ? null : $"읽은 값: {Step.ReadValue}",
    }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>Task history with filters, details per step, partial-completion breakdown, export and "run again" (never automatic).</summary>
public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly OperatorContext _context;
    private readonly IDialogService _dialogs;
    private readonly Action<string> _rerun;

    public HistoryViewModel(OperatorContext context, IDialogService dialogs, Action<string> rerun)
        : base("기록", "", "history")
    {
        _context = context;
        _dialogs = dialogs;
        _rerun = rerun;
        StatusFilters =
        [
            new("전체", null), new("완료", nameof(TaskRunStatus.Completed)), new("부분 완료", nameof(TaskRunStatus.PartiallyCompleted)),
            new("실패", nameof(TaskRunStatus.Failed)), new("거부·차단", nameof(TaskRunStatus.Denied)), new("중단", nameof(TaskRunStatus.Cancelled)),
            new("비정상 종료", nameof(TaskRunStatus.Interrupted)), new("사용자 입력 필요", nameof(TaskRunStatus.NeedsUserInput)),
        ];
        PeriodFilters = [new("전체 기간", null), new("오늘", 1), new("7일", 7), new("30일", 30)];
        SelectedStatus = StatusFilters[0];
        SelectedPeriod = PeriodFilters[0];
    }

    public IReadOnlyList<Option<string?>> StatusFilters { get; }
    public IReadOnlyList<Option<int?>> PeriodFilters { get; }
    public ObservableCollection<HistoryRow> Rows { get; } = [];
    public ObservableCollection<HistoryStepRow> Steps { get; } = [];
    public ObservableCollection<string> Events { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Option<string?> SelectedStatus { get; set; }

    [ObservableProperty]
    public partial Option<int?> SelectedPeriod { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(DeleteCommand), nameof(RunAgainCommand))]
    public partial HistoryRow? Selected { get; set; }

    [ObservableProperty]
    public partial TaskDetail? Detail { get; set; }

    [ObservableProperty]
    public partial string? Breakdown { get; set; }

    [ObservableProperty]
    public partial string? Timing { get; set; }

    public override void OnNavigatedTo() => Refresh();

    partial void OnSelectedStatusChanged(Option<string?> value) => Refresh();

    partial void OnSelectedPeriodChanged(Option<int?> value) => Refresh();

    [RelayCommand]
    public void Refresh()
    {
        if (SelectedStatus is null || SelectedPeriod is null)
        {
            return;
        }

        var id = Selected?.Summary.Id;
        Rows.Clear();
        var since = SelectedPeriod.Value is { } days ? DateTimeOffset.Now.Date.AddDays(1 - days) : (DateTimeOffset?)null;
        foreach (var summary in _context.Store.QueryTasks(new HistoryQuery(string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(), SelectedStatus.Value, since, 500)))
        {
            Rows.Add(new HistoryRow(summary));
        }

        Selected = Rows.FirstOrDefault(r => r.Summary.Id == id) ?? Rows.FirstOrDefault();
        Notice = _context.Recovered.Count > 0 ? $"지난 실행이 비정상 종료되어 작업 {_context.Recovered.Count}개를 정리했습니다(자동 재실행 없음). 상세 내용에서 실행된 변경과 실행하지 않은 단계를 확인하세요." : null;
    }

    partial void OnSelectedChanged(HistoryRow? value)
    {
        Steps.Clear();
        Events.Clear();
        Detail = value is null ? null : _context.Store.GetTask(value.Summary.Id);
        Breakdown = null;
        Timing = null;
        if (Detail is null)
        {
            return;
        }

        foreach (var step in Detail.Steps)
        {
            Steps.Add(new HistoryStepRow(step));
        }

        foreach (var e in Detail.Events)
        {
            Events.Add($"{e.At.LocalDateTime:HH:mm:ss} [{e.Level}] {e.Message}");
        }

        var performed = Detail.Steps.Count(s => s.Performed && s.ChangesState);
        var uncertain = Detail.Steps.Count(s => s.Status == nameof(StepRunStatus.Unknown));
        var notRun = Detail.Steps.Count(s => s.Status is nameof(StepRunStatus.Skipped) or nameof(StepRunStatus.Cancelled) or nameof(StepRunStatus.Pending));
        var failed = Detail.Steps.Count(s => s.Status is nameof(StepRunStatus.Failed) or nameof(StepRunStatus.Denied));
        Breakdown = $"실행된 변경 {performed}개 · 실행 여부 불확실 {uncertain}개 · 실패·거부 {failed}개 · 실행하지 않은 단계 {notRun}개";
        var started = Detail.StartedAt ?? Detail.Summary.CreatedAt;
        var ended = Detail.Summary.EndedAt;
        Timing = $"작업 ID {Detail.Summary.Id} · 시작 {started.LocalDateTime:yyyy-MM-dd HH:mm:ss}" +
                 (ended is { } end ? $" · 종료 {end.LocalDateTime:HH:mm:ss} ({(end - started).TotalSeconds:0.0}초)" : " · 종료 기록 없음") +
                 $" · 계획 {Detail.Rounds}회 · 모델 호출 {Detail.Summary.ModelCalls}회(오류 {Detail.ModelErrors}) · 승인 {Detail.ApprovalsGranted}/{Detail.Summary.ApprovalsRequested}";
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RunAgain()
    {
        if (Selected is not null)
        {
            _rerun(Selected.Summary.Request);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Export()
    {
        if (Detail is null)
        {
            return;
        }

        var path = _dialogs.SaveFile("작업 기록 내보내기", "JSON (*.json)|*.json", $"task-{Detail.Summary.CreatedAt:yyyyMMdd-HHmmss}.json");
        if (path is not null)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(Detail, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        if (Selected is null)
        {
            return;
        }

        if (_dialogs.Confirm("기록 삭제", "이 작업 기록(단계·이벤트 포함)을 삭제합니다. 되돌리려면 설정 화면의 백업에서 복원해야 합니다. 계속할까요?"))
        {
            _context.Store.DeleteTask(Selected.Summary.Id);
            Refresh();
        }
    }
}
