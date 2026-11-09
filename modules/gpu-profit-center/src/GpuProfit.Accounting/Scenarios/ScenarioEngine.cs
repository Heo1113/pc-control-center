using GpuProfit.Accounting.Costs;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Core.Domain;

namespace GpuProfit.Accounting.Scenarios;

public enum ScenarioKind
{
    Optimistic = 0,
    Base = 1,
    Pessimistic = 2,
    Custom = 3,
}

public static class ScenarioKindText
{
    public static string ToKorean(ScenarioKind kind) => kind switch
    {
        ScenarioKind.Optimistic => "낙관",
        ScenarioKind.Base => "기준",
        ScenarioKind.Pessimistic => "비관",
        ScenarioKind.Custom => "사용자 정의",
        _ => kind.ToString(),
    };

    public static IReadOnlyList<ScenarioKind> All { get; } = Enum.GetValues<ScenarioKind>();
}

/// <summary>
/// Conditions of a "what if" calculation. These are the user's assumptions; a scenario is a conditional calculation,
/// not a forecast of prices or electricity rates.
/// </summary>
public sealed record ScenarioParameters
{
    /// <summary>Gross revenue per operating hour, in KRW (before percentage fees).</summary>
    public decimal RevenuePerHourKrw { get; init; }

    public double GpuPowerW { get; init; } = 300;
    public double SystemExtraPowerW { get; init; }
    public decimal ElectricityRateKrwPerKwh { get; init; } = 200m;

    /// <summary>Share of the scheduled hours the work actually runs (가동률), 0–100 %.</summary>
    public double UtilizationPercent { get; init; } = 100;

    /// <summary>Scheduled hours per day (작업 시간).</summary>
    public double HoursPerDay { get; init; } = 24;

    /// <summary>Length of the period being calculated, in days.</summary>
    public double Days { get; init; } = 30;

    public double PlatformFeePercent { get; init; }
    public decimal FixedCostPerMonthKrw { get; init; }
    public decimal EquipmentCostKrw { get; init; }
    public decimal EquipmentSalvageKrw { get; init; }
    public double EquipmentLifeMonths { get; init; } = 36;

    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        if (RevenuePerHourKrw < 0) p.Add("시간당 매출은 0 이상이어야 합니다.");
        if (GpuPowerW is < 0 or > 10000 || !double.IsFinite(GpuPowerW)) p.Add("GPU 소비전력은 0~10000 W여야 합니다.");
        if (SystemExtraPowerW is < 0 or > 10000 || !double.IsFinite(SystemExtraPowerW)) p.Add("시스템 추가 전력은 0~10000 W여야 합니다.");
        if (ElectricityRateKrwPerKwh < 0) p.Add("전기요금 단가는 0 이상이어야 합니다.");
        if (UtilizationPercent is < 0 or > 100 || !double.IsFinite(UtilizationPercent)) p.Add("가동률은 0~100%여야 합니다.");
        if (HoursPerDay is < 0 or > 24 || !double.IsFinite(HoursPerDay)) p.Add("하루 작업 시간은 0~24시간이어야 합니다.");
        if (Days is <= 0 or > 3660 || !double.IsFinite(Days)) p.Add("기간은 0일 초과 3660일 이하여야 합니다.");
        if (PlatformFeePercent is < 0 or >= 100 || !double.IsFinite(PlatformFeePercent)) p.Add("수수료는 0% 이상 100% 미만이어야 합니다.");
        if (FixedCostPerMonthKrw < 0 || EquipmentCostKrw < 0 || EquipmentSalvageKrw < 0) p.Add("비용은 0 이상이어야 합니다.");
        if (EquipmentCostKrw > 0 && (EquipmentLifeMonths <= 0 || !double.IsFinite(EquipmentLifeMonths))) p.Add("장비 감가상각 기간은 0개월보다 커야 합니다.");
        return p;
    }
}

public sealed record ScenarioResult
{
    public required ScenarioParameters Parameters { get; init; }
    public double OperatingHours { get; init; }
    public double EnergyKwh { get; init; }
    public decimal GrossRevenue { get; init; }
    public decimal PlatformFees { get; init; }
    public decimal ElectricityCost { get; init; }
    public decimal FixedCost { get; init; }
    public decimal Depreciation { get; init; }
    public decimal TotalCost { get; init; }
    public decimal NetProfit { get; init; }
    public Maybe<decimal> NetPerHour { get; init; }
    public Maybe<decimal> NetPerKwh { get; init; }
    public Maybe<decimal> Margin { get; init; }
    public Maybe<decimal> BreakevenElectricityRate { get; init; }
    public Maybe<decimal> BreakevenRevenuePerHour { get; init; }

    /// <summary>Months of profit before depreciation needed to repay the equipment cost.</summary>
    public Maybe<double> PaybackMonths { get; init; }
}

public static class ScenarioEngine
{
    public const double DaysPerMonth = 30.4375; // 365.25 / 12

    public static ScenarioResult Compute(ScenarioParameters p)
    {
        var hours = Math.Max(0, p.HoursPerDay) * Math.Max(0, p.Days) * Math.Clamp(p.UtilizationPercent, 0, 100) / 100.0;
        var kwh = (Math.Max(0, p.GpuPowerW) + Math.Max(0, p.SystemExtraPowerW)) * hours / 1000.0;
        var h = ElectricityCostCalculator.ToDecimal(hours);
        var k = ElectricityCostCalculator.ToDecimal(kwh);
        var fee = (decimal)Math.Clamp(p.PlatformFeePercent, 0, 99.999999) / 100m;
        var months = ElectricityCostCalculator.ToDecimal(p.Days / DaysPerMonth);
        var gross = p.RevenuePerHourKrw * h;
        var fees = gross * fee;
        var electricity = k * p.ElectricityRateKrwPerKwh;
        var fixedCost = p.FixedCostPerMonthKrw * months;
        var depreciation = p.EquipmentCostKrw > 0 && p.EquipmentLifeMonths > 0
            ? Math.Max(0, p.EquipmentCostKrw - p.EquipmentSalvageKrw) / ElectricityCostCalculator.ToDecimal(p.EquipmentLifeMonths) * months
            : 0;
        var total = fees + electricity + fixedCost + depreciation;
        var net = gross - total;

        var beforeDepreciationPerMonth = months > 0 ? (net + depreciation) / months : 0;
        return new ScenarioResult
        {
            Parameters = p,
            OperatingHours = hours,
            EnergyKwh = kwh,
            GrossRevenue = gross,
            PlatformFees = fees,
            ElectricityCost = electricity,
            FixedCost = fixedCost,
            Depreciation = depreciation,
            TotalCost = total,
            NetProfit = net,
            NetPerHour = hours > 0 ? Maybe<decimal>.Of(net / h) : Maybe<decimal>.None("가동 시간이 0시간입니다."),
            NetPerKwh = kwh > 0 ? Maybe<decimal>.Of(net / k) : Maybe<decimal>.None("에너지가 0 kWh입니다."),
            Margin = gross > 0 ? Maybe<decimal>.Of(net / gross) : Maybe<decimal>.None("매출이 0입니다."),
            BreakevenElectricityRate = kwh > 0
                ? Maybe<decimal>.Of((gross - fees - fixedCost - depreciation) / k)
                : Maybe<decimal>.None("에너지가 0 kWh라 전기요금과 무관합니다."),
            BreakevenRevenuePerHour = hours > 0
                ? Maybe<decimal>.Of((electricity + fixedCost + depreciation) / (1 - fee) / h)
                : Maybe<decimal>.None("가동 시간이 0시간입니다."),
            PaybackMonths = p.EquipmentCostKrw <= 0
                ? Maybe<double>.None("장비 비용이 없습니다.")
                : beforeDepreciationPerMonth > 0
                    ? Maybe<double>.Of((double)(p.EquipmentCostKrw / beforeDepreciationPerMonth))
                    : Maybe<double>.None("감가상각 전 월 이익이 0 이하라 회수할 수 없습니다."),
        };
    }

    /// <summary>
    /// Starting values from a recorded session: measured average GPU power, the profile's system assumption, the
    /// effective electricity rate and the session's realized revenue per hour (expected revenue when nothing was received).
    /// </summary>
    public static ScenarioParameters FromSession(SessionAccountingResult result, AccountingProfile profile)
    {
        var avgW = result.Energy.Devices.Where(d => d.AveragePowerW is not null).Sum(d => d.AveragePowerW!.Value);
        decimal revenuePerHour = 0;
        if (result.BasisHours > 0)
        {
            var realized = result.View(RevenueView.Realized).Revenue;
            var expected = result.View(RevenueView.Expected).Revenue;
            var chosen = realized.Value is > 0 ? realized : expected;
            if (chosen.HasValue)
            {
                revenuePerHour = chosen.Value!.Value / ElectricityCostCalculator.ToDecimal(result.BasisHours);
            }
        }

        return new ScenarioParameters
        {
            RevenuePerHourKrw = Math.Round(revenuePerHour, 2),
            GpuPowerW = Math.Round(avgW, 1),
            SystemExtraPowerW = profile.ExtraSystemPowerW,
            ElectricityRateKrwPerKwh = result.Electricity.RateKrwPerKwh.Or(profile.Tariff.FlatRateKrwPerKwh),
            UtilizationPercent = 100,
            HoursPerDay = 24,
            Days = 30,
            PlatformFeePercent = (double)result.PlatformFeePercent,
        };
    }
}

public enum SensitivityVariable
{
    RevenuePerHour = 0,
    GpuPower = 1,
    SystemExtraPower = 2,
    ElectricityRate = 3,
    Utilization = 4,
    HoursPerDay = 5,
    PlatformFee = 6,
}

public static class SensitivityVariableText
{
    public static string ToKorean(SensitivityVariable v) => v switch
    {
        SensitivityVariable.RevenuePerHour => "작업 수익 단가 (원/시간)",
        SensitivityVariable.GpuPower => "GPU 소비전력 (W)",
        SensitivityVariable.SystemExtraPower => "시스템 추가 전력 (W)",
        SensitivityVariable.ElectricityRate => "전기요금 (원/kWh)",
        SensitivityVariable.Utilization => "GPU 가동률 (%)",
        SensitivityVariable.HoursPerDay => "하루 작업 시간 (시간)",
        SensitivityVariable.PlatformFee => "플랫폼 수수료 (%)",
        _ => v.ToString(),
    };

    public static IReadOnlyList<SensitivityVariable> All { get; } = Enum.GetValues<SensitivityVariable>();
}

public sealed record SensitivityPoint(double X, decimal NetProfit);

public sealed record SensitivitySeries
{
    public required SensitivityVariable Variable { get; init; }
    public required double BaseValue { get; init; }
    public IReadOnlyList<SensitivityPoint> Points { get; init; } = [];

    /// <summary>Value of the variable at which net profit is zero (all other conditions fixed), when it exists.</summary>
    public Maybe<double> Breakeven { get; init; }

    /// <summary>True when the breakeven value lies inside the plotted range.</summary>
    public bool BreakevenInRange { get; init; }
}

public sealed record TornadoBar(SensitivityVariable Variable, double LowValue, double HighValue, decimal NetAtLow, decimal NetAtHigh)
{
    public decimal Swing => Math.Abs(NetAtHigh - NetAtLow);
}

/// <summary>One-at-a-time sensitivity: vary one condition, keep the others, recompute with <see cref="ScenarioEngine"/>.</summary>
public static class SensitivityAnalyzer
{
    public static double Get(ScenarioParameters p, SensitivityVariable v) => v switch
    {
        SensitivityVariable.RevenuePerHour => (double)p.RevenuePerHourKrw,
        SensitivityVariable.GpuPower => p.GpuPowerW,
        SensitivityVariable.SystemExtraPower => p.SystemExtraPowerW,
        SensitivityVariable.ElectricityRate => (double)p.ElectricityRateKrwPerKwh,
        SensitivityVariable.Utilization => p.UtilizationPercent,
        SensitivityVariable.HoursPerDay => p.HoursPerDay,
        SensitivityVariable.PlatformFee => p.PlatformFeePercent,
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static ScenarioParameters With(ScenarioParameters p, SensitivityVariable v, double x) => v switch
    {
        SensitivityVariable.RevenuePerHour => p with { RevenuePerHourKrw = (decimal)Math.Max(0, x) },
        SensitivityVariable.GpuPower => p with { GpuPowerW = Math.Max(0, x) },
        SensitivityVariable.SystemExtraPower => p with { SystemExtraPowerW = Math.Max(0, x) },
        SensitivityVariable.ElectricityRate => p with { ElectricityRateKrwPerKwh = (decimal)Math.Max(0, x) },
        SensitivityVariable.Utilization => p with { UtilizationPercent = Math.Clamp(x, 0, 100) },
        SensitivityVariable.HoursPerDay => p with { HoursPerDay = Math.Clamp(x, 0, 24) },
        SensitivityVariable.PlatformFee => p with { PlatformFeePercent = Math.Clamp(x, 0, 99.999) },
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    /// <summary>Natural limits of each variable (the plotted range is clipped to them).</summary>
    public static (double Min, double Max) Limits(SensitivityVariable v) => v switch
    {
        SensitivityVariable.Utilization => (0, 100),
        SensitivityVariable.HoursPerDay => (0, 24),
        SensitivityVariable.PlatformFee => (0, 99.999),
        _ => (0, double.MaxValue),
    };

    /// <summary>Default range: 0 % to 200 % of the base value (or a fixed span when the base is 0), clipped to the limits.</summary>
    public static (double Low, double High) DefaultRange(ScenarioParameters p, SensitivityVariable v)
    {
        var b = Get(p, v);
        var (min, max) = Limits(v);
        var span = b > 0 ? b : v switch
        {
            SensitivityVariable.RevenuePerHour => 1000,
            SensitivityVariable.GpuPower or SensitivityVariable.SystemExtraPower => 300,
            SensitivityVariable.ElectricityRate => 300,
            SensitivityVariable.Utilization => 50,
            SensitivityVariable.HoursPerDay => 12,
            _ => 10,
        };
        return (Math.Max(min, b - span), Math.Min(max, b + span));
    }

    public static SensitivitySeries Sweep(ScenarioParameters p, SensitivityVariable v, double low, double high, int steps = 41)
    {
        if (steps < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(steps));
        }

        if (high < low)
        {
            (low, high) = (high, low);
        }

        var points = new List<SensitivityPoint>(steps);
        for (var i = 0; i < steps; i++)
        {
            var x = low + (high - low) * i / (steps - 1);
            points.Add(new SensitivityPoint(x, ScenarioEngine.Compute(With(p, v, x)).NetProfit));
        }

        var breakeven = Breakeven(p, v);
        return new SensitivitySeries
        {
            Variable = v,
            BaseValue = Get(p, v),
            Points = points,
            Breakeven = breakeven,
            BreakevenInRange = breakeven.Value is double be && be >= low && be <= high,
        };
    }

    /// <summary>
    /// Net profit is linear in each single variable (others fixed), so the zero crossing is solved exactly from two
    /// evaluations and checked against a third. Outside the variable's natural limits there is no breakeven.
    /// </summary>
    public static Maybe<double> Breakeven(ScenarioParameters p, SensitivityVariable v)
    {
        var (min, max) = Limits(v);
        var x0 = min;
        var x1 = v switch
        {
            SensitivityVariable.Utilization => 100,
            SensitivityVariable.HoursPerDay => 24,
            SensitivityVariable.PlatformFee => 50,
            _ => Math.Max(1, Get(p, v) * 2),
        };
        var y0 = (double)ScenarioEngine.Compute(With(p, v, x0)).NetProfit;
        var y1 = (double)ScenarioEngine.Compute(With(p, v, x1)).NetProfit;
        var slope = (y1 - y0) / (x1 - x0);
        if (Math.Abs(slope) < 1e-12)
        {
            return Maybe<double>.None(y0 >= 0
                ? "이 조건은 순수익에 영향을 주지 않으며, 항상 이익 또는 손익 0입니다."
                : "이 조건은 순수익에 영향을 주지 않으며, 항상 손실입니다.");
        }

        var root = x0 - y0 / slope;
        var check = (double)ScenarioEngine.Compute(With(p, v, Math.Clamp(root, min, max))).NetProfit;
        if (root < min - 1e-9 || root > max + 1e-9)
        {
            return Maybe<double>.None(slope > 0
                ? $"가능한 범위({min:0.##} 이상)에서 손익분기점이 없습니다: 최소값에서도 {(y0 >= 0 ? "이익" : "손실")}입니다."
                : "가능한 범위에서 손익분기점이 없습니다.");
        }

        var scale = Math.Max(1, Math.Abs(y0) + Math.Abs(y1));
        return Math.Abs(check) <= scale * 1e-6 ? Maybe<double>.Of(root) : Maybe<double>.None("손익분기점을 정확히 계산하지 못했습니다.");
    }

    /// <summary>Net profit when each variable alone moves ±<paramref name="percent"/> % (clipped to its limits).</summary>
    public static IReadOnlyList<TornadoBar> Tornado(ScenarioParameters p, double percent = 20)
    {
        var bars = new List<TornadoBar>();
        foreach (var v in SensitivityVariableText.All)
        {
            var b = Get(p, v);
            var (min, max) = Limits(v);
            var low = Math.Clamp(b * (1 - percent / 100), min, max);
            var high = Math.Clamp(b * (1 + percent / 100), min, max);
            bars.Add(new TornadoBar(v, low, high, ScenarioEngine.Compute(With(p, v, low)).NetProfit, ScenarioEngine.Compute(With(p, v, high)).NetProfit));
        }

        return [.. bars.OrderByDescending(b => b.Swing)];
    }

    /// <summary>Optimistic and pessimistic variants of a base scenario: revenue ±x %, power and electricity ∓x %.</summary>
    public static ScenarioParameters Variant(ScenarioParameters basis, ScenarioKind kind, double percent = 20)
    {
        var f = kind switch
        {
            ScenarioKind.Optimistic => 1,
            ScenarioKind.Pessimistic => -1,
            _ => 0,
        } * percent / 100.0;
        return basis with
        {
            RevenuePerHourKrw = Math.Round(basis.RevenuePerHourKrw * (decimal)(1 + f), 2),
            GpuPowerW = Math.Round(basis.GpuPowerW * (1 - f), 1),
            ElectricityRateKrwPerKwh = Math.Round(basis.ElectricityRateKrwPerKwh * (decimal)(1 - f), 2),
        };
    }
}
