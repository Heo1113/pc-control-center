using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LocalAiPerf.Core.Diagnostics;
using WinDiag.Analysis;
using WinDiag.Collection;
using WinDiag.Core;
using WinDiag.Core.Events;
using WinDiag.Core.Privacy;
using WinDiag.Core.Settings;
using WinDiag.Export;
using WinDiag.Storage;

namespace WinDiag.Cli;

/// <summary>
/// wdl: the same collection and analysis as the GUI, from the command line (scripts, CI checks on real logs).
/// It uses the same data folder and database as the GUI; do not run both on one data folder at the same time.
/// </summary>
public static class Program
{
    private const string Usage = """
        사용법: wdl <명령> [옵션]

        명령:
          status [--json]                 수집 상태와 데이터베이스 요약 (--json: 스크립트용, 무결성 검사·누락 종류 포함)
          collect [--days N] [--no-snapshot]
                                          이벤트 로그를 한 번 수집 (처음이면 과거 N일 포함), 구성 스냅샷, 덤프 확인
          import --from yyyy-MM-dd [--to yyyy-MM-dd]
                                          지정한 기간의 과거 로그 가져오기
          import-xml <파일> [<파일>...]   wevtutil XML 파일 가져오기 (출처 'import', 이 PC의 수집 위치는 바꾸지 않음)
          report [--days N] [--out 파일.html|파일.md] [--detailed]
                                          진단 보고서 (사실·연관성·원인 후보 구분)
          export --out 폴더 [--days N] [--detailed]
                                          진단 자료 묶음 (보고서, 이벤트, 타임라인, 센서, 구성)
          compare --baseline 이름|번호 [--before N] [--after N] [--out 파일.html|.md|.json|.csv]
          demo                            가상 시나리오 데이터 추가 (가상으로 표시됨)
          backup --out 파일.db            데이터베이스 백업 (검증 포함)
          verify-backup 파일.db           백업 파일 검증
          catalog [--out 파일.md]         이벤트 분류 규칙 목록 (Markdown)

        공통 옵션:
          --data-dir 폴더                 데이터 폴더 (기본: %LOCALAPPDATA%\WindowsDiagnosticsLab 또는 포터블 data 폴더)
          --mock-sensors                  테스트용 가상 센서

        한 데이터 폴더는 한 프로그램만 사용할 수 있습니다. GUI가 같은 폴더로 실행 중이면 wdl은 실행되지 않습니다(종료 코드 3).
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var options = Options.Parse(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "status" => await WithEngine(options, e => Status(e, options)),
                "collect" => await WithEngine(options, e => Collect(e, options), recordSensors: true),
                "import" => await WithEngine(options, e => Import(e, options)),
                "import-xml" => await WithEngine(options, e => ImportXml(e, options)),
                "report" => await WithEngine(options, e => Report(e, options)),
                "export" => await WithEngine(options, e => ExportBundleAsync(e, options)),
                "compare" => await WithEngine(options, e => Compare(e, options)),
                "demo" => await WithEngine(options, Demo),
                "backup" => await WithEngine(options, e => Backup(e, options)),
                "verify-backup" => VerifyBackup(options),
                "catalog" => Catalog(options),
                _ => Fail($"알 수 없는 명령: {args[0]}\n\n{Usage}", 2),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or LocalAiPerf.Storage.StorageVersionException)
        {
            return Fail("오류: " + ex.Message);
        }
    }

    private static int Fail(string message, int code = 1)
    {
        Console.Error.WriteLine(message);
        return code;
    }

    /// <param name="recordSensors">Only collection records sensor samples; commands that read the database do not.</param>
    private static async Task<int> WithEngine(Options options, Func<DiagnosticsEngine, Task<int>> action, bool recordSensors = false)
    {
        var paths = AppPaths.Resolve(options.DataDirectory, AppContext.BaseDirectory);
        paths.EnsureDirectories();

        // The GUI and wdl never share a data folder at the same time: starting the engine closes unfinished sessions as
        // "ended uncleanly", which would be wrong for a GUI that is still running.
        using var folderLock = DataFolderLock.TryAcquire(paths.DataDirectory);
        if (folderLock is null)
        {
            return Fail($"이 데이터 폴더를 다른 Windows Diagnostics Lab(GUI 또는 wdl)이 사용 중입니다: {paths.DataDirectory}\n"
                        + "GUI를 닫은 뒤 다시 실행하거나 --data-dir로 다른 폴더를 지정하세요.", 3);
        }

        using var fileLog = new FileAppLogger(paths.LogDirectory, filePrefix: AppInfo.ShortName + "-cli");
        var settings = new SettingsStore(paths.SettingsPath).Load().Settings;
        if (options.Days is int days)
        {
            settings.Collection.HistoryDays = days;
        }

        settings.Normalize();
        var engine = await DiagnosticsEngine.StartAsync(new EngineOptions
        {
            Paths = paths, Settings = settings, UseMockSensors = options.MockSensors, StartBackgroundLoops = false, ScanDumps = false, Log = fileLog,
            RecordSensors = recordSensors,
        });
        try
        {
            if (!options.Json)
            {
                Console.WriteLine($"{AppInfo.ProductName} {AppInfo.Version} · 데이터 폴더 {paths.DataDirectory}{(paths.IsPortable ? " (포터블)" : "")}");
            }

            return await action(engine);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    private static Task<int> Status(DiagnosticsEngine engine, Options options)
    {
        var stats = engine.Store.GetStats();
        if (options.Json)
        {
            // For scripts (the CI abnormal-end check): ASCII keys, times in ISO 8601.
            var status = new
            {
                product = AppInfo.ProductName,
                version = AppInfo.Version,
                dataDirectory = engine.Paths.DataDirectory,
                database = new
                {
                    stats.FileBytes, stats.Events, stats.VirtualEvents, stats.SensorSamples, stats.Snapshots, stats.Runs, stats.Gaps, stats.OldestEvent,
                    stats.NewestEvent,
                },
                integrity = engine.Store.IntegrityCheck(),
                recoveredRuns = engine.RecoveredRuns.Select(r => new { r.Id, r.Started, r.Ended, r.EndReason }),
                gapKinds = engine.Store.ListGaps(limit: 100_000).GroupBy(g => g.Kind).ToDictionary(g => g.Key, g => g.Count()),
                channels = engine.Store.GetChannelStates().Select(c => new { c.Channel, status = c.Status.ToString(), c.LastRecordId, c.OldestEventTime, c.LastScan }),
                eventCollectionUnavailable = engine.EventCollectionUnavailableReason,
            };
            Console.WriteLine(JsonSerializer.Serialize(status, new JsonSerializerOptions
            {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            return Task.FromResult(0);
        }

        Console.WriteLine($"데이터베이스: {stats.FileBytes / 1048576.0:0.0} MB, 이벤트 {stats.Events:N0}건 (가상 {stats.VirtualEvents:N0}), 센서 샘플 {stats.SensorSamples:N0}, 스냅샷 {stats.Snapshots}, 누락·제한 {stats.Gaps}");
        Console.WriteLine($"이벤트 기간: {Time(stats.OldestEvent)} ~ {Time(stats.NewestEvent)}");
        foreach (var c in engine.Store.GetChannelStates())
        {
            Console.WriteLine($"  {c.Channel}: {ChannelStatusText.Name(c.Status)} · 마지막 확인 {Time(c.LastScan)} · 가장 오래된 기록 {Time(c.OldestEventTime)}");
        }

        if (engine.EventCollectionUnavailableReason is { } reason)
        {
            Console.WriteLine(reason);
        }

        return Task.FromResult(0);
    }

    private static async Task<int> Collect(DiagnosticsEngine engine, Options options)
    {
        var report = await engine.PollNowAsync();
        if (report is null)
        {
            return Fail(engine.EventCollectionUnavailableReason ?? engine.LastPollError ?? "수집하지 못했습니다.");
        }

        PrintReport(report);
        if (!options.NoSnapshot)
        {
            var snapshot = await engine.TakeSnapshotAsync("cli");
            Console.WriteLine(snapshot.IsNew ? "구성 스냅샷 저장 (새 구성)" : "구성 변경 없음 (확인 시각만 갱신)");
        }

        var dumps = await engine.ScanDumpsAsync();
        Console.WriteLine($"덤프 파일 {dumps.Files.Count}개 확인. {string.Join(" ", dumps.Notes)}");
        await Task.Delay(TimeSpan.FromSeconds(2)); // let the sensor loop store its first samples
        return report.Channels.Any(c => c.Status == ChannelStatus.Ok) ? 0 : 1;
    }

    private static async Task<int> Import(DiagnosticsEngine engine, Options options)
    {
        if (options.From is not { } from)
        {
            return Fail("--from yyyy-MM-dd 가 필요합니다.", 2);
        }

        var to = options.To ?? DateTimeOffset.Now;
        var report = await engine.ImportHistoryAsync(from, to,
            new Progress<CollectionProgress>(p => Console.WriteLine($"  {p.Channel}: {p.Read:N0}건 읽음, {p.Inserted:N0}건 저장")));
        if (report is null)
        {
            return Fail(engine.EventCollectionUnavailableReason ?? "가져오지 못했습니다.");
        }

        PrintReport(report);
        return 0;
    }

    private static async Task<int> ImportXml(DiagnosticsEngine engine, Options options)
    {
        if (options.Positional.Count == 0)
        {
            return Fail("가져올 XML 파일을 지정하세요.", 2);
        }

        var report = await engine.ImportXmlFilesAsync(options.Positional);
        PrintReport(report);
        return 0;
    }

    private static async Task<int> Report(DiagnosticsEngine engine, Options options)
    {
        var report = Analyze(engine, options);
        Console.WriteLine($"분석 기간 {Time(report.Range.From)} ~ {Time(report.Range.To)}, 이벤트 {report.EventCount:N0}건");
        PrintFindings("확인된 사실", report.Facts);
        PrintFindings("관측된 연관성", report.Associations);
        PrintFindings("원인 후보 (추가 검증 필요)", report.Hypotheses);
        foreach (var limitation in report.Limitations)
        {
            Console.WriteLine($"  한계: {limitation}");
        }

        if (options.Out is { } path)
        {
            var export = ExportOptionsFor(options);
            await Task.Run(() =>
            {
                if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    ReportExporter.WriteMarkdown(report, path, export);
                }
                else
                {
                    ReportExporter.WriteHtml(report, path, export);
                }
            });
            Console.WriteLine($"보고서 저장: {path} ({export.PrivacyLabel})");
        }

        return 0;
    }

    private static async Task<int> ExportBundleAsync(DiagnosticsEngine engine, Options options)
    {
        if (options.Out is not { } folder)
        {
            return Fail("--out 폴더가 필요합니다.", 2);
        }

        var report = Analyze(engine, options);
        var files = await Task.Run(() => ExportBundle.Write(engine.Store, report, folder, ExportOptionsFor(options)));
        foreach (var file in files)
        {
            Console.WriteLine($"  {file}");
        }

        return 0;
    }

    private static async Task<int> Compare(DiagnosticsEngine engine, Options options)
    {
        var baselines = engine.Store.ListBaselines();
        var baseline = baselines.FirstOrDefault(b => b.Id.ToString(CultureInfo.InvariantCulture) == options.Baseline || b.Name == options.Baseline);
        if (baseline is null)
        {
            Console.WriteLine("기준선 목록:");
            foreach (var b in baselines)
            {
                Console.WriteLine($"  {b.Id}: {b.Name} ({Time(b.Time)})");
            }

            return Fail("--baseline 으로 위 목록의 이름이나 번호를 지정하세요.", 2);
        }

        var result = BaselineComparer.Compare(engine.Store, baseline.Name, baseline.Time, options.Before ?? 14, options.After ?? 14, DateTimeOffset.UtcNow, baseline.UserRecordId);
        Console.WriteLine(BaselineComparer.Summary(result));
        foreach (var note in result.Notes)
        {
            Console.WriteLine($"  ⓘ {note}");
        }

        if (options.Out is { } path)
        {
            var export = ExportOptionsFor(options);
            await Task.Run(() =>
            {
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".json":
                        ComparisonExporter.WriteJson(result, path, export);
                        break;
                    case ".csv":
                        ComparisonExporter.WriteCsv(result, path);
                        break;
                    case ".md":
                        ComparisonExporter.WriteMarkdown(result, path, export);
                        break;
                    default:
                        ComparisonExporter.WriteHtml(result, path, export);
                        break;
                }
            });
            Console.WriteLine($"비교 결과 저장: {path}");
        }

        return 0;
    }

    private static async Task<int> Demo(DiagnosticsEngine engine)
    {
        var report = await engine.SeedVirtualScenarioAsync();
        Console.WriteLine($"가상 이벤트 {report.Inserted}건을 추가했습니다 (모두 '가상'으로 표시됨).");
        return 0;
    }

    private static Task<int> Backup(DiagnosticsEngine engine, Options options)
    {
        if (options.Out is not { } path)
        {
            return Task.FromResult(Fail("--out 파일.db 가 필요합니다.", 2));
        }

        engine.Store.BackupTo(path);
        var validation = DiagnosticsStore.ValidateBackup(path);
        Console.WriteLine(validation.IsValid ? $"백업 완료·검증 정상: {path} (이벤트 {validation.Events:N0}건)" : $"백업 검증 실패: {validation.Message}");
        return Task.FromResult(validation.IsValid ? 0 : 1);
    }

    private static int VerifyBackup(Options options)
    {
        if (options.Positional.Count == 0)
        {
            return Fail("검증할 파일을 지정하세요.", 2);
        }

        var v = DiagnosticsStore.ValidateBackup(options.Positional[0]);
        Console.WriteLine(v.IsValid ? $"정상: 스키마 {v.SchemaVersion}, 이벤트 {v.Events:N0}건, {Time(v.Oldest)} ~ {Time(v.Newest)}" : $"사용할 수 없음: {v.Message}");
        return v.IsValid ? 0 : 1;
    }

    private static int Catalog(Options options)
    {
        var text = CatalogMarkdown.Build(EventCatalog.Default);
        if (options.Out is { } path)
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
            Console.WriteLine($"저장: {path}");
        }
        else
        {
            Console.WriteLine(text);
        }

        return 0;
    }

    private static DiagnosticReport Analyze(DiagnosticsEngine engine, Options options)
    {
        var analysis = engine.Settings.Analysis;
        var now = DateTimeOffset.UtcNow;
        return DiagnosticAnalyzer.Analyze(engine.Store, AnalysisRange.LastDays(now, options.Days ?? 30), new DiagnosticOptions
        {
            CorrelationWindowSeconds = analysis.CorrelationWindowSeconds,
            RepeatThreshold = analysis.RepeatThreshold,
            HighGpuTempC = analysis.HighGpuTempC,
            SensorWindowMinutes = analysis.SensorWindowMinutes,
            AcknowledgedEventId = engine.Store.LastAcknowledgedEventId,
        }, now);
    }

    private static ExportOptions ExportOptionsFor(Options options) => new()
    {
        Detail = options.Detailed ? ExportDetail.Detailed : ExportDetail.Minimal,
        Masker = PrivacyMasker.ForCurrentMachine(),
    };

    private static void PrintReport(CollectionReport report)
    {
        foreach (var c in report.Channels)
        {
            Console.WriteLine($"  {c.Channel}: {ChannelStatusText.Name(c.Status)} · 읽음 {c.Read:N0} · 새로 저장 {c.Inserted:N0} · 이미 있음 {c.Duplicates:N0}"
                              + (c.Gaps.Count > 0 ? $" · 누락·제한 {string.Join(", ", c.Gaps.Select(g => GapKinds.Name(g.Kind)))}" : "")
                              + (c.Status is ChannelStatus.AccessDenied ? " (관리자 권한 또는 Event Log Readers 그룹 필요)" : ""));
        }

        Console.WriteLine($"합계: 읽음 {report.Read:N0}, 새로 저장 {report.Inserted:N0}, 중복으로 건너뜀 {report.Duplicates:N0}");
    }

    private static void PrintFindings(string title, IReadOnlyList<Finding> findings)
    {
        Console.WriteLine($"[{title}] {findings.Count}건");
        foreach (var f in findings.Take(15))
        {
            Console.WriteLine($"  - {LevelText.Severity(f.Severity)} · {f.Title}" + (f.Strength is null ? "" : $" (근거 강도: {f.Strength}, 확정 아님)"));
        }
    }

    private static string Time(DateTimeOffset? time) => time is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "N/A";

    internal sealed record Options
    {
        public string? DataDirectory { get; init; }
        public bool MockSensors { get; init; }
        public int? Days { get; init; }
        public DateTimeOffset? From { get; init; }
        public DateTimeOffset? To { get; init; }
        public string? Out { get; init; }
        public bool Detailed { get; init; }
        public bool NoSnapshot { get; init; }
        public bool Json { get; init; }
        public string? Baseline { get; init; }
        public int? Before { get; init; }
        public int? After { get; init; }
        public List<string> Positional { get; init; } = [];

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                string? Next() => i + 1 < args.Length ? args[++i] : null;
                int? Int() => int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
                DateTimeOffset? Date() => DateTime.TryParseExact(Next(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
                    ? new DateTimeOffset(d) : null;
                switch (args[i])
                {
                    case "--data-dir": o = o with { DataDirectory = Next() }; break;
                    case "--mock-sensors": o = o with { MockSensors = true }; break;
                    case "--days": o = o with { Days = Int() }; break;
                    case "--from": o = o with { From = Date() }; break;
                    case "--to": o = o with { To = Date()?.AddDays(1) }; break;
                    case "--out": o = o with { Out = Next() }; break;
                    case "--detailed": o = o with { Detailed = true }; break;
                    case "--no-snapshot": o = o with { NoSnapshot = true }; break;
                    case "--json": o = o with { Json = true }; break;
                    case "--baseline": o = o with { Baseline = Next() }; break;
                    case "--before": o = o with { Before = Int() }; break;
                    case "--after": o = o with { After = Int() }; break;
                    default: o.Positional.Add(args[i]); break;
                }
            }

            return o;
        }
    }
}
