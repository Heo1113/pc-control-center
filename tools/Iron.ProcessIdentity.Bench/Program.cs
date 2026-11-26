// IRON process identity — measurement tool (Windows, run as a standard user).
//
//   measure --mode baseline|identity --minutes 5 --out <file.json>   CPU / memory / GDI of this process, 1 Hz process-list loop
//                                                                     (identity = the same loop + ProcessIdentityResolver)
//   samples --work <dir> --python <venv python.exe> --node <node.exe> --out <file.json>
//                                                                     starts its own test processes and records identity before/after
//   report  --baseline <b.json> --identity <i.json> --samples <s.json> --out <report.md>
//
// Only processes this tool started are stopped; nothing else is touched. Command lines appear only redacted.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Iron.ProcessIdentity;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!OperatingSystem.IsWindows() || args.Length == 0)
        {
            Console.Error.WriteLine("Windows 전용: measure | samples | report");
            return 1;
        }

        string? Opt(string n) => Array.IndexOf(args, n) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        return args[0] switch
        {
            "measure" => Measure(Opt("--mode") ?? "baseline", double.Parse(Opt("--minutes") ?? "5", CultureInfo.InvariantCulture), Opt("--out")!),
            "samples" => Samples(Opt("--work")!, Opt("--python"), Opt("--node"), Opt("--out")!),
            "report" => Report(Opt("--baseline")!, Opt("--identity")!, Opt("--samples")!, Opt("--out")!),
            _ => 1,
        };
    }

    private static object Environment_()
    {
        var elevated = false;
        if (OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0008, out var token))
        {
            elevated = GetTokenInformation(token, 20, out var e, 4, out _) && e != 0; // TokenElevation
            CloseHandle(token);
        }

        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        return new
        {
            elevated,
            administratorsGroupActive = admin,
            os = RuntimeInformation.OSDescription,
            cpus = Environment.ProcessorCount,
            runtime = RuntimeInformation.FrameworkDescription,
            uiCulture = CultureInfo.CurrentUICulture.Name,
        };
    }

    // ---------- measure ----------
    private sealed record Sample(double T, double CpuPct, double PrivateMb, double WorkingSetMb, double ManagedMb, uint Gdi, uint User, int Handles, int Processes, int Identified);

    private static int Measure(string mode, double minutes, string outPath)
    {
        var identity = mode == "identity";
        using var query = new WindowsProcessQuery();
        using var resolver = identity ? new ProcessIdentityResolver(new WindowsProcessQuery(), new ResolverOptions { Workers = 1 }) : null;
        var me = Process.GetCurrentProcess();
        var samples = new List<Sample>();
        var sw = Stopwatch.StartNew();
        var lastCpu = me.TotalProcessorTime;
        var lastT = sw.Elapsed;
        var nextSample = TimeSpan.FromSeconds(5);
        var end = TimeSpan.FromMinutes(minutes);
        var tick = 0;
        while (sw.Elapsed < end)
        {
            var snap = query.Snapshot(); // the polling loop's own work in both modes
            var identified = 0;
            if (resolver is not null)
            {
                resolver.Observe(snap);
                foreach (var e in snap)
                {
                    if (resolver.TryGet(e.Key, out _))
                    {
                        identified++;
                    }
                }
            }

            tick++;
            if (sw.Elapsed >= nextSample)
            {
                me.Refresh();
                var cpu = me.TotalProcessorTime;
                var now = sw.Elapsed;
                var pct = (cpu - lastCpu).TotalMilliseconds / (now - lastT).TotalMilliseconds / Environment.ProcessorCount * 100;
                samples.Add(new Sample(Math.Round(now.TotalSeconds, 1), Math.Round(pct, 3), Mb(me.PrivateMemorySize64), Mb(me.WorkingSet64), Mb(GC.GetTotalMemory(false)),
                    GetGuiResources(me.Handle, 0), GetGuiResources(me.Handle, 1), me.HandleCount, snap.Count, identified));
                lastCpu = cpu;
                lastT = now;
                nextSample += TimeSpan.FromSeconds(5);
            }

            var wait = TimeSpan.FromSeconds(tick) - sw.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
            }
        }

        var steady = samples.Where(s => s.T >= 30).ToList();
        var result = new
        {
            mode,
            minutes,
            environment = Environment_(),
            summary = new
            {
                avgCpuPct = Avg(steady, s => s.CpuPct, 3),
                maxCpuPct = steady.Count > 0 ? steady.Max(s => s.CpuPct) : 0,
                avgPrivateMb = Avg(steady, s => s.PrivateMb, 1),
                maxPrivateMb = samples.Count > 0 ? samples.Max(s => s.PrivateMb) : 0,
                avgWorkingSetMb = Avg(steady, s => s.WorkingSetMb, 1),
                gdiFirst = samples.FirstOrDefault()?.Gdi ?? 0,
                gdiLast = samples.LastOrDefault()?.Gdi ?? 0,
                gdiMax = samples.Count > 0 ? samples.Max(s => s.Gdi) : 0,
                userMax = samples.Count > 0 ? samples.Max(s => s.User) : 0,
                handlesFirst = samples.FirstOrDefault()?.Handles ?? 0,
                handlesLast = samples.LastOrDefault()?.Handles ?? 0,
                processes = samples.LastOrDefault()?.Processes ?? 0,
                identified = samples.LastOrDefault()?.Identified ?? 0,
                steadyFrom = "30 s (JIT·초기 해석 제외)",
            },
            resolver = resolver?.Stats,
            fileCache = resolver is null ? null : new { resolver.Files.Count, resolver.Files.Capacity, resolver.Files.Hits, resolver.Files.Evictions, resolver.Files.Reloads },
            samples,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, Json));
        Console.WriteLine($"[{mode}] {minutes} 분 · 평균 CPU {result.summary.avgCpuPct}% · 평균 Private {result.summary.avgPrivateMb} MB · GDI {result.summary.gdiFirst}→{result.summary.gdiLast} (최대 {result.summary.gdiMax})");
        return 0;
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);
    private static double Avg<T>(List<T> l, Func<T, double> f, int d) => l.Count == 0 ? 0 : Math.Round(l.Average(f), d);

    // ---------- samples ----------
    private static int Samples(string work, string? python, string? node, string outPath)
    {
        Directory.CreateDirectory(work);
        var started = new List<Process>();
        var startedAt = DateTime.Now;
        var log = new List<string>();
        void Start(string file, string arguments, string label)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work });
                if (p is not null)
                {
                    started.Add(p);
                    log.Add($"{label}: 시작 PID {p.Id}");
                }
            }
            catch (Exception ex)
            {
                log.Add($"{label}: 시작 실패 ({ex.GetType().Name})");
            }
        }

        // Test programs (all owned by this tool).
        var jobs = Directory.CreateDirectory(Path.Combine(work, "jobs")).FullName;
        File.WriteAllText(Path.Combine(jobs, "nightly-backup.bat"), "@echo off\r\nping -n 90 127.0.0.1 >nul\r\n");
        File.WriteAllText(Path.Combine(jobs, "sync-photos.ps1"), "Start-Sleep -Seconds 90\r\n");
        Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c \"{Path.Combine(jobs, "nightly-backup.bat")}\" --password=hunter2", "cmd");
        Start(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(jobs, "sync-photos.ps1")}\"", "powershell");
        if (python is not null && File.Exists(python))
        {
            var src = Directory.CreateDirectory(Path.Combine(work, "ml-lab", "src")).FullName;
            File.WriteAllText(Path.Combine(src, "train.py"), "import time\ntime.sleep(90)\n");
            Start(python, $"\"{Path.Combine(src, "train.py")}\" --epochs 3 --api-key=sk-test-1234567890abcdef", "python(venv)");
        }

        if (node is not null && File.Exists(node))
        {
            var app = Directory.CreateDirectory(Path.Combine(work, "web-shop")).FullName;
            File.WriteAllText(Path.Combine(app, "package.json"), "{ \"name\": \"web-shop\", \"version\": \"1.0.0\" }");
            File.WriteAllText(Path.Combine(app, "server.js"), "setTimeout(() => {}, 90000);\n");
            Start(node, $"\"{Path.Combine(app, "server.js")}\" --token=abc.def.ghi", "node");
        }

        var wv = Directory.GetFiles(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application", "msedgewebview2.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (wv is not null)
        {
            Start(wv, $"--headless --no-first-run --webview-exe-name=IronBenchHost.exe --user-data-dir=\"{Path.Combine(work, "IronBenchHost", "EBWebView")}\" about:blank", "msedgewebview2");
        }
        else
        {
            log.Add("msedgewebview2: 런타임 없음");
        }

        Start(Path.Combine(Environment.SystemDirectory, "dllhost.exe"), "/Processid:{AB8902B4-09CA-4BB6-B78D-A8F59079A8D5}", "dllhost(썸네일 캐시)");

        Thread.Sleep(2500);
        using var query = new WindowsProcessQuery();
        using var resolver = new ProcessIdentityResolver(new WindowsProcessQuery(), new ResolverOptions { Workers = 2 });
        var deadline = DateTime.UtcNow.AddSeconds(8); // includes the 3 s second module check of dllhost
        IReadOnlyList<ProcessEntry> snap = [];
        while (DateTime.UtcNow < deadline)
        {
            snap = query.Snapshot();
            resolver.Observe(snap);
            Thread.Sleep(500);
        }

        var mine = started.Where(p => !HasExited(p)).Select(p => p.Id).ToHashSet();
        var webviews = snap.Where(e => e.ImageName.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)
                                       && (mine.Contains(e.Pid) || mine.Contains(e.ParentPid) || snap.Any(x => x.Pid == e.ParentPid && x.ImageName.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase) && mine.Contains(x.ParentPid))))
            .Take(4).ToList();
        var svchosts = snap.Where(e => e.ImageName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)).Take(40)
            .Where(e => resolver.TryGet(e.Key, out var i) && i.Hosted.Count > 0).Take(6);
        var others = snap.Where(e => e.ImageName is "taskhostw.exe" or "WmiPrvSE.exe" or "dllhost.exe" or "explorer.exe" or "dwm.exe" or "RuntimeBroker.exe" && !mine.Contains(e.Pid)).Take(4);
        var picked = snap.Where(e => mine.Contains(e.Pid) && !e.ImageName.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase))
            .Concat(webviews).Concat(svchosts).Concat(others).DistinctBy(e => e.Key).ToList();

        var rows = picked.Select(e =>
        {
            resolver.TryGet(e.Key, out var info);
            return new
            {
                before = e.ImageName,
                ownedByTool = mine.Contains(e.Pid) || webviews.Any(w => w.Key == e.Key),
                pid = e.Pid,
                after = info is null ? null : new
                {
                    title = info.Title.Text + ProcessIdentityInfo.Mark(info.Title.Confidence),
                    role = info.Role?.Text,
                    executor = info.Executor is null ? null : info.Executor.Text + ProcessIdentityInfo.Mark(info.Executor.Confidence),
                    product = info.ProductName,
                    company = info.CompanyName,
                    sharedHost = info.IsSharedHost,
                    hosted = info.Hosted.Select(h => $"{h.Kind}: {h.Name}{ProcessIdentityInfo.Mark(h.Confidence)}").ToList(),
                    hasIcon = info.Icon is not null,
                    redactedCommandLine = mine.Contains(e.Pid) ? info.RedactedCommandLine : null,
                    notes = info.Notes,
                },
            };
        }).ToList();

        foreach (var p in started)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // already gone
            }
        }

        var result = new { environment = Environment_(), startedAt, log, resolver = resolver.Stats, rows };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, Json));
        Console.WriteLine($"samples: {rows.Count}개 기록 · " + string.Join(" / ", log));
        return 0;
    }

    private static bool HasExited(Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // ---------- report ----------
    private static int Report(string baselinePath, string identityPath, string samplesPath, string outPath)
    {
        using var b = JsonDocument.Parse(File.ReadAllText(baselinePath));
        using var i = JsonDocument.Parse(File.ReadAllText(identityPath));
        using var s = JsonDocument.Parse(File.ReadAllText(samplesPath));
        var bs = b.RootElement.GetProperty("summary");
        var isum = i.RootElement.GetProperty("summary");
        var env = i.RootElement.GetProperty("environment");
        double D(JsonElement e, string n) => e.GetProperty(n).GetDouble();
        var sb = new StringBuilder();
        sb.AppendLine("# IRON 프로세스 식별 — 실측 리포트 (Windows 러너, 일반 사용자)").AppendLine();
        sb.AppendLine($"- 실행 환경: {env.GetProperty("os").GetString()} · CPU {env.GetProperty("cpus").GetInt32()}개 · {env.GetProperty("runtime").GetString()} · UI 언어 {env.GetProperty("uiCulture").GetString()}");
        sb.AppendLine($"- 권한: 상승(elevated) = {env.GetProperty("elevated").GetBoolean()}, Administrators 활성 = {env.GetProperty("administratorsGroupActive").GetBoolean()}");
        sb.AppendLine($"- 측정: 1초마다 프로세스 목록(NtQuerySystemInformation)을 읽는 루프 {D(i.RootElement, "minutes")}분씩. 기준선 = 목록만, 식별 = 같은 루프 + 비동기 식별. 5초 표본, 처음 30초 제외 평균.").AppendLine();
        sb.AppendLine("| 항목 | 기준선 | 식별 포함 | 차이 |").AppendLine("|---|---|---|---|");
        void Row(string label, string name, int d)
        {
            var x = D(bs, name);
            var y = D(isum, name);
            sb.AppendLine($"| {label} | {x.ToString("F" + d, CultureInfo.InvariantCulture)} | {y.ToString("F" + d, CultureInfo.InvariantCulture)} | {(y - x >= 0 ? "+" : "")}{(y - x).ToString("F" + d, CultureInfo.InvariantCulture)} |");
        }

        Row("평균 CPU % (전체 코어 대비)", "avgCpuPct", 3);
        Row("최대 CPU %", "maxCpuPct", 3);
        Row("평균 Private 메모리 MB", "avgPrivateMb", 1);
        Row("최대 Private 메모리 MB", "maxPrivateMb", 1);
        Row("평균 Working Set MB", "avgWorkingSetMb", 1);
        Row("GDI 객체(처음)", "gdiFirst", 0);
        Row("GDI 객체(마지막)", "gdiLast", 0);
        Row("GDI 객체(최대)", "gdiMax", 0);
        Row("핸들(마지막)", "handlesLast", 0);
        sb.AppendLine().AppendLine($"- 식별된 프로세스: {isum.GetProperty("identified").GetInt32()} / {isum.GetProperty("processes").GetInt32()}");
        if (i.RootElement.TryGetProperty("resolver", out var rs) && rs.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine($"- 해석기: 큐 투입 {rs.GetProperty("Enqueued").GetInt64()} · 큐 가득 참으로 보류 {rs.GetProperty("Dropped").GetInt64()} · 완료 {rs.GetProperty("Resolved").GetInt64()} · 오류 {rs.GetProperty("Failed").GetInt64()} · 서비스 스냅샷 갱신 {rs.GetProperty("ServiceRefreshes").GetInt32()}회");
        }

        if (i.RootElement.TryGetProperty("fileCache", out var fc) && fc.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine($"- 파일 캐시: {fc.GetProperty("Count").GetInt32()} / {fc.GetProperty("Capacity").GetInt32()}개 · 적중 {fc.GetProperty("Hits").GetInt64()} · 크기 제한으로 제거 {fc.GetProperty("Evictions").GetInt64()} · 파일 변경으로 재조회 {fc.GetProperty("Reloads").GetInt64()}");
        }

        sb.AppendLine().AppendLine("## 식별 전 / 후").AppendLine();
        sb.AppendLine("식별 전 = 기존 행이 보여 주는 실행 파일 이름. (추정) 표시가 없는 항목은 시스템에서 확인한 값입니다.").AppendLine();
        sb.AppendLine("| 식별 전 | 식별 후 — 이름 | 실행 주체 | 공유 호스트 · 보조 정보 | 아이콘 |").AppendLine("|---|---|---|---|---|");
        foreach (var r in s.RootElement.GetProperty("rows").EnumerateArray())
        {
            var a = r.GetProperty("after");
            if (a.ValueKind != JsonValueKind.Object)
            {
                sb.AppendLine($"| {r.GetProperty("before").GetString()} | (해석 전 종료) | | | |");
                continue;
            }

            var hosted = string.Join("; ", a.GetProperty("hosted").EnumerateArray().Take(4).Select(h => h.GetString()));
            var notes = string.Join("; ", a.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
            sb.AppendLine($"| {r.GetProperty("before").GetString()} | {Esc(a.GetProperty("title").GetString())} | {Esc(a.GetProperty("executor").GetString())} | "
                          + $"{(a.GetProperty("sharedHost").GetBoolean() ? "공유 호스트 · " : "")}{Esc(hosted)}{(notes.Length > 0 ? " · " + Esc(notes) : "")} | {(a.GetProperty("hasIcon").GetBoolean() ? "있음" : "없음")} |");
        }

        sb.AppendLine().AppendLine("### 이 도구가 시작한 프로세스의 명령줄(민감정보 제거 후)").AppendLine();
        foreach (var r in s.RootElement.GetProperty("rows").EnumerateArray())
        {
            if (r.GetProperty("after") is { ValueKind: JsonValueKind.Object } a && a.GetProperty("redactedCommandLine") is { ValueKind: JsonValueKind.String } c)
            {
                sb.AppendLine($"- `{r.GetProperty("before").GetString()}`: `{c.GetString()}`");
            }
        }

        sb.AppendLine().AppendLine("- 시작 기록: " + string.Join(" / ", s.RootElement.GetProperty("log").EnumerateArray().Select(x => x.GetString())));
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine(sb.ToString());
        return 0;
    }

    private static string Esc(string? s) => (s ?? "").Replace("|", "\\|", StringComparison.Ordinal);
}
