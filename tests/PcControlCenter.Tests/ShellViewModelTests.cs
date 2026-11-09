using PcControlCenter.Core;
using PcControlCenter.Modules;
using PcControlCenter.Presentation;

namespace PcControlCenter.Tests;

/// <summary>Runs UI work under one lock (module status events arrive on background threads).</summary>
internal sealed class LockedDispatcher : IShellDispatcher
{
    private readonly Lock _gate = new();

    public void Post(Action action)
    {
        lock (_gate)
        {
            action();
        }
    }
}

public class ShellViewModelTests
{
    private static async Task<bool> Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
    }

    [Fact(Timeout = 180_000)]
    public async Task The_shell_shows_all_modules_in_one_menu_and_a_live_dashboard()
    {
        using var temp = new TempDir();
        await using var runtime = ShellRuntime.Create(new ShellOptions
        {
            DataDirectory = temp.Sub("data"),
            ExecutableDirectory = temp.Path,
            MockSensors = true,
            DemoData = true,
            Unattended = true,
            LegacyLocations = [],
        });
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        var ui = new LockedDispatcher();
        var shell = new ShellViewModel(runtime, ui, new RecordingShellDialogs());

        // One menu: eight sections, every planned page present exactly once.
        Assert.Equal(["대시보드", "하드웨어", "로컬 AI", "GPU 경제성", "시스템 진단", "AI Operator", "통합 기록", "설정"], shell.Sections.Select(s => s.Title));
        Assert.Equal(ShellViewModel.Plan.Count, shell.Sections.Sum(s => s.Tabs.Count));
        Assert.Equal(ShellViewModel.Plan.Count, shell.Sections.SelectMany(s => s.Tabs).Select(t => t.Key).Distinct().Count());

        // Module pages wait for the UI layer to bind the module's own view models; until then the placeholder says why.
        Assert.All(shell.TabsOf(ModuleIds.GpuEconomics), t => Assert.IsType<ModuleUnavailableViewModel>(t.Content));

        // Live hardware values come from the shared reader (mock here, and labelled so).
        Assert.True(await Eventually(() =>
        {
            ui.Post(shell.Tick);
            return shell.Dashboard.CpuUtil != Text.NA;
        }, TimeSpan.FromSeconds(10)));
        Assert.True(shell.Dashboard.IsMock);
        Assert.Contains("MOCK", shell.Dashboard.SensorSource, StringComparison.Ordinal);
        Assert.Contains("MOCK", shell.SensorStatusText, StringComparison.Ordinal);

        // Database-backed cards refresh while the dashboard is visible: C's demo scenario has (virtual) errors.
        Assert.True(await Eventually(() =>
        {
            ui.Post(shell.Tick);
            return shell.Dashboard.RecentErrors.Count > 0 && shell.Dashboard.EconomicsSessions != Text.NA;
        }, TimeSpan.FromSeconds(20)), $"{shell.Dashboard.DiagnosticsNote} / {shell.Dashboard.EconomicsNote}");
        if (!OperatingSystem.IsWindows())
        {
            // Without a Windows event log the only events are C's labelled demo scenario.
            Assert.All(shell.Dashboard.RecentErrors, e => Assert.True(e.IsVirtual));
        }
        else
        {
            // On Windows C also collects the real event log: real events must not be labelled as test data.
            var stored = runtime.Diagnostics.Engine!.Store.QueryEvents(new WinDiag.Storage.EventQuery { MinSeverity = WinDiag.Core.Events.DiagSeverity.Error, Limit = 500 });
            Assert.All(shell.Dashboard.RecentErrors, line => Assert.Contains(stored, e => e.Title == line.Title && e.IsVirtual == line.IsVirtual));
        }
        Assert.Contains("추정", shell.Dashboard.EconomicsNote, StringComparison.Ordinal);

        // Status rows: A, B, C usable (C is limited on Linux: no Windows event log), E reported separately.
        var rows = shell.ModuleRows.ToDictionary(r => r.ModuleId);
        Assert.Equal(Level.Good, rows[ModuleIds.GpuEconomics].Level);
        Assert.NotEqual(Level.Error, rows[ModuleIds.LocalAi].Level);
        Assert.NotEqual(Level.Error, rows[ModuleIds.Diagnostics].Level);

        // Navigation by section or by page key.
        shell.Navigate(SectionKeys.Diagnostics, "diagnostics/events");
        Assert.Equal(SectionKeys.Diagnostics, shell.CurrentSection!.Key);
        Assert.Equal("diagnostics/events", shell.CurrentSection.SelectedTab!.Key);
        shell.Navigate("settings/data");
        Assert.Equal("settings/data", shell.CurrentSection!.SelectedTab!.Key);

        // A stopped module puts placeholders back and tells the UI layer to drop its view models.
        var unbound = new List<string>();
        shell.ModuleUnbound += unbound.Add;
        var marker = new object();
        shell.BindModulePage("economics/overview", marker, null, null);
        await runtime.Economics.StopAsync();
        Assert.True(await Eventually(() => unbound.Contains(ModuleIds.GpuEconomics), TimeSpan.FromSeconds(10)));
        Assert.IsType<ModuleUnavailableViewModel>(shell.FindTab("economics/overview")!.Content);
    }

    [Fact]
    public void A_data_folder_inside_an_original_program_is_refused_before_anything_starts()
    {
        using var temp = new TempDir();
        var legacy = LegacyDataLocations.Defaults(temp.Path);
        var ex = Assert.Throws<ShellStartException>(() => ShellRuntime.Create(new ShellOptions
        {
            DataDirectory = Path.Combine(legacy[0].Directory, "x"),
            ExecutableDirectory = temp.Path,
            MockSensors = true,
            LegacyLocations = legacy,
        }));
        Assert.Equal(ShellStartProblem.DataFolder, ex.Problem);
    }

    [Fact]
    public async Task A_second_instance_on_the_same_data_folder_is_refused()
    {
        using var temp = new TempDir();
        var options = new ShellOptions { DataDirectory = temp.Sub("data"), ExecutableDirectory = temp.Path, MockSensors = true, Unattended = true, LegacyLocations = [] };
        await using var first = ShellRuntime.Create(options);
        var ex = Assert.Throws<ShellStartException>(() => ShellRuntime.Create(options));
        Assert.Equal(ShellStartProblem.AlreadyRunning, ex.Problem);
    }
}

public class OperatorModuleTests
{
    private static string MockHostPath()
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PcControlCenter.MockOperatorHost", "bin", configuration, "net10.0",
            "PcControlCenter.MockOperatorHost.dll"));
    }

    [Fact(Timeout = 180_000)]
    public async Task The_operator_runs_e_in_the_shell_with_its_desktop_in_a_separate_process()
    {
        using var temp = new TempDir();
        await using var runtime = ShellRuntime.Create(new ShellOptions
        {
            DataDirectory = temp.Sub("data"),
            ExecutableDirectory = temp.Path,
            MockSensors = true,
            Unattended = true,
            LegacyLocations = [],
            Operator = new OperatorModuleOptions
            {
                HostPath = MockHostPath(),
                MockDesktop = true,
                MockModel = true,
                SecretStore = (_, _) => new WindowsAiOperator.Core.Settings.InMemorySecretStore(),
            },
        });
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        var shell = new ShellViewModel(runtime, new LockedDispatcher(), new RecordingShellDialogs());
        var module = runtime.Operator!;

        Assert.True(module.Status.IsUsable, module.Status.Summary);
        Assert.NotNull(module.Context);
        Assert.True(module.Context.Desktop.IsMock);
        Assert.NotEqual(Environment.ProcessId, module.Host!.ProcessId);
        Assert.True(File.Exists(runtime.Paths.ModuleDatabasePath(ModuleIds.Operator)));
        Assert.NotNull(shell.Operator);
        Assert.NotEmpty(module.Context.Desktop.ListWindows());

        // E's executor in the shell, the desktop in the host: a built-in workflow on the simulated test app.
        var workflow = module.Context.Workflows.List().First(w => w.Id == "builtin-test-counter");
        Assert.NotNull(workflow);

        await shell.Operator!.EmergencyStopCommand.ExecuteAsync(null);
        Assert.True(module.Host.EmergencyLatched);
        Assert.Equal(ModuleState.Degraded, module.Status.State);
        Assert.Contains("긴급 중단", shell.Operator.HostText, StringComparison.Ordinal);
        await shell.Operator.ResumeCommand.ExecuteAsync(null);
        Assert.False(module.Host.EmergencyLatched);
        Assert.Equal(ModuleState.Running, module.Status.State);

        // The dashboard reads the Operator state (without E's task runner bound: host state only).
        var summary = module.Summary();
        Assert.True(summary.HostConnected);
        Assert.True(summary.IsMockDesktop);
    }
}
