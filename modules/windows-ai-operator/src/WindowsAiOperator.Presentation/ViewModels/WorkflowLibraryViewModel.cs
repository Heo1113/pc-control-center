using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Workflows;
using WindowsAiOperator.Storage;

namespace WindowsAiOperator.Presentation.ViewModels;

public sealed partial class WorkflowParameterInput(WorkflowParameter parameter) : ObservableObject
{
    public WorkflowParameter Parameter { get; } = parameter;
    public string Label => Parameter.Label + (Parameter.Required ? " *" : string.Empty);
    public bool IsFolder => Parameter.Kind == "folder";

    [ObservableProperty]
    public partial string Value { get; set; } = parameter.Default ?? string.Empty;
}

/// <summary>Saved workflows: details, parameters, likely approvals, JSON editing, import/export and user-started runs.</summary>
public sealed partial class WorkflowLibraryViewModel : PageViewModel
{
    private readonly OperatorContext _context;
    private readonly IDialogService _dialogs;
    private readonly TaskRunnerViewModel _runner;
    private readonly Action _showRunner;

    public WorkflowLibraryViewModel(OperatorContext context, IDialogService dialogs, TaskRunnerViewModel runner, Action showRunner)
        : base("워크플로", "", "workflows")
    {
        _context = context;
        _dialogs = dialogs;
        _runner = runner;
        _showRunner = showRunner;
        Reload();
    }

    public ObservableCollection<WorkflowRecord> Workflows { get; } = [];
    public ObservableCollection<WorkflowParameterInput> Parameters { get; } = [];
    public ObservableCollection<string> StepLines { get; } = [];
    public ObservableCollection<string> ApprovalHints { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(DeleteCommand), nameof(DuplicateCommand), nameof(ExportCommand), nameof(SaveJsonCommand))]
    public partial WorkflowRecord? Selected { get; set; }

    [ObservableProperty]
    public partial string JsonText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? DetailError { get; set; }

    public override void OnNavigatedTo() => Reload();

    [RelayCommand]
    public void Reload()
    {
        var id = Selected?.Id;
        Workflows.Clear();
        foreach (var workflow in _context.Workflows.List())
        {
            Workflows.Add(workflow);
        }

        Selected = Workflows.FirstOrDefault(w => w.Id == id) ?? Workflows.FirstOrDefault();
    }

    partial void OnSelectedChanged(WorkflowRecord? value)
    {
        Parameters.Clear();
        StepLines.Clear();
        ApprovalHints.Clear();
        DetailError = null;
        JsonText = value?.DefinitionJson ?? string.Empty;
        if (value is null)
        {
            return;
        }

        var (workflow, error) = WorkflowSerializer.Parse(value.DefinitionJson);
        if (workflow is null)
        {
            DetailError = error;
            return;
        }

        foreach (var parameter in workflow.Parameters)
        {
            Parameters.Add(new WorkflowParameterInput(parameter));
        }

        var preview = WorkflowSerializer.Instantiate(workflow, workflow.Parameters.ToDictionary(p => p.Name, p => string.IsNullOrEmpty(p.Default) ? $"<{p.Label}>" : p.Default!),
            new PlanParserOptions { MaxSteps = 100 });
        if (preview.Plan is null)
        {
            DetailError = string.Join("\n", preview.Errors);
            return;
        }

        foreach (var (step, index) in preview.Plan.Steps.Select((s, i) => (s, i)))
        {
            StepLines.Add($"{index + 1}. {step.Summary()}");
        }

        foreach (var hint in WorkflowSerializer.ApprovalHints(preview.Plan, _context.Settings.Safety))
        {
            ApprovalHints.Add($"{hint.StepNumber}단계({hint.Action}): {hint.Reason}");
        }

        if (ApprovalHints.Count == 0)
        {
            ApprovalHints.Add("미리 판단한 승인 필요 단계는 없습니다(실행 중 실제 화면 기준으로 다시 판단합니다).");
        }
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RunAsync()
    {
        if (Selected is null)
        {
            return;
        }

        var values = new Dictionary<string, string>();
        var grants = new List<string>();
        foreach (var input in Parameters)
        {
            if (input.IsFolder)
            {
                var folder = string.IsNullOrWhiteSpace(input.Value) ? _dialogs.PickFolder($"{input.Parameter.Label} 선택(이번 실행에서만 목록 읽기 허용)") : input.Value;
                if (folder is null)
                {
                    return;
                }

                input.Value = folder;
                grants.Add(folder);
            }

            values[input.Parameter.Name] = input.Value;
        }

        _showRunner();
        await _runner.RunWorkflowAsync(Selected, values, grants).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate()
    {
        if (Selected is null || WorkflowSerializer.Parse(Selected.DefinitionJson).Workflow is not { } workflow)
        {
            return;
        }

        var copy = _context.Workflows.Save(workflow with { Name = workflow.Name + " (사본)" });
        Reload();
        Selected = Workflows.FirstOrDefault(w => w.Id == copy.Id);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        if (Selected is null)
        {
            return;
        }

        if (Selected.BuiltIn)
        {
            _dialogs.Info("기본 워크플로", "기본 예시 워크플로는 지우지 않습니다. 사본을 만들어 고쳐 쓰세요.");
            return;
        }

        if (_dialogs.Confirm("워크플로 삭제", $"'{Selected.Name}' 워크플로를 삭제합니다. 실행 기록은 남습니다.\n삭제 전에 [내보내기]로 백업할 수 있습니다. 계속할까요?"))
        {
            _context.Workflows.Delete(Selected);
            Reload();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SaveJson()
    {
        if (Selected is null)
        {
            return;
        }

        var (workflow, error) = WorkflowSerializer.Parse(JsonText);
        if (workflow is null)
        {
            DetailError = error;
            return;
        }

        if (Selected.BuiltIn)
        {
            var copy = _context.Workflows.Save(workflow with { Name = workflow.Name.EndsWith("(수정본)", StringComparison.Ordinal) ? workflow.Name : workflow.Name + " (수정본)" });
            Reload();
            Selected = Workflows.FirstOrDefault(w => w.Id == copy.Id);
            _dialogs.Info("수정본 저장", "기본 워크플로는 그대로 두고 수정본으로 저장했습니다.");
            return;
        }

        _context.Workflows.Save(workflow, Selected.Id);
        Reload();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Export()
    {
        if (Selected is null)
        {
            return;
        }

        var path = _dialogs.SaveFile("워크플로 내보내기", "JSON (*.json)|*.json", Selected.Name.Replace(' ', '_') + ".json");
        if (path is not null)
        {
            File.WriteAllText(path, Selected.DefinitionJson);
        }
    }

    [RelayCommand]
    private void Import()
    {
        var path = _dialogs.OpenFile("워크플로 가져오기", "JSON (*.json)|*.json");
        if (path is null)
        {
            return;
        }

        var info = new FileInfo(path);
        if (info.Length > 512 * 1024)
        {
            _dialogs.Error("가져오기", "파일이 너무 큽니다(512 KB 이하).");
            return;
        }

        var (workflow, error) = WorkflowSerializer.Parse(File.ReadAllText(path));
        if (workflow is null)
        {
            _dialogs.Error("가져오기", error ?? "워크플로 형식이 아닙니다.");
            return;
        }

        var record = _context.Workflows.Save(workflow);
        Reload();
        Selected = Workflows.FirstOrDefault(w => w.Id == record.Id);
    }
}
