using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Suite.Core.Storage;

namespace Suite.Tests;

public sealed class SampleSettings : IVersionedSettings<SampleSettings>
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public static int CurrentSchemaVersion => 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int IntervalMs { get; set; } = 1000;
    public SampleSection Section { get; set; } = new();

    public IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        Section ??= new SampleSection();
        if (IntervalMs is < 100 or > 5000)
        {
            notes.Add($"IntervalMs {IntervalMs} -> 1000");
            IntervalMs = 1000;
        }

        return notes;
    }
}

public sealed class SampleSection
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public string Name { get; set; } = "default";
}

public class JsonSettingsStoreTests
{
    [Fact]
    public void A_missing_file_gives_defaults_marked_as_created()
    {
        using var dir = new TempDir();
        var loaded = new JsonSettingsStore<SampleSettings>(dir.Sub("s.json")).Load();

        Assert.True(loaded.WasCreated);
        Assert.False(loaded.ReadFailed);
        Assert.Equal(1000, loaded.Settings.IntervalMs);
        Assert.False(File.Exists(dir.Sub("s.json")));
    }

    [Fact]
    public void Unknown_fields_at_every_depth_survive_load_and_save()
    {
        using var dir = new TempDir();
        var path = dir.Sub("s.json");
        File.WriteAllText(path, """
            {
              // comments and trailing commas are accepted, like ShellSettingsStore
              "SchemaVersion": 2,
              "IntervalMs": 750,
              "FutureTop": { "a": [1, 2, { "deep": true }] },
              "Section": { "Name": "x", "FutureNested": { "keep": "me" }, },
            }
            """);
        var store = new JsonSettingsStore<SampleSettings>(path);

        var loaded = store.Load();
        loaded.Settings.Section.Name = "changed";
        store.Save(loaded.Settings);

        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(750, saved["IntervalMs"]!.GetValue<int>());
        Assert.True(saved["FutureTop"]!["a"]![2]!["deep"]!.GetValue<bool>());
        Assert.Equal("changed", saved["Section"]!["Name"]!.GetValue<string>());
        Assert.Equal("me", saved["Section"]!["FutureNested"]!["keep"]!.GetValue<string>());
    }

    [Fact]
    public void A_newer_schema_is_read_noted_and_its_version_kept_on_save()
    {
        using var dir = new TempDir();
        var path = dir.Sub("s.json");
        File.WriteAllText(path, """{ "SchemaVersion": 9, "IntervalMs": 1200 }""");
        var store = new JsonSettingsStore<SampleSettings>(path);

        var loaded = store.Load();
        store.Save(loaded.Settings);

        Assert.Contains(loaded.Notes, n => n.Contains("스키마 9", StringComparison.Ordinal));
        Assert.Equal(9, JsonNode.Parse(File.ReadAllText(path))!["SchemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Out_of_range_values_are_fixed_with_a_note()
    {
        using var dir = new TempDir();
        var path = dir.Sub("s.json");
        File.WriteAllText(path, """{ "IntervalMs": 99999, "Section": null }""");

        var loaded = new JsonSettingsStore<SampleSettings>(path).Load();

        Assert.Equal(1000, loaded.Settings.IntervalMs);
        Assert.NotNull(loaded.Settings.Section);
        Assert.Contains(loaded.Notes, n => n.Contains("99999", StringComparison.Ordinal));
    }

    [Fact]
    public void A_damaged_file_is_kept_aside_never_overwritten_and_defaults_are_used()
    {
        using var dir = new TempDir();
        var path = dir.Sub("s.json");
        var time = new ManualTime(Gpus.T0);
        var store = new JsonSettingsStore<SampleSettings>(path, time);

        File.WriteAllText(path, "{ broken 1");
        var first = store.Load();
        File.WriteAllText(path, "{ broken 2");
        var second = store.Load(); // same second: must not collide with the first copy

        Assert.True(first.ReadFailed);
        Assert.True(second.ReadFailed);
        Assert.NotNull(first.KeptAsidePath);
        Assert.NotNull(second.KeptAsidePath);
        Assert.NotEqual(first.KeptAsidePath, second.KeptAsidePath);
        Assert.Equal("{ broken 1", File.ReadAllText(first.KeptAsidePath!));
        Assert.Equal("{ broken 2", File.ReadAllText(second.KeptAsidePath!));
        Assert.False(File.Exists(path));
        Assert.Equal(1000, second.Settings.IntervalMs);
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temporary_files()
    {
        using var dir = new TempDir();
        var path = dir.Sub("nested", "s.json");
        var store = new JsonSettingsStore<SampleSettings>(path);

        store.Save(new SampleSettings { IntervalMs = 1500 });
        store.Save(new SampleSettings { IntervalMs = 1600 });

        Assert.Equal(1600, store.Load().Settings.IntervalMs);
        Assert.Equal(["s.json"], Directory.GetFiles(dir.Sub("nested")).Select(Path.GetFileName));
    }
}

public class SqliteDatabaseTests
{
    private const int AppId = 0x54455354; // "TEST"

    private static readonly string[] Migrations =
    [
        "CREATE TABLE a (id INTEGER PRIMARY KEY, v REAL);",
        "CREATE TABLE b (id INTEGER PRIMARY KEY);",
    ];

    [Fact]
    public void A_new_database_gets_every_migration_the_mark_and_wal()
    {
        using var dir = new TempDir();
        using (var c = SqliteDatabase.OpenAndMigrate(dir.Sub("t.db"), Migrations, AppId))
        {
            Assert.Equal(2, SqliteDatabase.SchemaVersion(c));
            Assert.Equal((long)AppId, SqliteDatabase.Scalar(c, "PRAGMA application_id;"));
            Assert.Equal("wal", SqliteDatabase.Scalar(c, "PRAGMA journal_mode;"));
            SqliteDatabase.Exec(c, "INSERT INTO a (v) VALUES (1.5);");
        }

        using var again = SqliteDatabase.OpenAndMigrate(dir.Sub("t.db"), Migrations, AppId);
        Assert.Equal(1L, SqliteDatabase.Scalar(again, "SELECT COUNT(*) FROM a;"));
    }

    [Fact]
    public void Later_migrations_are_applied_to_an_older_database()
    {
        using var dir = new TempDir();
        SqliteDatabase.OpenAndMigrate(dir.Sub("t.db"), Migrations[..1], AppId).Dispose();

        using var c = SqliteDatabase.OpenAndMigrate(dir.Sub("t.db"), Migrations, AppId);

        Assert.Equal(2, SqliteDatabase.SchemaVersion(c));
        Assert.Equal(0L, SqliteDatabase.Scalar(c, "SELECT COUNT(*) FROM b;"));
    }

    [Fact]
    public void A_newer_database_is_refused_and_left_unchanged()
    {
        using var dir = new TempDir();
        var path = dir.Sub("t.db");
        using (var c = SqliteDatabase.OpenAndMigrate(path, Migrations, AppId))
        {
            SqliteDatabase.Exec(c, "PRAGMA user_version=7;");
            SqliteDatabase.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        var before = Hash(path);

        Assert.Throws<StorageVersionException>(() => SqliteDatabase.OpenAndMigrate(path, Migrations, AppId));
        Assert.Equal(before, Hash(path));
    }

    [Fact]
    public void A_file_that_is_not_sqlite_is_refused_and_left_unchanged()
    {
        using var dir = new TempDir();
        var path = dir.Sub("t.db");
        File.WriteAllText(path, "this is not a database, but somebody's notes");

        Assert.Throws<StorageCorruptException>(() => SqliteDatabase.OpenAndMigrate(path, Migrations, AppId));
        Assert.Equal("this is not a database, but somebody's notes", File.ReadAllText(path));
    }

    [Fact]
    public void Another_programs_database_is_refused_before_anything_is_written()
    {
        using var dir = new TempDir();
        var marked = dir.Sub("other.db");
        var unmarked = dir.Sub("lapc.db");
        foreach (var (path, mark) in new[] { (marked, 0x4F544852), (unmarked, 0) })
        {
            using var c = new SqliteConnection(SqliteDatabase.ConnectionString(path, SqliteOpenMode.ReadWriteCreate));
            c.Open();
            SqliteDatabase.Exec(c, $"PRAGMA application_id={mark}; CREATE TABLE frames (ts INTEGER);");
        }

        foreach (var path in new[] { marked, unmarked })
        {
            var before = Hash(path);
            Assert.Throws<StorageCorruptException>(() => SqliteDatabase.OpenAndMigrate(path, Migrations, AppId));
            Assert.Equal(before, Hash(path));
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

public class ProductDataRootTests
{
    [Fact]
    public void Override_wins_over_portable_and_installed()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Sub("portable.txt"), "");

        var root = ProductDataRoot.Resolve(dir.Sub("custom"), dir.Path, dir.Sub("local"));

        Assert.Equal(dir.Sub("custom"), root.Root);
        Assert.False(root.IsPortable);
    }

    [Fact]
    public void Portable_marker_uses_the_data_folder_next_to_the_exe()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Sub("portable.txt"), "");

        var root = ProductDataRoot.Resolve(null, dir.Path, dir.Sub("local"));

        Assert.True(root.IsPortable);
        Assert.Equal(dir.Sub("data"), root.Root);
    }

    [Fact]
    public void Installed_location_is_the_suite_folder_under_local_app_data()
    {
        using var dir = new TempDir();

        var root = ProductDataRoot.Resolve(null, dir.Sub("exe"), dir.Sub("local"));

        Assert.Equal(dir.Sub("local", "PcControlSuite", "data"), root.Root);
    }

    [Fact]
    public void Each_product_gets_its_own_subfolder()
    {
        using var dir = new TempDir();
        var root = new ProductDataRoot(dir.Sub("data"), IsPortable: true);

        Assert.Equal(dir.Sub("data", "iron"), root.ProductDirectory("iron", []));
        Assert.Equal(dir.Sub("data", "relay"), root.ProductDirectory("relay", []));
        Assert.Throws<ArgumentException>(() => root.ProductDirectory("Iron", []));
        Assert.Throws<ArgumentException>(() => root.ProductDirectory("..", []));
        Assert.Throws<ArgumentException>(() => root.ProductDirectory("a/b", []));
    }

    [Theory]
    [InlineData("LocalAiPerformanceCenter")]
    [InlineData("GpuProfitCenter")]
    [InlineData("WindowsDiagnosticsLab")]
    [InlineData("WindowsAiOperator")]
    public void Original_program_folders_are_refused(string legacy)
    {
        using var dir = new TempDir();
        var local = dir.Sub("local");
        var reserved = ProductDataRoot.KnownReserved(dir.Sub("exe"), local);

        // --data-dir pointing at (or into) an original program's folder.
        var inside = new ProductDataRoot(Path.Combine(local, legacy), IsPortable: false);
        Assert.Throws<InvalidOperationException>(() => inside.ProductDirectory("iron", reserved));
        var parent = new ProductDataRoot(Path.Combine(local, legacy, ".."), IsPortable: false);
        Assert.NotNull(ProductDataRoot.Overlapping(Path.Combine(local, legacy, "x"), reserved));
        Assert.Equal(Path.Combine(local, "iron"), parent.ProductDirectory("iron", reserved));
    }

    [Fact]
    public void Pc_control_center_module_folders_are_refused_installed_and_portable()
    {
        using var dir = new TempDir();
        var local = dir.Sub("local");
        var exe = dir.Sub("exe");
        var reserved = ProductDataRoot.KnownReserved(exe, local);

        Assert.Throws<InvalidOperationException>(() =>
            new ProductDataRoot(Path.Combine(local, "PcControlCenter", "modules"), false).ProductDirectory("iron", reserved));
        Assert.Throws<InvalidOperationException>(() =>
            new ProductDataRoot(Path.Combine(exe, "data", "modules", "local-ai"), false).ProductDirectory("iron", reserved));

        // Sharing PC Control Center's portable data folder is fine: IRON stays in data\iron, apart from data\modules.
        Assert.Equal(Path.Combine(exe, "data", "iron"), new ProductDataRoot(Path.Combine(exe, "data"), true).ProductDirectory("iron", reserved));
    }
}

public class DataFolderLockTests
{
    [Fact]
    public void Only_one_holder_per_folder_until_released()
    {
        using var dir = new TempDir();

        using (var first = DataFolderLock.TryAcquire(dir.Path, "p.lock"))
        {
            Assert.NotNull(first);
            Assert.Null(DataFolderLock.TryAcquire(dir.Path, "p.lock"));
            using var other = DataFolderLock.TryAcquire(dir.Path, "q.lock");
            Assert.NotNull(other);
        }

        using var again = DataFolderLock.TryAcquire(dir.Path, "p.lock");
        Assert.NotNull(again);
    }
}
