using System.Globalization;

namespace GpuProfit.Core.Domain;

public enum RevenueKind
{
    /// <summary>Actually received (on the user's account or wallet).</summary>
    Received = 0,

    /// <summary>Earned but not yet settled or paid out.</summary>
    Pending = 1,

    /// <summary>Reported by an external service (dashboard, API, statement) but not confirmed as received.</summary>
    ExternalReported = 2,

    /// <summary>An assumption made by the user (expected earnings).</summary>
    Expected = 3,
}

public static class RevenueKindText
{
    public static string ToKorean(RevenueKind kind) => kind switch
    {
        RevenueKind.Received => "실제 수령",
        RevenueKind.Pending => "미정산·미수령",
        RevenueKind.ExternalReported => "외부 서비스 보고",
        RevenueKind.Expected => "예상(가정)",
        _ => kind.ToString(),
    };

    public static IReadOnlyList<RevenueKind> All { get; } = Enum.GetValues<RevenueKind>();
}

/// <summary>An amount of revenue attached to a session (or to a workload for period totals).</summary>
public sealed record RevenueEntry
{
    public long Id { get; init; }
    public long? SessionId { get; init; }
    public long? WorkloadId { get; init; }
    public RevenueKind Kind { get; init; }
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 code or an asset ticker (KRW, USD, BTC ...). Converted to KRW with <see cref="ExchangeRate"/>.</summary>
    public string Currency { get; init; } = "KRW";

    /// <summary>True when platform or pool fees have not been taken from this amount yet (percentage fees apply to it).</summary>
    public bool IsGross { get; init; }

    public DateTimeOffset Occurred { get; init; }

    /// <summary>Who reported or paid it (free text; for imported files the provider name).</summary>
    public string? Provider { get; init; }

    /// <summary>Identifier at the provider (payout id, transaction id). Used to skip duplicates on import.</summary>
    public string? ExternalId { get; init; }

    public string? Note { get; init; }
    public ValueOrigin Origin { get; init; } = ValueOrigin.UserEntered;

    /// <summary>KRW per one unit of <see cref="Currency"/> fixed for this entry (for example the rate on the payout day).</summary>
    public decimal? KrwPerUnit { get; init; }
    public DateTimeOffset? RateAsOf { get; init; }
    public string? RateSource { get; init; }

    public DateTimeOffset Created { get; init; }
}

public enum CostCategory
{
    Fixed = 0,
    PlatformFee = 1,
    Network = 2,
    Other = 3,
}

public static class CostCategoryText
{
    public static string ToKorean(CostCategory category) => category switch
    {
        CostCategory.Fixed => "고정 운영비",
        CostCategory.PlatformFee => "플랫폼·풀 수수료",
        CostCategory.Network => "네트워크 비용",
        CostCategory.Other => "기타 비용",
        _ => category.ToString(),
    };

    public static IReadOnlyList<CostCategory> All { get; } = Enum.GetValues<CostCategory>();
}

public enum CostBasis
{
    /// <summary>A single amount (session scope: counted in that session; otherwise counted in the period it occurred).</summary>
    OneTime = 0,

    /// <summary>Amount per hour of session time.</summary>
    PerHour = 1,

    /// <summary>Amount per month, allocated to sessions in proportion to their hours (an estimate).</summary>
    PerMonth = 2,

    /// <summary>A percentage of the gross revenue (platform or pool fee).</summary>
    PercentOfRevenue = 3,
}

public static class CostBasisText
{
    public static string ToKorean(CostBasis basis) => basis switch
    {
        CostBasis.OneTime => "일회성 금액",
        CostBasis.PerHour => "시간당 금액",
        CostBasis.PerMonth => "월 금액 (시간 비례 배분)",
        CostBasis.PercentOfRevenue => "매출 대비 비율(%)",
        _ => basis.ToString(),
    };

    public static IReadOnlyList<CostBasis> All { get; } = Enum.GetValues<CostBasis>();
}

/// <summary>
/// A cost that is not electricity. Scope: a session (<see cref="SessionId"/>), a workload (<see cref="WorkloadId"/>),
/// or every session (both null).
/// </summary>
public sealed record CostEntry
{
    public long Id { get; init; }
    public long? SessionId { get; init; }
    public long? WorkloadId { get; init; }
    public CostCategory Category { get; init; }
    public CostBasis Basis { get; init; }

    /// <summary>The amount (for <see cref="CostBasis.PercentOfRevenue"/> the percentage, e.g. 1.5 for 1.5 %).</summary>
    public required decimal Amount { get; init; }

    public string Currency { get; init; } = "KRW";
    public DateTimeOffset Occurred { get; init; }
    public string? Note { get; init; }
    public bool Active { get; init; } = true;
    public ValueOrigin Origin { get; init; } = ValueOrigin.UserEntered;
    public DateTimeOffset Created { get; init; }

    public bool AppliesTo(WorkSession session)
        => Active && (SessionId is long sid ? sid == session.Id : WorkloadId is not long wid || wid == session.WorkloadId);
}

public enum DepreciationMethod
{
    /// <summary>Straight line over the useful life in months, allocated to sessions by their hours.</summary>
    StraightLineMonths = 0,

    /// <summary>Straight line over a number of operating hours.</summary>
    UsageHours = 1,
}

/// <summary>Equipment bought for the work (GPU, PSU, rig parts) and how its cost is spread.</summary>
public sealed record EquipmentAsset
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public required decimal PriceKrw { get; init; }
    public decimal SalvageKrw { get; init; }
    public DateTimeOffset Purchased { get; init; }
    public DepreciationMethod Method { get; init; }
    public double LifeMonths { get; init; } = 36;
    public double? LifeHours { get; init; }

    /// <summary>Only sessions of this workload carry the cost; null means every session.</summary>
    public long? WorkloadId { get; init; }

    public string? Note { get; init; }
    public bool Active { get; init; } = true;

    public bool AppliesTo(WorkSession session) => Active && (WorkloadId is not long wid || wid == session.WorkloadId);
}

/// <summary>KRW per one unit of a currency or asset, as of a time, with its source.</summary>
public sealed record ExchangeRate
{
    public long Id { get; init; }
    public required string Currency { get; init; }
    public required decimal KrwPerUnit { get; init; }
    public required DateTimeOffset AsOf { get; init; }
    public string? Source { get; init; }
    public ValueOrigin Origin { get; init; } = ValueOrigin.UserEntered;
    public string? Note { get; init; }
}

public static class CurrencyCodes
{
    public const string Krw = "KRW";

    /// <summary>Upper-case, trimmed, letters/digits only, 2..10 characters. Returns null when not acceptable.</summary>
    public static string? Normalize(string? code)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        return c.Length is >= 2 and <= 10 && c.All(char.IsAsciiLetterOrDigit) ? c : null;
    }

    public static bool IsKrw(string? code) => string.Equals(Normalize(code), Krw, StringComparison.Ordinal);
}

/// <summary>Invariant decimal text for storage (never culture dependent, never float).</summary>
public static class DecimalText
{
    public static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static decimal Parse(string text) => decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);

    public static bool TryParseUser(string? text, out decimal value)
    {
        var t = (text ?? "").Trim().Replace(",", "", StringComparison.Ordinal).Replace("₩", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseUserDouble(string? text, out double value)
    {
        var t = (text ?? "").Trim().Replace(",", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
}
