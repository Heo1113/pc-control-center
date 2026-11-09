using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using GpuProfit.Accounting.Analysis;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Core;
using GpuProfit.Core.Domain;
using GpuProfit.Export;
using GpuProfit.Recording;
using GpuProfit.Storage;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Storage;

namespace GpuProfit.Cli;

/// <summary>
/// <c>gpc</c>: command line access to the same database and the same economics engine as the GUI.
/// It never samples sensors and never closes or changes sessions, except for the explicit recalc and demo-data commands.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const string Usage = """
        gpc - GPU Profit & Compute Center command line

        usage: gpc <command> [options]

          status [--json]                     database, integrity, open runs and sessions, gaps (read only)
          sessions [--json] [--limit N]       sessions with their calculated figures (read only)
          recalc [--session ID]               recalculate one or every session and save changed results
          export --out PATH [--format csv|json] [--view realized|unconfirmed|expected]
          periods --out PATH [--by day|week|month]
          backup --out PATH                   consistent copy of the database (never overwrites)
          verify-backup PATH                  check a backup file
          demo-data                           add MOCK demo sessions (flagged as virtual)

        common options:
          --data-dir DIR                      data folder (default: portable 'data' or %LOCALAPPDATA%\GpuProfitCenter)
        """;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            return Run(args, Console.Out, Console.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or InvalidDataException or StorageVersionException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine("오류: " + SecretRedactor.Redact(ex.Message));
            return 1;
        }
    }

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(Usage);
            return args.Count == 0 ? 2 : 0;
        }

        var command = args[0];
        var options = Options.Parse(args.Skip(1).ToList());
        var paths = AppPaths.Resolve(options.Get("--data-dir"), AppContext.BaseDirectory);
        var mutating = command is "recalc" or "demo-data";
        using var folderLock = mutating ? DataFolderLock.TryAcquire(paths.DataDirectory) : null;
        if (mutating && folderLock is null)
        {
            error.WriteLine("같은 데이터 폴더를 쓰는 GPU Profit & Compute Center가 실행 중입니다. 앱을 닫고 다시 실행하세요.");
            return 3;
        }

        var engine = ProfitEngine.Start(new EngineOptions { Paths = paths, ReadOnlyTools = true });
        try
        {
            return command switch
            {
                "status" => Status(engine, options, output),
                "sessions" => Sessions(engine, options, output),
                "recalc" => Recalc(engine, options, output),
                "export" => ExportLedger(engine, options, output, error),
                "periods" => Periods(engine, options, output, error),
                "backup" => Backup(engine, options, output, error),
                "verify-backup" => VerifyBackup(options, output),
                "demo-data" => Demo(engine, output),
                _ => Unknown(command, error),
            };
        }
        finally
        {
            engine.StopAsync().GetAwaiter().GetResult();
        }
    }

    private static int Unknown(string command, TextWriter error)
    {
        error.WriteLine($"알 수 없는 명령: {command}");
        error.WriteLine(Usage);
        return 2;
    }

    private static int Status(ProfitEngine engine, Options options, TextWriter output)
    {
        var store = engine.Store;
        var stats = store.GetStats();
        var runs = store.ListRuns();
        var open = store.ListSessions(new SessionQuery { State = SessionState.Running })
            .Concat(store.ListSessions(new SessionQuery { State = SessionState.Paused }))
            .Concat(store.ListSessions(new SessionQuery { State = SessionState.Interrupted })).ToList();
        var status = new
        {
            app = AppInfo.ProductName,
            version = AppInfo.Version,
            integrity = store.IntegrityCheck(),
            openNotice = store.OpenNotice,
            database = new
            {
                stats.FileBytes, stats.WalBytes, stats.Runs, stats.Devices, stats.GpuSamples, stats.SystemSamples, stats.Gaps, stats.Workloads, stats.Sessions,
                stats.RevenueEntries, stats.CostEntries, stats.Results, stats.Scenarios, stats.FirstSample, stats.LastSample,
            },
            unfinishedRuns = runs.Where(r => r.Ended is null).Select(r => new { r.Id, r.Started, r.LastHeartbeat, r.IsMock }),
            recoveredRuns = runs.Where(r => r.EndReason == RunEndReasons.RecoveredAfterCrash).Select(r => new { r.Id, r.Started, r.Ended }),
            openSessions = open.Select(s => new { s.Id, s.Title, state = s.State, s.Started, s.IsMock, segments = store.ListSegments(s.Id).Count }),
            gapKinds = store.ListGaps(limit: 100_000).GroupBy(g => g.Kind).ToDictionary(g => g.Key, g => g.Count()),
            devices = store.ListDevices().Select(d => new { d.GpuIndex, d.Name, d.IsMock, d.LastSeen }),
        };
        if (options.Has("--json"))
        {
            output.WriteLine(JsonSerializer.Serialize(status, Json));
            return 0;
        }

        output.WriteLine($"{AppInfo.ProductName} {AppInfo.Version}");
        output.WriteLine($"무결성 검사: {status.integrity}");
        output.WriteLine($"세션 {stats.Sessions}개, GPU 샘플 {stats.GpuSamples:N0}행, 시스템 샘플 {stats.SystemSamples:N0}행, 공백 기록 {stats.Gaps}건");
        output.WriteLine($"정상 종료되지 않은 실행: {status.unfinishedRuns.Count()}개, 열린 세션: {open.Count}개");
        return 0;
    }

    private static List<AccountedSession> Accounted(ProfitEngine engine, int limit = 100_000)
    {
        var workloads = engine.Store.ListWorkloads().ToDictionary(w => w.Id);
        var profile = engine.Store.GetProfile();
        return engine.Store.ListSessions(new SessionQuery { Limit = limit })
            .Select(s => new AccountedSession(s, workloads.GetValueOrDefault(s.WorkloadId), engine.Sessions.Compute(s.Id, profile)))
            .ToList();
    }

    private static int Sessions(ProfitEngine engine, Options options, TextWriter output)
    {
        var limit = int.TryParse(options.Get("--limit"), out var l) ? l : 50;
        var items = Accounted(engine, limit);
        if (options.Has("--json"))
        {
            output.WriteLine(JsonSerializer.Serialize(items.Select(i => new { i.Session.Id, i.Session.Title, workload = i.Workload?.Name, result = Exporters.ResultDocument(i.Result) }), Json));
            return 0;
        }

        foreach (var i in items)
        {
            var v = i.Result.View(RevenueView.Realized);
            output.WriteLine($"#{i.Session.Id} {i.Session.Title ?? "(제목 없음)"} [{SessionStateText.ToKorean(i.Session.State)}] "
                             + $"{i.Result.BasisHours:0.##} h, {(i.Result.Energy.TotalKwh.Value is double k ? $"{k:0.###} kWh" : "에너지 N/A")}, "
                             + $"실현 순수익 {(v.NetProfit.Value is decimal n ? $"{n:N0} 원" : "N/A (" + v.NetProfit.Reason + ")")}");
        }

        return 0;
    }

    private static int Recalc(ProfitEngine engine, Options options, TextWriter output)
    {
        if (options.Get("--session") is { } text && long.TryParse(text, out var id))
        {
            var (_, changed) = engine.Sessions.Recalculate(id, "명령줄 재계산");
            output.WriteLine(changed ? $"세션 #{id}: 결과가 바뀌어 새 계산을 저장했습니다." : $"세션 #{id}: 입력이 같아 결과가 그대로입니다.");
            return 0;
        }

        var count = engine.Sessions.RecalculateAll("명령줄 재계산");
        output.WriteLine($"결과가 바뀐 세션 {count}개를 저장했습니다.");
        return 0;
    }

    private static int ExportLedger(ProfitEngine engine, Options options, TextWriter output, TextWriter error)
    {
        if (options.Get("--out") is not { } path)
        {
            error.WriteLine("--out PATH가 필요합니다.");
            return 2;
        }

        var view = options.Get("--view") switch
        {
            "unconfirmed" => RevenueView.IncludingUnconfirmed,
            "expected" => RevenueView.Expected,
            _ => RevenueView.Realized,
        };
        var items = Accounted(engine);
        if (string.Equals(options.Get("--format"), "json", StringComparison.OrdinalIgnoreCase))
        {
            Exporters.LedgerJson(path, engine.Store, items, DateTimeOffset.UtcNow);
        }
        else
        {
            Exporters.SessionsCsv(path, items, view);
        }

        output.WriteLine($"내보냈습니다: {Path.GetFileName(path)} (세션 {items.Count}개)");
        return 0;
    }

    private static int Periods(ProfitEngine engine, Options options, TextWriter output, TextWriter error)
    {
        if (options.Get("--out") is not { } path)
        {
            error.WriteLine("--out PATH가 필요합니다.");
            return 2;
        }

        var by = options.Get("--by") switch
        {
            "week" => PeriodGranularity.Week,
            "month" => PeriodGranularity.Month,
            _ => PeriodGranularity.Day,
        };
        var segments = engine.Store.ListAllSegments();
        var items = Accounted(engine).Select(i => new PeriodItem(i.Session, segments.GetValueOrDefault(i.Session.Id, []), i.Result)).ToList();
        var buckets = PeriodAggregator.Aggregate(items, by, TimeZoneInfo.Local, DateTimeOffset.UtcNow, engine.Store.ListCosts(), engine.Store.ListRates());
        Exporters.PeriodsCsv(path, buckets);
        output.WriteLine($"내보냈습니다: {Path.GetFileName(path)} ({buckets.Count}개 기간)");
        return 0;
    }

    private static int Backup(ProfitEngine engine, Options options, TextWriter output, TextWriter error)
    {
        if (options.Get("--out") is not { } path)
        {
            error.WriteLine("--out PATH가 필요합니다.");
            return 2;
        }

        engine.Store.BackupTo(path);
        var validation = ProfitStore.ValidateBackup(path);
        output.WriteLine(validation.IsValid ? $"백업 완료: {Path.GetFileName(path)} (세션 {validation.Sessions}개)" : $"백업 검증 실패: {validation.Message}");
        return validation.IsValid ? 0 : 1;
    }

    private static int VerifyBackup(Options options, TextWriter output)
    {
        var path = options.Positional.FirstOrDefault();
        if (path is null)
        {
            output.WriteLine("검사할 백업 파일 경로가 필요합니다.");
            return 2;
        }

        var v = ProfitStore.ValidateBackup(path);
        output.WriteLine(v.IsValid ? $"정상: 스키마 {v.SchemaVersion}, 세션 {v.Sessions}개, GPU 샘플 {v.GpuSamples:N0}행" : $"사용할 수 없음: {v.Message}");
        return v.IsValid ? 0 : 1;
    }

    private static int Demo(ProfitEngine engine, TextWriter output)
    {
        var n = DemoDataSeeder.Seed(engine.Store, engine.Sessions, DateTimeOffset.UtcNow);
        output.WriteLine(n == 0 ? "데모 데이터가 이미 있습니다." : $"MOCK 데모 세션 {n}개를 추가했습니다 (모두 가상 데이터로 표시됨).");
        return 0;
    }

    internal sealed class Options
    {
        private readonly Dictionary<string, string?> _named = new(StringComparer.Ordinal);

        public List<string> Positional { get; } = [];

        public static Options Parse(IReadOnlyList<string> args)
        {
            var o = new Options();
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    var takesValue = args[i] is "--data-dir" or "--out" or "--format" or "--view" or "--by" or "--session" or "--limit";
                    o._named[args[i]] = takesValue && i + 1 < args.Count ? args[++i] : null;
                }
                else
                {
                    o.Positional.Add(args[i]);
                }
            }

            return o;
        }

        public bool Has(string name) => _named.ContainsKey(name);

        public string? Get(string name) => _named.GetValueOrDefault(name);
    }
}
