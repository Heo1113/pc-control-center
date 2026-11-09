using System.Globalization;
using System.Text;
using LocalAiPerf.Analytics;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Metrics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Export;
using LocalAiPerf.Recording;
using LocalAiPerf.Runtimes;
using LocalAiPerf.Storage;
using LocalAiPerf.Telemetry;

namespace LocalAiPerf.Cli;

/// <summary>
/// lapc - headless companion of Local AI Performance Center. Uses the same data folder, database and settings as the app.
/// </summary>
public static class Program
{
    private const string Usage = """
        lapc - Local AI Performance Center (CLI)

        사용법:
          lapc sensors   [--mock-sensors]                        센서 지원 표와 현재 값 1회 출력
          lapc strata    [--strata-url URL]                      Strata 연결 상태와 공개 API 정보 확인 (읽기 전용)
          lapc record    [--seconds N] [--interval-ms N] [--name NAME] [--strata-url URL] [--no-strata] [--mock-sensors]
          lapc sessions  [--filter TEXT]                         기록된 세션 목록
          lapc export    --session ID --format csv|json --out PATH
          lapc compare   --sessions ID,ID[,...] [--baseline ID] [--out PATH.json|PATH.csv]
          lapc backup    --out PATH [--force]
          lapc restore   --from PATH --yes                       (현재 DB는 옆에 보관됨)
          lapc check                                             무결성 검사와 DB 통계
          lapc retention                                         보존 정책 즉시 적용

        공통 옵션: --data-dir DIR (기본: 앱과 같은 데이터 폴더)
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var options = Options.Parse(args.Skip(1));
        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "sensors" => await SensorsAsync(options),
                "strata" => await StrataAsync(options),
                "record" => await RecordAsync(options),
                "sessions" => Sessions(options),
                "export" => Export(options),
                "compare" => Compare(options),
                "backup" => Backup(options),
                "restore" => Restore(options),
                "check" => Check(options),
                "retention" => Retention(options),
                _ => UsageError($"알 수 없는 명령: {args[0]}"),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or StorageVersionException
                                       or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"오류: {ex.Message}");
            return 1;
        }
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static (AppPaths Paths, AppSettings Settings) Load(Options options)
    {
        var paths = AppPaths.Resolve(options.Get("data-dir"), AppContext.BaseDirectory);
        var loaded = new SettingsStore(paths.SettingsPath).Load();
        foreach (var note in loaded.Notes)
        {
            Console.Error.WriteLine($"설정: {note}");
        }

        var settings = loaded.Settings;
        if (options.Get("strata-url") is string url)
        {
            settings.Strata.BaseUrl = url;
            settings.Strata.Enabled = true;
        }

        if (options.Has("no-strata"))
        {
            settings.Strata.Enabled = false;
        }

        if (options.GetInt("interval-ms") is int interval)
        {
            settings.Sampling.IntervalMs = interval;
        }

        foreach (var note in settings.Normalize())
        {
            Console.Error.WriteLine($"설정: {note}");
        }

        return (paths, settings);
    }

    private static async Task<int> SensorsAsync(Options options)
    {
        var (_, settings) = Load(options);
        using var monitor = HardwareMonitor.Create(settings.Sampling, options.Has("mock-sensors"));
        var info = monitor.Info;
        Console.WriteLine($"CPU: {info.CpuName ?? "알 수 없음"} ({info.LogicalProcessors} 논리 프로세서)");
        Console.WriteLine($"RAM: {Gib(info.RamTotalBytes)} GiB, OS: {info.OsDescription}, 관리자 권한: {(info.IsElevated ? "예" : "아니오")}");
        Console.WriteLine($"GPU 제공자: {monitor.Gpu.Name} {(monitor.Gpu.IsAvailable ? "" : $"(사용 불가: {monitor.Gpu.UnavailableReason})")}");
        Console.WriteLine($"드라이버: {info.GpuDriverVersion ?? "N/A"}, NVML: {info.NvmlVersion ?? "N/A"}");
        foreach (var gpu in info.Gpus)
        {
            Console.WriteLine($"  GPU {gpu.Index}: {gpu.Name}, VRAM {Gib(gpu.MemoryTotalBytes)} GiB, 전력 제한 {Fmt(gpu.PowerLimitW, "0")} W");
        }

        Console.WriteLine();
        Console.WriteLine("센서 지원 표:");
        foreach (var c in monitor.Capabilities)
        {
            Console.WriteLine($"  {c.DisplayName,-24} {SensorStatusText.ToKorean(c.Status),-22} {c.Source,-14} {(c.RequiresElevation ? "[관리자 권한 필요] " : "")}{c.Detail}");
        }

        await Task.Delay(1000);
        var sample = monitor.Sample();
        Console.WriteLine();
        Console.WriteLine($"현재 값 ({sample.Timestamp.ToLocalTime():HH:mm:ss}){(sample.IsMock ? " [MOCK - 가상 값]" : "")}:");
        Console.WriteLine($"  CPU {Fmt(sample.System.CpuUtilizationPercent, "0.0")} %, RAM {Gib(sample.System.MemoryUsedBytes)}/{Gib(sample.System.MemoryTotalBytes)} GiB");
        foreach (var g in sample.Gpus)
        {
            Console.WriteLine($"  GPU {g.Index}: 사용률 {Fmt(g.UtilizationPercent, "0")} %, 온도 {Fmt(g.TemperatureC, "0")} °C, 전력 {Fmt(g.PowerW, "0.0")}/{Fmt(g.PowerLimitW, "0")} W, " +
                $"VRAM {Gib(g.MemoryUsedBytes)}/{Gib(g.MemoryTotalBytes)} GiB, 클럭 {Fmt(g.GraphicsClockMhz, "0")}/{Fmt(g.MemoryClockMhz, "0")} MHz, " +
                $"제한 사유: {(g.ThrottleReasons is ulong r ? GpuThrottleReasonText.Describe(r) : "N/A")}");
        }

        foreach (var issue in sample.Issues)
        {
            Console.WriteLine($"  문제: {issue.Source} {issue.Scope}: {issue.Message}");
        }

        return 0;
    }

    private static async Task<int> StrataAsync(Options options)
    {
        var (_, settings) = Load(options);
        settings.Strata.Enabled = true;
        using var adapter = RuntimeAdapterFactory.Create(settings)!;
        var snapshot = await adapter.PollAsync(CancellationToken.None);
        if (snapshot.IsReachable)
        {
            await Task.Delay(1500); // metadata (/health, /v1/status, /props, /v1/models) is fetched in the background
            adapter.RequestReconnect();
            snapshot = await adapter.PollAsync(CancellationToken.None);
            await Task.Delay(1500);
            snapshot = await adapter.PollAsync(CancellationToken.None);
        }

        Console.WriteLine($"주소: {adapter.EndpointDisplay}");
        Console.WriteLine($"상태: {RuntimeStateText.ToKorean(snapshot.State)}");
        if (snapshot.Error is { } error)
        {
            Console.WriteLine($"오류 분류: {RuntimeStateText.ToKorean(error.Category)} - {error.Message}");
            if (error.Hint is not null)
            {
                Console.WriteLine($"안내: {error.Hint}");
            }
        }

        if (snapshot.IsReachable)
        {
            Console.WriteLine($"텔레메트리: {snapshot.Server?.TelemetrySource} (schema {snapshot.Server?.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "-"})");
            Console.WriteLine($"엔진 버전: {snapshot.Server?.EngineVersion ?? $"N/A ({snapshot.Server?.EngineVersionNote})"}");
            Console.WriteLine($"모델: {snapshot.Model?.Id ?? "N/A"} (로드됨: {Bool(snapshot.Model?.Loaded)}), 파일: {snapshot.Model?.ModelFileName ?? "N/A"}");
            Console.WriteLine($"컨텍스트: {snapshot.Model?.ContextTokens?.ToString(CultureInfo.InvariantCulture) ?? "N/A"} tokens, 슬롯: {snapshot.Work?.SlotsEffective?.ToString(CultureInfo.InvariantCulture) ?? "N/A"}");
            Console.WriteLine($"답변 속도: {Fmt(snapshot.Rates?.AnswerTokPerSec, "0.0")} tok/s, 프롬프트 처리: {Fmt(snapshot.Rates?.PrefillTokPerSec, "0.0")} tok/s, " +
                $"전체 출력(계산값): {Fmt(snapshot.Rates?.AggregateOutputTokPerSec, "0.0")} tok/s");
            Console.WriteLine($"처리 중/대기: {snapshot.Work?.Active?.ToString(CultureInfo.InvariantCulture) ?? "N/A"}/{snapshot.Work?.Queued?.ToString(CultureInfo.InvariantCulture) ?? "N/A"}, 최근 완료 요청 {snapshot.RecentRequests.Count}건");
            foreach (var s in snapshot.Settings)
            {
                Console.WriteLine($"  설정 {s.Key} = {s.Value}");
            }
        }

        return snapshot.IsReachable ? 0 : 1;
    }

    private static async Task<int> RecordAsync(Options options)
    {
        var (paths, settings) = Load(options);
        var seconds = options.GetInt("seconds") ?? 30;
        using var log = new FileAppLogger(paths.LogDirectory);
        await using var service = new MonitoringService(paths, settings,
            new MonitoringOptions { UseMockSensors = options.Has("mock-sensors"), AutoRecord = false }, log);
        await service.StartAsync();
        foreach (var notice in service.Notices)
        {
            Console.WriteLine($"알림: {notice}");
        }

        if (service.Recorder is null)
        {
            Console.Error.WriteLine(service.StorageProblem ?? "기록할 수 없습니다.");
            return 1;
        }

        var session = await service.StartRecordingAsync(options.Get("name") ?? "CLI 기록");
        Console.WriteLine($"세션 #{session!.Id} '{session.Name}' 기록 중: {seconds}초, 간격 {settings.Sampling.IntervalMs} ms (Ctrl+C로 중지)");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, seconds)), stop.Token);
                PrintLive(service);
            }
        }
        catch (OperationCanceledException)
        {
        }

        PrintLive(service);
        await service.StopRecordingAsync();
        await service.StopAsync();
        using var store = TelemetryStore.Open(paths.DatabasePath);
        var data = store.LoadSessionData(session.Id)!;
        Console.WriteLine($"완료: 세션 #{session.Id}, 시스템 샘플 {data.System.Count}, GPU 샘플 {data.Gpu.Count}, 런타임 샘플 {data.Runtime.Count}, " +
            $"요청 {data.Requests.Count}, 이벤트 {data.Events.Count}, 종료 사유 {data.Session.EndReason}");
        return 0;
    }

    private static void PrintLive(MonitoringService service)
    {
        var frame = service.Live.Latest;
        if (frame is null)
        {
            return;
        }

        var g = frame.Hardware.Gpus.FirstOrDefault();
        var rt = service.LatestRuntime;
        Console.WriteLine($"  {frame.Timestamp.ToLocalTime():HH:mm:ss} CPU {Fmt(frame.Hardware.System.CpuUtilizationPercent, "0")}% " +
            $"GPU {Fmt(g?.UtilizationPercent, "0")}% {Fmt(g?.PowerW, "0")}W {Fmt(g?.TemperatureC, "0")}°C | " +
            $"{rt?.RuntimeKind ?? "runtime"}: {(rt is null ? "-" : RuntimeStateText.ToKorean(rt.State))} {Fmt(rt?.Rates?.AnswerTokPerSec, "0.0")} tok/s" +
            (frame.Hardware.IsMock ? " [MOCK]" : ""));
    }

    private static int Sessions(Options options)
    {
        var (paths, _) = Load(options);
        using var store = TelemetryStore.Open(paths.DatabasePath);
        var sessions = store.ListSessions(new SessionFilter(options.Get("filter")));
        Console.WriteLine($"{"ID",5}  {"시작",-16}  {"길이",9}  {"샘플",7}  {"종료 사유",-20}  {"모델",-28}  이름");
        foreach (var s in sessions)
        {
            Console.WriteLine($"{s.Id,5}  {s.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {s.Duration:hh\\:mm\\:ss}  {s.SampleCount,7}  " +
                $"{Core.Sessions.SessionEndReasons.ToKorean(s.EndReason),-20}  {s.ModelSummary ?? "-",-28}  {s.Name}{(s.IsMock ? " [MOCK]" : "")}");
        }

        return 0;
    }

    private static int Export(Options options)
    {
        var (paths, settings) = Load(options);
        if (options.GetLong("session") is not long id || options.Get("out") is not string output)
        {
            return UsageError("export에는 --session ID와 --out PATH가 필요합니다.");
        }

        using var store = TelemetryStore.Open(paths.DatabasePath);
        var data = store.LoadSessionData(id) ?? throw new ArgumentException($"세션 #{id}이(가) 없습니다.");
        var report = SessionAnalyzer.Analyze(data, DetectionFrom(settings));
        if (string.Equals(options.Get("format"), "csv", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var file in SessionExporter.ExportSessionCsv(data, report, output))
            {
                Console.WriteLine(file);
            }
        }
        else
        {
            SessionExporter.ExportSessionJson(data, report, output);
            Console.WriteLine(output);
        }

        return 0;
    }

    private static int Compare(Options options)
    {
        var (paths, settings) = Load(options);
        var ids = (options.Get("sessions") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1).ToList();
        if (ids.Count < 2 || ids.Any(i => i < 0))
        {
            return UsageError("compare에는 --sessions ID,ID 형식으로 세션 2개 이상이 필요합니다.");
        }

        using var store = TelemetryStore.Open(paths.DatabasePath);
        var detection = DetectionFrom(settings);
        var reports = ids.Select(id => SessionAnalyzer.Analyze(store.LoadSessionData(id) ?? throw new ArgumentException($"세션 #{id}이(가) 없습니다."), detection)).ToList();
        var baseline = options.GetLong("baseline") is long b ? Math.Max(0, ids.IndexOf(b)) : 0;
        var comparison = SessionComparer.Compare(reports, baseline);
        Console.WriteLine($"기준: #{reports[baseline].Session.Id} {reports[baseline].Session.Name}");
        foreach (var row in comparison.Rows)
        {
            var cells = string.Join("  ", row.PerSession.Select((s, i) => $"#{reports[i].Session.Id} {row.Metric.FormatValue(s.Mean)}" +
                (i == baseline ? "" : $" ({NumberText.Delta(row.DeltaPercent[i])})")));
            Console.WriteLine($"  {row.Metric.DisplayName} [{row.Metric.Unit}, {row.Metric.OriginLabel}]: {cells}{(row.Note is null ? "" : $"  ※ {row.Note}")}");
        }

        foreach (var warning in comparison.Warnings)
        {
            Console.WriteLine($"주의: {warning.Message}");
        }

        if (options.Get("out") is string output)
        {
            if (output.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                SessionExporter.ExportComparisonCsv(comparison, output);
            }
            else
            {
                SessionExporter.ExportComparisonJson(comparison, output);
            }

            Console.WriteLine(output);
        }

        return 0;
    }

    private static int Backup(Options options)
    {
        var (paths, _) = Load(options);
        var output = options.Get("out") ?? Path.Combine(paths.BackupDirectory, $"lapc-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        using var store = TelemetryStore.Open(paths.DatabasePath);
        store.BackupTo(output, overwrite: options.Has("force"));
        var validation = TelemetryStore.ValidateBackup(output);
        Console.WriteLine($"백업 완료: {output} (검증: {validation.Message}, 세션 {validation.Sessions}개)");
        return validation.IsValid ? 0 : 1;
    }

    private static int Restore(Options options)
    {
        var (paths, _) = Load(options);
        if (options.Get("from") is not string source)
        {
            return UsageError("restore에는 --from PATH가 필요합니다.");
        }

        var validation = TelemetryStore.ValidateBackup(source);
        Console.WriteLine($"백업 검증: {validation.Message}, 세션 {validation.Sessions}개, 기간 {validation.Oldest?.ToLocalTime():yyyy-MM-dd} ~ {validation.Newest?.ToLocalTime():yyyy-MM-dd}");
        if (!validation.IsValid)
        {
            return 1;
        }

        if (!options.Has("yes"))
        {
            Console.WriteLine("현재 데이터베이스를 이 백업으로 바꾸려면 --yes를 붙여 다시 실행하세요. (현재 DB는 삭제되지 않고 옆에 보관됩니다.) 앱이 실행 중이면 먼저 종료하세요.");
            return 2;
        }

        var aside = TelemetryStore.RestoreFrom(source, paths.DatabasePath);
        Console.WriteLine($"복원 완료. 이전 데이터베이스: {aside ?? "(없었음)"}");
        return 0;
    }

    private static int Check(Options options)
    {
        var (paths, _) = Load(options);
        using var store = TelemetryStore.Open(paths.DatabasePath);
        if (store.OpenNotice is string notice)
        {
            Console.WriteLine($"알림: {notice}");
        }

        var result = store.IntegrityCheck();
        var stats = store.GetStats();
        Console.WriteLine($"무결성 검사: {result}");
        Console.WriteLine($"파일: {stats.Path} ({stats.FileBytes / 1024.0 / 1024:0.0} MB, WAL {stats.WalBytes / 1024.0 / 1024:0.0} MB, 사용 {stats.UsedBytes / 1024.0 / 1024:0.0} MB)");
        Console.WriteLine($"세션 {stats.Sessions}, 시스템 샘플 {stats.SystemSamples}, GPU 샘플 {stats.GpuSamples}, 런타임 샘플 {stats.RuntimeSamples}, 요청 {stats.Requests}, 이벤트 {stats.Events}");
        return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    private static int Retention(Options options)
    {
        var (paths, settings) = Load(options);
        using var store = TelemetryStore.Open(paths.DatabasePath);
        var result = store.ApplyRetention(settings.Retention.RetentionDays, (long)settings.Retention.MaxDatabaseSizeMb * 1024 * 1024, null);
        Console.WriteLine($"보존 정책 적용: 세션 {result.SessionsDeleted}개, 행 {result.RowsDeleted}개 삭제, 사용량 {result.BytesBefore / 1048576.0:0.0} → {result.BytesAfter / 1048576.0:0.0} MB");
        foreach (var note in result.Notes)
        {
            Console.WriteLine($"  {note}");
        }

        return 0;
    }

    internal static DetectionOptions DetectionFrom(AppSettings settings)
        => new(settings.Analysis.DegradationThresholdPercent, TimeSpan.FromSeconds(settings.Analysis.MinDegradationSeconds),
            TimeSpan.FromSeconds(settings.Analysis.BaselineWindowSeconds), settings.Analysis.HighGpuTempC);

    private static string Fmt(double? value, string format) => value is double v ? v.ToString(format, CultureInfo.InvariantCulture) : "N/A";

    private static string Gib(long? bytes) => bytes is long b ? (b / 1024.0 / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) : "N/A";

    private static string Bool(bool? value) => value switch { true => "예", false => "아니오", null => "N/A" };

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "mock-sensors", "no-strata", "force", "yes" };

        public string? Error { get; private set; }

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            var list = args.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                if (!list[i].StartsWith("--", StringComparison.Ordinal))
                {
                    options.Error = $"알 수 없는 인수: {list[i]}";
                    return options;
                }

                var name = list[i][2..];
                if (Flags.Contains(name))
                {
                    options._values[name] = null;
                }
                else if (i + 1 < list.Count)
                {
                    options._values[name] = list[++i];
                }
                else
                {
                    options.Error = $"--{name} 값이 없습니다.";
                    return options;
                }
            }

            return options;
        }

        public bool Has(string name) => _values.ContainsKey(name);

        public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

        public int? GetInt(string name) => int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        public long? GetLong(string name) => long.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
