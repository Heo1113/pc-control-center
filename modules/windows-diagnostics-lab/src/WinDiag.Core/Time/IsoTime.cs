using System.Globalization;
using System.Text.RegularExpressions;

namespace WinDiag.Core.Time;

/// <summary>ISO 8601 times as Windows writes them (up to 9 fractional digits, 'Z' or an offset).</summary>
public static partial class IsoTime
{
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().Trim('‎', '‏'); // Windows inserts LTR/RTL marks in some rendered times
        // .NET parses at most 7 fractional digits; Windows writes 9 in some events (Kernel-General 1).
        trimmed = LongFraction().Replace(trimmed, m => m.Groups[1].Value + m.Groups[2].Value[..7]);
        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    public static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(\.)(\d{8,})", RegexOptions.CultureInvariant)]
    private static partial Regex LongFraction();
}

/// <summary>How times are shown: local time zone (default) or UTC. Storage is always UTC.</summary>
public sealed class TimeDisplay
{
    public static TimeDisplay Local { get; } = new(false);
    public static TimeDisplay Utc { get; } = new(true);

    private TimeDisplay(bool utc)
    {
        IsUtc = utc;
    }

    public bool IsUtc { get; }

    public string ZoneLabel => IsUtc ? "UTC" : $"현지 시간 ({FormatOffset(TimeZoneInfo.Local.BaseUtcOffset)})";

    public DateTimeOffset Convert(DateTimeOffset time) => IsUtc ? time.ToUniversalTime() : time.ToLocalTime();

    public string Full(DateTimeOffset time) => Convert(time).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string Short(DateTimeOffset time) => Convert(time).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string Date(DateTimeOffset time) => Convert(time).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string WithZone(DateTimeOffset time) => Convert(time).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    public static string FormatOffset(TimeSpan offset)
        => (offset < TimeSpan.Zero ? "UTC-" : "UTC+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    public static string Duration(TimeSpan span)
    {
        span = span.Duration();
        if (span.TotalSeconds < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0}초");
        }

        if (span.TotalMinutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:0}분");
        }

        if (span.TotalHours < 48)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{span.TotalHours:0.#}시간");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{span.TotalDays:0.#}일");
    }
}
