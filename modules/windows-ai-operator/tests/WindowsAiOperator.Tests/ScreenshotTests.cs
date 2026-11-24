using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Automation.Mock;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Policy;

namespace WindowsAiOperator.Tests;

public sealed class ScreenshotTests
{
    [Fact]
    public async Task Vision_screenshots_reach_the_sink_only_when_saving_is_on()
    {
        var desktop = MockDesktop.CreateDemo(openTestTarget: false);
        var canvas = desktop.AddWindow(MockApps.CreateCanvasApp(desktop));
        var settings = TestSupport.Settings(s => s.AllowedApps.Add(new() { Id = "canvas", DisplayName = "캔버스", ProcessNames = ["CanvasApp"] }));
        var saved = new List<ScreenImage>();
        var source = new ScriptedPlanSource("""{"steps":[{"action":"finish","message":"확인만"}]}""") { VisionAvailable = true };
        var executor = new TaskExecutor(desktop, new PolicyEngine(), ScriptedApprovalHandler.DenyAll(),
            options: TestSupport.FastOptions with { ScreenshotSink = (_, image) => saved.Add(image) });
        using var control = new ExecutionControl();
        await executor.RunAsync(new TaskRun { Request = "캔버스 보기", InitialWindowHandle = canvas.Handle }, source, settings, control, TestContext.Current.CancellationToken);

        var image = Assert.Single(saved);
        Assert.Equal(canvas.Handle, image.WindowHandle);

        var withoutSink = new TaskExecutor(desktop, new PolicyEngine(), ScriptedApprovalHandler.DenyAll(), options: TestSupport.FastOptions);
        using var control2 = new ExecutionControl();
        var run = await withoutSink.RunAsync(new TaskRun { Request = "캔버스 보기", InitialWindowHandle = canvas.Handle },
            new ScriptedPlanSource("""{"steps":[{"action":"finish"}]}""") { VisionAvailable = true }, settings, control2, TestContext.Current.CancellationToken);
        Assert.Equal(TaskRunStatus.Completed, run.Status);
        Assert.Single(saved);
    }
}
