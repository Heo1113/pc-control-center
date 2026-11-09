using LocalAiPerf.Core.Diagnostics;
using PcControlCenter.Core;

namespace PcControlCenter.Tests;

public class ShellPathsTests
{
    [Fact]
    public void Each_module_gets_its_own_folder_with_the_original_database_name()
    {
        using var temp = new TempDir();
        var paths = ShellPaths.Resolve(temp.Sub("data"), temp.Path, legacy: []);
        paths.EnsureDirectories();

        Assert.Equal(temp.Sub("data", "modules", "local-ai", "lapc.db"), paths.ModuleDatabasePath(ModuleIds.LocalAi));
        Assert.Equal(temp.Sub("data", "modules", "gpu-economics", "gpc.db"), paths.ModuleDatabasePath(ModuleIds.GpuEconomics));
        Assert.Equal(temp.Sub("data", "modules", "diagnostics", "wdl.db"), paths.ModuleDatabasePath(ModuleIds.Diagnostics));
        Assert.Equal(temp.Sub("data", "modules", "operator", "wao.db"), paths.ModuleDatabasePath(ModuleIds.Operator));
        Assert.All(ModuleIds.All, id => Assert.True(Directory.Exists(paths.ModuleDirectory(id))));
    }

    [Fact]
    public void A_data_folder_overlapping_an_original_program_is_refused()
    {
        using var temp = new TempDir();
        var legacy = LegacyDataLocations.Defaults(temp.Path);
        var original = legacy.Single(l => l.ModuleId == ModuleIds.GpuEconomics).Directory;

        Assert.Throws<InvalidOperationException>(() => ShellPaths.Resolve(original, temp.Path, legacy));
        Assert.Throws<InvalidOperationException>(() => ShellPaths.Resolve(Path.Combine(original, "sub"), temp.Path, legacy));
        Assert.Throws<InvalidOperationException>(() => ShellPaths.Resolve(temp.Path, temp.Path, legacy));
        Assert.False(Directory.Exists(original), "resolving must not create anything in an original program's folder");
    }

    [Fact]
    public void Portable_marker_puts_data_next_to_the_executable()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, ShellPaths.PortableMarkerFile), "");

        var paths = ShellPaths.Resolve(null, temp.Path, legacy: []);

        Assert.True(paths.IsPortable);
        Assert.Equal(temp.Sub("data"), paths.Root);
    }

    [Fact]
    public void Legacy_defaults_match_the_original_programs_folders()
    {
        var legacy = LegacyDataLocations.Defaults("/x");
        Assert.Equal(["LocalAiPerformanceCenter", "GpuProfitCenter", "WindowsDiagnosticsLab", "WindowsAiOperator"],
            legacy.Select(l => Path.GetFileName(l.Directory)));
        Assert.Equal(["lapc.db", "gpc.db", "wdl.db", "wao.db"], legacy.Select(l => l.DatabaseFile));
    }

    [Fact]
    public void Only_one_process_per_data_folder()
    {
        using var temp = new TempDir();
        using var first = DataRootLock.TryAcquire(temp.Path);
        Assert.NotNull(first);
        Assert.Null(DataRootLock.TryAcquire(temp.Path));
        first.Dispose();
        using var again = DataRootLock.TryAcquire(temp.Path);
        Assert.NotNull(again);
    }
}

public class ShellSettingsTests
{
    [Fact]
    public void Settings_round_trip_and_out_of_range_values_are_reported()
    {
        using var temp = new TempDir();
        var store = new ShellSettingsStore(temp.Sub("shell-settings.json"));
        var created = store.Load();
        Assert.True(created.WasCreated);
        created.Settings.Modules.Operator = false;
        created.Settings.Hardware.MaxIntervalMs = 10;
        store.Save(created.Settings);

        var loaded = store.Load();

        Assert.False(loaded.Settings.Modules.Operator);
        Assert.False(loaded.Settings.IsEnabled(ModuleIds.Operator));
        Assert.Equal(1000, loaded.Settings.Hardware.MaxIntervalMs);
        Assert.Contains(loaded.Notes, n => n.Contains("공통 센서", StringComparison.Ordinal));
    }

    [Fact]
    public void A_damaged_settings_file_is_kept_aside_not_deleted()
    {
        using var temp = new TempDir();
        var path = temp.Sub("shell-settings.json");
        File.WriteAllText(path, "{ not json");

        var loaded = new ShellSettingsStore(path).Load();

        Assert.True(loaded.Settings.Modules.LocalAi);
        Assert.Single(loaded.Notes);
        Assert.Single(Directory.GetFiles(temp.Path, "shell-settings.corrupt-*.json"));
    }
}

public class ModuleSupervisorTests
{
    private sealed class TestModule(string id, Func<CancellationToken, Task> start, bool enabled = true) : ShellModuleBase(id, NullLogger.Instance)
    {
        public int Stops { get; private set; }
        protected override bool Enabled => enabled;
        protected override Task OnStartAsync(CancellationToken cancellationToken) => start(cancellationToken);

        protected override Task OnStopAsync()
        {
            Stops++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_failing_or_hanging_module_does_not_stop_the_others()
    {
        var previous = ShellModuleBase.StartTimeout;
        ShellModuleBase.StartTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var supervisor = new ModuleSupervisor(NullLogger.Instance);
            var ok = supervisor.Add(new TestModule("ok", _ => Task.CompletedTask));
            var throws = supervisor.Add(new TestModule("throws", _ => throw new InvalidOperationException("db locked")));
            var hangs = supervisor.Add(new TestModule("hangs", _ => Task.Delay(Timeout.Infinite)));
            var off = supervisor.Add(new TestModule("off", _ => Task.CompletedTask, enabled: false));

            await supervisor.StartAllAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ModuleState.Running, ok.Status.State);
            Assert.Equal(ModuleState.Failed, throws.Status.State);
            Assert.Contains("db locked", throws.Status.Summary, StringComparison.Ordinal);
            Assert.Equal(1, throws.Stops);
            Assert.Equal(ModuleState.Failed, hangs.Status.State);
            Assert.Equal(ModuleState.Disabled, off.Status.State);

            await supervisor.StopAllAsync();
            Assert.Equal(ModuleState.Stopped, ok.Status.State);
            Assert.Equal(ModuleState.Failed, throws.Status.State);
        }
        finally
        {
            ShellModuleBase.StartTimeout = previous;
        }
    }

    [Fact]
    public async Task A_failed_module_can_be_restarted()
    {
        var attempts = 0;
        var module = new TestModule("flaky", _ => ++attempts == 1 ? throw new IOException("first start fails") : Task.CompletedTask);

        await module.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ModuleState.Failed, module.Status.State);
        await module.RestartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ModuleState.Running, module.Status.State);
    }
}
