using System.Security.Cryptography;
using LocalAiPerf.Core.Diagnostics;
using Microsoft.Data.Sqlite;
using PcControlCenter.Core;
using PcControlCenter.Migration;
using PcControlCenter.Modules;

namespace PcControlCenter.Tests;

public class MigrationTests
{
    /// <summary>A data folder of the original program B, written by B's own engine (mock sensors, labelled demo data).</summary>
    private static async Task<LegacyLocation> CreateLegacyB(string folder)
    {
        var paths = GpuProfit.Core.AppPaths.Resolve(folder, folder);
        var engine = await GpuProfit.Recording.ProfitEngine.StartAsync(new GpuProfit.Recording.EngineOptions { Paths = paths, UseMockSensors = true, AutoSample = false });
        GpuProfit.Recording.DemoDataSeeder.Seed(engine.Store, engine.Sessions, DateTimeOffset.UtcNow);
        engine.Recorder!.SampleOnce();
        await engine.StopAsync();
        File.WriteAllText(Path.Combine(folder, "settings.json"), File.Exists(paths.SettingsPath) ? File.ReadAllText(paths.SettingsPath) : "{}");
        SqliteConnection.ClearAllPools();
        return new LegacyLocation(ModuleIds.GpuEconomics, "GPU Profit & Compute Center", folder, "gpc.db");
    }

    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));

    private static ShellPaths Shell(TempDir temp)
    {
        var paths = ShellPaths.Resolve(temp.Sub("pcc"), temp.Path, legacy: []);
        paths.EnsureDirectories();
        return paths;
    }

    [Fact(Timeout = 120_000)]
    public async Task Rehearsal_copies_and_verifies_without_touching_the_original()
    {
        using var temp = new TempDir();
        var legacy = await CreateLegacyB(temp.Sub("GpuProfitCenter"));
        var before = Snapshot(legacy.Directory);
        var migrator = new LegacyMigrator(Shell(temp), _ => []);

        var rehearsal = migrator.Rehearse(legacy);

        Assert.True(rehearsal.Passed, string.Join("\n", rehearsal.Checks.Select(c => $"{c.State} {c.Name}: {c.Detail}")));
        Assert.Contains(rehearsal.Checks, c => c.Name == "복사본 일치" && c.State == CheckState.Passed);
        Assert.Contains(rehearsal.Checks, c => c.Name == "행 수 비교 (모든 테이블)" && c.State == CheckState.Passed);
        Assert.Contains(rehearsal.Checks, c => c.Name.StartsWith("주요 집계", StringComparison.Ordinal));
        Assert.True(rehearsal.TableCounts["sessions"] > 0);
        Assert.True(File.Exists(rehearsal.FinalDatabase));
        Assert.True(File.Exists(Path.Combine(rehearsal.StagingDirectory, "rehearsal-report.json")));

        // Not a byte of the original folder changed, and no file (journal, -shm) appeared in it.
        Assert.Equal(before, Snapshot(legacy.Directory));
    }

    [Fact(Timeout = 120_000)]
    public async Task A_running_original_program_stops_the_rehearsal()
    {
        using var temp = new TempDir();
        var legacy = await CreateLegacyB(temp.Sub("GpuProfitCenter"));
        var migrator = new LegacyMigrator(Shell(temp), id => id == ModuleIds.GpuEconomics ? ["GpuProfitCenter (PID 4321)"] : []);

        var rehearsal = migrator.Rehearse(legacy);

        Assert.False(rehearsal.Passed);
        Assert.Contains(rehearsal.Checks, c => c.State == CheckState.Failed && c.Detail.Contains("실행 중", StringComparison.Ordinal));
        Assert.Null(rehearsal.FinalDatabase);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_newer_schema_is_refused()
    {
        using var temp = new TempDir();
        var legacy = await CreateLegacyB(temp.Sub("GpuProfitCenter"));
        using (var c = new SqliteConnection($"Data Source={legacy.DatabasePath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=99;";
            cmd.ExecuteNonQuery();
        }

        var rehearsal = new LegacyMigrator(Shell(temp), _ => []).Rehearse(legacy);

        Assert.False(rehearsal.Passed);
        Assert.Contains(rehearsal.Checks, c => c.Name == "스키마 버전" && c.State == CheckState.Failed);
    }

    [Fact]
    public void Supported_schema_versions_match_the_modules()
    {
        using var temp = new TempDir();
        int Version(string db)
        {
            using var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        using (LocalAiPerf.Storage.TelemetryStore.Open(temp.Sub("lapc.db"))) { }
        using (GpuProfit.Storage.ProfitStore.Open(temp.Sub("gpc.db"))) { }
        using (WinDiag.Storage.DiagnosticsStore.Open(temp.Sub("wdl.db"))) { }
        using (new WindowsAiOperator.Storage.OperatorStore(temp.Sub("wao.db"))) { }
        SqliteConnection.ClearAllPools();

        Assert.Equal(LegacyMigrator.SupportedSchema(ModuleIds.LocalAi), Version(temp.Sub("lapc.db")));
        Assert.Equal(LegacyMigrator.SupportedSchema(ModuleIds.GpuEconomics), Version(temp.Sub("gpc.db")));
        Assert.Equal(LegacyMigrator.SupportedSchema(ModuleIds.Diagnostics), Version(temp.Sub("wdl.db")));
        Assert.Equal(LegacyMigrator.SupportedSchema(ModuleIds.Operator), Version(temp.Sub("wao.db")));
    }

    [Fact]
    public void Operator_and_other_databases_rehearse_too()
    {
        using var temp = new TempDir();
        var folder = temp.Sub("WindowsAiOperator");
        Directory.CreateDirectory(folder);
        using (new WindowsAiOperator.Storage.OperatorStore(Path.Combine(folder, "wao.db"))) { }
        SqliteConnection.ClearAllPools();
        var location = LegacyMigrator.FromFolder(folder);
        Assert.NotNull(location);
        Assert.Equal(ModuleIds.Operator, location.ModuleId);

        var rehearsal = new LegacyMigrator(Shell(temp), _ => []).Rehearse(location);

        Assert.True(rehearsal.Passed, string.Join("\n", rehearsal.Checks.Select(c => $"{c.State} {c.Name}: {c.Detail}")));
    }

    [Fact(Timeout = 180_000)]
    public async Task Apply_replaces_the_module_data_keeps_the_previous_file_and_restarts_the_module()
    {
        using var temp = new TempDir();
        var legacy = await CreateLegacyB(temp.Sub("GpuProfitCenter"));
        var originalBefore = Snapshot(legacy.Directory);
        await using var runtime = ShellRuntime.Create(new ShellOptions
        {
            DataDirectory = temp.Sub("pcc"), ExecutableDirectory = temp.Path, MockSensors = true, Unattended = true, LegacyLocations = [],
        });
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(runtime.Economics.Status.IsUsable);
        var sessionsBefore = runtime.Economics.Engine!.Store.ListSessions(new GpuProfit.Storage.SessionQuery()).Count;
        var migrator = new LegacyMigrator(runtime.Paths, _ => []);
        var rehearsal = migrator.Rehearse(legacy);
        Assert.True(rehearsal.Passed);

        var result = await migrator.ApplyAsync(rehearsal, new SupervisorModuleRestarter(runtime.Supervisor), includeSettings: true);

        Assert.True(result.Applied, result.Message + "\n" + string.Join("\n", result.Checks.Select(c => $"{c.State} {c.Name}: {c.Detail}")));
        Assert.True(runtime.Economics.Status.IsUsable, runtime.Economics.Status.Summary);
        var sessionsAfter = runtime.Economics.Engine!.Store.ListSessions(new GpuProfit.Storage.SessionQuery()).Count;
        Assert.Equal(rehearsal.TableCounts["sessions"], sessionsAfter);
        Assert.NotEqual(sessionsBefore, sessionsAfter);
        Assert.NotNull(result.PreviousDatabaseKeptAt);
        Assert.True(File.Exists(result.PreviousDatabaseKeptAt), "the integrated app's previous database is kept, not deleted");
        Assert.Equal(originalBefore, Snapshot(legacy.Directory));
    }

    [Fact]
    public void A_folder_inside_the_integrated_data_root_is_refused()
    {
        using var temp = new TempDir();
        var paths = Shell(temp);
        var inside = new LegacyLocation(ModuleIds.GpuEconomics, "B", paths.ModuleDirectory(ModuleIds.GpuEconomics), "gpc.db");

        var rehearsal = new LegacyMigrator(paths, _ => []).Rehearse(inside);

        Assert.False(rehearsal.Passed);
    }
}
