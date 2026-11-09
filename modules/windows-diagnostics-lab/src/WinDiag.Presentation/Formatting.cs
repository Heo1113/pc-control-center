using System.Globalization;
using WinDiag.Core.Events;
using WinDiag.Core.Time;

namespace WinDiag.Presentation;

/// <summary>Display text. A missing value is always "N/A" — never 0, never a guess. (Adapted from project A's Fmt.)</summary>
public static class Fmt
{
    public const string NA = "N/A";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Set from the settings (local time or UTC).</summary>
    public static TimeDisplay Display { get; set; } = TimeDisplay.Local;

    public static string Num(double? value, string format = "0.0", string unit = "")
        => value is double v && double.IsFinite(v) ? v.ToString(format, Inv) + (unit.Length > 0 ? " " + unit : "") : NA;

    public static string Int(long? value, string unit = "")
        => value is long v ? v.ToString("N0", Inv) + (unit.Length > 0 ? " " + unit : "") : NA;

    public static string Percent(double? value, string format = "0.0") => Num(value, format, "%");

    public static string Gib(long? bytes, string format = "0.0") => bytes is long b ? (b / 1073741824.0).ToString(format, Inv) + " GiB" : NA;

    public static string GibPair(long? used, long? total)
        => used is long u && total is long t
            ? string.Create(Inv, $"{u / 1073741824.0:0.0} / {t / 1073741824.0:0.0} GiB")
            : used is long only ? Gib(only) : NA;

    public static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", Inv) + " MB";

    public static string Time(DateTimeOffset? time) => time is { } t ? Display.Full(t) : NA;

    public static string Short(DateTimeOffset? time) => time is { } t ? Display.Short(t) : NA;

    public static string Clock(DateTimeOffset? time) => time is { } t ? Display.Convert(t).ToString("HH:mm:ss", Inv) : NA;

    public static string Ago(DateTimeOffset? time, DateTimeOffset now) => time is { } t ? TimeDisplay.Duration(now - t) + " 전" : NA;

    public static string Severity(DiagSeverity severity) => LevelText.Severity(severity);
}
