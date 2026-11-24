using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Telemetry;
using Microsoft.Data.Sqlite;
using PcControlCenter.Core;
using PcControlCenter.Hardware;
using PcControlCenter.Modules;

namespace PcControlCenter.Tests;

/// <summary>A, B and C run together in one process on one sensor reader, each with its own database.</summary>
public class ModuleIntegrationTests
{
    private static List<long> ReadTimes(string database, string sql)
    {
        // Read-only connection, the way the integrated history reads module databases.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var list = new List<long>();
        while (reader.Read())
        {
            list.Add(reader.GetInt64(0));
        }

        return list;
    }

    [Fact(Timeout = 120_000)]
    public async Task A_B_and_C_record_from_one_sensor_reader_with_the_readings_timestamps()
    {
        using var temp = new TempDir();
        var paths = ShellPaths.Resolve(temp.Sub("data"), temp.Path, legacy: []);
        paths.EnsureDirectories();
        var gpu = new CountingGpuProvider(TimeProvider.System);
        var system = new CountingSystemProvider();
        var hub = new SharedHardwareHub(new HardwareMonitor(gpu, system), TimeSpan.FromSeconds(1));
        var context = new ModuleContext
        {
            Paths = paths,
            Settings = new ShellSettings(),
            Hub = hub,
            Intervals = new SensorIntervalCoordinator(hub, TimeSpan.FromSeconds(1)),
            ExecutableDirectory = temp.Path,
            Unattended = true,
        };
        var supervisor = new ModuleSupervisor(NullLogger.Instance);
        var a = supervisor.Add(new LocalAiModule(context));
        var b = supervisor.Add(new GpuEconomicsModule(context));
        var c = supervisor.Add(new DiagnosticsModule(context));

        hub.Start();
        await supervisor.StartAllAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3.5), TestContext.Current.CancellationToken);
        var states = supervisor.Modules.ToDictionary(m => m.Id, m => m.Status);
        var aLive = a.Service!.Live.Snapshot();
        var bStored = b.Engine!.Recorder!.SamplesStored;
        var cStored = c.Engine!.Recorder!.SamplesStored;
        await supervisor.StopAllAsync();
        await hub.StopAsync();
        var hubTimes = hub.Live(TimeSpan.FromHours(1)).Select(s => s.Timestamp.ToUnixTimeMilliseconds()).ToHashSet();

        Assert.All(states.Values, s => Assert.True(s.IsUsable, $"{s.ModuleId}: {s.State} {s.Summary} {s.Detail}"));

        // One real device read per hub reading, however many modules consumed it.
        Assert.Equal(hub.Health.Readings, gpu.Reads);
        Assert.True(gpu.Reads >= 3, $"hub readings: {gpu.Reads}");

        // Every value a module kept is a hub reading with that reading's own timestamp.
        Assert.NotEmpty(aLive);
        Assert.All(aLive, f => Assert.Contains(f.Timestamp.ToUnixTimeMilliseconds(), hubTimes));
        Assert.True(bStored > 0 && cStored > 0, $"B stored {bStored}, C stored {cStored}");
        var bTimes = ReadTimes(paths.ModuleDatabasePath(ModuleIds.GpuEconomics), "SELECT t_ms FROM gpu_samples");
        var cTimes = ReadTimes(paths.ModuleDatabasePath(ModuleIds.Diagnostics), "SELECT t_ms FROM sensor_samples");
        Assert.NotEmpty(bTimes);
        Assert.NotEmpty(cTimes);
        Assert.All(bTimes, t => Assert.Contains(t, hubTimes));
        Assert.All(cTimes, t => Assert.Contains(t, hubTimes));

        // Separate databases in separate module folders; nothing written next to them by another module.
        Assert.True(File.Exists(paths.ModuleDatabasePath(ModuleIds.LocalAi)));
        Assert.False(File.Exists(Path.Combine(paths.ModuleDirectory(ModuleIds.GpuEconomics), "wdl.db")));
        Assert.False(File.Exists(Path.Combine(paths.ModuleDirectory(ModuleIds.Diagnostics), "gpc.db")));
    }

    [Fact(Timeout = 120_000)]
    public async Task A_module_that_cannot_open_its_folder_fails_alone()
    {
        using var temp = new TempDir();
        var paths = ShellPaths.Resolve(temp.Sub("data"), temp.Path, legacy: []);
        paths.EnsureDirectories();
        var hub = SharedHardwareHub.Create(true, true, useMock: true, TimeSpan.FromSeconds(1));
        var context = new ModuleContext
        {
            Paths = paths,
            Settings = new ShellSettings(),
            Hub = hub,
            Intervals = new SensorIntervalCoordinator(hub, TimeSpan.FromSeconds(1)),
            ExecutableDirectory = temp.Path,
            Unattended = true,
        };

        // Another process holds B's data folder (for example B's gpc command line pointed at it).
        using var foreignLock = GpuProfit.Core.DataFolderLock.TryAcquire(paths.ModuleDirectory(ModuleIds.GpuEconomics));
        var supervisor = new ModuleSupervisor(NullLogger.Instance);
        var b = supervisor.Add(new GpuEconomicsModule(context));
        var c = supervisor.Add(new DiagnosticsModule(context));
        hub.Start();
        await supervisor.StartAllAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);

        Assert.Equal(ModuleState.Failed, b.Status.State);
        Assert.True(c.Status.IsUsable, c.Status.Summary);
        Assert.True(c.Engine!.Recorder!.SamplesStored > 0);
        await supervisor.StopAllAsync();
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task The_reader_runs_at_the_fastest_interval_a_module_asks_for()
    {
        var hub = SharedHardwareHub.Create(true, true, useMock: true, TimeSpan.FromSeconds(1));
        var intervals = new SensorIntervalCoordinator(hub, TimeSpan.FromSeconds(2));

        await intervals.RequestAsync("c", TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(2), hub.Interval);
        await intervals.RequestAsync("a", TimeSpan.FromMilliseconds(750));
        Assert.Equal(TimeSpan.FromMilliseconds(750), hub.Interval);
        await intervals.ReleaseAsync("a");
        Assert.Equal(TimeSpan.FromSeconds(2), hub.Interval);
        await hub.DisposeAsync();
    }
}
