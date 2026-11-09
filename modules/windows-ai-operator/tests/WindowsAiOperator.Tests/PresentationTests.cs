using Microsoft.Data.Sqlite;
using WindowsAiOperator.Core;
using WindowsAiOperator.Core.Automation.Mock;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Settings;
using WindowsAiOperator.Presentation;
using WindowsAiOperator.Presentation.ViewModels;

namespace WindowsAiOperator.Tests;

/// <summary>Answers plan reviews with "start" and step approvals as configured (the GUI shows windows instead).</summary>
internal sealed class AutoPresenter(bool approveSteps) : IApprovalPresenter
{
    public int Approvals { get; private set; }
    public int Reviews { get; private set; }

    public void ShowApproval(ApprovalViewModel approval)
    {
        Approvals++;
        if (approveSteps)
        {
            approval.ApproveCommand.Execute(null);
        }
        else
        {
            approval.DenyCommand.Execute(null);
        }
    }

    public void ShowPlanReview(PlanReviewViewModel review)
    {
        Reviews++;
        review.StartCommand.Execute(null);
    }

    public void Close(object viewModel)
    {
    }
}

public sealed class PresentationTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "ui-test-" + Guid.NewGuid().ToString("N"));
    private readonly OperatorContext _context;
    private readonly NullUiServices _ui = new() { ConfirmAnswer = true };
    private readonly AutoPresenter _presenter = new(approveSteps: false);
    private readonly MainViewModel _main;

    public PresentationTests()
    {
        _context = new OperatorContext(new AppPaths(_folder, false), MockDesktop.CreateDemo(), new InMemorySecretStore(), new MemoryOperatorLog(), forceMockModel: true);
        _context.Tasks.ExecutorOptions = TestSupport.FastOptions;
        _main = new MainViewModel(_context, new InlineDispatcher(), _ui, _presenter, _ui, _ui);
    }

    public void Dispose()
    {
        _context.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void All_eight_pages_open()
    {
        Assert.Equal(8, _main.Pages.Count);
        Assert.Equal(["dashboard", "task-runner", "inspector", "workflows", "history", "models", "safety", "settings"], _main.Pages.Select(p => p.Key));
        foreach (var page in _main.Pages)
        {
            _main.CurrentPage = page;
        }

        Assert.True(_main.IsMock);
        Assert.Contains("MOCK", _main.Title, StringComparison.Ordinal);
        Assert.Equal(4, _main.Workflows.Workflows.Count);
    }

    [Fact]
    public async Task Natural_language_request_runs_and_lands_in_history()
    {
        _main.Runner.RequestText = "메모장을 열고 \"화면 테스트\"를 입력해 줘.";
        await _main.Runner.RunCommand.ExecuteAsync(null);

        Assert.Equal(TaskRunStatus.Completed, _main.Runner.LastRun!.Status);
        Assert.Equal(2, _presenter.Reviews);
        Assert.Contains(_main.Runner.Steps, s => s.StatusText == "성공" && s.Summary.Contains("텍스트 입력", StringComparison.Ordinal));
        Assert.NotEmpty(_main.Runner.Events);
        Assert.True(_main.Runner.SaveAsWorkflowCommand.CanExecute(null));

        _main.History.Refresh();
        var row = Assert.Single(_main.History.Rows);
        Assert.Equal("완료", row.StatusText);
        Assert.NotNull(_main.History.Detail);
        Assert.Contains("실행된 변경", _main.History.Breakdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Risky_step_goes_through_the_approval_presenter_and_is_denied()
    {
        var plan = TestSupport.Plan("""{"steps":[{"action":"click","window":{"app":"test-target"},"element":{"automation_id":"DeleteItemButton"}}]}""");
        var run = await _main.Runner.RunStaticPlanAsync(plan, "삭제 시험", TaskSource.Manual);

        Assert.Equal(1, _presenter.Approvals);
        Assert.Equal(TaskRunStatus.Denied, run!.Status);
        Assert.Null(_main.Runner.PendingApproval);
    }

    [Fact]
    public async Task Built_in_workflow_runs_without_a_model()
    {
        var workflow = _main.Workflows.Workflows.First(w => w.Id == "builtin-test-counter");
        _main.Workflows.Selected = workflow;
        Assert.NotEmpty(_main.Workflows.StepLines);
        var run = await _main.Runner.RunWorkflowAsync(workflow, new Dictionary<string, string> { ["text"] = "워크플로 화면 시험" }, []);

        Assert.Equal(TaskRunStatus.Completed, run!.Status);
        Assert.Equal(0, _presenter.Reviews);
    }

    [Fact]
    public async Task Inspector_reads_the_tree_and_acts_directly_through_the_policy()
    {
        await _main.Inspector.RefreshAsync();
        var window = Assert.Single(_main.Inspector.Windows);
        Assert.Equal("조작 가능", window.Access);
        _main.Inspector.SelectedWindow = window;
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Contains("구조 인식 가능", _main.Inspector.StructureText, StringComparison.Ordinal);

        var button = Find(_main.Inspector.Tree, "IncrementButton");
        _main.Inspector.SelectedElement = button;
        Assert.Contains(_main.Inspector.ElementProperties, p => p.Name.StartsWith("지원 패턴", StringComparison.Ordinal) && p.Value.Contains("Invoke", StringComparison.Ordinal));
        await _main.Inspector.ClickCommand.ExecuteAsync(null);

        Assert.Contains("완료", _main.Inspector.ActionResult, StringComparison.Ordinal);
        Assert.Equal("카운트: 1", ((MockDesktop)_context.Desktop).Element("CounterText").Name);

        static ElementNodeViewModel Find(IEnumerable<ElementNodeViewModel> nodes, string id)
        {
            foreach (var node in nodes)
            {
                if (node.Element.AutomationId == id)
                {
                    return node;
                }

                var found = node.Children.Count > 0 ? TryFind(node.Children, id) : null;
                if (found is not null)
                {
                    return found;
                }
            }

            throw new InvalidOperationException(id);
        }

        static ElementNodeViewModel? TryFind(IEnumerable<ElementNodeViewModel> nodes, string id)
        {
            try
            {
                return Find(nodes, id);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    [Fact]
    public void Safety_settings_refuse_blocked_programs_and_save_limits()
    {
        var safety = _main.Safety;
        safety.AddAppCommand.Execute(null);
        var row = safety.Apps.Last();
        row.Id = "shell";
        row.ProcessNames = "powershell";
        row.Enabled = true;
        safety.SaveCommand.Execute(null);
        Assert.StartsWith("저장하지 않았습니다", safety.SaveResult, StringComparison.Ordinal);

        safety.Apps.Remove(row);
        safety.MaxSteps = 500;
        safety.SaveCommand.Execute(null);
        Assert.Equal(100, _context.Settings.Safety.MaxStepsPerTask);
        Assert.Contains("조정", safety.SaveResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Models_page_probes_the_mock_and_compares_models()
    {
        var models = _main.Models;
        models.Selected = models.Profiles.First(p => p.Kind == ProviderKind.Mock);
        await models.ProbeCommand.ExecuteAsync(null);
        Assert.Contains("MOCK", models.ConnectionText, StringComparison.Ordinal);
        Assert.Contains("꺼짐", models.VisionText, StringComparison.Ordinal);

        foreach (var scenario in models.Scenarios.Skip(1))
        {
            scenario.IsSelected = false;
        }

        await models.RunComparisonCommand.ExecuteAsync(null);
        Assert.Contains("가상 테스트 앱", models.EvaluationProgress, StringComparison.Ordinal);
        var summary = Assert.Single(models.EvalSummary);
        Assert.True(summary.IsMock);
    }

    [Fact]
    public void Settings_page_backs_up_and_checks_integrity()
    {
        var settings = _main.Settings;
        var before = settings.Backups.Count;
        settings.BackupNowCommand.Execute(null);
        Assert.Equal(before + 1, settings.Backups.Count);
        settings.CheckIntegrityCommand.Execute(null);
        Assert.Contains("ok", settings.ResultText, StringComparison.Ordinal);
    }
}
