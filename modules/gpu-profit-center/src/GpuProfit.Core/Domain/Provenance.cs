namespace GpuProfit.Core.Domain;

/// <summary>
/// Where a number comes from. Kept on every stored value and every calculation component so that measured, entered,
/// externally reported and estimated amounts are never mixed up silently.
/// </summary>
public enum ValueOrigin
{
    /// <summary>Read from a sensor on this PC (for example NVML GPU board power).</summary>
    Measured = 0,

    /// <summary>Typed in by the user (wall power meter reading, received payout, invoice).</summary>
    UserEntered = 1,

    /// <summary>Reported by an external service (pool dashboard, cloud console, imported payout file).</summary>
    ExternalReported = 2,

    /// <summary>An assumption or an estimate (extra system power, idle deduction, prorated monthly cost).</summary>
    Estimated = 3,

    /// <summary>Calculated exactly from other values (for example an hourly rate times recorded hours).</summary>
    Calculated = 4,
}

public static class ValueOriginText
{
    public static string ToKorean(ValueOrigin origin) => origin switch
    {
        ValueOrigin.Measured => "실측",
        ValueOrigin.UserEntered => "사용자 입력",
        ValueOrigin.ExternalReported => "외부 보고",
        ValueOrigin.Estimated => "추정·가정",
        ValueOrigin.Calculated => "계산",
        _ => origin.ToString(),
    };
}

/// <summary>A value that may be undefined. When it is, <see cref="Reason"/> says why (shown instead of a number).</summary>
public readonly record struct Maybe<T>(T? Value, string? Reason) where T : struct
{
    public bool HasValue => Value.HasValue;

    public static Maybe<T> Of(T value) => new(value, null);

    public static Maybe<T> None(string reason) => new(null, reason);

    public T Or(T fallback) => Value ?? fallback;

    public override string ToString() => Value?.ToString() ?? $"N/A ({Reason})";
}
