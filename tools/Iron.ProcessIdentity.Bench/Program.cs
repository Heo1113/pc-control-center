// IRON process identity — measurement tool (Windows, run as a standard user).
//
//   measure --mode baseline|identity --minutes 5 --out <file.json>   CPU / memory / GDI of this process, 1 Hz process-list loop
//                                                                     (identity = the same loop + ProcessIdentityResolver)
//   samples --work <dir> --python <venv python.exe> --node <node.exe> --out <file.json>
//                                                                     starts its own test processes and records identity before/after
//   report  --baseline <b.json> --identity <i.json> --samples <s.json> --out <report.md>
//   soak    --minutes 720 --out <file.json> [--churn-seconds 5] [--warmup-minutes 10]  long run with the resolver while short processes come
//                                                                     and go; handles by type, GDI, memory every minute, slopes per hour
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
            "soak" => Soak(double.Parse(Opt("--minutes") ?? "20", CultureInfo.InvariantCulture), Opt("--out")!, int.Parse(Opt("--churn-seconds") ?? "5", CultureInfo.InvariantCulture),
                double.Parse(Opt("--warmup-minutes") ?? "10", CultureInfo.InvariantCulture)),
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
        var startCpu = me.TotalProcessorTime;
        TimeSpan? cpuAt30 = null;
        double? allIdentifiedAt = null;
        SortedDictionary<string, int>? typesAt30 = null;
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
            if (resolver is not null && allIdentifiedAt is null && identified == snap.Count)
            {
                allIdentifiedAt = Math.Round(sw.Elapsed.TotalSeconds, 1);
            }

            if (cpuAt30 is null && sw.Elapsed >= TimeSpan.FromSeconds(30))
            {
                me.Refresh();
                cpuAt30 = me.TotalProcessorTime;
                typesAt30 = HandleCensus.Take();
            }

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
        var typesEnd = HandleCensus.Take();
        me.Refresh();
        var cpuEnd = me.TotalProcessorTime;
        var elapsed = sw.Elapsed;
        // Whole-period CPU: much finer than 5 s samples (Windows CPU time advances in ~15.6 ms steps).
        var steadyCpu = cpuAt30 is { } c30 ? (cpuEnd - c30).TotalMilliseconds / (elapsed - TimeSpan.FromSeconds(30)).TotalMilliseconds / Environment.ProcessorCount * 100 : 0;
        var warmupCpu = cpuAt30 is { } w30 ? (w30 - startCpu).TotalMilliseconds / 30000.0 / Environment.ProcessorCount * 100 : 0;
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
                steadyCpuPctFromTotals = Math.Round(steadyCpu, 4),
                warmupCpuPctFirst30s = Math.Round(warmupCpu, 3),
                allIdentifiedAtS = allIdentifiedAt,
            },
            // which kinds of handles changed between 30 s and the end (the increase seen in run 3-10: +17 to +18 in 5 min)
            handleTypes = new
            {
                at30s = typesAt30,
                end = typesEnd,
                change = typesEnd.Keys.Union(typesAt30?.Keys ?? Enumerable.Empty<string>())
                    .Select(k => (k, d: typesEnd.GetValueOrDefault(k) - (typesAt30?.GetValueOrDefault(k) ?? 0))).Where(x => x.d != 0)
                    .ToDictionary(x => x.k, x => x.d),
                threads = me.Threads.Count,
            },
            resolver = resolver?.Stats,
            fileCache = resolver is null ? null : new { resolver.Files.Count, resolver.Files.Capacity, resolver.Files.Hits, resolver.Files.Evictions, resolver.Files.Reloads },
            samples,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, Json));
        Console.WriteLine($"[{mode}] {minutes} 분 · 평균 CPU {result.summary.avgCpuPct}% · 평균 Private {result.summary.avgPrivateMb} MB · GDI {result.summary.gdiFirst}→{result.summary.gdiLast} (최대 {result.summary.gdiMax})");
        return 0;
    }

    private sealed record SoakSample(double Minute, int Handles, int Threads, uint Gdi, uint User, double PrivateMb, double ManagedMb, double CpuPct, int Processes,
        int Identified, long Started, SortedDictionary<string, int> Types, int Gen2 = 0, double CommittedMb = 0, int ProcessCache = 0, int FileCache = 0);

    /// <summary>Memory after a forced full, compacting collection: what is still referenced (managed) and what the process keeps.</summary>
    private sealed record GcCheckpoint(double Minute, double PrivateBeforeMb, double ManagedBeforeMb, double PrivateAfterMb, double ManagedAfterMb, int ProcessCache, int FileCache);

    private static GcCheckpoint ForcedGc(Process me, TimeSpan now, ProcessIdentityResolver resolver)
    {
        me.Refresh();
        var privateBefore = Mb(me.PrivateMemorySize64);
        var managedBefore = Mb(GC.GetTotalMemory(false));
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Thread.Sleep(1000);
        me.Refresh();
        return new GcCheckpoint(Math.Round(now.TotalMinutes, 2), privateBefore, managedBefore, Mb(me.PrivateMemorySize64), Mb(GC.GetTotalMemory(false)), resolver.Cache.Count, resolver.Files.Count);
    }

    /// <summary>
    /// Long run: the identity resolver on the 1 Hz process list while this tool starts a short process every few seconds
    /// (new keys, evictions, file cache, service refreshes). Every minute: handles by type, threads, GDI/USER, memory.
    /// Slopes per hour are fitted after the warm-up (10 min by default, as Analyze-SoakLog.ps1 does for the IRON window; at most
    /// half of a short run): the file and icon caches fill during the first minutes (CI run 4: Private 20 -> 28.5 MB from
    /// minute 2 to 5, then flat), which a 2-minute warm-up counted as growth. A leak shows as a steady slope, a plateau as ~0.
    /// </summary>
    private static int Soak(double minutes, string outPath, int churnSeconds, double warmupMinutes)
    {
        using var query = new WindowsProcessQuery();
        using var resolver = new ProcessIdentityResolver(new WindowsProcessQuery(), new ResolverOptions { Workers = 1 });
        var me = Process.GetCurrentProcess();
        var sw = Stopwatch.StartNew();
        var end = TimeSpan.FromMinutes(minutes);
        var samples = new List<SoakSample>();
        var nextSample = TimeSpan.Zero;
        var nextChurn = TimeSpan.FromSeconds(churnSeconds);
        var lastCpu = me.TotalProcessorTime;
        var lastT = TimeSpan.Zero;
        long started = 0;
        var tick = 0;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        // One forced full GC at the end of the warm-up and one at the end: the managed memory still referenced after each tells
        // a leak (it grows between the two) from garbage the GC has not collected yet (CI runs 4 and 5 of the same code: Private
        // flat at ~29 MB vs. rising 39 -> 67 MB over minutes 9-19).
        var checkpoints = new List<GcCheckpoint>();
        var checkpointAt = TimeSpan.FromMinutes(Math.Min(warmupMinutes, minutes / 2));
        while (sw.Elapsed < end)
        {
            var snap = query.Snapshot();
            resolver.Observe(snap);
            if (churnSeconds > 0 && sw.Elapsed >= nextChurn)
            {
                // alternately a process that exits at once and one that lives ~2 s (seen, resolved, then evicted)
                using var p = Process.Start(new ProcessStartInfo(cmd, started % 2 == 0 ? "/c exit 0" : "/c ping -n 3 127.0.0.1 >nul")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                started++;
                nextChurn += TimeSpan.FromSeconds(churnSeconds);
            }

            if (sw.Elapsed >= nextSample)
            {
                me.Refresh();
                var cpu = me.TotalProcessorTime;
                var now = sw.Elapsed;
                var pct = now > lastT ? (cpu - lastCpu).TotalMilliseconds / (now - lastT).TotalMilliseconds / Environment.ProcessorCount * 100 : 0;
                var identified = snap.Count(e => resolver.TryGet(e.Key, out _));
                samples.Add(new SoakSample(Math.Round(now.TotalMinutes, 2), me.HandleCount, me.Threads.Count, GetGuiResources(me.Handle, 0), GetGuiResources(me.Handle, 1),
                    Mb(me.PrivateMemorySize64), Mb(GC.GetTotalMemory(false)), Math.Round(pct, 3), snap.Count, identified, started, HandleCensus.Take(),
                    GC.CollectionCount(2), Mb(GC.GetGCMemoryInfo().TotalCommittedBytes), resolver.Cache.Count, resolver.Files.Count));
                var sm = samples[^1];
                Console.WriteLine($"[soak] {now.TotalMinutes,6:F1} 분 · 핸들 {sm.Handles} · 스레드 {sm.Threads} · GDI {sm.Gdi} · Private {sm.PrivateMb} MB · 관리 힙 {sm.ManagedMb} MB"
                    + $" (GC 확보 {sm.CommittedMb} MB, 2세대 GC {sm.Gen2}회) · 캐시 프로세스 {sm.ProcessCache}/파일 {sm.FileCache} · 실행 중 {sm.Processes} · 시작한 프로세스 {started}");
                lastCpu = cpu;
                lastT = now;
                nextSample += TimeSpan.FromMinutes(1);
            }

            if (checkpoints.Count == 0 && sw.Elapsed >= checkpointAt)
            {
                checkpoints.Add(ForcedGc(me, sw.Elapsed, resolver));
                PrintCheckpoint(checkpoints[^1]);
            }

            tick++;
            var wait = TimeSpan.FromSeconds(tick) - sw.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
            }
        }

        checkpoints.Add(ForcedGc(me, sw.Elapsed, resolver));
        PrintCheckpoint(checkpoints[^1]);
        static void PrintCheckpoint(GcCheckpoint c) => Console.WriteLine(
            $"[soak] 강제 전체 GC {c.Minute:F1} 분: Private {c.PrivateBeforeMb} → {c.PrivateAfterMb} MB · 관리 힙 {c.ManagedBeforeMb} → {c.ManagedAfterMb} MB (GC 후 남은 것 = 참조 중) · 캐시 프로세스 {c.ProcessCache}/파일 {c.FileCache}");
        var warmup = Math.Min(warmupMinutes, minutes / 2);
        // strictly after the warm-up: the sample at the warm-up minute is taken just before the forced GC checkpoint
        var after = samples.Where(s => s.Minute > warmup).ToList();
        double PerHour(Func<SoakSample, double> f) => Math.Round(HandleCensus.Slope(after.Select(s => (s.Minute, f(s))).ToList()) * 60, 2);
        var types = after.SelectMany(s => s.Types.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new { type = k, first = after.First().Types.GetValueOrDefault(k), last = after.Last().Types.GetValueOrDefault(k), perHour = PerHour(s => s.Types.GetValueOrDefault(k)) })
            .Where(t => t.first != t.last || t.perHour != 0).ToList();
        var handlesPerHour = PerHour(s => s.Handles);
        var result = new
        {
            mode = "soak",
            minutes,
            churnSeconds,
            environment = Environment_(),
            summary = new
            {
                processesStarted = started,
                handlesFirst = after.FirstOrDefault()?.Handles,
                handlesLast = after.LastOrDefault()?.Handles,
                handlesMax = after.Count > 0 ? after.Max(s => s.Handles) : 0,
                handlesPerHour,
                threadsFirst = after.FirstOrDefault()?.Threads,
                threadsLast = after.LastOrDefault()?.Threads,
                gdiFirst = after.FirstOrDefault()?.Gdi,
                gdiLast = after.LastOrDefault()?.Gdi,
                gdiPerHour = PerHour(s => s.Gdi),
                privateMbFirst = after.FirstOrDefault()?.PrivateMb,
                privateMbLast = after.LastOrDefault()?.PrivateMb,
                privateMbPerHour = PerHour(s => s.PrivateMb),
                managedMbPerHour = PerHour(s => s.ManagedMb),
                // the decisive leak number: managed memory still referenced after a full GC, end vs. end of warm-up
                managedAfterGcFirstMb = checkpoints.Count > 1 ? checkpoints[0].ManagedAfterMb : (double?)null,
                managedAfterGcLastMb = checkpoints[^1].ManagedAfterMb,
                privateAfterGcFirstMb = checkpoints.Count > 1 ? checkpoints[0].PrivateAfterMb : (double?)null,
                privateAfterGcLastMb = checkpoints[^1].PrivateAfterMb,
                avgCpuPct = after.Count > 0 ? Math.Round(after.Average(s => s.CpuPct), 3) : 0,
                warmupMinutes = warmup,
                note = $"{warmup}분(강제 GC 지점) 이후 1분 표본의 최소제곱 기울기(시간당). 누수는 꾸준한 양의 기울기로, 안정 상태는 0 근처로 나타남. 메모리 판단은 강제 GC 후 남은 관리 힙(처음 → 끝)으로 함.",
            },
            handleTypes = types,
            resolver = resolver.Stats,
            fileCache = new { resolver.Files.Count, resolver.Files.Capacity, resolver.Files.Hits, resolver.Files.Evictions, resolver.Files.Reloads },
            gcCheckpoints = checkpoints,
            samples = samples.Select(s => new { s.Minute, s.Handles, s.Threads, s.Gdi, s.User, s.PrivateMb, s.ManagedMb, s.CommittedMb, s.Gen2, s.ProcessCache, s.FileCache, s.CpuPct, s.Processes, s.Identified, s.Started }),
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, Json));
        Console.WriteLine($"[soak] {minutes} 분 ({warmup}분 이후 기울기) · 핸들 기울기 {handlesPerHour}/시간 · GDI {result.summary.gdiFirst}→{result.summary.gdiLast} · Private 기울기 {result.summary.privateMbPerHour} MB/시간 · 관리 힙 기울기 {result.summary.managedMbPerHour} MB/시간");
        if (checkpoints.Count > 1)
        {
            Console.WriteLine($"[soak] 강제 GC 후 남은 메모리: 관리 힙 {checkpoints[0].ManagedAfterMb} → {checkpoints[^1].ManagedAfterMb} MB · Private {checkpoints[0].PrivateAfterMb} → {checkpoints[^1].PrivateAfterMb} MB ({checkpoints[0].Minute:F0}분 → {checkpoints[^1].Minute:F0}분)");
        }
        foreach (var t in types)
        {
            Console.WriteLine($"[soak]   {t.type}: {t.first} → {t.last} ({t.perHour}/시간)");
        }

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

        var wvRoot = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application";
        var wv = Directory.Exists(wvRoot) ? Directory.GetFiles(wvRoot, "msedgewebview2.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (wv is not null)
        {
            // Without a host app the WebView2 browser may refuse to stay up; two ways are tried and what survives is recorded.
            Start(wv, $"--headless=new --no-first-run --webview-exe-name=IronBenchHost.exe --user-data-dir=\"{Path.Combine(work, "IronBenchHost", "EBWebView")}\" about:blank", "msedgewebview2(headless)");
            Start(wv, $"--embedded-browser-webview=1 --no-first-run --webview-exe-name=IronBenchHost2.exe --user-data-dir=\"{Path.Combine(work, "IronBenchHost2", "EBWebView")}\" about:blank", "msedgewebview2(embedded)");
        }
        else
        {
            log.Add("msedgewebview2: 런타임 없음");
        }

        if (node is null || !File.Exists(node))
        {
            log.Add("node: 실행 파일 없음(" + (node ?? "경로 미지정") + ")");
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

        foreach (var p in started.Where(HasExited))
        {
            log.Add($"PID {p.Id}: 식별 전에 종료(종료 코드 {ExitCode(p)})");
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

        // Self-check: none of the secrets planted in the test command lines may appear anywhere in what is kept.
        var planted = new[] { "hunter2", "sk-test-1234567890abcdef", "abc.def.ghi" };
        var serializedRows = JsonSerializer.Serialize(rows, Json);
        var leaked = planted.Count(x => serializedRows.Contains(x, StringComparison.Ordinal));
        var result = new { environment = Environment_(), startedAt, log, resolver = resolver.Stats, secretCheck = new { planted = planted.Length, leaked }, rows };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, Json));
        Console.WriteLine($"samples: {rows.Count}개 기록 · " + string.Join(" / ", log));
        return 0;
    }

    private static string ExitCode(Process p)
    {
        try
        {
            return p.ExitCode.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return "?";
        }
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

        Row("CPU % 정밀(30초 이후 누적 CPU 시간 ÷ 경과)", "steadyCpuPctFromTotals", 4);
        Row("CPU % 첫 30초(초기 식별 포함)", "warmupCpuPctFirst30s", 3);
        Row("평균 CPU % (5초 표본)", "avgCpuPct", 3);
        Row("최대 CPU %", "maxCpuPct", 3);
        Row("평균 Private 메모리 MB", "avgPrivateMb", 1);
        Row("최대 Private 메모리 MB", "maxPrivateMb", 1);
        Row("평균 Working Set MB", "avgWorkingSetMb", 1);
        Row("GDI 객체(처음)", "gdiFirst", 0);
        Row("GDI 객체(마지막)", "gdiLast", 0);
        Row("GDI 객체(최대)", "gdiMax", 0);
        Row("핸들(처음)", "handlesFirst", 0);
        Row("핸들(마지막)", "handlesLast", 0);
        foreach (var (label, doc) in new[] { ("기준선", b), ("식별 포함", i) })
        {
            if (doc.RootElement.TryGetProperty("handleTypes", out var ht) && ht.TryGetProperty("change", out var ch))
            {
                var parts = ch.EnumerateObject().Select(x => $"{x.Name} {x.Value.GetInt32():+0;-0}").ToList();
                sb.AppendLine().Append($"- 핸들 종류별 변화(30초→끝, {label}): ").Append(parts.Count == 0 ? "변화 없음" : string.Join(", ", parts))
                  .Append($" · 스레드 {ht.GetProperty("threads").GetInt32()}개");
            }
        }

        sb.AppendLine();
        sb.AppendLine().AppendLine($"- 식별된 프로세스: {isum.GetProperty("identified").GetInt32()} / {isum.GetProperty("processes").GetInt32()}"
                                   + (isum.GetProperty("allIdentifiedAtS") is { ValueKind: JsonValueKind.Number } at ? $" · 전부 식별까지 {at.GetDouble():0.0}초" : ""));
        sb.AppendLine("- CPU 시간은 Windows에서 약 15.6 ms 단위로 늘어나므로 5초 표본 값은 해상도 한계 근처입니다. 비교는 '정밀' 행을 보십시오.");
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

            var parts = new List<string>();
            if (a.GetProperty("sharedHost").GetBoolean())
            {
                parts.Add("공유 호스트");
            }

            var hostedAll = a.GetProperty("hosted").EnumerateArray().Select(h => h.GetString()!).ToList();
            if (hostedAll.Count > 0)
            {
                parts.Add(string.Join("; ", hostedAll.Take(4)) + (hostedAll.Count > 4 ? $" 외 {hostedAll.Count - 4}개" : ""));
            }

            var notes = a.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToList();
            if (notes.Count > 0)
            {
                parts.Add("메모: " + string.Join("; ", notes));
            }

            sb.AppendLine($"| {r.GetProperty("before").GetString()} | {Esc(a.GetProperty("title").GetString())} | {Esc(a.GetProperty("executor").GetString())} | "
                          + $"{Esc(string.Join(" · ", parts))} | {(a.GetProperty("hasIcon").GetBoolean() ? "있음" : "없음")} |");
        }

        sb.AppendLine().AppendLine("### 이 도구가 시작한 프로세스의 명령줄(민감정보 제거 후)").AppendLine();
        foreach (var r in s.RootElement.GetProperty("rows").EnumerateArray())
        {
            if (r.GetProperty("after") is { ValueKind: JsonValueKind.Object } a && a.GetProperty("redactedCommandLine") is { ValueKind: JsonValueKind.String } c)
            {
                sb.AppendLine($"- `{r.GetProperty("before").GetString()}`: `{c.GetString()}`");
            }
        }

        if (s.RootElement.TryGetProperty("secretCheck", out var sc))
        {
            sb.AppendLine().AppendLine($"- 민감정보 자체 점검: 시험 명령줄에 심어 둔 비밀값 {sc.GetProperty("planted").GetInt32()}개 중 결과 파일에 남은 것 {sc.GetProperty("leaked").GetInt32()}개");
        }

        sb.AppendLine("- 시작 기록: " + string.Join(" / ", s.RootElement.GetProperty("log").EnumerateArray().Select(x => x.GetString())));
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine(sb.ToString());
        return 0;
    }

    private static string Esc(string? s) => (s ?? "").Replace("|", "\\|", StringComparison.Ordinal);
}
