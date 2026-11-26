using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IronPoc;

public sealed record RawRow(string Sensor, string Path, string Name, string TypeKey, string Raw, double? Num);

public sealed record ReadFrame(double? Rs, double? Ts, double? Re, List<RawRow> Rows);

public sealed record SdbMeta(string Unit, double? EType, bool Hide, bool Monitoring);

/// <summary>config/*.json 세 파일. JS(assets/iron-config.js)와 같은 원본.</summary>
public sealed class IronConfig
{
    public required JsonElement Rules { get; init; }
    public required JsonElement Thresholds { get; init; }
    public required JsonElement Gates { get; init; }

    public static IronConfig Load(string configDir)
    {
        JsonElement L(string n) => JsonDocument.Parse(File.ReadAllText(Path.Combine(configDir, n))).RootElement.Clone();
        return new IronConfig { Rules = L("mapping_rules.json"), Thresholds = L("iron_stage1_thresholds.json"), Gates = L("gates.json") };
    }

    public double Th(string group, string field) => Thresholds.GetProperty(group).GetProperty(field).GetDouble();
}

/// <summary>
/// assets/iron-model.js 의 .NET 이식. 함수 이름·출력 구조·문구가 같아야 하며 tests/fixtures/expected 와 값 단위로 비교된다.
/// </summary>
public static class IronModel
{
    public static readonly string[] Sources = ["LIVE", "LIVE_CAPTURE", "UNVERIFIED_ENDPOINT", "OFFLINE_JSON", "SYNTHETIC"];
    private static readonly HashSet<string> MetaKeys = new(StringComparer.Ordinal) { "tSensorLocation", "tReadingLocation", "tReadingType" };
    private static readonly Regex NumRe = new(@"^[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex SettingRe = new(@"^[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex TypeKeyRe = new("^[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex DecimalCommaRe = new(",[0-9]{1,2}$", RegexOptions.CultureInvariant);

    /// <summary>설정 파일의 정규식: JS와 같은 뜻이 되도록 ECMAScript 모드(\d = [0-9]).</summary>
    public static Regex Re(string pattern) => new(pattern, RegexOptions.ECMAScript | RegexOptions.CultureInvariant);

    private static void CheckVersion(JsonElement json, string kind)
    {
        if (!Js.IsObj(json)) throw new InvalidDataException(kind + ": JSON 객체가 아닙니다.");
        if (!Js.Has(json, "Version", out var v)) throw new InvalidDataException(kind + ": 'Version' 필드가 없습니다.");
        if (v.ValueKind != JsonValueKind.Number || v.GetDouble() != 1) throw new InvalidDataException(kind + ": 지원하지 않는 Version " + Js.Str(v) + " (지원: 1)");
    }

    public static double? ParseValue(string raw)
    {
        var s = raw.Replace(",", "").Trim();
        if (!NumRe.IsMatch(s)) return null;
        var n = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        return double.IsFinite(n) ? n : null;
    }

    public static ReadFrame ParseRead(JsonElement json)
    {
        CheckVersion(json, "read");
        var rows = new List<RawRow>();
        void Walk(string sensor, JsonElement node, List<string> path)
        {
            foreach (var (k, v) in Js.Keys(node))
            {
                if (!Js.IsObj(v)) continue;
                if (Js.Has(v, "Value", out var value))
                {
                    var isType = TypeKeyRe.IsMatch(k);
                    var rPath = isType ? path : [.. path, k];
                    rows.Add(new RawRow(sensor, string.Join(" / ", rPath), rPath.Count > 0 ? rPath[^1] : "", isType ? k : "", Js.Str(value), ParseValue(Js.Str(value))));
                }
                else
                {
                    Walk(sensor, v, [.. path, k]);
                }
            }
        }

        foreach (var (sensor, v) in Js.Keys(json))
        {
            if (Js.IsObj(v)) Walk(sensor, v, []);
        }

        double? T(string k) => Js.Has(json, k, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
        return new ReadFrame(T("Timestamp RS ms"), T("Timestamp ms"), T("Timestamp RE ms"), rows);
    }

    public static Dictionary<string, SdbMeta> ParseSdb(JsonElement json)
    {
        CheckVersion(json, "sdb");
        var index = new Dictionary<string, SdbMeta>(StringComparer.Ordinal);
        void Walk(string sensor, JsonElement node, List<string> path)
        {
            foreach (var (k, v) in Js.Keys(node))
            {
                if (!Js.IsObj(v) || MetaKeys.Contains(k)) continue;
                if (Js.Has(v, "hide", out var hide))
                {
                    var isType = TypeKeyRe.IsMatch(k);
                    var rPath = isType ? path : [.. path, k];
                    var key = sensor + "|" + string.Join(" / ", rPath) + "|" + (isType ? k : "");
                    var unit = Js.Has(v, "Unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "";
                    double? eType = Js.Has(v, "tReadingType", out var rt) && Js.IsObj(rt) && Js.Has(rt, "eType", out var et) && et.ValueKind == JsonValueKind.Number ? et.GetDouble() : null;
                    var hidden = (hide.ValueKind == JsonValueKind.Number && hide.GetDouble() == 1) || hide.ValueKind == JsonValueKind.True;
                    var monitoring = !(Js.Has(v, "monitoring", out var mo) && ((mo.ValueKind == JsonValueKind.Number && mo.GetDouble() == 0) || mo.ValueKind == JsonValueKind.False));
                    index[key] = new SdbMeta(unit, eType, hidden, monitoring);
                }
                else
                {
                    Walk(sensor, v, [.. path, k]);
                }
            }
        }

        foreach (var (sensor, v) in Js.Keys(json))
        {
            if (Js.IsObj(v)) Walk(sensor, v, []);
        }

        return index;
    }

    public static string NormUnit(string? unit, JsonElement rules)
    {
        var u = (unit ?? "").Trim();
        foreach (var (canon, aliases) in Js.Keys(rules.GetProperty("units")))
        {
            if (aliases.EnumerateArray().Any(a => a.GetString() == u)) return canon;
        }

        return u.Length == 0 ? "" : "?" + u;
    }

    private static double? ToBase(double? num, string? unit) => num is not double n ? null : unit switch
    {
        "GB" => n * 1024,
        "°F" => (n - 32) * 5 / 9,
        _ => n,
    };

    public static (double? Value, string? Error) ValidateSetting(string? text, JsonElement spec)
    {
        var t = (text ?? "").Trim();
        var d = spec.GetProperty("decimals").GetInt32();
        var min = spec.GetProperty("min").GetDouble();
        var max = spec.GetProperty("max").GetDouble();
        var range = min.ToString("F" + d, CultureInfo.InvariantCulture) + "~" + max.ToString("F" + d, CultureInfo.InvariantCulture);
        if (t.Length == 0) return (null, "값을 입력하세요 (" + range + ").");
        if (!SettingRe.IsMatch(t)) return (null, "숫자로 입력하세요 (" + range + ").");
        var dot = t.IndexOf('.');
        if (dot >= 0 && t.Length - dot - 1 > d) return (null, d == 0 ? "정수로 입력하세요 (" + range + ")." : "소수 " + d + "자리까지 입력하세요 (" + range + ").");
        var v = double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (v < min || v > max) return (null, range + " 범위로 입력하세요.");
        return (v, null);
    }

    public static (double GpuTempC, double PinA) LimitsOf(IronConfig cfg, double? gpuTempC = null, double? pinA = null)
    {
        var g = gpuTempC ?? cfg.Th("gpuTemperatureC", "default");
        var p = pinA ?? cfg.Th("wireViewPinCurrentA", "default");
        if (ValidateSetting(Js.Num(g), cfg.Thresholds.GetProperty("gpuTemperatureC")).Error is not null) throw new ArgumentOutOfRangeException(nameof(gpuTempC), "GPU 온도 기준이 Stage 1 범위 밖: " + Js.Num(g));
        if (ValidateSetting(Js.Num(p), cfg.Thresholds.GetProperty("wireViewPinCurrentA")).Error is not null) throw new ArgumentOutOfRangeException(nameof(pinA), "핀 전류 기준이 Stage 1 범위 밖: " + Js.Num(p));
        return (g, p);
    }

    private sealed record Row(string Sensor, string Path, string Name, string TypeKey, string Raw, double? Num, string? Unit, string Key);

    public static JsonObject MapFrame(ReadFrame read, Dictionary<string, SdbMeta> sdb, string source, IronConfig cfg, double? gpuTempC = null, double? pinA = null)
    {
        if (!Sources.Contains(source)) throw new ArgumentException("알 수 없는 데이터 출처: " + source);
        var rules = cfg.Rules;
        var lim = LimitsOf(cfg, gpuTempC, pinA);
        var mapped = 0;
        var unmapped = new List<string>();
        var mismatches = new JsonArray();
        var candidates = new JsonObject();
        var resolved = new JsonObject();
        var commaSuspects = new List<string>();

        var rows = read.Rows.Select(r =>
        {
            var key = r.Sensor + "|" + r.Path + "|" + r.TypeKey;
            var has = sdb.TryGetValue(key, out var meta);
            if (has) mapped++; else unmapped.Add(key);
            if (DecimalCommaRe.IsMatch(r.Raw.Trim())) commaSuspects.Add(key);
            return new Row(r.Sensor, r.Path, r.Name, r.TypeKey, r.Raw, r.Num, has ? NormUnit(meta!.Unit, rules) : null, key);
        }).ToList();

        static List<string> Units(JsonElement rule) => rule.GetProperty("units").EnumerateArray().Select(x => x.GetString()!).ToList();

        Row? Pick(List<Row> list, JsonElement rule, string label)
        {
            var re = Re(rule.GetProperty("name").GetString()!);
            var allowed = Units(rule);
            Row? found = null;
            foreach (var r in list)
            {
                if (!re.IsMatch(r.Name)) continue;
                if (candidates[label] is not JsonArray c) { c = new JsonArray(); candidates[label] = c; }
                c.Add(r.Key);
                if (r.Unit is null) continue;
                if (!allowed.Contains(r.Unit))
                {
                    mismatches.Add(new JsonObject { ["item"] = label, ["key"] = r.Key, ["unit"] = r.Unit, ["allowed"] = Js.Arr(allowed.Select(a => (JsonNode?)a)) });
                    continue;
                }

                if (found is null && r.Num is not null) found = r;
            }

            resolved[label] = found?.Key;
            return found;
        }

        static double? Val(Row? r, int decimals) => r is null ? null : Js.Round(ToBase(r.Num, r.Unit), decimals);

        List<(string Sensor, Match M, List<Row> Rows)> GroupBy(string sensorRe)
        {
            var re = Re(sensorRe);
            var map = new List<(string Sensor, Match M, List<Row> Rows)>();
            foreach (var r in rows)
            {
                var m = re.Match(r.Sensor);
                if (!m.Success) continue;
                var i = map.FindIndex(x => x.Sensor == r.Sensor);
                if (i < 0) map.Add((r.Sensor, m, [r])); else map[i].Rows.Add(r);
            }

            return map;
        }

        var g = rules.GetProperty("groups");
        JsonElement R(string group, string reading) => g.GetProperty(group).GetProperty("readings").GetProperty(reading);

        // CPU
        var cpuGroups = GroupBy(g.GetProperty("cpu").GetProperty("sensor").GetString()!);
        var cpuName = cpuGroups.Count > 0 ? cpuGroups[0].Sensor : null;
        var cpuRows = cpuGroups.Count > 0 ? cpuGroups[0].Rows : [];
        var cpu = new JsonObject
        {
            ["name"] = cpuName is null ? null : Re(@"^CPU \[#\d+\]:\s*").Replace(cpuName, "", 1).Trim(),
            ["tctlC"] = Js.N(Val(Pick(cpuRows, R("cpu", "tctl"), "cpu.tctl"), 1)),
            ["packagePowerW"] = Js.N(Val(Pick(cpuRows, R("cpu", "packagePower"), "cpu.packagePower"), 1)),
            ["usagePct"] = Js.N(Val(Pick(cpuRows, R("cpu", "usage"), "cpu.usage"), 1)),
        };

        // GPUs
        var gpuSensorRe = g.GetProperty("gpu").GetProperty("sensor").GetString()!;
        var byIndex = new Dictionary<int, (List<string> Names, List<Row> Rows)>();
        foreach (var grp in GroupBy(gpuSensorRe))
        {
            var idx = int.Parse(grp.M.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!byIndex.TryGetValue(idx, out var e)) { e = ([], []); byIndex[idx] = e; }
            e.Names.Add(grp.Sensor);
            e.Rows.AddRange(grp.Rows);
        }

        var gre = Re(gpuSensorRe);
        var gpuIdxSet = new HashSet<int>(byIndex.Keys);
        foreach (var k in sdb.Keys)
        {
            var m = gre.Match(k.Split('|')[0]);
            if (m.Success) gpuIdxSet.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        var gpus = new List<JsonObject>();
        foreach (var i in gpuIdxSet.OrderBy(x => x))
        {
            var e = byIndex.TryGetValue(i, out var found) ? found : (Names: new List<string>(), Rows: new List<Row>());
            var L = "gpu[" + i + "].";
            var core = Val(Pick(e.Rows, R("gpu", "coreTemp"), L + "coreTemp"), 1);
            var o = new JsonObject
            {
                ["index"] = i,
                ["name"] = e.Names.Count > 0 ? Re(@"^GPU \[#\d+\]:\s*").Replace(e.Names[0], "", 1).Trim() : null,
                ["coreTempC"] = Js.N(core),
            };
            o["hotspotC"] = Js.N(Val(Pick(e.Rows, R("gpu", "hotspotTemp"), L + "hotspotTemp"), 1));
            o["memJunctionC"] = Js.N(Val(Pick(e.Rows, R("gpu", "memJunctionTemp"), L + "memJunctionTemp"), 1));
            o["powerW"] = Js.N(Val(Pick(e.Rows, R("gpu", "power"), L + "power"), 1));
            o["loadPct"] = Js.N(Val(Pick(e.Rows, R("gpu", "load"), L + "load"), 1));
            o["vramUsedMB"] = Js.N(Val(Pick(e.Rows, R("gpu", "vramUsed"), L + "vramUsed"), 0));
            o["clockMHz"] = Js.N(Val(Pick(e.Rows, R("gpu", "clock"), L + "clock"), 0));
            o["hot"] = core is double c ? c >= lim.GpuTempC : null;
            gpus.Add(o);
        }

        static double? D(JsonNode? n) => n is null ? null : n.GetValue<double>();
        var powers = gpus.Select(x => D(x["powerW"])).ToList();
        var reported = powers.Count(p => p is double v && v >= 0);
        var state = gpus.Count == 0 || reported == 0 ? "UNAVAILABLE" : reported == gpus.Count ? "COMPLETE" : "PARTIAL";
        var gpuPower = new JsonObject { ["state"] = state, ["totalW"] = null, ["reported"] = reported, ["expected"] = gpus.Count };
        if (state == "COMPLETE")
        {
            var sum = 0.0;
            foreach (var p in powers) sum += p!.Value;
            gpuPower["totalW"] = Js.N(Js.Round(sum, 1));
        }

        double? MaxOf(string key)
        {
            var v = gpus.Select(x => D(x[key])).Where(x => x is not null).Select(x => x!.Value).ToList();
            return v.Count > 0 ? v.Max() : null;
        }

        var gpuTemps = new JsonObject { ["coreMaxC"] = Js.N(MaxOf("coreTempC")), ["hotspotMaxC"] = Js.N(MaxOf("hotspotC")), ["memJunctionMaxC"] = Js.N(MaxOf("memJunctionC")) };

        // RAM
        var ramRows = GroupBy(g.GetProperty("ram").GetProperty("sensor").GetString()!).SelectMany(x => x.Rows).ToList();
        var ram = new JsonObject
        {
            ["usedMB"] = Js.N(Val(Pick(ramRows, R("ram", "usedMB"), "ram.usedMB"), 0)),
            ["availableMB"] = Js.N(Val(Pick(ramRows, R("ram", "availableMB"), "ram.availableMB"), 0)),
            ["loadPct"] = Js.N(Val(Pick(ramRows, R("ram", "loadPct"), "ram.loadPct"), 1)),
        };

        JsonArray Many(string group)
        {
            var gr = g.GetProperty(group);
            var arr = new JsonArray();
            foreach (var x in GroupBy(gr.GetProperty("sensor").GetString()!))
            {
                var o = new JsonObject { ["sensor"] = x.Sensor };
                foreach (var (k, rule) in Js.Keys(gr.GetProperty("readings")))
                {
                    o[k] = Js.N(Val(Pick(x.Rows, rule, group + "." + k), 1));
                }

                arr.Add(o);
            }

            return arr;
        }

        var dimms = Many("dimm");
        var nvme = Many("nvme");

        // WireView
        var wvRule = g.GetProperty("wireview");
        var wvRe = Re(wvRule.GetProperty("sensor").GetString()!);
        var wvRows = rows.Where(r => wvRe.IsMatch(r.Sensor)).ToList();
        var wireview = new JsonObject
        {
            ["present"] = wvRows.Count > 0,
            ["completeness"] = "ABSENT",
            ["presentPins"] = 0,
            ["pins"] = new JsonArray(),
            ["totalW"] = null,
            ["temperaturesC"] = new JsonArray(),
            ["deltaA"] = null,
            ["ownership"] = new JsonObject { ["state"] = "UNCONFIRMED", ["basis"] = "REST 센서 노출만으로는 USB 소유권을 확정하지 않음 — 운영자 확인·프로세스·PnP 증거 필요" },
        };
        if (wvRows.Count > 0)
        {
            var pinRe = Re(wvRule.GetProperty("pinName").GetString()!);
            var pu = wvRule.GetProperty("pinUnits");
            var uA = Units2(pu, "currentA");
            var uV = Units2(pu, "voltageV");
            var uW = Units2(pu, "powerW");
            var cur = new double?[6];
            var volt = new double?[6];
            var pw = new double?[6];
            foreach (var r in wvRows)
            {
                var m = pinRe.Match(r.Path);
                if (!m.Success) continue;
                var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
                if (r.Unit is null || r.Num is null) continue;
                if (uA.Contains(r.Unit) && cur[n] is null) { cur[n] = Js.Round(r.Num, 2); resolved["wireview.pin" + m.Groups[1].Value] = r.Key; }
                else if (uV.Contains(r.Unit) && volt[n] is null) volt[n] = Js.Round(r.Num, 2);
                else if (uW.Contains(r.Unit) && pw[n] is null) pw[n] = Js.Round(r.Num, 1);
                else if (!uA.Contains(r.Unit) && !uV.Contains(r.Unit) && !uW.Contains(r.Unit))
                {
                    mismatches.Add(new JsonObject { ["item"] = "wireview.pin" + m.Groups[1].Value, ["key"] = r.Key, ["unit"] = r.Unit, ["allowed"] = new JsonArray("A", "V", "W") });
                }
            }

            var limit = lim.PinA;
            var share = cfg.Thresholds.GetProperty("pinApproachingShare").GetDouble();
            var pins = new JsonArray();
            var levels = new string[6];
            for (var n = 0; n < 6; n++)
            {
                levels[n] = cur[n] is not double a ? "N/A" : a >= limit ? "OVER" : a >= limit * share ? "APPROACHING" : "NORMAL";
                if (cur[n] is null) resolved["wireview.pin" + (n + 1)] = null;
                pins.Add(new JsonObject { ["pin"] = n + 1, ["currentA"] = Js.N(cur[n]), ["voltageV"] = Js.N(volt[n]), ["powerW"] = Js.N(pw[n]), ["level"] = levels[n] });
            }

            var amps = cur.Where(a => a is not null).Select(a => a!.Value).ToList();
            wireview["pins"] = pins;
            wireview["presentPins"] = amps.Count;
            wireview["completeness"] = amps.Count == 6 ? "COMPLETE" : "INCOMPLETE";
            wireview["deltaA"] = amps.Count == 6 ? Js.N(Js.Round(amps.Max() - amps.Min(), 2)) : null;
            var tot = Pick(wvRows.Where(r => !pinRe.IsMatch(r.Path)).ToList(), wvRule.GetProperty("totalPower"), "wireview.totalPower");
            wireview["totalW"] = tot is null ? null : Js.N(Js.Round(tot.Num, 1));
            var tRule = wvRule.GetProperty("temperature");
            var tRe = Re(tRule.GetProperty("name").GetString()!);
            var tUnits = Units(tRule);
            wireview["temperaturesC"] = Js.Arr(wvRows.Where(r => tRe.IsMatch(r.Name) && r.Unit is not null && tUnits.Contains(r.Unit) && r.Num is not null)
                .Select(r => (JsonNode?)new JsonObject { ["name"] = r.Path, ["valueC"] = Js.N(Js.Round(ToBase(r.Num, r.Unit), 1)) }));
            wireview["anyPinOver"] = levels.Contains("OVER");
        }

        var rs = read.Rs;
        var ts = read.Ts;
        var re = read.Re;
        return new JsonObject
        {
            ["source"] = source,
            ["limits"] = new JsonObject { ["gpuTempC"] = lim.GpuTempC, ["pinA"] = lim.PinA },
            ["hwinfo"] = new JsonObject
            {
                ["timestampMs"] = Js.N(ts),
                ["timestampRsMs"] = Js.N(rs),
                ["timestampReMs"] = Js.N(re),
                ["readoutMs"] = ts is not null && rs is not null ? Js.N(Js.Round(ts - rs, 1)) : null,
                ["jsonBuildMs"] = re is not null && ts is not null ? Js.N(Js.Round(re - ts, 1)) : null,
                ["ordered"] = rs is not null && ts is not null && re is not null && rs > 0 && rs <= ts && ts <= re,
            },
            ["cpu"] = cpu,
            ["gpus"] = Js.Arr(gpus),
            ["gpuPower"] = gpuPower,
            ["gpuTemps"] = gpuTemps,
            ["ram"] = ram,
            ["dimms"] = dimms,
            ["nvme"] = nvme,
            ["wireview"] = wireview,
            ["mapping"] = new JsonObject
            {
                ["readings"] = rows.Count,
                ["mappedToSdb"] = mapped,
                ["mappingPct"] = rows.Count > 0 ? Js.N(Js.Round(mapped * 100.0 / rows.Count, 2)) : 0,
                ["unmappedKeys"] = Js.Arr(unmapped.Take(50).Select(k => (JsonNode?)k)),
                ["unitMismatches"] = mismatches,
                ["candidates"] = candidates,
                ["resolved"] = resolved,
                ["decimalCommaSuspects"] = Js.Arr(commaSuspects.Take(50).Select(k => (JsonNode?)k)),
                ["decimalCommaSuspectCount"] = commaSuspects.Count,
            },
        };

        static List<string> Units2(JsonElement pu, string k) => pu.GetProperty(k).EnumerateArray().Select(x => x.GetString()!).ToList();
    }

    /// <summary>JS summarize 와 같다.</summary>
    public static JsonObject Summarize(JsonObject model, JsonObject tracked)
    {
        var gpuHot = model["gpus"]!.AsArray().Any(x => x!["hot"] is JsonNode h && h.GetValue<bool>());
        var wv = model["wireview"]!;
        var fresh = tracked["freshness"]!.GetValue<string>();
        var pinAlert = tracked["pinAlert"]!.GetValue<bool>();
        var present = wv["present"]!.GetValue<bool>();
        var wvStatus = fresh is "STALE" or "INVALID" ? fresh
            : !present ? "N/A"
            : wv["completeness"]!.GetValue<string>() == "INCOMPLETE" ? "INCOMPLETE (" + wv["presentPins"]!.GetValue<int>() + "/6)"
            : pinAlert ? "핀 불균형 주의" : "OK";
        var status = fresh == "STALE" ? "STALE" : fresh == "INVALID" ? "INVALID"
            : pinAlert && gpuHot ? "핀 불균형 · GPU 온도 주의" : pinAlert ? "핀 불균형 주의" : gpuHot ? "GPU 온도 주의" : "ONLINE";
        var gp = model["gpuPower"]!;
        var gpState = gp["state"]!.GetValue<string>();
        return new JsonObject
        {
            ["source"] = model["source"]!.GetValue<string>(),
            ["status"] = status,
            ["wireViewStatus"] = wvStatus,
            ["gpuPowerText"] = gpState == "COMPLETE" ? Js.Num(gp["totalW"]!.GetValue<double>()) + " W" : gpState == "PARTIAL" ? "일부 미확인" : "N/A",
        };
    }

    /// <summary>JS evaluateG2 와 같다.</summary>
    public static JsonObject EvaluateG2(JsonObject model, IronConfig cfg)
    {
        var G = cfg.Gates.GetProperty("G2");
        var checks = new JsonArray();
        void Add(string name, JsonNode? value, string limit, bool pass) => checks.Add(new JsonObject { ["name"] = name, ["value"] = value, ["limit"] = limit, ["pass"] = pass });
        var mapping = model["mapping"]!;
        var res = mapping["resolved"]!.AsObject();
        var blocking = new JsonArray();
        foreach (var m in mapping["unitMismatches"]!.AsArray())
        {
            var item = m!["item"]!.GetValue<string>();
            if (!res.TryGetPropertyValue(item, out var v) || v is null) blocking.Add(m.DeepClone());
        }

        var pct = mapping["mappingPct"]!.GetValue<double>();
        var pctMin = G.GetProperty("readToSdbMappingPctMin").GetDouble();
        Add("readToSdbMappingPct", pct, ">= " + Js.Num(pctMin), pct >= pctMin);
        var mmMax = G.GetProperty("unitMismatchesMax").GetDouble();
        Add("blockingUnitMismatches", blocking.Count, "<= " + Js.Num(mmMax), blocking.Count <= mmMax);
        var dcCount = mapping["decimalCommaSuspectCount"]!.GetValue<int>();
        var dcMax = G.GetProperty("decimalCommaSuspectsMax").GetDouble();
        Add("decimalCommaSuspects", dcCount, "<= " + Js.Num(dcMax), dcCount <= dcMax);
        var gpus = model["gpus"]!.AsArray();
        var expGpu = G.GetProperty("expectedGpuCount").GetDouble();
        Add("gpuCount", gpus.Count, "== " + Js.Num(expGpu), gpus.Count == expGpu);
        var cpuField = new Dictionary<string, string> { ["tctl"] = "tctlC", ["usage"] = "usagePct", ["packagePower"] = "packagePowerW" };
        var gpuField = new Dictionary<string, string> { ["coreTemp"] = "coreTempC", ["power"] = "powerW", ["load"] = "loadPct", ["hotspotTemp"] = "hotspotC", ["memJunctionTemp"] = "memJunctionC", ["vramUsed"] = "vramUsedMB", ["clock"] = "clockMHz" };
        foreach (var k in G.GetProperty("requiredCpu").EnumerateArray().Select(x => x.GetString()!))
        {
            var v = model["cpu"]![cpuField[k]];
            Add("cpu." + k, v?.DeepClone(), "present", v is not null);
        }

        foreach (var gpu in gpus)
        {
            foreach (var k in G.GetProperty("requiredPerGpu").EnumerateArray().Select(x => x.GetString()!))
            {
                var v = gpu![gpuField[k]];
                Add("gpu[" + gpu["index"]!.GetValue<int>() + "]." + k, v?.DeepClone(), "present", v is not null);
            }
        }

        foreach (var k in G.GetProperty("requiredRam").EnumerateArray().Select(x => x.GetString()!))
        {
            var v = model["ram"]![k];
            Add("ram." + k, v?.DeepClone(), "present", v is not null);
        }

        var info = new JsonObject();
        foreach (var k in G.GetProperty("informational").EnumerateArray().Select(x => x.GetString()!))
        {
            var parts = k.Split('.');
            var (grp, f) = (parts[0], parts[1]);
            if (grp == "cpu") info[k] = model["cpu"]![cpuField[f]] is not null;
            else if (grp == "gpu") info[k] = Js.Arr(gpus.Select(x => (JsonNode?)(x![gpuField[f]] is not null)));
            else if (grp is "dimm" or "nvme") info[k] = Js.Arr(model[grp == "dimm" ? "dimms" : "nvme"]!.AsArray().Select(d => (JsonNode?)(d![f] is not null)));
        }

        var met = checks.All(c => c!["pass"]!.GetValue<bool>());
        var source = model["source"]!.GetValue<string>();
        return new JsonObject
        {
            ["gate"] = "G2",
            ["source"] = source,
            ["criteriaMet"] = met,
            ["verdict"] = source == "LIVE_CAPTURE" ? (met ? "PASS" : "FAIL") : "UNVERIFIED",
            ["checks"] = checks,
            ["blockingUnitMismatches"] = blocking,
            ["informational"] = info,
        };
    }
}

/// <summary>JS createTracker 와 같다: HWiNFO 타임스탬프 기반 신선도와 Stage 1 핀 래치.</summary>
public sealed class Tracker(IronConfig cfg)
{
    private readonly double _maxFrozen = cfg.Gates.GetProperty("freshness").GetProperty("maxFrozenMs").GetDouble();
    private readonly double _maxAge = cfg.Gates.GetProperty("freshness").GetProperty("maxAgeMs").GetDouble();
    private readonly int _latch = cfg.Thresholds.GetProperty("pinLatchReadings").GetInt32();
    private double? _lastTs;
    private double? _lastAdvanceAt;
    private int _pinOver;
    private bool _latched;
    private double? _offset;

    public int Invalid { get; private set; }
    public double? OffsetMs => _offset;

    public void SetClockOffset(double pingT, double t0, double t3) => _offset = pingT - (t0 + t3) / 2;

    public JsonObject Update(JsonObject model, double receivedAt)
    {
        var h = model["hwinfo"]!;
        var ts = h["timestampMs"] is JsonNode t ? t.GetValue<double>() : (double?)null;
        var reasons = new JsonArray();
        var o = new JsonObject { ["freshness"] = "UNKNOWN", ["ageMs"] = null, ["frozenMs"] = null, ["reasons"] = reasons, ["newMeasurement"] = false, ["pinAlert"] = false, ["pinOverCount"] = 0 };
        if (!h["ordered"]!.GetValue<bool>() || ts is null || (_lastTs is not null && ts < _lastTs))
        {
            Invalid++;
            o["freshness"] = "INVALID";
            reasons.Add("타임스탬프 순서 오류(RS<=TS<=RE 위반 또는 역행) — 프레임 폐기");
            return o;
        }

        var isNew = false;
        if (_lastTs is null || ts > _lastTs)
        {
            isNew = true;
            _lastTs = ts;
            _lastAdvanceAt = receivedAt;
        }

        o["newMeasurement"] = isNew;
        var frozen = receivedAt - _lastAdvanceAt!.Value;
        o["frozenMs"] = frozen;
        double? age = _offset is double off ? Js.Round(receivedAt + off - ts.Value, 0) : null;
        o["ageMs"] = Js.N(age);
        var stale = false;
        if (frozen > _maxFrozen) { stale = true; reasons.Add("HWiNFO 타임스탬프가 " + Js.Num(frozen) + " ms 동안 그대로"); }
        if (age is double a && a > _maxAge) { stale = true; reasons.Add("측정 후 " + Js.Num(a) + " ms 경과"); }
        o["freshness"] = stale ? "STALE" : age is null ? "FRESH_BY_PROGRESS" : "FRESH";

        var wv = model["wireview"]!;
        var present = wv["present"]!.GetValue<bool>();
        var presentPins = wv["presentPins"]!.GetValue<int>();
        if (stale || !present || presentPins == 0) { _pinOver = 0; _latched = false; }
        else if (isNew)
        {
            if (wv["anyPinOver"]!.GetValue<bool>()) { _pinOver = Math.Min(_latch, _pinOver + 1); if (_pinOver >= _latch) _latched = true; }
            else { _pinOver = 0; _latched = false; }
        }

        o["pinAlert"] = _latched;
        o["pinOverCount"] = _pinOver;
        return o;
    }
}
