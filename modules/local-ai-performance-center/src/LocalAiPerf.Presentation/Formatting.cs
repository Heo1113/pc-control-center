using System.Globalization;
using LocalAiPerf.Core.Metrics;

namespace LocalAiPerf.Presentation;

/// <summary>Display text. A missing value is always "N/A" - never 0, never a guess.</summary>
public static class Fmt
{
    public const string NA = "N/A";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Num(double? value, string format = "0.0", string unit = "")
        => value is double v && double.IsFinite(v) ? v.ToString(format, Inv) + (unit.Length > 0 ? " " + unit : "") : NA;

    public static string Int(long? value, string unit = "")
        => value is long v ? v.ToString("N0", Inv) + (unit.Length > 0 ? " " + unit : "") : NA;

    public static string Percent(double? value, string format = "0.0") => Num(value, format, "%");

    public static string Gib(long? bytes, string format = "0.00") => bytes is long b ? (b / 1073741824.0).ToString(format, Inv) + " GiB" : NA;

    public static string GibPair(long? used, long? total)
        => used is long u && total is long t ? $"{u / 1073741824.0:0.0} / {t / 1073741824.0:0.0} GiB" : used is long only ? Gib(only, "0.0") : NA;

    public static string Bool(bool? value) => value switch { true => "예", false => "아니오", null => NA };

    public static string Time(DateTimeOffset? time) => time is DateTimeOffset t ? t.ToLocalTime().ToString("HH:mm:ss", Inv) : NA;

    public static string DateTime(DateTimeOffset? time) => time is DateTimeOffset t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Inv) : NA;

    public static string Unix(double? seconds) => seconds is double s ? DateTime(DateTimeOffset.FromUnixTimeMilliseconds((long)(s * 1000))) : NA;

    public static string Duration(TimeSpan? span)
    {
        if (span is not TimeSpan d)
        {
            return NA;
        }

        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}시간 {d.Minutes}분" : d.TotalMinutes >= 1 ? $"{d.Minutes}분 {d.Seconds}초" : $"{d.TotalSeconds:0}초";
    }

    public static string Delta(double? percent) => NumberText.Delta(percent);

    public static string Metric(MetricDefinition metric, double? value)
        => value is double v && double.IsFinite(v) ? v.ToString(metric.Format, Inv) + " " + metric.Unit : NA;

    public static string Bytes(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.00} GiB" : bytes >= 1L << 20 ? $"{bytes / 1048576.0:0.0} MB" : $"{bytes / 1024.0:0} KB";
}
