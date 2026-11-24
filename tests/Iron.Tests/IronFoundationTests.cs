using System.Reflection;
using System.Text.Json.Nodes;
using Iron.Core;
using LocalAiPerf.Core.Hardware;
using Microsoft.Data.Sqlite;
using Suite.Core.Hardware;
using Suite.Core.Storage;

namespace Iron.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iron-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public class IronPathsTests
{
    [Fact]
    public void Portable_layout_is_data_iron_with_its_own_settings_and_database()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Sub("portable.txt"), "");

        var paths = IronPaths.Resolve(null, dir.Path, dir.Sub("local"));

        Assert.True(paths.IsPortable);
        Assert.Equal(dir.Sub("data", "iron"), paths.Directory);
        Assert.Equal(dir.Sub("data", "iron", "iron-settings.json"), paths.SettingsPath);
        Assert.Equal(dir.Sub("data", "iron", "iron-telemetry.db"), paths.DatabasePath);
    }

    [Fact]
    public void Installed_layout_is_under_the_suite_folder_not_pc_control_center()
    {
        using var dir = new TempDir();

        var paths = IronPaths.Resolve(null, dir.Sub("exe"), dir.Sub("local"));

        Assert.Equal(dir.Sub("local", "PcControlSuite", "data", "iron", "iron-telemetry.db"), paths.DatabasePath);
        Assert.DoesNotContain("PcControlCenter", paths.Directory, StringComparison.Ordinal);
    }

    [Fact]
    public void Iron_files_never_share_a_folder_or_name_with_the_module_databases()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Sub("portable.txt"), "");
        var paths = IronPaths.Resolve(null, dir.Path, dir.Sub("local"));
        string[] moduleDatabases = ["lapc.db", "gpc.db", "wdl.db", "wao.db"];

        Assert.DoesNotContain(Path.GetFileName(paths.DatabasePath), moduleDatabases);
        Assert.False(paths.Directory.StartsWith(dir.Sub("data", "modules"), StringComparison.Ordinal));
        Assert.NotEqual("shell-settings.json", Path.GetFileName(paths.SettingsPath));
    }

    [Fact]
    public void A_data_folder_inside_pc_control_center_modules_or_an_original_program_is_refused()
    {
        using var dir = new TempDir();
        var local = dir.Sub("local");

        Assert.Throws<InvalidOperationException>(() => IronPaths.Resolve(Path.Combine(local, "PcControlCenter", "modules"), dir.Sub("exe"), local));
        Assert.Throws<InvalidOperationException>(() => IronPaths.Resolve(Path.Combine(local, "GpuProfitCenter"), dir.Sub("exe"), local));
    }
}

public class IronSettingsTests
{
    [Fact]
    public void Defaults_match_the_iron_a_slim_design()
    {
        var s = new IronSettings();
        Assert.Empty(s.Normalize());
        Assert.Equal(IronWindowSettings.Maximized, s.Window.StartLayout);
        Assert.True(s.Window.CloseToTray);
        Assert.Equal(1000, s.Hardware.IntervalMs);
        Assert.Null(s.Electricity.RateKrwPerKwh);
        Assert.Equal(1, IronSettings.CurrentSchemaVersion);
    }

    [Fact]
    public void Unknown_nested_fields_are_preserved_through_load_and_save()
    {
        using var dir = new TempDir();
        var path = dir.Sub("iron-settings.json");
        File.WriteAllText(path, """
            {
              "SchemaVersion": 1,
              "Window": { "StartLayout": "HalfWidth", "Monitor": { "Id": "\\\\.\\DISPLAY2" } },
              "Devices": { "WireView": { "Port": "COM7" }, "FluxPro": { "Enabled": false } }
            }
            """);
        var store = new JsonSettingsStore<IronSettings>(path);

        var loaded = store.Load();
        loaded.Settings.Hardware.IntervalMs = 2000;
        store.Save(loaded.Settings);

        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("HalfWidth", saved["Window"]!["StartLayout"]!.GetValue<string>());
        Assert.Equal(@"\\.\DISPLAY2", saved["Window"]!["Monitor"]!["Id"]!.GetValue<string>());
        Assert.Equal("COM7", saved["Devices"]!["WireView"]!["Port"]!.GetValue<string>());
        Assert.False(saved["Devices"]!["FluxPro"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal(2000, saved["Hardware"]!["IntervalMs"]!.GetValue<int>());
    }

    [Fact]
    public void Out_of_range_values_are_fixed_with_notes()
    {
        var s = new IronSettings
        {
            Hardware = { IntervalMs = 50 },
            Recording = { IntervalSeconds = 0, RetentionDays = 9999 },
            Window = { StartLayout = "Fullscreen" },
            Electricity = { RateKrwPerKwh = -3m },
            Alerts = { GpuTemperatureC = 200, VramPercent = 10 },
        };

        var notes = s.Normalize();

        Assert.Equal(7, notes.Count);
        Assert.Equal(1000, s.Hardware.IntervalMs);
        Assert.Equal(5, s.Recording.IntervalSeconds);
        Assert.Equal(30, s.Recording.RetentionDays);
        Assert.Equal(IronWindowSettings.Maximized, s.Window.StartLayout);
        Assert.Null(s.Electricity.RateKrwPerKwh);
        Assert.Equal(85, s.Alerts.GpuTemperatureC);
        Assert.Equal(95, s.Alerts.VramPercent);
    }

    [Fact]
    public void Missing_sections_are_filled_in()
    {
        using var dir = new TempDir();
        var path = dir.Sub("iron-settings.json");
        File.WriteAllText(path, """{ "Hardware": null, "Alerts": null }""");

        var loaded = new JsonSettingsStore<IronSettings>(path).Load();

        Assert.NotNull(loaded.Settings.Hardware);
        Assert.NotNull(loaded.Settings.Alerts);
        Assert.False(loaded.ReadFailed);
    }
}

public class IronTelemetryStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static GpuSample Gpu(int index, string name, double? power, string uuid) => new()
    {
        Index = index, Name = name, PowerW = power, TemperatureC = 60 + index, UtilizationPercent = 90,
        MemoryUsedBytes = 12L << 30, MemoryTotalBytes = 16L << 30, GraphicsClockMhz = 2800, DeviceUuid = uuid,
    };

    private static HardwareSnapshot Dual(DateTimeOffset at, double? p5080, double? p5060)
        => new()
        {
            Timestamp = at,
            Gpus = [Gpu(0, "NVIDIA GeForce RTX 5080", p5080, "GPU-A"), Gpu(1, "NVIDIA GeForce RTX 5060 Ti", p5060, "GPU-B")],
            System = new SystemSample { CpuUtilizationPercent = 23, MemoryUsedBytes = 40L << 30, MemoryTotalBytes = 64L << 30 },
        };

    [Fact]
    public void A_new_database_has_the_iron_mark_and_source_named_tables()
    {
        using var dir = new TempDir();
        using var store = IronTelemetryStore.Open(dir.Sub("iron-telemetry.db"));
        var c = store.ConnectionForTests;

        Assert.Equal((long)IronTelemetryStore.ApplicationId, SqliteDatabase.Scalar(c, "PRAGMA application_id;"));
        Assert.Equal((long)IronTelemetryStore.Migrations.Count, SqliteDatabase.SchemaVersion(c));
        var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                tables.Add(r.GetString(0));
            }
        }

        Assert.Equal(["gpu_power_totals", "nvml_gpu_samples", "system_samples"], tables);
    }

    [Fact]
    public void A_complete_dual_gpu_reading_stores_the_total()
    {
        using var dir = new TempDir();
        using var store = IronTelemetryStore.Open(dir.Sub("iron-telemetry.db"));
        var snapshot = Dual(T0, 312.4, 151.8);

        store.Append(snapshot, GpuPowerTotal.From(snapshot, 2));

        Assert.Equal(2, store.Count("nvml_gpu_samples"));
        Assert.Equal(1, store.Count("system_samples"));
        Assert.Equal("COMPLETE", SqliteDatabase.Scalar(store.ConnectionForTests, "SELECT state FROM gpu_power_totals;"));
        Assert.Equal(464.2, (double)SqliteDatabase.Scalar(store.ConnectionForTests, "SELECT total_w FROM gpu_power_totals;")!, 6);
    }

    [Fact]
    public void One_gpu_without_power_is_stored_as_partial_without_a_total_and_its_power_as_null()
    {
        using var dir = new TempDir();
        using var store = IronTelemetryStore.Open(dir.Sub("iron-telemetry.db"));
        var snapshot = Dual(T0, 312.4, null);

        store.Append(snapshot, GpuPowerTotal.From(snapshot, 2));

        var c = store.ConnectionForTests;
        Assert.Equal("PARTIAL", SqliteDatabase.Scalar(c, "SELECT state FROM gpu_power_totals;"));
        Assert.Null(SqliteDatabase.Scalar(c, "SELECT total_w FROM gpu_power_totals;"));
        Assert.Null(SqliteDatabase.Scalar(c, "SELECT power_w FROM nvml_gpu_samples WHERE device_key = 'uuid:GPU-B';"));
        Assert.Equal(312.4, (double)SqliteDatabase.Scalar(c, "SELECT power_w FROM nvml_gpu_samples WHERE device_key = 'uuid:GPU-A';")!, 6);
    }

    [Theory]
    [InlineData("INSERT INTO gpu_power_totals (ts_ms, state, total_w, reported, expected) VALUES (1, 'PARTIAL', 312.4, 1, 2);")]
    [InlineData("INSERT INTO gpu_power_totals (ts_ms, state, total_w, reported, expected) VALUES (1, 'COMPLETE', NULL, 2, 2);")]
    [InlineData("INSERT INTO gpu_power_totals (ts_ms, state, total_w, reported, expected) VALUES (1, 'COMPLETE', 312.4, 1, 2);")]
    [InlineData("INSERT INTO gpu_power_totals (ts_ms, state, total_w, reported, expected) VALUES (1, 'UNAVAILABLE', 0, 0, 2);")]
    public void The_database_itself_refuses_a_total_from_an_incomplete_set(string sql)
    {
        using var dir = new TempDir();
        using var store = IronTelemetryStore.Open(dir.Sub("iron-telemetry.db"));

        var ex = Assert.Throws<SqliteException>(() => SqliteDatabase.Exec(store.ConnectionForTests, sql));
        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Fact]
    public void Gaps_stay_null_and_the_same_reading_is_stored_once()
    {
        using var dir = new TempDir();
        using var store = IronTelemetryStore.Open(dir.Sub("iron-telemetry.db"));
        var stale = new HardwareSnapshot { Timestamp = T0 }; // the hub's "not measured" placeholder
        store.Append(stale, GpuPowerTotal.From(stale, 2));
        store.Append(stale, GpuPowerTotal.From(stale, 2));

        var c = store.ConnectionForTests;
        Assert.Equal(1, store.Count("system_samples"));
        Assert.Null(SqliteDatabase.Scalar(c, "SELECT cpu_pct FROM system_samples;"));
        Assert.Equal("UNAVAILABLE", SqliteDatabase.Scalar(c, "SELECT state FROM gpu_power_totals;"));
        Assert.Equal(0, store.Count("nvml_gpu_samples"));
    }

    [Fact]
    public void Old_readings_are_pruned()
    {
        using var dir = new TempDir();
        using var store = IronTelemetryStore.Open(dir.Sub("iron-telemetry.db"));
        for (var i = 0; i < 3; i++)
        {
            var s = Dual(T0.AddDays(i), 300, 150);
            store.Append(s, GpuPowerTotal.From(s, 2));
        }

        var removed = store.PruneOlderThan(T0.AddDays(1));

        Assert.Equal(4, removed); // 2 GPU rows + 1 system row + 1 total of day 0
        Assert.Equal(4, store.Count("nvml_gpu_samples"));
    }

    [Fact]
    public void A_module_database_is_never_opened_as_irons()
    {
        using var dir = new TempDir();
        var lapc = dir.Sub("lapc.db");
        using (var c = new SqliteConnection(SqliteDatabase.ConnectionString(lapc, SqliteOpenMode.ReadWriteCreate)))
        {
            c.Open();
            SqliteDatabase.Exec(c, "CREATE TABLE sessions (id INTEGER PRIMARY KEY);");
        }

        Assert.Throws<StorageCorruptException>(() => IronTelemetryStore.Open(lapc));
    }
}

public class IronIndependenceTests
{
    /// <summary>
    /// IRON's code must not reach PC Control Center's assemblies: in particular not PcControlCenter.GpuMemory, the WDDM
    /// (PDH) memory reader, so IRON's VRAM can only be the hub's NVML value.
    /// </summary>
    [Fact]
    public void Iron_core_does_not_depend_on_pc_control_center_or_the_wddm_reader()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<AssemblyName>([typeof(IronSettings).Assembly.GetName()]);
        while (queue.TryDequeue(out var name))
        {
            if (!seen.Add(name.Name!) || IsFramework(name.Name!))
            {
                continue;
            }

            foreach (var reference in Assembly.Load(name).GetReferencedAssemblies())
            {
                queue.Enqueue(reference);
            }
        }

        Assert.Contains("Suite.Core.Hardware", seen);
        Assert.Contains("Suite.Core.Storage", seen);
        Assert.DoesNotContain(seen, n => n.StartsWith("PcControlCenter", StringComparison.Ordinal));
        Assert.DoesNotContain(seen, n => n.StartsWith("GpuProfit", StringComparison.Ordinal) || n.StartsWith("WindowsAiOperator", StringComparison.Ordinal)
                                         || n.StartsWith("WinDiag", StringComparison.Ordinal));
    }

    private static bool IsFramework(string name)
        => name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
           || name is "netstandard" or "mscorlib" || name.StartsWith("SQLitePCLRaw", StringComparison.Ordinal);
}
