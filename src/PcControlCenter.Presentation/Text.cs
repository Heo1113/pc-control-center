using System.Globalization;

namespace PcControlCenter.Presentation;

/// <summary>Formatting for the shell's own pages. A missing value is always N/A, never 0.</summary>
public static class Text
{
    public const string NA = "N/A";
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    public static string Num(double? value, string format, string unit = "")
        => value is double v && double.IsFinite(v) ? v.ToString(format, Ko) + (unit.Length > 0 ? " " + unit : "") : NA;

    public static string Percent(double? value) => Num(value, "0", "%");

    public static string Watts(double? value) => Num(value, "0.0", "W");

    public static string Celsius(double? value) => Num(value, "0", "°C");

    public static string Mhz(double? value) => Num(value, "0", "MHz");

    public static string Gib(long? bytes) => bytes is long b ? (b / (1024.0 * 1024 * 1024)).ToString("0.0", Ko) + " GiB" : NA;

    public static string GibPair(long? used, long? total) => used is null && total is null ? NA : $"{Gib(used)} / {Gib(total)}";

    /// <summary>Bytes in binary units: GiB with two decimals from 1 GiB, otherwise MiB (KiB below 1 MiB). A measured 0 stays "0 MiB".</summary>
    public static string Bytes(long? bytes)
    {
        if (bytes is not long b)
        {
            return NA;
        }

        var magnitude = Math.Abs(b);
        return magnitude >= 1L << 30 ? (b / (1024.0 * 1024 * 1024)).ToString("0.00", Ko) + " GiB"
            : magnitude >= 1L << 20 || b == 0 ? (b / (1024.0 * 1024)).ToString("0", Ko) + " MiB"
            : (b / 1024.0).ToString("0", Ko) + " KiB";
    }

    /// <summary>A difference of byte counts with its sign (+ / −).</summary>
    public static string SignedBytes(long? bytes) => bytes is long b ? (b > 0 ? "+" : b < 0 ? "−" : "") + Bytes(Math.Abs(b)) : NA;

    public static string Krw(decimal? value) => value is decimal d ? d.ToString("N0", Ko) + " 원" : NA;

    public static string Kwh(double? value) => value is double d && double.IsFinite(d) ? d.ToString(Math.Abs(d) < 10 ? "0.000" : "N2", Ko) + " kWh" : NA;

    public static string Clock(DateTimeOffset? time) => time is { } t ? t.ToLocalTime().ToString("HH:mm:ss", Ko) : NA;

    public static string DateTime(DateTimeOffset? time) => time is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Ko) : NA;

    public static string Age(TimeSpan age) => age.TotalSeconds < 0 ? "방금" :
        age.TotalSeconds < 60 ? $"{age.TotalSeconds:0.0}초 전" :
        age.TotalMinutes < 60 ? $"{age.TotalMinutes:0}분 전" : $"{age.TotalHours:0.#}시간 전";

    public static string Count(long value) => value.ToString("N0", Ko);
}
