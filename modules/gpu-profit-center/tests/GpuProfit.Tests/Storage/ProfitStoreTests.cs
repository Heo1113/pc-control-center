using GpuProfit.Core.Domain;
using GpuProfit.Storage;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Storage;
using Microsoft.Data.Sqlite;

namespace GpuProfit.Tests.Storage;

public class ProfitStoreTests
{
    private static HardwareSnapshot Snap(DateTimeOffset t, params double?[] watts) => new()
    {
        Timestamp = t,
        System = new SystemSample { CpuUtilizationPercent = 12, MemoryUsedBytes = 1L << 30, MemoryTotalBytes = 2L << 30 },
        Gpus = watts.Select((w, i) => new GpuSample { Index = i, Name = $"GPU {i}", PowerW = w, UtilizationPercent = 90 }).ToList(),
    };

    [Fact]
    public void New_database_has_the_schema_and_can_be_reopened()
    {
        using var dir = new TempDir();
        var path = dir.File("gpc.db");
        using (var store = ProfitStore.Open(path))
        {
            Assert.Null(store.OpenNotice);
            Assert.Equal("ok", store.IntegrityCheck());
            store.AddWorkload(new Workload { Name = "채굴 A", Type = WorkloadType.Mining });
        }

        using var again = ProfitStore.Open(path);
        Assert.Single(again.ListWorkloads());
    }

    [Fact]
    public void Newer_schema_is_not_opened_and_file_is_untouched()
    {
        using var dir = new TempDir();
        var path = dir.File("gpc.db");
        using (var store = ProfitStore.Open(path))
        {
        }

        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=99;";
            cmd.ExecuteNonQuery();
        }

        var before = File.ReadAllBytes(path);
        Assert.Throws<StorageVersionException>(() => ProfitStore.Open(path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Foreign_sqlite_database_is_refused()
    {
        using var dir = new TempDir();
        var path = dir.File("other.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE stuff (x INTEGER); INSERT INTO stuff VALUES (1);";
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<StorageVersionException>(() => ProfitStore.Open(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_never_deleted()
    {
        using var dir = new TempDir();
        var path = dir.File("gpc.db");
        File.WriteAllText(path, "this is not a database at all, but it is the user's file");
        using var store = ProfitStore.Open(path);
        Assert.NotNull(store.OpenNotice);
        Assert.Contains("손상", store.OpenNotice);
        var aside = Assert.Single(Directory.GetFiles(dir.Path, "gpc.corrupt-*.db"));
        Assert.Equal("this is not a database at all, but it is the user's file", File.ReadAllText(aside));
        Assert.Equal("ok", store.IntegrityCheck());
    }

    [Fact]
    public void Damaged_pages_are_detected_and_quarantined()
    {
        using var dir = new TempDir();
        var path = dir.File("gpc.db");
        using (var store = ProfitStore.Open(path))
        {
            var w = store.AddWorkload(new Workload { Name = "w" });
            for (var i = 0; i < 300; i++)
            {
                store.AddRevenue(new RevenueEntry { WorkloadId = w, Amount = i, Occurred = T.At(i), Note = new string('x', 200) });
            }

            store.StartRun(T.T0, "test", false, 1000); // left open: the next open runs the integrity check
        }

        // Overwrite a few pages in the middle of the file (header intact).
        using (var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            f.Seek(4096 * 3, SeekOrigin.Begin);
            f.Write(Enumerable.Repeat((byte)0x5A, 4096 * 4).ToArray());
        }

        using var reopened = ProfitStore.Open(path);
        Assert.NotNull(reopened.OpenNotice);
        Assert.Single(Directory.GetFiles(dir.Path, "gpc.corrupt-*.db"));
    }

    [Fact]
    public void Duplicate_snapshots_are_ignored()
    {
        using var dir = new TempDir();
        using var store = ProfitStore.Open(dir.File("gpc.db"));
        var run = store.StartRun(T.T0, "test", false, 1000);
        Assert.True(store.WriteSnapshot(run, Snap(T.At(1), 100, 200)));
        Assert.False(store.WriteSnapshot(run, Snap(T.At(1), 100, 200)));
        Assert.True(store.WriteSnapshot(run, Snap(T.At(2), 100, 200)));
        var stats = store.GetStats();
        Assert.Equal(4, stats.GpuSamples);
        Assert.Equal(2, stats.SystemSamples);
        Assert.Equal(2, stats.Devices);
        var samples = store.LoadPowerSamples(T.At(0), T.At(10), isMock: false);
        Assert.Equal([0, 1, 0, 1], samples.Select(s => s.DeviceIndex));
        Assert.Empty(store.LoadPowerSamples(T.At(0), T.At(10), isMock: true));
    }

    [Fact]
    public void Missing_and_non_finite_values_are_stored_as_null()
    {
        using var dir = new TempDir();
        using var store = ProfitStore.Open(dir.File("gpc.db"));
        var run = store.StartRun(T.T0, "test", false, 1000);
        store.WriteSnapshot(run, Snap(T.At(1), null, double.NaN));
        var samples = store.LoadPowerSamples(T.At(0), T.At(10), false);
        Assert.All(samples, s => Assert.Null(s.PowerW));
    }

    [Fact]
    public void Money_round_trips_exactly_as_decimal_text()
    {
        using var dir = new TempDir();
        using var store = ProfitStore.Open(dir.File("gpc.db"));
        var w = store.AddWorkload(new Workload { Name = "w" });
        store.AddRevenue(new RevenueEntry { WorkloadId = w, Amount = 0.000000012345678901m, Currency = "btc", KrwPerUnit = 123456789.123456789m, Occurred = T.T0 });
        var e = Assert.Single(store.ListRevenue());
        Assert.Equal(0.000000012345678901m, e.Amount);
        Assert.Equal(123456789.123456789m, e.KrwPerUnit);
        Assert.Equal("BTC", e.Currency);
    }

    [Theory]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("password=hunter2hunter2")]
    public void Secrets_are_not_stored(string note)
    {
        using var dir = new TempDir();
        using var store = ProfitStore.Open(dir.File("gpc.db"));
        Assert.Throws<ArgumentException>(() => store.AddWorkload(new Workload { Name = "w", Notes = note }));
        Assert.Empty(store.ListWorkloads());
    }

    [Fact]
    public void Imported_payouts_are_stored_once()
    {
        using var dir = new TempDir();
        using var store = ProfitStore.Open(dir.File("gpc.db"));
        var w = store.AddWorkload(new Workload { Name = "w" });
        var e = new RevenueEntry { WorkloadId = w, Amount = 1, Provider = "pool", ExternalId = "tx1", Occurred = T.T0, Kind = RevenueKind.Received };
        Assert.Equal((1, 0), store.ImportRevenue([e]));
        Assert.Equal((0, 1), store.ImportRevenue([e]));
        Assert.Single(store.ListRevenue());
    }

    [Fact]
    public void Backup_validate_and_restore_round_trip_keeps_the_previous_database()
    {
        using var dir = new TempDir();
        var path = dir.File("gpc.db");
        var backup = dir.File("backup.db");
        using (var store = ProfitStore.Open(path))
        {
            store.AddWorkload(new Workload { Name = "before" });
            store.BackupTo(backup);
            Assert.Throws<IOException>(() => store.BackupTo(backup)); // never overwritten silently
            Assert.Throws<IOException>(() => store.BackupTo(path)); // never onto the live file
            store.AddWorkload(new Workload { Name = "after" });
        }

        var validation = ProfitStore.ValidateBackup(backup);
        Assert.True(validation.IsValid, validation.Message);
        var aside = ProfitStore.RestoreFrom(backup, path);
        Assert.NotNull(aside);
        Assert.True(File.Exists(aside));
        using (var restored = ProfitStore.Open(path))
        {
            Assert.Equal(["before"], restored.ListWorkloads().Select(w => w.Name));
        }

        using var previous = ProfitStore.Open(aside!);
        Assert.Equal(2, previous.ListWorkloads().Count);
    }

    [Fact]
    public void Invalid_backup_is_rejected_and_the_database_is_untouched()
    {
        using var dir = new TempDir();
        var path = dir.File("gpc.db");
        using (var store = ProfitStore.Open(path))
        {
            store.AddWorkload(new Workload { Name = "keep" });
        }

        var bogus = dir.File("bogus.db");
        File.WriteAllText(bogus, "nope");
        Assert.False(ProfitStore.ValidateBackup(bogus).IsValid);
        Assert.Throws<InvalidDataException>(() => ProfitStore.RestoreFrom(bogus, path));
        Assert.False(ProfitStore.ValidateBackup(dir.File("missing.db")).IsValid);
        using var store2 = ProfitStore.Open(path);
        Assert.Equal("keep", Assert.Single(store2.ListWorkloads()).Name);
    }

    [Fact]
    public void Retention_keeps_samples_inside_sessions()
    {
        using var dir = new TempDir();
        var time = new ManualTime(T.T0.AddDays(100));
        using var store = ProfitStore.Open(dir.File("gpc.db"), time);
        var run = store.StartRun(T.T0, "test", false, 1000);
        for (var i = 0; i < 100; i++)
        {
            store.WriteSnapshot(run, Snap(T.At(i * 60), 100));
        }

        var w = store.AddWorkload(new Workload { Name = "w" });
        store.CreateSession(new WorkSession { WorkloadId = w, Started = T.At(1200), State = SessionState.Ended, Source = SessionSource.Live }, T.At(1800), "stopped");
        var result = store.ApplyRetention(30, marginSeconds: 0);
        Assert.Equal(89, result.GpuSamplesRemoved); // samples at 1200..1800 s (11) are kept
        Assert.Equal(11, store.GetStats().GpuSamples);
        Assert.Equal(new RetentionResult(0, 0), store.ApplyRetention(0));
    }

    [Fact]
    public void Profile_is_stored_in_the_database_and_validated()
    {
        using var dir = new TempDir();
        using var store = ProfitStore.Open(dir.File("gpc.db"));
        Assert.Equal(new AccountingProfile().MaxSampleGapSeconds, store.GetProfile().MaxSampleGapSeconds);
        var profile = new AccountingProfile
        {
            Tariff = new ElectricityTariff { FlatRateKrwPerKwh = 187.5m, Tiers = [new TariffTier { UpToKwh = 200, EnergyRateKrwPerKwh = 120m }] },
            ExtraSystemPowerW = 85,
        };
        store.SaveProfile(profile);
        var loaded = store.GetProfile();
        Assert.Equal(187.5m, loaded.Tariff.FlatRateKrwPerKwh);
        Assert.Equal(85, loaded.ExtraSystemPowerW);
        Assert.Single(loaded.Tariff.Tiers);
        Assert.Throws<ArgumentException>(() => store.SaveProfile(profile with { MaxSampleGapSeconds = -1 }));
    }

    [Fact]
    public void Session_search_and_filters()
    {
        using var dir = new TempDir();
        using var store = ProfitStore.Open(dir.File("gpc.db"));
        var mining = store.AddWorkload(new Workload { Name = "이더 채굴", Type = WorkloadType.Mining });
        var ai = store.AddWorkload(new Workload { Name = "LLM 추론", Type = WorkloadType.AiInference });
        store.CreateSession(new WorkSession { WorkloadId = mining, Title = "야간 50%_test", Started = T.At(0), Ended = T.At(100), State = SessionState.Ended }, T.At(100), "stopped");
        store.CreateSession(new WorkSession { WorkloadId = ai, Title = "주간", Notes = "vLLM", Started = T.At(1000), Ended = T.At(1100), State = SessionState.Ended }, T.At(1100), "stopped");
        Assert.Single(store.ListSessions(new SessionQuery { Text = "vllm" }));
        Assert.Single(store.ListSessions(new SessionQuery { Text = "50%_" })); // LIKE wildcards are escaped
        Assert.Empty(store.ListSessions(new SessionQuery { Text = "5_%" }));
        Assert.Single(store.ListSessions(new SessionQuery { Type = WorkloadType.AiInference }));
        Assert.Single(store.ListSessions(new SessionQuery { From = T.At(500) }));
        Assert.Single(store.ListSessions(new SessionQuery { Text = "채굴" }));
    }
}
