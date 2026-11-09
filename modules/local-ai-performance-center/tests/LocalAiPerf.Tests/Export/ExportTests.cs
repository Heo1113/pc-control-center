using System.Text;
using System.Text.Json;
using LocalAiPerf.Analytics;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Export;
using LocalAiPerf.Storage;
using Xunit;
using static LocalAiPerf.Tests.FrameFactory;

namespace LocalAiPerf.Tests.Export;

public class ExportTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("-12.5", "-12.5")]
    [InlineData("-cmd", "'-cmd")]
    public void Csv_values_are_escaped_and_formulas_neutralised(string input, string expected)
        => Assert.Equal(expected, CsvWriter.Escape(input));

    [Fact]
    public void Session_exports_to_csv_and_json_with_origins_and_no_secrets()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var session = store.StartSession(Start(T0, "export, \"test\""));
        for (var i = 0; i < 20; i++)
        {
            store.WriteFrame(new FrameWrite { SessionId = session.Id, Frame = Frame(T0.AddSeconds(i), seq: i) });
        }

        var data = store.LoadSessionData(session.Id)!;
        var report = SessionAnalyzer.Analyze(data, DetectionOptions.Default);
        var files = SessionExporter.ExportSessionCsv(data, report, Path.Combine(dir.Path, "csv"));
        var json = Path.Combine(dir.Path, "session.json");
        SessionExporter.ExportSessionJson(data, report, json);

        Assert.Equal(4, files.Count);
        var samples = File.ReadAllLines(files[0], Encoding.UTF8);
        Assert.Equal(21, samples.Length); // header + 20
        Assert.StartsWith("time_utc,", samples[0].TrimStart('﻿'));
        var bytes = File.ReadAllBytes(files[0]);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // BOM for Excel
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        Assert.Equal(1, doc.RootElement.GetProperty("exportFormatVersion").GetInt32());
        Assert.Equal(20, doc.RootElement.GetProperty("samples").GetProperty("system").GetArrayLength());
        Assert.Contains(doc.RootElement.GetProperty("summary").EnumerateArray(), m => m.GetProperty("origin").GetString() == "Derived");
    }

    [Fact]
    public void Diagnostics_bundle_contains_the_key_variable_name_but_never_the_key()
    {
        using var dir = new TempDir();
        Environment.SetEnvironmentVariable("LAPC_TEST_KEY_VAR", "super-secret-value-123");
        try
        {
            var settings = new AppSettings();
            settings.Strata.ApiKeyEnvironmentVariable = "LAPC_TEST_KEY_VAR";
            var zip = Path.Combine(dir.Path, "diag.zip");
            DiagnosticsBundle.Create(zip, settings, new AppPaths(dir.Path, false), null, [], null, null, ["notice"],
                ["Authorization: Bearer abc.def.ghi", "token=xyz"]);

            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            var all = string.Join("\n", archive.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
            Assert.Contains("LAPC_TEST_KEY_VAR", all);
            Assert.DoesNotContain("super-secret-value-123", all);
            Assert.DoesNotContain("abc.def.ghi", all);
            Assert.DoesNotContain("xyz", all);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPC_TEST_KEY_VAR", null);
        }
    }

    [Theory]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("   ", "session")]
    public void File_names_are_sanitised(string input, string expected) => Assert.Equal(expected, SessionExporter.SafeFileName(input));
}
