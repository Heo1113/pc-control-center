using PcControlCenter.Core;
using PcControlCenter.History;
using PcControlCenter.Modules;
using PcControlCenter.Presentation;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Planning;

namespace PcControlCenter.Tests;

public class HistoryTests
{
    private static string MockHostPath()
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PcControlCenter.MockOperatorHost", "bin", configuration, "net10.0",
            "PcControlCenter.MockOperatorHost.dll"));
    }

    [Fact(Timeout = 240_000)]
    public async Task One_time_axis_shows_every_module_with_sources_and_gaps()
    {
        using var temp = new TempDir();
        var started = DateTimeOffset.UtcNow;
        await using (var runtime = ShellRuntime.Create(new ShellOptions
        {
            DataDirectory = temp.Sub("data"),
            ExecutableDirectory = temp.Path,
            MockSensors = true,
            DemoData = true,
            Unattended = true,
            LegacyLocations = [],
            Operator = new OperatorModuleOptions
            {
                HostPath = MockHostPath(),
                MockDesktop = true,
                SecretStore = (_, _) => new WindowsAiOperator.Core.Settings.InMemorySecretStore(),
            },
        }))
        {
            await runtime.StartAsync(TestContext.Current.CancellationToken);

            // A: a manual inference session; E: a task through E's TaskService (journaled into wao.db).
            await runtime.LocalAi.Service!.StartRecordingAsync("[test] 통합 기록", "history test");
            var plan = PlanParser.Parse("""{"steps":[{"action":"click","window":{"app":"test-target"},"element":{"automation_id":"IncrementButton"}}]}""").Plan!;
            using var control = new ExecutionControl();
            var context = runtime.Operator!.Context!;
            var run = await context.Tasks.RunAsync(new TaskRun { Request = "[test] 카운트 증가", IsMockDesktop = true, Source = TaskSource.Workflow },
                new StaticPlanSource(plan, "test"), ScriptedApprovalHandler.DenyAll(), new NullTaskObserver(), control, TestContext.Current.CancellationToken);
            Assert.Equal(TaskRunStatus.Completed, run.Status);
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await runtime.LocalAi.Service.StopRecordingAsync();

            var paths = runtime.Paths;
            var reader = new TimelineReader(paths);
            var result = reader.Read(started.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(1));

            // Sensors from B's own recording, labelled MOCK, with B's source.
            var power = Assert.Single(result.Series, s => s.Key.StartsWith("b.gpu", StringComparison.Ordinal) && s.Key.EndsWith(".power", StringComparison.Ordinal));
            Assert.Equal(TimelineReader.SourceB, power.Source);
            Assert.True(power.IsMock);
            Assert.NotEmpty(power.Points);
            Assert.Contains(result.Series, s => s.Key == "c.cpu");

            // Events of all four sources on one list, test data flagged.
            Assert.Contains(result.Events, e => e.Source == TimelineReader.SourceA && e.Title == "[test] 통합 기록");
            Assert.Contains(result.Events, e => e.Source == TimelineReader.SourceE && e.Title == "[test] 카운트 증가" && e.IsTestData);

            // C's labelled demo scenario is spread over the last 30 days.
            var month = reader.Read(started.AddDays(-31), DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.Contains(month.Events, e => e.Source == TimelineReader.SourceC && e.IsTestData);

            // The hour before the app started is an observation gap of B, not a flat line.
            Assert.Contains(result.Gaps, g => g.Source == TimelineReader.SourceB && g.Kind == "not-running" && g.From <= started.AddMinutes(-30));
            Assert.All(result.Coverage, c => Assert.True(c.Available, $"{c.Source}: {c.Problem}"));

            // The view model draws it: one chart per unit, gaps as spans, events and coverage tables.
            var history = new HistoryViewModel(reader, new InlineShellDispatcher());
            history.Apply(result);
            Assert.False(history.PowerChart!.IsEmpty);
            Assert.NotEmpty(history.PowerChart.Spans);
            Assert.Contains("[MOCK]", history.PowerChart.Series[0].Label, StringComparison.Ordinal);
            Assert.Equal(4, history.Coverage.Count);
            Assert.Contains(history.Events, e => e.IsTestData);
            Assert.Contains("MOCK", history.StatusText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_missing_module_database_is_reported_and_never_created()
    {
        using var temp = new TempDir();
        var paths = ShellPaths.Resolve(temp.Sub("data"), temp.Path, legacy: []);
        var result = new TimelineReader(paths).Read(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);

        Assert.All(result.Coverage, c => Assert.False(c.Available));
        Assert.Empty(result.Series);
        Assert.All(ModuleIds.All, id => Assert.False(File.Exists(paths.ModuleDatabasePath(id))));
    }

    [Fact]
    public void Display_buckets_never_invent_points()
    {
        var start = DateTimeOffset.UtcNow;
        var points = Enumerable.Range(0, 10_000).Select(i => new TimelinePoint(start.AddSeconds(i), i % 2)).ToList();
        var bucketed = HistoryViewModel.Bucket(points);
        Assert.InRange(bucketed.Count, 1, HistoryViewModel.MaxDisplayPoints + 1);
        Assert.All(bucketed, p => Assert.InRange(p.Value, 0, 1));
        Assert.Same(points.Take(5).ToList() is var few ? few : null, HistoryViewModel.Bucket(few));
    }
}
