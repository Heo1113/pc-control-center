using System.Globalization;

namespace LocalAiPerf.Core.Metrics;

public static class NumberText
{
    /// <summary>Signed percentage with one decimal; values that round to zero show as ±0.0%.</summary>
    public static string Delta(double? percent)
    {
        if (percent is not double value || !double.IsFinite(value))
        {
            return "N/A";
        }

        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        var sign = rounded > 0 ? "+" : rounded < 0 ? "-" : "±";
        return sign + Math.Abs(rounded).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    public static string Gib(long? bytes, string format = "0.0")
        => bytes is long b ? (b / 1024.0 / 1024 / 1024).ToString(format, CultureInfo.InvariantCulture) : "N/A";

    public static string Value(double? value, string format)
        => value is double v && double.IsFinite(v) ? v.ToString(format, CultureInfo.InvariantCulture) : "N/A";
}
