// IRON Stage 2 RC3 — HWiNFO REST API PoC (.NET 10, 읽기 전용)
//
//   selftest [--root <RC3 폴더>] [--out <폴더>]         합성 픽스처로 JS 모델(tests/fixtures/expected)과 같은지 확인
//   live <url> <samples> <outDir> [--interval-ms 1000] [--gate G1|G3A] [--insecure-loopback-tls]
//                                                       실제 HWiNFO REST를 읽어 모델을 출력하고 수집 폴더를 남긴 뒤 analyze
//   analyze <runDir> [--root <RC3 폴더>]                수집 폴더(PowerShell 수집기 또는 live)를 G1/G2/G3A로 판정
//
// 종료 코드: 0 정상(판정 결과와 무관하게 실행 완료) · 1 사용법/정책 위반(포트 8080, 비-루프백) · 2 연결·버전 실패
//            3 수집 중 실패 · 4 selftest 불일치
// 보내는 요청은 GET /api/ping, /api/sensors/sdb, /api/sensors/read 뿐이다(POST·filter·logging·update_display 없음).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using IronPoc;

internal static class Program
{
    private static readonly string[] AllowedPaths = ["/api/ping", "/api/sensors/sdb", "/api/sensors/read"];
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0) return Usage();
        try
        {
            return args[0] switch
            {
                "selftest" => SelfTest(Opt(args, "--root"), Opt(args, "--out")),
                "analyze" when args.Length >= 2 => Analyze(args[1], Opt(args, "--root")),
                "live" when args.Length >= 4 => await Live(args),
                _ => Usage(),
            };
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine("[ERROR] " + ex.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("사용법: HWiNfoRestPoC selftest | live <url> <samples> <outDir> [--interval-ms N] [--gate G1|G3A] | analyze <runDir>");
        return 1;
    }

    private static string? Opt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>config/ 와 tests/fixtures/ 가 있는 RC3 폴더: 지정값 → 현재 폴더 → 실행 파일 위치에서 위로 찾기.</summary>
    private static string FindRoot(string? given)
    {
        if (given is not null) return Path.GetFullPath(given);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "config", "gates.json"))) return d.FullName;
            }
        }

        throw new InvalidDataException("config/gates.json 이 있는 RC3 폴더를 찾지 못했습니다. --root 로 지정하세요.");
    }

    private static JsonElement LoadJson(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();

    // ---------- selftest ----------
    private static readonly (string Name, string Read, string Sdb)[] Frames =
    [
        ("synthetic_full", "synthetic_full_read_v1.json", "synthetic_full_sdb_v1.json"),
        ("synthetic_mismatch", "synthetic_mismatch_read_v1.json", "synthetic_mismatch_sdb_v1.json"),
        ("rc2_synthetic", "synthetic_read_v1.json", "synthetic_sdb_v1.json"),
    ];

    private static readonly string[] Seqs = ["seq_latch_release", "seq_frozen", "seq_invalid", "seq_age"];

    private static int SelfTest(string? rootArg, string? outArg)
    {
        var root = FindRoot(rootArg);
        var cfg = IronConfig.Load(Path.Combine(root, "config"));
        var fx = Path.Combine(root, "tests", "fixtures");
        var outDir = outArg ?? Path.Combine(root, "results", "selftest_dotnet_SYNTHETIC");
        Directory.CreateDirectory(outDir);
        var results = new Dictionary<string, JsonObject>();

        foreach (var (name, read, sdb) in Frames)
        {
            var model = IronModel.MapFrame(IronModel.ParseRead(LoadJson(Path.Combine(fx, read))), IronModel.ParseSdb(LoadJson(Path.Combine(fx, sdb))), "SYNTHETIC", cfg);
            results[name + ".model.json"] = new JsonObject { ["fixture"] = name, ["model"] = model, ["g2"] = IronModel.EvaluateG2((JsonObject)model.DeepClone(), cfg) };
        }

        foreach (var name in Seqs)
        {
            var seq = LoadJson(Path.Combine(fx, name + ".json"));
            var sdb = IronModel.ParseSdb(LoadJson(Path.Combine(fx, seq.GetProperty("sdb").GetString()!)));
            var tr = new Tracker(cfg);
            if (seq.GetProperty("clock").ValueKind == JsonValueKind.Object)
            {
                var c = seq.GetProperty("clock");
                tr.SetClockOffset(c.GetProperty("pingT").GetDouble(), c.GetProperty("t0").GetDouble(), c.GetProperty("t3").GetDouble());
            }

            var frames = new JsonArray();
            foreach (var f in seq.GetProperty("frames").EnumerateArray())
            {
                var model = IronModel.MapFrame(IronModel.ParseRead(f.GetProperty("read")), sdb, seq.GetProperty("source").GetString()!, cfg);
                var t = tr.Update(model, f.GetProperty("receivedAtMs").GetDouble());
                var sum = IronModel.Summarize(model, t);
                frames.Add(new JsonObject
                {
                    ["i"] = f.GetProperty("i").GetInt32(),
                    ["freshness"] = t["freshness"]!.DeepClone(), ["ageMs"] = t["ageMs"]?.DeepClone(), ["frozenMs"] = t["frozenMs"]?.DeepClone(),
                    ["newMeasurement"] = t["newMeasurement"]!.DeepClone(), ["pinOverCount"] = t["pinOverCount"]!.DeepClone(), ["pinAlert"] = t["pinAlert"]!.DeepClone(),
                    ["gpuHot"] = Js.Arr(model["gpus"]!.AsArray().Select(g => g!["hot"]?.DeepClone())),
                    ["wireViewCompleteness"] = model["wireview"]!["completeness"]!.DeepClone(),
                    ["status"] = sum["status"]!.DeepClone(), ["wireViewStatus"] = sum["wireViewStatus"]!.DeepClone(),
                    ["reasons"] = t["reasons"]!.DeepClone(),
                });
            }

            results[name + ".seq.json"] = new JsonObject { ["fixture"] = name, ["invalidCount"] = tr.Invalid, ["frames"] = frames };
        }

        var failed = 0;
        foreach (var (file, obj) in results)
        {
            var text = obj.ToJsonString(Pretty);
            File.WriteAllText(Path.Combine(outDir, file), text + "\n", new UTF8Encoding(false));
            var expectedPath = Path.Combine(fx, "expected", file);
            var diffs = new List<string>();
            var same = File.Exists(expectedPath) && Js.DeepEqual(LoadJson(expectedPath), JsonDocument.Parse(text).RootElement, "$", diffs);
            if (!File.Exists(expectedPath)) diffs.Add("기대 파일 없음");
            Console.WriteLine($"[{(same ? "SAME" : "DIFF")}] {file}");
            foreach (var d in diffs.Take(10)) Console.WriteLine("       " + d);
            if (!same) failed++;
        }

        // 합성 수집 폴더(있으면) 분석: 출처가 SYNTHETIC이므로 모든 게이트는 UNVERIFIED여야 한다.
        var runFx = Path.Combine(fx, "run_synthetic");
        if (Directory.Exists(runFx))
        {
            var report = Gates.Report(Gates.Load(runFx), cfg);
            var bad = new[] { "G1", "G2", "G3A" }.Where(g => report[g]!["verdict"]!.GetValue<string>() is "PASS" or "FAIL").ToList();
            Console.WriteLine($"[{(bad.Count == 0 ? "SAME" : "DIFF")}] run_synthetic: 합성 수집은 PASS/FAIL 없이 UNVERIFIED " + (bad.Count == 0 ? "" : string.Join(",", bad)));
            if (bad.Count > 0) failed++;
        }

        Console.WriteLine(failed == 0 ? $"selftest 통과: .NET 결과가 JS 기대값과 같음 ({results.Count}개, SYNTHETIC)" : $"selftest 실패: {failed}개 불일치");
        return failed == 0 ? 0 : 4;
    }

    // ---------- analyze ----------
    private static int Analyze(string runDir, string? rootArg)
    {
        var root = FindRoot(rootArg);
        var cfg = IronConfig.Load(Path.Combine(root, "config"));
        var run = Gates.Load(runDir);
        var report = Gates.Report(run, cfg);
        File.WriteAllText(Path.Combine(runDir, "gate_report.json"), report.ToJsonString(Pretty) + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(runDir, "gate_report.md"), Gates.Markdown(report), new UTF8Encoding(false));
        foreach (var g in new[] { "G1", "G2", "G3A", "G3B" })
        {
            Console.WriteLine($"{g}: {report[g]!["verdict"]}");
        }

        Console.WriteLine("보고서: " + Path.Combine(runDir, "gate_report.md"));
        return 0;
    }

    // ---------- live ----------
    private static double NowMs() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

    private static string Compact(string json)
    {
        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            doc.RootElement.WriteTo(w);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<int> Live(string[] args)
    {
        var root = FindRoot(Opt(args, "--root"));
        var cfg = IronConfig.Load(Path.Combine(root, "config"));
        var baseUrl = args[1].TrimEnd('/');
        if (!int.TryParse(args[2], out var samples) || samples < 1) return Usage();
        var outDir = args[3];
        var interval = int.TryParse(Opt(args, "--interval-ms"), out var iv) ? iv : cfg.Gates.GetProperty("G1").GetProperty("intervalMs").GetInt32();
        var gate = Opt(args, "--gate") ?? "G1";
        var minPoll = cfg.Gates.GetProperty("hwinfo").GetProperty("minPollMs").GetInt32();
        if (interval < minPoll) { Console.Error.WriteLine($"[ERROR] 폴링 간격은 {minPoll} ms 이상이어야 합니다."); return 1; }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) { Console.Error.WriteLine("[ERROR] 잘못된 URL: " + baseUrl); return 1; }
        var forbidden = cfg.Gates.GetProperty("ports").GetProperty("forbidden").EnumerateArray().Select(x => x.GetInt32()).ToList();
        if (forbidden.Contains(uri.Port)) { Console.Error.WriteLine($"[ERROR] 포트 {uri.Port}는 Basalt 운영 포트입니다. 사용하지 않습니다."); return 1; }
        if (!uri.IsLoopback) { Console.Error.WriteLine("[ERROR] 루프백(127.0.0.1/localhost/::1)만 허용합니다. LAN 접근은 별도 승인 전 금지."); return 1; }

        var handler = new HttpClientHandler();
        if (uri.Scheme == "https" && args.Contains("--insecure-loopback-tls"))
        {
            // HWiNFO 기본 HTTPS는 자체 서명 인증서. 루프백 주소에 한해서만, 명시적으로 요청했을 때만 검증을 생략한다.
            handler.ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri is { IsLoopback: true };
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        async Task<(int Status, string Body, double T0, double T3)> Get(string path)
        {
            if (!AllowedPaths.Contains(path)) throw new InvalidOperationException("허용되지 않은 경로: " + path);
            var t0 = NowMs();
            using var resp = await http.GetAsync(baseUrl + path);
            var body = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode, body, t0, NowMs());
        }

        Directory.CreateDirectory(outDir);
        var started = DateTime.UtcNow;
        var pings = new StringBuilder();
        var pingOk = 0;
        for (var i = 0; i < 5; i++)
        {
            try
            {
                var (st, body, t0, t3) = await Get("/api/ping");
                using var d = JsonDocument.Parse(body);
                var ok = st == 200 && d.RootElement.TryGetProperty("pong", out var p) && p.ValueKind == JsonValueKind.True;
                double? t = d.RootElement.TryGetProperty("t", out var te) && te.ValueKind == JsonValueKind.Number ? te.GetDouble() : null;
                pings.AppendLine(new JsonObject { ["t0Ms"] = t0, ["t3Ms"] = t3, ["rttMs"] = t3 - t0, ["ok"] = ok, ["t"] = Js.N(t) }.ToJsonString());
                if (ok) pingOk++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                pings.AppendLine(new JsonObject { ["ok"] = false, ["error"] = ex.GetType().Name + ": " + ex.Message }.ToJsonString());
            }

            await Task.Delay(100);
        }

        File.WriteAllText(Path.Combine(outDir, "pings.jsonl"), pings.ToString());
        if (pingOk == 0) { Console.Error.WriteLine("[ERROR] /api/ping 응답 없음 — HWiNFO REST 서버가 켜져 있는지 확인하세요."); return 2; }

        var (sdbStatus, sdbBody, _, _) = await Get("/api/sensors/sdb");
        if (sdbStatus != 200) { Console.Error.WriteLine($"[ERROR] /api/sensors/sdb HTTP {sdbStatus}"); return 2; }
        var sdbJson = JsonDocument.Parse(sdbBody).RootElement.Clone();
        var sdb = IronModel.ParseSdb(sdbJson); // Version 1 확인
        File.WriteAllText(Path.Combine(outDir, "sdb.json"), sdbBody, new UTF8Encoding(false));
        Console.WriteLine($"[SDB] Version 1, 리딩 {sdb.Count}개");

        var manifest = new JsonObject
        {
            ["schema"] = "iron.rc3.run/1",
            ["source"] = "UNVERIFIED_ENDPOINT",
            ["sourceReason"] = "C# live 모드는 포트를 연 프로세스가 HWiNFO64인지 확인하지 않음 — 게이트 판정용 수집은 poc/Collect-HWiNfoRest.ps1 사용",
            ["collector"] = "HWiNfoRestPoC live (RC3)",
            ["gate"] = gate,
            ["url"] = baseUrl,
            ["startedAtUtc"] = started.ToString("o", CultureInfo.InvariantCulture),
            ["samplesRequested"] = samples,
            ["intervalMs"] = interval,
            ["endpointOwner"] = null,
            ["operatorAttestation"] = null,
            ["evidence"] = null,
            ["baselineFile"] = null,
        };

        var tracker = new Tracker(cfg);
        var (_, bestPing) = Gates.ClockOffset(File.ReadAllLines(Path.Combine(outDir, "pings.jsonl")).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList());
        if (bestPing is not null) tracker.SetClockOffset(bestPing["t"]!.GetValue<double>(), bestPing["t0Ms"]!.GetValue<double>(), bestPing["t3Ms"]!.GetValue<double>());
        var failures = 0;
        var synthetic = false; // 응답에 SYNTHETIC 표시(_notice)가 있으면 모의 서버 — 출처를 SYNTHETIC으로 기록
        var sw = Stopwatch.StartNew();
        await using (var w = new StreamWriter(Path.Combine(outDir, "samples_all.jsonl"), false, new UTF8Encoding(false)))
        {
            for (var i = 1; i <= samples; i++)
            {
                var wait = (i - 1) * (long)interval - sw.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay((int)wait);
                string line;
                try
                {
                    var (st, body, t0, t3) = await Get("/api/sensors/read");
                    var ok = st == 200;
                    var read = ok ? Compact(body) : "null";
                    line = $"{{\"i\":{i},\"t0Ms\":{Js.Num(t0)},\"t3Ms\":{Js.Num(t3)},\"rttMs\":{Js.Num(t3 - t0)},\"ok\":{(ok ? "true" : "false")},\"status\":{st},\"error\":null,\"cpuPct\":null,\"read\":{read}}}";
                    if (ok)
                    {
                        using var readDoc = JsonDocument.Parse(read);
                        if (readDoc.RootElement.TryGetProperty("_notice", out var notice) && notice.ValueKind == JsonValueKind.String && notice.GetString()!.Contains("SYNTHETIC", StringComparison.Ordinal))
                        {
                            synthetic = true;
                        }

                        var model = IronModel.MapFrame(IronModel.ParseRead(readDoc.RootElement), sdb, synthetic ? "SYNTHETIC" : "LIVE", cfg);
                        var t = tracker.Update(model, t3);
                        var s = IronModel.Summarize(model, t);
                        var gpus = string.Join(" ", model["gpus"]!.AsArray().Select(g => $"GPU{g!["index"]} {g["coreTempC"]?.ToJsonString() ?? "-"}°C/핫스팟 {g["hotspotC"]?.ToJsonString() ?? "-"}"));
                        Console.WriteLine($"[{i,4}] {t["freshness"],-17} RTT {t3 - t0,6:F1} ms | CPU {model["cpu"]!["tctlC"]?.ToJsonString() ?? "-"}°C | {gpus} | 합계 {s["gpuPowerText"]} | WireView {s["wireViewStatus"]} | {s["status"]}");
                    }
                    else
                    {
                        failures++;
                        Console.WriteLine($"[{i,4}] HTTP {st}");
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
                {
                    failures++;
                    var err = JsonValue.Create(ex.GetType().Name + ": " + ex.Message).ToJsonString();
                    line = $"{{\"i\":{i},\"t0Ms\":null,\"t3Ms\":null,\"rttMs\":null,\"ok\":false,\"status\":null,\"error\":{err},\"cpuPct\":null,\"read\":null}}";
                    Console.WriteLine($"[{i,4}] 실패: {ex.Message}");
                }

                await w.WriteLineAsync(line);
            }
        }

        manifest["finishedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        if (synthetic)
        {
            manifest["source"] = "SYNTHETIC";
            manifest["sourceReason"] = "응답에 SYNTHETIC 표시(_notice)가 있음 — 모의 서버";
        }

        File.WriteAllText(Path.Combine(outDir, "run_manifest.json"), manifest.ToJsonString(Pretty) + "\n", new UTF8Encoding(false));
        Analyze(outDir, root);
        return failures == 0 ? 0 : 3;
    }
}
