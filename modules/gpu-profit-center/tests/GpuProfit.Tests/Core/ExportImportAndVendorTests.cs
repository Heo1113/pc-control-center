using System.Security.Cryptography;
using System.Text.Json;
using GpuProfit.Accounting.Analysis;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Core.Domain;
using GpuProfit.Core.Settings;
using GpuProfit.Export;
using GpuProfit.Storage;

namespace GpuProfit.Tests.Core;

public class ExportTests
{
    private static AccountedSession Item(string title, decimal? revenue)
    {
        var session = T.Session() with { Title = title };
        var r = ProfitabilityEngine.Compute(new SessionAccountingInput
        {
            Session = session,
            Segments = [T.Segment(0, 3600)],
            Samples = T.Constant(250, -1, 3601),
            Revenue = revenue is decimal a ? [new RevenueEntry { Id = 1, Amount = a, Occurred = T.At(10) }] : [],
            Now = T.At(4000),
        });
        return new AccountedSession(session, new Workload { Id = 1, Name = "작업", Type = WorkloadType.GpuCompute }, r);
    }

    [Fact]
    public void Sessions_csv_has_bom_header_values_and_reasons_for_undefined_values()
    {
        using var dir = new TempDir();
        var path = dir.File("s.csv");
        Exporters.SessionsCsv(path, [Item("정상", 1000), Item("=HYPERLINK(\"x\")", null)]);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("session_id,title,", lines[0].TrimStart('﻿'));
        Assert.Contains("'=HYPERLINK", lines[2]); // spreadsheet formula injection neutralised
        Assert.Contains("0원으로 간주하지 않음", lines[2]);
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public void Ledger_json_is_valid_and_carries_origins_and_undefined_reasons()
    {
        using var dir = new TempDir();
        using var store = ProfitStore.Open(dir.File("gpc.db"));
        store.AddWorkload(new Workload { Name = "w" });
        var path = dir.File("ledger.json");
        Exporters.LedgerJson(path, store, [Item("a", null)], T.At(5000));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("gpu-profit-center/ledger", doc.RootElement.GetProperty("format").GetString());
        var view = doc.RootElement.GetProperty("sessions")[0].GetProperty("result").GetProperty("views")[0];
        Assert.Equal(JsonValueKind.Null, view.GetProperty("net").GetProperty("value").ValueKind);
        Assert.False(string.IsNullOrEmpty(view.GetProperty("net").GetProperty("reason").GetString()));
        var text = File.ReadAllText(path);
        Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(dir.Path, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Period_and_cost_csv_exports_write_files()
    {
        using var dir = new TempDir();
        var item = Item("a", 1000);
        var buckets = PeriodAggregator.Aggregate([new PeriodItem(item.Session, [T.Segment(0, 3600)], item.Result)], PeriodGranularity.Day, TimeZoneInfo.Utc, T.At(4000));
        Exporters.PeriodsCsv(dir.File("p.csv"), buckets);
        Exporters.CostsCsv(dir.File("c.csv"), [new CostEntry { Id = 1, Amount = 5 }], [new EquipmentAsset { Id = 1, Name = "GPU", PriceKrw = 100 }]);
        Exporters.RevenueCsv(dir.File("r.csv"), [new RevenueEntry { Id = 1, Amount = 1, Note = "+1 bonus" }]);
        Exporters.SessionEnergyCsv(dir.File("e.csv"), item);
        Assert.Equal(2, File.ReadAllLines(dir.File("p.csv")).Length);
        Assert.Contains("'+1 bonus", File.ReadAllText(dir.File("r.csv")));
        Assert.Contains("condition", File.ReadAllText(dir.File("e.csv")));
    }
}

public class PayoutImportTests
{
    private static PayoutImportResult Parse(string csv, RevenueKind kind = RevenueKind.Received, string? currency = null)
        => PayoutCsvImporter.Parse(new StringReader(csv), "Example Pool", kind, currency, TimeZoneInfo.Utc, T.T0, "payouts.csv");

    [Fact]
    public void Common_payout_csv_is_imported_with_provider_and_ids()
    {
        var r = Parse("""
            Date,Amount,Coin,TxID,Note
            2026-10-30 12:00:00,0.0012,btc,abc123,"daily, payout"
            2026-10-31T12:00:00+09:00,0.0011,BTC,abc124,
            1761912000,0.0010,BTC,,unix seconds
            """);
        Assert.Empty(r.Problems);
        Assert.Equal(3, r.Entries.Count);
        Assert.All(r.Entries, e => Assert.Equal("Example Pool", e.Provider));
        Assert.All(r.Entries, e => Assert.Equal(ValueOrigin.ExternalReported, e.Origin));
        Assert.Equal("daily, payout", r.Entries[0].Note);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), r.Entries[0].Occurred);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 3, 0, 0, TimeSpan.Zero), r.Entries[1].Occurred.ToUniversalTime());
        Assert.StartsWith("row:", r.Entries[2].ExternalId); // no id: content-based id prevents a second import
        Assert.Equal(0.0012m, r.Entries[0].Amount);
    }

    [Fact]
    public void Bad_rows_are_reported_and_skipped()
    {
        var r = Parse("""
            time,amount,currency,id
            not-a-date,1,KRW,a
            2026-10-30,abc,KRW,b
            2026-10-30,-5,KRW,c
            2026-10-30,5,??,d
            2026-10-30,5,KRW,e
            2026-10-31,6,KRW,e
            """);
        Assert.Single(r.Entries);
        Assert.Equal(5, r.Problems.Count);
        Assert.Equal([2, 3, 4, 5, 7], r.Problems.Select(p => p.Line));
    }

    [Fact]
    public void Missing_columns_or_provider_stop_the_import()
    {
        Assert.NotEmpty(Parse("foo,bar\n1,2").Problems);
        Assert.NotEmpty(Parse("time,amount\n2026-10-30,1").Problems); // no currency column and no default
        Assert.Single(Parse("time,amount\n2026-10-30,1", currency: "KRW").Entries);
        var noProvider = PayoutCsvImporter.Parse(new StringReader("time,amount\n2026-10-30,1"), " ", RevenueKind.Received, "KRW", TimeZoneInfo.Utc, T.T0);
        Assert.Empty(noProvider.Entries);
    }

    [Fact]
    public void Secrets_in_notes_are_dropped()
    {
        var r = Parse("time,amount,currency,note\n2026-10-30,1,KRW,-----BEGIN PRIVATE KEY-----");
        Assert.Null(Assert.Single(r.Entries).Note);
        Assert.Single(r.Problems);
    }

    [Fact]
    public void Quoted_fields_with_line_breaks_and_quotes()
    {
        var rows = PayoutCsvImporter.ReadCsv(new StringReader("a,b\r\n\"x\"\"y\",\"line1\nline2\"\r\nlast,row")).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("x\"y", rows[1].Fields[0]);
        Assert.Equal("line1\nline2", rows[1].Fields[1]);
        Assert.Equal(4, rows[2].Line);
    }

    [Fact]
    public void Entries_are_assigned_to_the_session_containing_their_time()
    {
        var entries = Parse("time,amount,currency\n2026-11-01 00:30:00,1,KRW\n2026-11-02 00:00:00,1,KRW").Entries;
        var segments = new Dictionary<long, IReadOnlyList<SessionSegment>> { [7] = [T.Segment(0, 3600, 7)] };
        var assigned = PayoutCsvImporter.AssignToSessions(entries, segments, T.At(100000));
        Assert.Equal(7, assigned[0].SessionId);
        Assert.Null(assigned[1].SessionId);
    }

    [Fact]
    public void Importing_the_same_file_twice_adds_nothing()
    {
        using var dir = new TempDir();
        using var store = ProfitStore.Open(dir.File("gpc.db"));
        var w = store.AddWorkload(new Workload { Name = "w" });
        var entries = Parse("time,amount,currency\n2026-10-30,1,KRW\n2026-10-31,2,KRW").Entries.Select(e => e with { WorkloadId = w }).ToList();
        Assert.Equal((2, 0), store.ImportRevenue(entries));
        Assert.Equal((0, 2), store.ImportRevenue(entries));
    }
}

public class SettingsTests
{
    [Fact]
    public void Unreadable_settings_file_is_kept_aside_and_defaults_are_used()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ not json");
        var result = new SettingsStore(path).Load();
        Assert.True(result.WasCreated);
        Assert.Single(result.Notes);
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.invalid-*"));
    }

    [Fact]
    public void Out_of_range_values_are_normalized()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var s = new AppSettings();
        s.Sampling.IntervalSeconds = 0;
        s.Ui.FontScale = 5;
        store.Save(s);
        var loaded = store.Load();
        Assert.Equal(1, loaded.Settings.Sampling.IntervalSeconds);
        Assert.Equal(1.0, loaded.Settings.Ui.FontScale);
    }
}

public class VendorIntegrityTests
{
    [Fact]
    public void Vendored_files_match_the_manifest()
    {
        var root = FindRepoRoot();
        var vendor = Path.Combine(root, "vendor", "local-ai-performance-center");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(vendor, "manifest.json")));
        Assert.Equal("5b6aae12c28efbdbebe9a7df280c02f5494b037e", manifest.RootElement.GetProperty("source").GetProperty("commit").GetString());
        var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(18, files.Count);
        foreach (var f in files)
        {
            var path = Path.Combine(vendor, f.GetProperty("path").GetString()!);
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(f.GetProperty("currentSha256").GetString(), hash);
            var unchanged = f.GetProperty("status").GetString() == "unchanged";
            Assert.Equal(unchanged, f.GetProperty("sourceSha256").GetString() == hash);
            if (!unchanged)
            {
                Assert.True(File.Exists(Path.Combine(vendor, f.GetProperty("patch").GetString()!)));
            }
        }
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GpuProfitCenter.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("저장소 루트를 찾을 수 없습니다.");
    }
}
