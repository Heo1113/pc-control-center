using System.Globalization;
using GpuProfit.Core.Domain;

namespace GpuProfit.Presentation;

/// <summary>
/// Display formats with explicit units. A value that does not exist is shown as "N/A" (with its reason where there is
/// room), never as 0.
/// </summary>
public static class Fmt
{
    public const string NA = "N/A";
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    /// <summary>Times are shown in local time unless the user chose UTC.</summary>
    public static bool UseUtc { get; set; }

    public static DateTimeOffset Display(DateTimeOffset t) => UseUtc ? t.ToUniversalTime() : t.ToLocalTime();

    public static string Time(DateTimeOffset? t) => t is { } v ? Display(v).ToString("yyyy-MM-dd HH:mm:ss", Ko) + (UseUtc ? " UTC" : "") : NA;

    public static string ShortTime(DateTimeOffset? t) => t is { } v ? Display(v).ToString("MM-dd HH:mm", Ko) : NA;

    public static string Krw(decimal? v, int decimals = 0) => v is decimal d ? d.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), Ko) + " 원" : NA;

    public static string Krw(Maybe<decimal> v, bool withReason = false, int decimals = 0)
        => v.HasValue ? Krw(v.Value, decimals) : withReason ? $"{NA} — {v.Reason}" : NA;

    /// <summary>Signed money for profit figures ("+1,234 원" / "−1,234 원").</summary>
    public static string SignedKrw(Maybe<decimal> v, bool withReason = false)
        => v.HasValue ? (v.Value >= 0 ? "+" : "−") + Math.Abs(v.Value!.Value).ToString("N0", Ko) + " 원" : withReason ? $"{NA} — {v.Reason}" : NA;

    public static string KrwPerHour(Maybe<decimal> v) => v.HasValue ? v.Value!.Value.ToString("N0", Ko) + " 원/시간" : NA;

    public static string KrwPerKwh(Maybe<decimal> v) => v.HasValue ? v.Value!.Value.ToString("N1", Ko) + " 원/kWh" : NA;

    public static string KrwPerKwh(decimal? v) => v is decimal d ? d.ToString("N1", Ko) + " 원/kWh" : NA;

    public static string Kwh(double? v) => v is double d && double.IsFinite(d) ? d.ToString(Math.Abs(d) < 10 ? "0.000" : "N2", Ko) + " kWh" : NA;

    public static string Kwh(Maybe<double> v, bool withReason = false) => v.HasValue ? Kwh(v.Value) : withReason ? $"{NA} — {v.Reason}" : NA;

    public static string Wh(double v) => v.ToString(Math.Abs(v) < 100 ? "0.0" : "N0", Ko) + " Wh";

    public static string Watts(double? v) => v is double d && double.IsFinite(d) ? d.ToString("0.0", Ko) + " W" : NA;

    public static string Percent(double? fraction, int decimals = 0) => fraction is double f && double.IsFinite(f) ? (f * 100).ToString("0." + new string('0', Math.Max(0, decimals)), Ko).TrimEnd('.') + " %" : NA;

    public static string PercentValue(double? percent) => percent is double p && double.IsFinite(p) ? p.ToString("0.#", Ko) + " %" : NA;

    public static string Ratio(Maybe<decimal> fraction) => fraction.HasValue ? ((double)fraction.Value!.Value * 100).ToString("0.0", Ko) + " %" : NA;

    public static string Celsius(double? v) => v is double d ? d.ToString("0", Ko) + " °C" : NA;

    public static string Mhz(double? v) => v is double d ? d.ToString("N0", Ko) + " MHz" : NA;

    public static string Gib(long? bytes) => bytes is long b ? (b / (1024.0 * 1024 * 1024)).ToString("0.0", Ko) + " GiB" : NA;

    public static string Hours(double? hours) => hours is double h && double.IsFinite(h) ? h.ToString(h < 10 ? "0.00" : "N1", Ko) + " 시간" : NA;

    public static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            return NA;
        }

        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}시간 {t.Minutes:00}분" : t.TotalMinutes >= 1 ? $"{t.Minutes}분 {t.Seconds:00}초" : $"{t.Seconds}초";
    }

    public static string Payback(Maybe<double> hours) => hours.HasValue
        ? $"가동 {hours.Value!.Value.ToString("N0", Ko)} 시간 (24시간 가동 시 약 {(hours.Value.Value / 24).ToString("N0", Ko)}일)"
        : NA;

    public static string Origin(ValueOrigin origin) => ValueOriginText.ToKorean(origin);

    public static string Reason<T>(Maybe<T> v) where T : struct => v.HasValue ? "" : v.Reason ?? "";

    public static string Number(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    public static string Number(double? value, string format = "0.###") => value is double d ? d.ToString(format, CultureInfo.InvariantCulture) : "";

    /// <summary>Text box input for a local time ("yyyy-MM-dd HH:mm"), shown in the display time zone.</summary>
    public static string InputTime(DateTimeOffset t) => Display(t).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static bool TryParseInputTime(string? text, out DateTimeOffset value)
    {
        value = default;
        if (!DateTime.TryParseExact((text ?? "").Trim(), ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            return false;
        }

        var zone = UseUtc ? TimeZoneInfo.Utc : TimeZoneInfo.Local;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        value = new DateTimeOffset(local, zone.GetUtcOffset(local));
        return true;
    }

    public static TimeZoneInfo Zone => UseUtc ? TimeZoneInfo.Utc : TimeZoneInfo.Local;
}
