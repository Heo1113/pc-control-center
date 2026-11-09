using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LocalAiPerf.Runtimes;

/// <summary>
/// Defensive readers for JSON from a runtime server. Server data is treated as untrusted:
/// wrong types become null, numbers must be finite, strings are shortened and stripped of control characters.
/// </summary>
internal static class JsonRead
{
    public static JsonElement? Obj(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    public static JsonElement? Arr(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : null;

    public static double? Num(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
    }

    public static long? Long(JsonElement parent, string name)
    {
        var d = Num(parent, name);
        if (d is not double v || v != Math.Floor(v) || v < long.MinValue || v > long.MaxValue)
        {
            return null;
        }

        return (long)v;
    }

    public static int? Int(JsonElement parent, string name)
    {
        var l = Long(parent, name);
        return l is long v && v >= int.MinValue && v <= int.MaxValue ? (int)v : null;
    }

    public static bool? Bool(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    public static string? Str(JsonElement parent, string name, int limit = 120)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Clean(value.GetString(), limit)
            : null;

    public static string? Clean(string? text, int limit = 120)
    {
        if (text is null)
        {
            return null;
        }

        var sb = new StringBuilder(Math.Min(text.Length, limit));
        foreach (var c in text)
        {
            if (sb.Length >= limit)
            {
                break;
            }

            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString();
    }

    /// <summary>Formats a scalar JSON value for display (settings tables). Objects and arrays are skipped.</summary>
    public static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetDouble(out var d) && double.IsFinite(d) ? d.ToString("G", CultureInfo.InvariantCulture) : null,
        JsonValueKind.String => Clean(value.GetString(), 64),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}
