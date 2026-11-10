using Microsoft.Data.Sqlite;
using PcControlCenter.Core;
using PcControlCenter.Migration;

namespace PcControlCenter.Tests;

/// <summary>
/// Failure injection for applying an import: whatever step fails, the module's previous database, its -wal/-shm and its
/// settings come back byte for byte, or the failure to restore them is reported and the module is not started on files
/// that may not be its own.
/// </summary>
public class MigrationRollbackTests
{
    private static readonly string[] DatabaseFiles = ["", "-wal", "-shm"];

    /// <summary>The module supervisor's part, with hooks that inject failures at the moments the migrator calls it.</summary>
    private sealed class Restarter : IModuleRestarter
    {
        public Action? OnStop { get; init; }
        public Action<int>? OnStart { get; init; }
        public int Stops { get; private set; }
        public int Starts { get; private set; }

        public Task StopAsync(string moduleId)
        {
            Stops++;
            OnStop?.Invoke();
            return Task.CompletedTask;
        }

        public Task StartAsync(string moduleId)
        {
            Starts++;
            OnStart?.Invoke(Starts);
            return Task.CompletedTask;
        }
    }

    private sealed record Setup(LegacyMigrator Migrator, MigrationRehearsal Rehearsal, string Target, string Settings, Dictionary<string, string> Before)
    {
        public string Directory => Path.GetDirectoryName(Target)!;
    }

    /// <summary>
    /// An original E folder and the integrated E module, both with real E databases. The module's database also has
    /// left-over -wal/-shm files (opaque bytes: nothing opens them during the import) and, optionally, a settings file.
    /// </summary>
    private static Setup PrepareOperator(TempDir temp, bool moduleSettings = true)
    {
        var legacy = temp.Sub("WindowsAiOperator");
        Directory.CreateDirectory(legacy);
        using (new WindowsAiOperator.Storage.OperatorStore(Path.Combine(legacy, "wao.db")))
        {
        }

        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"imported\":true}");
        var paths = ShellPaths.Resolve(temp.Sub("pcc"), temp.Path, legacy: []);
        paths.EnsureDirectories();
        var target = paths.ModuleDatabasePath(ModuleIds.Operator);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (new WindowsAiOperator.Storage.OperatorStore(target))
        {
        }

        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(target + "-wal", [1, 2, 3, 4, 5]);
        File.WriteAllBytes(target + "-shm", [6, 7, 8, 9]);
        var settings = Path.Combine(paths.ModuleDirectory(ModuleIds.Operator), "settings.json");
        if (moduleSettings)
        {
            File.WriteAllText(settings, "{\"sentinel\":true}");
        }

        var migrator = new LegacyMigrator(paths, _ => []);
        var rehearsal = migrator.Rehearse(LegacyMigrator.FromFolder(legacy)!);
        Assert.True(rehearsal.Passed, Describe(rehearsal.Checks));
        return new Setup(migrator, rehearsal, target, settings, Snapshot(target, settings));
    }

    private static Dictionary<string, string> Snapshot(string target, string settings) =>
        DatabaseFiles.Select(s => target + s).Append(settings).Where(File.Exists)
            .ToDictionary(f => Path.GetFileName(f), f => Convert.ToHexString(File.ReadAllBytes(f)));

    /// <summary>Kept or staged files the import left next to the module's database.</summary>
    private static List<string> Leftovers(string directory) =>
        Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>()
            .Where(f => f.Contains(".before-import-", StringComparison.Ordinal) || f.Contains(".incoming-", StringComparison.Ordinal)).ToList();

    private static string Describe(IEnumerable<MigrationCheck> checks) => string.Join("\n", checks.Select(c => $"{c.State} {c.Name}: {c.Detail}"));

    private static MigrationRehearsal WithImpossibleCounts(MigrationRehearsal rehearsal)
    {
        var counts = new Dictionary<string, long>(rehearsal.TableCounts);
        counts["tasks"] += 1;
        return rehearsal with { TableCounts = counts };
    }

    [Fact(Timeout = 120_000)]
    public async Task A_failed_check_after_the_swap_restores_database_wal_shm_and_settings()
    {
        using var temp = new TempDir();
        var s = PrepareOperator(temp);
        var restarter = new Restarter();

        var result = await s.Migrator.ApplyAsync(WithImpossibleCounts(s.Rehearsal), restarter, includeSettings: true);

        Assert.False(result.Applied);
        Assert.False(result.RestoreFailed, Describe(result.Checks));
        Assert.Contains(result.Checks, c => c.Name == "되돌리기" && c.State == CheckState.Passed);
        Assert.Equal(s.Before, Snapshot(s.Target, s.Settings));
        Assert.Empty(Leftovers(s.Directory));
        Assert.Equal(1, restarter.Starts); // started again, with its own files
    }

    [Fact(Timeout = 120_000)]
    public async Task A_new_database_that_cannot_be_copied_leaves_the_module_files_untouched()
    {
        using var temp = new TempDir();
        var s = PrepareOperator(temp);

        // The staged copy disappears after the checks at the start (the module is stopped by then).
        var restarter = new Restarter { OnStop = () => File.Delete(s.Rehearsal.FinalDatabase!) };
        var result = await s.Migrator.ApplyAsync(s.Rehearsal, restarter, includeSettings: true);

        Assert.False(result.Applied);
        Assert.False(result.RestoreFailed, Describe(result.Checks));
        Assert.Equal(s.Before, Snapshot(s.Target, s.Settings));
        Assert.Empty(Leftovers(s.Directory));
        Assert.Equal(1, restarter.Starts);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_rollback_that_cannot_complete_is_reported_and_the_module_is_not_started()
    {
        using var temp = new TempDir();
        var s = PrepareOperator(temp);
        string? kept = null;

        // The module does not start with the new data, and meanwhile the kept previous database was removed (for example
        // by hand): its previous state can not be brought back automatically.
        var restarter = new Restarter
        {
            OnStart = call =>
            {
                if (call == 1)
                {
                    kept = Directory.EnumerateFiles(s.Directory, "wao.before-import-*.db").Single();
                    File.Delete(kept);
                    throw new IOException("simulated: the module could not start with the new data");
                }
            },
        };
        var result = await s.Migrator.ApplyAsync(s.Rehearsal, restarter, includeSettings: true);

        Assert.False(result.Applied);
        Assert.True(result.RestoreFailed, Describe(result.Checks));
        Assert.Contains(result.Checks, c => c.Name == "되돌리기" && c.State == CheckState.Failed);
        Assert.Equal(1, restarter.Starts); // not started again on files that are not its own
        Assert.Equal(kept, result.PreviousDatabaseKeptAt);
        Assert.Contains("직접 복구", result.Message, StringComparison.Ordinal);

        // Nothing of the module's own was deleted: what could not be put back is still kept, the settings are back.
        Assert.True(File.Exists(kept + "-wal"));
        Assert.True(File.Exists(kept + "-shm"));
        Assert.Equal(s.Before["settings.json"], Snapshot(s.Target, s.Settings)["settings.json"]);
    }

    [Fact(Timeout = 120_000)]
    public async Task Settings_a_failed_import_added_are_removed()
    {
        using var temp = new TempDir();
        var s = PrepareOperator(temp, moduleSettings: false);

        var result = await s.Migrator.ApplyAsync(WithImpossibleCounts(s.Rehearsal), new Restarter(), includeSettings: true);

        Assert.False(result.Applied);
        Assert.False(result.RestoreFailed, Describe(result.Checks));
        Assert.False(File.Exists(s.Settings), "the module had no settings file before");
        Assert.Equal(s.Before, Snapshot(s.Target, s.Settings));
    }

    [Fact]
    public void A_move_that_stopped_half_way_puts_back_only_what_was_moved()
    {
        using var temp = new TempDir();
        var target = Path.Combine(temp.Path, "wao.db");
        var kept = Path.Combine(temp.Path, "wao.before-import-x.db");

        // Before: wao.db, wao.db-wal, wao.db-shm. The database file was moved aside, then moving its -wal failed.
        File.WriteAllText(kept, "db");
        File.WriteAllText(target + "-wal", "wal");
        File.WriteAllText(target + "-shm", "shm");
        var progress = new LegacyMigrator.ReplaceProgress { Kept = kept };
        progress.Moved.Add("");

        var failures = LegacyMigrator.RestoreDatabase(target, DatabaseFiles, progress);

        Assert.Empty(failures);
        Assert.Equal("db", File.ReadAllText(target));
        Assert.Equal("wal", File.ReadAllText(target + "-wal")); // the module's own WAL is never deleted
        Assert.Equal("shm", File.ReadAllText(target + "-shm"));
    }

    [Fact(Timeout = 120_000)]
    public async Task A_locked_wal_during_the_swap_stays_in_place_and_the_database_comes_back()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to move a file that is open without delete sharing.");
        using var temp = new TempDir();
        var s = PrepareOperator(temp);
        FileStream? holder = null;
        var restarter = new Restarter
        {
            OnStop = () => holder = new FileStream(s.Target + "-wal", FileMode.Open, FileAccess.Read, FileShare.Read),
        };

        MigrationResult result;
        try
        {
            result = await s.Migrator.ApplyAsync(s.Rehearsal, restarter, includeSettings: true);
        }
        finally
        {
            holder?.Dispose();
        }

        Assert.False(result.Applied);
        Assert.False(result.RestoreFailed, Describe(result.Checks));
        Assert.Equal(s.Before, Snapshot(s.Target, s.Settings));
        Assert.Empty(Leftovers(s.Directory));
        Assert.Equal(1, restarter.Starts);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_corrupted_staged_copy_refused_by_the_modules_own_restore_is_handled()
    {
        using var temp = new TempDir();
        var legacy = temp.Sub("GpuProfitCenter");
        Directory.CreateDirectory(legacy);
        GpuProfit.Storage.ProfitStore.Open(Path.Combine(legacy, "gpc.db")).Dispose();
        var paths = ShellPaths.Resolve(temp.Sub("pcc"), temp.Path, legacy: []);
        paths.EnsureDirectories();
        var target = paths.ModuleDatabasePath(ModuleIds.GpuEconomics);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        GpuProfit.Storage.ProfitStore.Open(target).Dispose();
        SqliteConnection.ClearAllPools();
        var settings = Path.Combine(paths.ModuleDirectory(ModuleIds.GpuEconomics), "settings.json");
        var before = Snapshot(target, settings);
        var migrator = new LegacyMigrator(paths, _ => []);
        var rehearsal = migrator.Rehearse(LegacyMigrator.FromFolder(legacy)!);
        Assert.True(rehearsal.Passed, Describe(rehearsal.Checks));

        // The staged copy is damaged between the rehearsal and the apply: B's RestoreFrom refuses it (InvalidDataException).
        var restarter = new Restarter { OnStop = () => File.WriteAllBytes(rehearsal.FinalDatabase!, [0x42, 0x41, 0x44]) };
        var result = await migrator.ApplyAsync(rehearsal, restarter, includeSettings: false);

        Assert.False(result.Applied);
        Assert.False(result.RestoreFailed, Describe(result.Checks));
        Assert.Equal(before, Snapshot(target, settings));
        Assert.Equal(1, restarter.Starts); // not left stopped
    }
}
