using System.Text.Json;
using WinDiag.Analysis;
using WinDiag.Core.Events;
using WinDiag.Core.Privacy;
using WinDiag.Export;
using WinDiag.Storage;
using WinDiag.Tests.Analysis;

namespace WinDiag.Tests.Export;

public class ExportTests
{
    private static StoredEvent Stored(string title, string? message, params (string? Name, string Value)[] data) => new()
    {
        Id = 1, Channel = "Application", Provider = "Application Error", EventId = 1000, RecordId = 5, TimeCreated = Scenario.Now,
        Category = EventCategories.AppCrash, Severity = DiagSeverity.Error, GroupKey = "g", Signature = "app=x", Title = title, Message = message,
        Data = data.Select(d => new EventDataItem(d.Name, d.Value)).ToList(), IsMasked = false,
    };

    [Fact]
    public void Minimal_export_leaves_out_messages_and_data_and_masks_device_instances()
    {
        using var dir = new TempDir();
        var path = dir.File("events.csv");
        var e = Stored(@"장치 USB\VID_046D&PID_C52B\5&2A3B&0&1 문제", "kimtester의 경로 C:\\Users\\kimtester\\a", ("AppPath", @"C:\Users\kimtester\a.exe"));
        EventExporter.WriteCsv([e], path, new ExportOptions { Masker = new PrivacyMasker("kimtester", "PC-1", null) });
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("message", text.Split('\n')[0]);
        Assert.DoesNotContain("kimtester", text);
        Assert.Contains(PrivacyMasker.InstanceToken, text);
        Assert.Contains("time_utc", text);
    }

    [Fact]
    public void Detailed_export_includes_messages_but_still_masks_names()
    {
        using var dir = new TempDir();
        var path = dir.File("events.json");
        var e = Stored("앱 충돌", @"crash at C:\Users\kimtester\a.exe on PC-1", ("AppPath", @"C:\Users\kimtester\a.exe"));
        EventExporter.WriteJson([e], path, new ExportOptions { Detail = ExportDetail.Detailed, Masker = new PrivacyMasker("kimtester", "PC-1", null) });
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var item = json.RootElement.GetProperty("events")[0];
        Assert.Contains("<사용자>", item.GetProperty("message").GetString());
        Assert.Contains("<컴퓨터>", item.GetProperty("message").GetString());
        Assert.DoesNotContain("kimtester", File.ReadAllText(path));
        Assert.Contains("상세", json.RootElement.GetProperty("privacy").GetString());
    }

    [Fact]
    public void Csv_cells_cannot_become_spreadsheet_formulas()
    {
        using var dir = new TempDir();
        var path = dir.File("events.csv");
        EventExporter.WriteCsv([Stored("=HYPERLINK(\"http://x\")", null)], path, new ExportOptions());
        Assert.Contains("'=HYPERLINK", File.ReadAllText(path));
    }

    [Fact]
    public void Report_document_escapes_markdown_and_html()
    {
        var doc = new ReportDocument("제목 <b>")
            .H(2, "섹션 | 1").P("<script>alert(1)</script> *굵게*").List(["a_b", "c#d"]).Grid(["열|1"], [["x|y"]]);
        var html = doc.ToHtml();
        var md = doc.ToMarkdown();
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("\\*굵게\\*", md);
        Assert.Contains("x\\|y", md);
        Assert.Contains("\\<script\\>", md);
    }

    [Fact]
    public async Task Diagnostic_report_and_bundle_contain_all_parts()
    {
        using var s = await Scenario.CreateAsync();
        using var dir = new TempDir();
        var report = DiagnosticAnalyzer.Analyze(s.Store, s.Range, new DiagnosticOptions(), Scenario.Now);
        var files = ExportBundle.Write(s.Store, report, dir.File("bundle"), new ExportOptions());
        Assert.Contains(files, f => f.EndsWith("report.html", StringComparison.Ordinal));
        Assert.All(files, f => Assert.True(File.Exists(f), f));
        var md = File.ReadAllText(Path.Combine(dir.Path, "bundle", "report.md"));
        Assert.Contains("확인된 사실", md);
        Assert.Contains("관측된 연관성", md);
        Assert.Contains("원인 후보 (추가 검증 필요)", md);
        Assert.Contains("가상(테스트) 이벤트가 포함", md);
        Assert.Contains("추가 검증 방법", md);
        Assert.Contains("가상", File.ReadAllText(Path.Combine(dir.Path, "bundle", "README.txt")));
        Assert.True(File.ReadAllLines(Path.Combine(dir.Path, "bundle", "events.csv")).Length > 100);
    }

    [Fact]
    public async Task Comparison_and_configuration_exports_are_written()
    {
        using var s = await Scenario.CreateAsync();
        using var dir = new TempDir();
        var comparison = BaselineComparer.Compare(s.Store, "드라이버", WinDiag.Collection.Sources.VirtualScenario.DriverUpdate(Scenario.Now), 14, 14, Scenario.Now);
        var options = new ExportOptions();
        ComparisonExporter.WriteJson(comparison, dir.File("c.json"), options);
        ComparisonExporter.WriteCsv(comparison, dir.File("c.csv"));
        ComparisonExporter.WriteHtml(comparison, dir.File("c.html"), options);
        Assert.Contains("GPU 드라이버", File.ReadAllText(dir.File("c.html")));
        Assert.Contains("gpu.driver", File.ReadAllText(dir.File("c.csv")));
        using var json = JsonDocument.Parse(File.ReadAllText(dir.File("c.json")));
        Assert.True(json.RootElement.GetProperty("comparison").GetProperty("categories").GetArrayLength() > 0);

        var snapshots = s.Store.ListSnapshots();
        var before = WinDiag.Core.Snapshots.SystemSnapshot.FromJson(snapshots[0].Json);
        var after = WinDiag.Core.Snapshots.SystemSnapshot.FromJson(snapshots[1].Json);
        ConfigExporter.WriteComparison(before, after, dir.File("config.md"), options, html: false);
        Assert.Contains("GPU 드라이버", File.ReadAllText(dir.File("config.md")));
        Assert.False(string.IsNullOrEmpty(ComparisonExporter.ToStoredJson(comparison)));
    }
}

public class DocumentationTests
{
    [Fact]
    public void Event_catalog_document_matches_the_code()
    {
        // Regenerate with: wdl catalog --out docs/EVENT_CATALOG.md
        var path = Path.Combine(Fixtures.RepositoryRoot(), "docs", "EVENT_CATALOG.md");
        var expected = CatalogMarkdown.Build(EventCatalog.Default).ReplaceLineEndings("\n");
        Assert.Equal(expected, File.ReadAllText(path).ReplaceLineEndings("\n"));
    }
}
