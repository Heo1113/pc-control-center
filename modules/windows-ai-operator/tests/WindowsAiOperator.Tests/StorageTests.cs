using Microsoft.Data.Sqlite;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation.Mock;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Storage;

namespace WindowsAiOperator.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "store-test-" + Guid.NewGuid().ToString("N"));

    public StorageTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Db => Path.Combine(_folder, "wao.db");

    [Fact]
    public async Task Journal_records_steps_events_and_redacts_secrets()
    {
        using var store = new OperatorStore(Db);
        var desktop = MockDesktop.CreateDemo();
        var executor = new TaskExecutor(desktop, new PolicyEngine(), ScriptedApprovalHandler.DenyAll(), new TaskJournal(store), options: TestSupport.FastOptions);
        using var control = new ExecutionControl();
        var plan = TestSupport.Plan("""
            {"steps":[
              {"action":"set_text","window":{"app":"test-target"},"element":{"automation_id":"InputBox"},"text":"api_key=sk-abcdefghijklmnopqrstuvwx 입력"},
              {"action":"click","element":{"automation_id":"DeleteItemButton"}}]}
            """);
        var run = await executor.RunAsync(new TaskRun { Request = "내 비밀번호: hunter2 를 넣어 줘", IsMockDesktop = true }, new StaticPlanSource(plan, "test"), TestSupport.Settings(), control, TestContext.Current.CancellationToken);

        var detail = store.GetTask(run.Id.ToString())!;
        Assert.Equal(TaskRunStatus.Denied.ToString(), detail.Summary.Status);
        Assert.True(detail.Summary.IsMock);
        Assert.Equal(2, detail.Steps.Count);
        Assert.DoesNotContain("hunter2", detail.Summary.Request, StringComparison.Ordinal);
        Assert.All(detail.Steps, s => Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwx", (s.ParamsJson ?? string.Empty) + s.Summary, StringComparison.Ordinal));
        Assert.NotEmpty(detail.Events);
        Assert.Equal("Denied", detail.Steps[0].Status);
        Assert.Equal("Denied", detail.Steps[0].Approval);
        Assert.Contains("민감정보", detail.Steps[0].DecisionReasons, StringComparison.Ordinal);
        Assert.Equal("Skipped", detail.Steps[1].Status);
    }

    [Fact]
    public void Interrupted_tasks_are_closed_with_what_ran_and_what_did_not()
    {
        var running = new TaskRun { Request = "중단될 작업", Status = TaskRunStatus.Running };
        running.Steps.Add(new StepRecord { Sequence = 1, Round = 1, Step = Step(ActionType.Click), Status = StepRunStatus.Succeeded, Performed = true });
        running.Steps.Add(new StepRecord { Sequence = 2, Round = 1, Step = Step(ActionType.SetText), Status = StepRunStatus.Running, Performed = false });
        running.Steps.Add(new StepRecord { Sequence = 3, Round = 1, Step = Step(ActionType.Click), Status = StepRunStatus.Pending });
        var waiting = new TaskRun { Request = "승인 대기 중 종료", Status = TaskRunStatus.AwaitingApproval };
        waiting.Steps.Add(new StepRecord { Sequence = 1, Round = 1, Step = Step(ActionType.Click), Status = StepRunStatus.AwaitingApproval });
        using (var store = new OperatorStore(Db))
        {
            foreach (var run in new[] { running, waiting })
            {
                store.SaveTask(run);
                foreach (var step in run.Steps)
                {
                    store.SaveStep(run.Id, step);
                }
            }
        }

        using var reopened = new OperatorStore(Db);
        Assert.Equal(2, reopened.GetStatus().UnfinishedTaskIds.Count);
        var recovered = reopened.RecoverInterruptedTasks();

        var partial = Assert.Single(recovered, r => r.Id == running.Id.ToString());
        Assert.Equal("PartiallyCompleted", partial.NewStatus);
        Assert.Equal((1, 1, 1), (partial.ChangesPerformed, partial.Uncertain, partial.NotExecuted));
        var detail = reopened.GetTask(running.Id.ToString())!;
        Assert.Equal("Unknown", detail.Steps[1].Status);
        Assert.Equal("Skipped", detail.Steps[2].Status);
        Assert.Equal("Interrupted", detail.Summary.ErrorKind);
        Assert.Equal("Interrupted", Assert.Single(recovered, r => r.Id == waiting.Id.ToString()).NewStatus);
        Assert.Equal("Cancelled", reopened.GetTask(waiting.Id.ToString())!.Steps[0].Status);
        Assert.Empty(reopened.GetStatus().UnfinishedTaskIds);
        Assert.Empty(reopened.RecoverInterruptedTasks());
    }

    [Fact]
    public void Backup_is_checked_and_restore_keeps_a_safety_copy()
    {
        var backups = Path.Combine(_folder, "backups");
        using var store = new OperatorStore(Db);
        var first = new TaskRun { Request = "백업 전 작업", Status = TaskRunStatus.Completed };
        store.SaveTask(first);
        var backup = store.Backup(backups);
        Assert.True(File.Exists(backup.Path));
        Assert.Equal("ok", store.IntegrityCheck());

        store.SaveTask(new TaskRun { Request = "백업 후 작업", Status = TaskRunStatus.Completed });
        Assert.Equal(2, store.QueryTasks(new HistoryQuery()).Count);

        var safety = store.RestoreFrom(backup.Path, backups);
        Assert.True(File.Exists(safety));
        var tasks = store.QueryTasks(new HistoryQuery());
        Assert.Equal("백업 전 작업", Assert.Single(tasks).Request);
        Assert.True(OperatorStore.ListBackups(backups).Count >= 2);
    }

    [Fact]
    public void Database_from_a_newer_version_is_never_written()
    {
        using (var connection = new SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99;";
            command.ExecuteNonQuery();
        }

        using var store = new OperatorStore(Db);
        Assert.True(store.IsReadOnly);
        store.SaveTask(new TaskRun { Request = "쓰면 안 됨" });
        Assert.Throws<SqliteException>(() => store.QueryTasks(new HistoryQuery()));
    }

    [Fact]
    public void Retention_removes_old_finished_tasks_only()
    {
        using var store = new OperatorStore(Db);
        store.SaveTask(new TaskRun { Request = "오래된 완료", Status = TaskRunStatus.Completed, CreatedAt = DateTimeOffset.Now.AddDays(-400) });
        store.SaveTask(new TaskRun { Request = "오래된 실행 중", Status = TaskRunStatus.Running, CreatedAt = DateTimeOffset.Now.AddDays(-400) });
        store.SaveTask(new TaskRun { Request = "최근", Status = TaskRunStatus.Completed });

        Assert.Equal(1, store.ApplyRetention(180));
        Assert.Equal(["최근", "오래된 실행 중"], store.QueryTasks(new HistoryQuery()).Select(t => t.Request).ToArray());
        Assert.Single(store.QueryTasks(new HistoryQuery(Text: "실행")));
    }

    [Fact]
    public void Workflows_and_model_comparison_results_round_trip()
    {
        using var store = new OperatorStore(Db);
        store.SaveWorkflow(new WorkflowRecord("w1", "카운트", "설명", "test-target", """{"steps":[]}""", false, DateTimeOffset.Now, DateTimeOffset.Now));
        store.AddEvalRun(new EvalRunRecord("b1", DateTimeOffset.Now, "로컬", "qwen", "counter", true, true, true, 3, 1200, 2, 0, 0, 1, "Completed", null));

        Assert.Equal("카운트", Assert.Single(store.ListWorkflows()).Name);
        Assert.True(Assert.Single(store.ListEvalRuns()).GuiSuccess);
        Assert.True(store.DeleteWorkflow("w1"));
        Assert.Empty(store.ListWorkflows());
    }

    private static PlanStep Step(ActionType action) => new() { Id = 1, Action = action, Text = action == ActionType.SetText ? "x" : null };
}
