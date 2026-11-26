using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IronPoc;

/// <summary>
/// assets/iron-model.js 와 같은 결과를 내기 위한 JavaScript 동작 흉내: 객체 키 순서, Math.round, 숫자→문자열.
/// </summary>
internal static class Js
{
    /// <summary>
    /// Object.keys 순서: 배열 인덱스 형태의 키("0", "5" …)가 먼저 오름차순, 나머지는 문서 순서. 중복 키는 첫 위치에 마지막 값(JSON.parse 와 같음).
    /// </summary>
    public static List<KeyValuePair<string, JsonElement>> Keys(JsonElement obj)
    {
        var order = new List<string>();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
        {
            if (!values.ContainsKey(p.Name))
            {
                order.Add(p.Name);
            }

            values[p.Name] = p.Value;
        }

        var idx = order.Where(IsArrayIndex).OrderBy(k => uint.Parse(k, CultureInfo.InvariantCulture)).ToList();
        var rest = order.Where(k => !IsArrayIndex(k));
        return idx.Concat(rest).Select(k => new KeyValuePair<string, JsonElement>(k, values[k])).ToList();
    }

    public static bool IsArrayIndex(string k)
    {
        if (k.Length == 0 || k.Length > 10 || (k.Length > 1 && k[0] == '0') || !k.All(c => c is >= '0' and <= '9'))
        {
            return false;
        }

        return ulong.Parse(k, CultureInfo.InvariantCulture) < uint.MaxValue;
    }

    public static bool IsObj(JsonElement e) => e.ValueKind == JsonValueKind.Object;

    public static bool Has(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var p in obj.EnumerateObject())
        {
            if (p.NameEquals(name))
            {
                value = p.Value;
                found = true; // 마지막 값
            }
        }

        return found;
    }

    /// <summary>JS Math.round(v * 10^d) / 10^d — 0.5는 +∞ 쪽으로.</summary>
    public static double? Round(double? v, int d)
    {
        if (v is not double x || !double.IsFinite(x))
        {
            return null;
        }

        var f = Math.Pow(10, d);
        var y = x * f;
        var r = Math.Floor(y);
        if (y - r >= 0.5)
        {
            r += 1;
        }

        return r / f;
    }

    /// <summary>JS String(number).</summary>
    public static string Num(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "Infinity";
        if (double.IsNegativeInfinity(v)) return "-Infinity";
        if (v == Math.Floor(v) && Math.Abs(v) < 1e21) return v.ToString("0", CultureInfo.InvariantCulture);
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>JS String(value) for a JSON value.</summary>
    public static string Str(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => Num(e.GetDouble()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "[object Object]",
        JsonValueKind.Array => string.Join(",", e.EnumerateArray().Select(x => x.ValueKind is JsonValueKind.Null ? "" : Str(x))),
        _ => "",
    };

    public static JsonNode? N(double? v) => v is double d ? JsonValue.Create(d) : null;

    public static JsonArray Arr(IEnumerable<JsonNode?> items)
    {
        var a = new JsonArray();
        foreach (var i in items)
        {
            a.Add(i);
        }

        return a;
    }

    /// <summary>값 비교: 숫자는 double로, 객체는 키 집합과 값으로, 배열은 순서대로.</summary>
    public static bool DeepEqual(JsonElement a, JsonElement b, string path, List<string> diffs)
    {
        if (a.ValueKind is JsonValueKind.Number && b.ValueKind is JsonValueKind.Number)
        {
            if (a.GetDouble() != b.GetDouble()) { diffs.Add($"{path}: {a.GetRawText()} != {b.GetRawText()}"); return false; }
            return true;
        }

        if (a.ValueKind != b.ValueKind && !(IsBool(a) && IsBool(b)))
        {
            diffs.Add($"{path}: {a.ValueKind} != {b.ValueKind}");
            return false;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var ka = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var kb = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var ok = true;
                foreach (var k in ka.Keys.Union(kb.Keys))
                {
                    if (!ka.ContainsKey(k) || !kb.ContainsKey(k)) { diffs.Add($"{path}.{k}: 한쪽에만 있음"); ok = false; continue; }
                    ok &= DeepEqual(ka[k], kb[k], path + "." + k, diffs);
                }

                return ok;
            }
            case JsonValueKind.Array:
            {
                var la = a.EnumerateArray().ToList();
                var lb = b.EnumerateArray().ToList();
                if (la.Count != lb.Count) { diffs.Add($"{path}: 길이 {la.Count} != {lb.Count}"); return false; }
                var ok = true;
                for (var i = 0; i < la.Count; i++) ok &= DeepEqual(la[i], lb[i], $"{path}[{i}]", diffs);
                return ok;
            }
            case JsonValueKind.String:
                if (a.GetString() != b.GetString()) { diffs.Add($"{path}: \"{a.GetString()}\" != \"{b.GetString()}\""); return false; }
                return true;
            case JsonValueKind.True or JsonValueKind.False:
                if (a.GetBoolean() != b.GetBoolean()) { diffs.Add($"{path}: {a.GetBoolean()} != {b.GetBoolean()}"); return false; }
                return true;
            default:
                return true;
        }
    }

    private static bool IsBool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;
}
