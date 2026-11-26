using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IronPoc;

/// <summary>
/// 수집 폴더(run_manifest.json, pings.jsonl, sdb.json, samples_all.jsonl, baseline.json)에서 G1·G2·G3A를 판정한다.
/// 계산 정의는 poc/Collect-HWiNfoRest.ps1 의 G1과 같고(테스트로 비교), 기준값은 모두 config/gates.json 에서 읽는다.
/// PASS/FAIL은 출처가 LIVE_CAPTURE일 때만 나온다. 그 밖에는 기준 충족 여부와 무관하게 UNVERIFIED.
/// </summary>
public static class Gates
{
    public sealed record Sample(int I, double T0, double T3, double Rtt, bool Ok, string? Error, double? CpuPct, JsonElement? Read);

    public sealed class RunData
    {
        public required JsonObject Manifest { get; init; }
        public required List<Sample> Samples { get; init; }
        public required List<JsonObject> Pings { get; init; }
        public JsonElement? Sdb { get; init; }
        public JsonObject? Baseline { get; init; }
        public required string Dir { get; init; }
    }

    public static RunData Load(string dir)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "run_manifest.json")))!.AsObject();
        var samples = new List<Sample>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "samples_all.jsonl")))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var e = JsonDocument.Parse(line).RootElement.Clone();
            double D(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
            JsonElement? read = e.TryGetProperty("read", out var r) && r.ValueKind == JsonValueKind.Object ? r : null;
            samples.Add(new Sample(
                (int)D("i"), D("t0Ms"), D("t3Ms"), D("rttMs"),
                e.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                e.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() : null,
                e.TryGetProperty("cpuPct", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null,
                read));
        }

        var pings = new List<JsonObject>();
        var pingPath = Path.Combine(dir, "pings.jsonl");
        if (File.Exists(pingPath))
        {
            pings.AddRange(File.ReadLines(pingPath).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => JsonNode.Parse(l)!.AsObject()));
        }

        var sdbPath = Path.Combine(dir, "sdb.json");
        JsonElement? sdb = File.Exists(sdbPath) ? JsonDocument.Parse(File.ReadAllText(sdbPath)).RootElement.Clone() : null;
        JsonObject? baseline = null;
        var bName = manifest["baselineFile"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(bName))
        {
            var bp = Path.IsPathRooted(bName) ? bName : Path.Combine(dir, bName);
            if (File.Exists(bp)) baseline = JsonNode.Parse(File.ReadAllText(bp))!.AsObject();
        }

        return new RunData { Manifest = manifest, Samples = samples, Pings = pings, Sdb = sdb, Baseline = baseline, Dir = dir };
    }

    /// <summary>최근접 순위 백분위수: 오름차순 정렬 후 ceil(p·n)번째. 표본이 없으면 null. (PowerShell 수집기와 같은 정의)</summary>
    public static double? Percentile(IEnumerable<double> values, double p)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return null;
        var rank = (int)Math.Ceiling(p * v.Count);
        return v[Math.Clamp(rank, 1, v.Count) - 1];
    }

    /// <summary>공식 문서 방식: RTT가 가장 짧은 ping으로 오프셋 = t − (t0+t3)/2.</summary>
    public static (double? Offset, JsonObject? Ping) ClockOffset(List<JsonObject> pings)
    {
        var best = pings.Where(p => p["ok"]?.GetValue<bool>() == true && p["t"] is not null)
            .OrderBy(p => p["rttMs"]!.GetValue<double>()).FirstOrDefault();
        if (best is null) return (null, null);
        var t = best["t"]!.GetValue<double>();
        return (t - (best["t0Ms"]!.GetValue<double>() + best["t3Ms"]!.GetValue<double>()) / 2, best);
    }

    private static bool VersionIs1(JsonElement read) => read.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() == 1;

    private static JsonObject Check(string name, double? value, string limit, bool? pass, string? note = null)
    {
        var o = new JsonObject { ["name"] = name, ["value"] = Js.N(value), ["limit"] = limit, ["pass"] = pass };
        if (note is not null) o["note"] = note;
        return o;
    }

    private static (bool? Met, string Verdict) Verdict(JsonArray checks, string source)
    {
        var passes = checks.Select(c => c!["pass"]?.GetValue<bool>()).ToList();
        bool? met = passes.Any(p => p == false) ? false : passes.Any(p => p is null) ? null : true;
        var verdict = source != "LIVE_CAPTURE" ? "UNVERIFIED" : met == false ? "FAIL" : met is null ? "UNVERIFIED" : "PASS";
        return (met, verdict);
    }

    /// <summary>G1: 연결·지연·신선도·오버헤드. 타임스탬프 판정은 Tracker와 같은 규칙.</summary>
    public static JsonObject G1(RunData run, IronConfig cfg)
    {
        var G = cfg.Gates.GetProperty("G1");
        var fresh = cfg.Gates.GetProperty("freshness");
        var maxFrozen = fresh.GetProperty("maxFrozenMs").GetDouble();
        var maxAge = fresh.GetProperty("maxAgeMs").GetDouble();
        var source = run.Manifest["source"]?.GetValue<string>() ?? "UNKNOWN";
        var (offset, _) = ClockOffset(run.Pings);
        var n = run.Samples.Count;
        int success = 0, versionMismatch = 0, violations = 0, stale = 0;
        var rtts = new List<double>();
        var readouts = new List<double>();
        double? lastTs = null, lastAdvanceAt = null;
        foreach (var s in run.Samples)
        {
            if (!s.Ok || s.Read is not JsonElement read) continue;
            if (!VersionIs1(read)) { versionMismatch++; continue; }
            success++;
            rtts.Add(s.Rtt);
            double? T(string k) => read.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            var (rs, ts, re) = (T("Timestamp RS ms"), T("Timestamp ms"), T("Timestamp RE ms"));
            var ordered = rs is not null && ts is not null && re is not null && rs > 0 && rs <= ts && ts <= re;
            if (!ordered || (lastTs is not null && ts < lastTs)) { violations++; continue; }
            readouts.Add(ts!.Value - rs!.Value);
            if (lastTs is null || ts > lastTs) { lastTs = ts; lastAdvanceAt = s.T3; }
            var frozen = s.T3 - lastAdvanceAt!.Value;
            double? age = offset is double off ? Js.Round(s.T3 + off - ts.Value, 0) : null;
            if (frozen > maxFrozen || (age is double a && a > maxAge)) stale++;
        }

        double? cpuCollect = run.Samples.Where(s => s.CpuPct is not null).Select(s => s.CpuPct!.Value).DefaultIfEmpty(double.NaN).Average();
        if (double.IsNaN(cpuCollect.Value)) cpuCollect = null;
        double? cpuBase = run.Baseline?["meanCpuPct"] is JsonNode b ? b.GetValue<double>() : null;
        double? overhead = cpuCollect is not null && cpuBase is not null ? Js.Round(cpuCollect - cpuBase, 2) : null;

        var rate = n > 0 ? Js.Round(success * 100.0 / n, 2) : 0;
        var rttP95 = Js.Round(Percentile(rtts, 0.95), 1);
        var readP95 = Js.Round(Percentile(readouts, 0.95), 1);
        var checks = new JsonArray
        {
            Check("samples", n, ">= " + Js.Num(G.GetProperty("samples").GetDouble()), n >= G.GetProperty("samples").GetDouble()),
            Check("successRatePct", rate, ">= " + Js.Num(G.GetProperty("successRatePctMin").GetDouble()), rate >= G.GetProperty("successRatePctMin").GetDouble()),
            Check("versionMismatches", versionMismatch, "<= 0 (Version " + Js.Num(G.GetProperty("versionRequired").GetDouble()) + ")", versionMismatch == 0),
            Check("rttP95Ms", rttP95, "<= " + Js.Num(G.GetProperty("rttP95MsMax").GetDouble()), rttP95 is double r && r <= G.GetProperty("rttP95MsMax").GetDouble()),
            Check("readoutP95Ms", readP95, "<= " + Js.Num(G.GetProperty("readoutP95MsMax").GetDouble()), readP95 is double q && q <= G.GetProperty("readoutP95MsMax").GetDouble()),
            Check("timestampViolations", violations, "<= " + Js.Num(G.GetProperty("timestampViolationsMax").GetDouble()), violations <= G.GetProperty("timestampViolationsMax").GetDouble()),
            Check("staleSamples", stale, "<= " + Js.Num(G.GetProperty("staleSamplesMax").GetDouble()), stale <= G.GetProperty("staleSamplesMax").GetDouble(),
                offset is null ? "ping 시계 오프셋 없음 — 정지 시간만으로 판정" : null),
            Check("hwinfoCpuOverheadPct", overhead, "<= " + Js.Num(G.GetProperty("hwinfoCpuOverheadPctMax").GetDouble()),
                overhead is double ov ? ov <= G.GetProperty("hwinfoCpuOverheadPctMax").GetDouble() : null,
                overhead is null ? (cpuBase is null ? "REST 비활성 기준선(baseline.json) 없음" : "수집 중 HWiNFO CPU 표본 없음") : null),
        };
        var (met, verdict) = Verdict(checks, source);
        return new JsonObject
        {
            ["gate"] = "G1", ["source"] = source, ["criteriaMet"] = met, ["verdict"] = verdict,
            ["clockOffsetMs"] = Js.N(Js.Round(offset, 1)),
            ["cpu"] = new JsonObject { ["collectMeanPct"] = Js.N(Js.Round(cpuCollect, 2)), ["baselineMeanPct"] = Js.N(Js.Round(cpuBase, 2)) },
            ["checks"] = checks,
        };
    }

    /// <summary>첫 성공 샘플의 모델(G2·인벤토리용).</summary>
    public static (JsonObject? Model, ReadFrame? Frame, Dictionary<string, SdbMeta>? Sdb) FirstModel(RunData run, IronConfig cfg)
    {
        if (run.Sdb is not JsonElement sdbJson) return (null, null, null);
        var sdb = IronModel.ParseSdb(sdbJson);
        var s = run.Samples.FirstOrDefault(x => x.Ok && x.Read is JsonElement r && VersionIs1(r));
        if (s is null) return (null, null, sdb);
        var frame = IronModel.ParseRead(s.Read!.Value);
        return (IronModel.MapFrame(frame, sdb, SourceForModel(run), cfg), frame, sdb);
    }

    public static string SourceForModel(RunData run)
    {
        var s = run.Manifest["source"]?.GetValue<string>() ?? "UNVERIFIED_ENDPOINT";
        return IronModel.Sources.Contains(s) ? s : "UNVERIFIED_ENDPOINT";
    }

    /// <summary>G3A: HWiNFO만 WireView에 접근한 상태의 600샘플. 동시 접근 시험(G3B)은 하지 않는다.</summary>
    public static JsonObject G3A(RunData run, IronConfig cfg)
    {
        var G = cfg.Gates.GetProperty("G3A");
        var source = run.Manifest["source"]?.GetValue<string>() ?? "UNKNOWN";
        if (run.Manifest["gate"]?.GetValue<string>() != "G3A")
        {
            return new JsonObject { ["gate"] = "G3A", ["source"] = source, ["verdict"] = "NOT_RUN", ["note"] = "이 수집은 G3A 모드(-Gate G3A)가 아님" };
        }

        if (run.Sdb is not JsonElement sdbJson) return new JsonObject { ["gate"] = "G3A", ["source"] = source, ["verdict"] = "UNVERIFIED", ["note"] = "sdb.json 없음" };
        var sdb = IronModel.ParseSdb(sdbJson);
        var tracker = new Tracker(cfg);
        var (offset, ping) = ClockOffset(run.Pings);
        if (ping is not null) tracker.SetClockOffset(ping["t"]!.GetValue<double>(), ping["t0Ms"]!.GetValue<double>(), ping["t3Ms"]!.GetValue<double>());
        int success = 0, incomplete = 0, absent = 0, latchedSamples = 0, frozenRun = 0, frozenMax = 0;
        string? lastSig = null;
        var pinMin = new double[6];
        var pinMax = new double[6];
        Array.Fill(pinMin, double.PositiveInfinity);
        Array.Fill(pinMax, double.NegativeInfinity);
        var wvRe = IronModel.Re(cfg.Rules.GetProperty("groups").GetProperty("wireview").GetProperty("sensor").GetString()!);
        foreach (var s in run.Samples)
        {
            if (!s.Ok || s.Read is not JsonElement read || !VersionIs1(read)) continue;
            success++;
            var frame = IronModel.ParseRead(read);
            var model = IronModel.MapFrame(frame, sdb, SourceForModel(run), cfg);
            var t = tracker.Update(model, s.T3);
            var wv = model["wireview"]!;
            if (!wv["present"]!.GetValue<bool>()) absent++;
            else if (wv["completeness"]!.GetValue<string>() != "COMPLETE") incomplete++;
            if (t["pinAlert"]!.GetValue<bool>()) latchedSamples++;
            foreach (var p in wv["pins"]!.AsArray())
            {
                if (p!["currentA"] is JsonNode a)
                {
                    var i = p["pin"]!.GetValue<int>() - 1;
                    pinMin[i] = Math.Min(pinMin[i], a.GetValue<double>());
                    pinMax[i] = Math.Max(pinMax[i], a.GetValue<double>());
                }
            }

            if (t["newMeasurement"]!.GetValue<bool>())
            {
                // WireView 센서의 모든 원문 값이 이전 '새 측정'과 같으면 정지 후보
                var sig = string.Join("\u001f", frame.Rows.Where(r => wvRe.IsMatch(r.Sensor)).Select(r => r.Path + "=" + r.Raw));
                frozenRun = sig.Length > 0 && sig == lastSig ? frozenRun + 1 : 0;
                frozenMax = Math.Max(frozenMax, frozenRun);
                lastSig = sig;
            }
        }

        var n = run.Samples.Count;
        var drops = n - success;
        var att = run.Manifest["operatorAttestation"]?["hwinfoIsOnlyWireViewClient"];
        bool? attested = att is null ? null : att.GetValue<bool>();
        var clients = run.Manifest["evidence"]?["wireViewClientProcesses"]?.AsArray().Count ?? 0;
        var checks = new JsonArray
        {
            Check("samples", n, ">= " + Js.Num(G.GetProperty("samples").GetDouble()), n >= G.GetProperty("samples").GetDouble()),
            Check("operatorAttestation", attested == true ? 1 : 0, "HWiNFO만 WireView에 접근(운영자 확인)", attested == true ? true : null,
                attested == true ? null : "운영자 확인 없음 — 소유권 미확정"),
            Check("otherWireViewClientProcesses", clients, "== 0", clients == 0),
            Check("dropSamples", drops, "<= " + Js.Num(G.GetProperty("dropSamplesMax").GetDouble()), drops <= G.GetProperty("dropSamplesMax").GetDouble()),
            Check("incompleteOrAbsentSamples", incomplete + absent, "== 0 (6핀 전류 모두)", incomplete + absent == 0),
            Check("frozenDistinctTimestampsMax", frozenMax, "<= " + Js.Num(G.GetProperty("frozenDistinctTimestampsMax").GetDouble()), frozenMax <= G.GetProperty("frozenDistinctTimestampsMax").GetDouble()),
        };
        var (met, verdict) = Verdict(checks, source);
        var pins = new JsonArray();
        for (var i = 0; i < 6; i++)
        {
            pins.Add(new JsonObject { ["pin"] = i + 1, ["minA"] = double.IsFinite(pinMin[i]) ? pinMin[i] : null, ["maxA"] = double.IsFinite(pinMax[i]) ? pinMax[i] : null });
        }

        return new JsonObject
        {
            ["gate"] = "G3A", ["source"] = source, ["criteriaMet"] = met, ["verdict"] = verdict,
            ["ownership"] = new JsonObject
            {
                ["state"] = attested == true && clients == 0 ? "OPERATOR_ATTESTED" : "UNCONFIRMED",
                ["basis"] = "REST 센서 노출은 소유권 근거가 아님. 운영자 확인 + 수집 중 다른 WireView 클라이언트 프로세스 0개 + PnP 기록으로만 '운영자 확인' 상태가 됨",
                ["evidence"] = run.Manifest["evidence"]?.DeepClone(),
            },
            ["checks"] = checks,
            ["info"] = new JsonObject { ["incompleteSamples"] = incomplete, ["absentSamples"] = absent, ["pinAlertSamples"] = latchedSamples, ["invalidFrames"] = tracker.Invalid, ["clockOffsetMs"] = Js.N(Js.Round(offset, 1)), ["pinRangeA"] = pins },
        };
    }

    /// <summary>SDB 전체와 첫 read를 합친 센서 인벤토리(CSV). 실기 매핑 규칙 확정용.</summary>
    public static string InventoryCsv(Dictionary<string, SdbMeta> sdb, ReadFrame? frame, IronConfig cfg)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var inRead = frame?.Rows.ToDictionary(r => r.Sensor + "|" + r.Path + "|" + r.TypeKey, r => r.Raw) ?? [];
        var sb = new StringBuilder("sensor,reading,typeKey,unit,unitNormalized,eType,hide,monitoring,inRead,value\n");
        foreach (var (key, m) in sdb)
        {
            var parts = key.Split('|');
            var has = inRead.TryGetValue(key, out var raw);
            sb.Append(string.Join(",", Q(parts[0]), Q(parts[1]), Q(parts[2]), Q(m.Unit), Q(IronModel.NormUnit(m.Unit, cfg.Rules)),
                m.EType is double et ? Js.Num(et) : "", m.Hide ? "1" : "0", m.Monitoring ? "1" : "0", has ? "1" : "0", Q(raw ?? ""))).Append('\n');
        }

        foreach (var (key, raw) in inRead.Where(kv => !sdb.ContainsKey(kv.Key)))
        {
            var parts = key.Split('|');
            sb.Append(string.Join(",", Q(parts[0]), Q(parts[1]), Q(parts[2]), Q(""), Q("(SDB에 없음)"), "", "", "", "1", Q(raw))).Append('\n');
        }

        return sb.ToString();
    }

    public static JsonObject Report(RunData run, IronConfig cfg)
    {
        var (model, frame, sdb) = FirstModel(run, cfg);
        var g2 = model is null ? new JsonObject { ["gate"] = "G2", ["verdict"] = "UNVERIFIED", ["note"] = "성공한 read 또는 sdb.json 없음" } : IronModel.EvaluateG2(model, cfg);
        if (sdb is not null)
        {
            File.WriteAllText(Path.Combine(run.Dir, "sensor_inventory.csv"), InventoryCsv(sdb, frame, cfg), new UTF8Encoding(true));
        }

        var g5 = cfg.Gates.GetProperty("G5");
        return new JsonObject
        {
            ["schema"] = "iron.rc3.gate_report/1",
            ["generatedBy"] = "HWiNfoRestPoC analyze (RC3)",
            ["runDir"] = Path.GetFullPath(run.Dir),
            ["source"] = run.Manifest["source"]?.DeepClone(),
            ["sourceReason"] = run.Manifest["sourceReason"]?.DeepClone(),
            ["passPolicy"] = "PASS/FAIL은 source=LIVE_CAPTURE일 때만. OFFLINE_JSON·SYNTHETIC·UNVERIFIED_ENDPOINT는 UNVERIFIED.",
            ["G1"] = G1(run, cfg),
            ["G2"] = g2,
            ["G3A"] = G3A(run, cfg),
            ["G3B"] = new JsonObject { ["gate"] = "G3B", ["verdict"] = "NOT_RUN", ["note"] = "WireView 동시 접근 시험은 사용자 별도 승인 전 금지" },
            ["G4S"] = new JsonObject { ["gate"] = "G4S", ["verdict"] = "MANUAL", ["checks"] = JsonNode.Parse(cfg.Gates.GetProperty("G4S").GetProperty("checks").GetRawText()) },
            ["G5"] = new JsonObject { ["gate"] = "G5", ["verdict"] = "RUN_SEPARATELY", ["command"] = g5.GetProperty("command").GetString(), ["lanExposure"] = g5.GetProperty("lanExposure").GetString() },
            ["firstFrame"] = model is null ? null : IronModel.Summarize(model, new JsonObject { ["freshness"] = "UNKNOWN", ["pinAlert"] = false }),
            ["longTermNotVerified"] = new JsonArray("12시간 이상 연속 운용(무료판 제한 여부 포함)", "HWiNFO 재부팅·자동 시작 반복", "WireView 동시 접근(G3B)", "Remote LAN 노출", "장시간 CPU 오버헤드 추세"),
        };
    }

    public static string Markdown(JsonObject r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# IRON RC3 게이트 보고서").AppendLine();
        sb.AppendLine($"- 출처: `{r["source"]}` — {r["sourceReason"]}");
        sb.AppendLine($"- 판정 정책: {r["passPolicy"]}").AppendLine();
        foreach (var g in new[] { "G1", "G2", "G3A", "G3B", "G4S", "G5" })
        {
            var o = r[g]!.AsObject();
            sb.AppendLine($"## {g}: {o["verdict"]}" + (o["criteriaMet"] is JsonNode m ? $" (기준 충족: {m})" : ""));
            if (o["note"] is JsonNode note) sb.AppendLine().AppendLine(note.ToString());
            if (o["command"] is JsonNode cmd) sb.AppendLine().AppendLine($"- 명령: `{cmd}`").AppendLine($"- LAN 노출: {o["lanExposure"]}");
            if (o["checks"] is JsonArray checks && checks.Count > 0 && checks[0] is JsonObject)
            {
                sb.AppendLine().AppendLine("| 항목 | 값 | 기준 | 결과 |").AppendLine("|---|---|---|---|");
                foreach (var c in checks)
                {
                    var pass = c!["pass"] is JsonNode p ? (p.GetValue<bool>() ? "충족" : "미달") : "판정 불가";
                    sb.AppendLine($"| {c["name"]} | {c["value"]?.ToJsonString() ?? "null"} | {c["limit"]} | {pass}{(c["note"] is JsonNode n ? " — " + n : "")} |");
                }
            }
            else if (o["checks"] is JsonArray manual)
            {
                foreach (var c in manual) sb.AppendLine($"- [ ] {c}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## 장기 미검증 항목");
        foreach (var x in r["longTermNotVerified"]!.AsArray()) sb.AppendLine($"- {x}");
        return sb.ToString();
    }
}
