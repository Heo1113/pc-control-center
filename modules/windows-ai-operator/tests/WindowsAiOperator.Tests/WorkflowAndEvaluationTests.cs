using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Automation.Mock;
using WindowsAiOperator.Core.Evaluation;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Core.Settings;
using WindowsAiOperator.Core.Workflows;
using WindowsAiOperator.ModelProviders;

namespace WindowsAiOperator.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public void Built_in_workflows_are_valid_and_round_trip()
    {
        foreach (var (_, workflow) in BuiltInWorkflows.All)
        {
            var json = WorkflowSerializer.Serialize(workflow);
            var (parsed, error) = WorkflowSerializer.Parse(json);
            Assert.True(parsed is not null, error);
            Assert.Equal(workflow.Name, parsed!.Name);
        }
    }

    [Fact]
    public async Task Parameters_are_filled_validated_and_run()
    {
        var workflow = BuiltInWorkflows.All.Single(w => w.Id == "builtin-test-counter").Workflow;
        var missing = WorkflowSerializer.Instantiate(workflow, new Dictionary<string, string>(), new PlanParserOptions());
        Assert.False(missing.IsValid);

        var plan = WorkflowSerializer.Instantiate(workflow, new Dictionary<string, string> { ["text"] = "워크플로 입력" }, new PlanParserOptions());
        Assert.True(plan.IsValid, string.Join("; ", plan.Errors));
        Assert.Equal("워크플로 입력", plan.Plan!.Steps.Single(s => s.Action == ActionType.SetText).Text);

        var desktop = MockDesktop.CreateDemo(openTestTarget: false);
        var executor = new TaskExecutor(desktop, new PolicyEngine(), ScriptedApprovalHandler.DenyAll(), options: TestSupport.FastOptions);
        using var control = new ExecutionControl();
        var run = await executor.RunAsync(new TaskRun { Request = workflow.Name, Source = TaskSource.Workflow }, new StaticPlanSource(plan.Plan, workflow.Name), TestSupport.Settings(), control, TestContext.Current.CancellationToken);

        Assert.Equal(TaskRunStatus.Completed, run.Status);
        Assert.StartsWith("완료: 카운트: 1", run.Report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_successful_task_becomes_a_workflow_with_stable_selectors()
    {
        var desktop = MockDesktop.CreateDemo();
        var source = new ScriptedPlanSource(
            _ => """{"steps":[{"action":"observe","window":{"app":"test-target"}}]}""",
            context => "{\"steps\":[{\"action\":\"click\",\"element\":{\"ref\":\"" + ScriptedPlanSource.RefOf(context, "IncrementButton") + "\"}},{\"action\":\"finish\",\"message\":\"끝\"}]}");
        var executor = new TaskExecutor(desktop, new PolicyEngine(), ScriptedApprovalHandler.DenyAll(), options: TestSupport.FastOptions);
        using var control = new ExecutionControl();
        var run = await executor.RunAsync(new TaskRun { Request = "카운트 올리기" }, source, TestSupport.Settings(), control, TestContext.Current.CancellationToken);

        var workflow = WorkflowSerializer.FromRun(run, "카운트 올리기");
        var json = WorkflowSerializer.Serialize(workflow);
        Assert.DoesNotContain("\"ref\"", json, StringComparison.Ordinal);
        Assert.Contains("IncrementButton", json, StringComparison.Ordinal);
        var plan = WorkflowSerializer.Instantiate(WorkflowSerializer.Parse(json).Workflow!, new Dictionary<string, string>(), new PlanParserOptions());
        Assert.True(plan.IsValid);
        Assert.Equal([ActionType.Click, ActionType.Finish], plan.Plan!.Steps.Select(s => s.Action));
    }

    [Fact]
    public void Approval_hints_show_what_will_be_asked()
    {
        var plan = TestSupport.Plan("""
            {"steps":[
              {"action":"click","window":{"app":"test-target"},"element":{"automation_id":"DeleteItemButton"}},
              {"action":"hotkey","keys":"Ctrl+S"},
              {"action":"click","element":{"automation_id":"IncrementButton"}}]}
            """);
        var hints = WorkflowSerializer.ApprovalHints(plan, TestSupport.Settings());

        Assert.Contains(hints, h => h.StepNumber == 1);
        Assert.Contains(hints, h => h.StepNumber == 2);
        Assert.DoesNotContain(hints, h => h.StepNumber == 3);
    }
}

public sealed class EvaluationTests
{
    [Fact]
    public async Task Model_comparison_separates_plan_validity_from_gui_success()
    {
        var desktop = MockDesktop.CreateDemo(openTestTarget: false);
        var evaluator = new ModelEvaluator(desktop, TestSupport.FastOptions);
        var scenarios = ModelEvaluator.DefaultScenarios.Where(s => s.Id is "counter-twice" or "read-checkbox").ToList();
        var models = new List<(string, IModelClient)> { ("MOCK 예시", new MockModelClient()), ("MOCK 잘못된 출력", new MockModelClient(MockModelBehavior.Garbage)) };

        var results = await evaluator.RunAsync(models, scenarios, TestSupport.Settings(), _ =>
        {
            var window = desktop.AddWindow(MockApps.CreateTestTarget(desktop));
            return Task.FromResult(new EvaluationTarget(window.Handle, () =>
            {
                desktop.CloseWindow(window.Handle);
                return Task.CompletedTask;
            }));
        }, null, TestContext.Current.CancellationToken);

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.True(r.IsMock));
        var counter = results.Single(r => r.ProfileName == "MOCK 예시" && r.ScenarioId == "counter-twice");
        Assert.True(counter.PlanValid);
        Assert.False(counter.GuiSuccess);
        Assert.True(results.Single(r => r.ProfileName == "MOCK 예시" && r.ScenarioId == "read-checkbox").GuiSuccess);
        Assert.All(results.Where(r => r.ProfileName == "MOCK 잘못된 출력"), r =>
        {
            Assert.False(r.PlanValid);
            Assert.False(r.GuiSuccess);
            Assert.True(r.ModelErrors >= 1);
        });
    }
}
