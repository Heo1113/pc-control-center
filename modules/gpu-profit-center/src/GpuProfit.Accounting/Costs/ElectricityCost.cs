using GpuProfit.Core.Domain;

namespace GpuProfit.Accounting.Costs;

public sealed record ElectricityCostResult(Maybe<decimal> CostKrw, Maybe<decimal> RateKrwPerKwh, bool IsEstimate, string Basis);

/// <summary>Turns kWh into KRW. The flat rate is exact for the price the user chose; the tiered mode is an estimate.</summary>
public static class ElectricityCostCalculator
{
    public static ElectricityCostResult Compute(Maybe<double> kwh, ElectricityTariff tariff, decimal? rateOverride = null)
    {
        var rate = EffectiveRate(tariff, kwh.Value, rateOverride, out var isEstimate, out var basis);
        if (!kwh.HasValue)
        {
            return new ElectricityCostResult(Maybe<decimal>.None("에너지를 계산할 수 없어 전력비도 계산할 수 없습니다: " + kwh.Reason), rate, isEstimate, basis);
        }

        if (!rate.HasValue)
        {
            return new ElectricityCostResult(Maybe<decimal>.None(rate.Reason!), rate, isEstimate, basis);
        }

        return new ElectricityCostResult(Maybe<decimal>.Of(ToDecimal(kwh.Value!.Value) * rate.Value!.Value), rate, isEstimate, basis);
    }

    /// <summary>KRW per kWh for this session: the override, the flat rate, or the tiered marginal estimate.</summary>
    public static Maybe<decimal> EffectiveRate(ElectricityTariff tariff, double? sessionKwh, decimal? rateOverride, out bool isEstimate, out string basis)
    {
        isEstimate = false;
        if (rateOverride is decimal o)
        {
            basis = $"세션별 지정 단가 {o:0.##} 원/kWh";
            return o >= 0 ? Maybe<decimal>.Of(o) : Maybe<decimal>.None("전기요금 단가가 음수입니다.");
        }

        if (tariff.Mode == TariffMode.FlatRate)
        {
            basis = $"사용자 지정 유효 단가 {tariff.FlatRateKrwPerKwh:0.##} 원/kWh";
            return tariff.FlatRateKrwPerKwh >= 0 ? Maybe<decimal>.Of(tariff.FlatRateKrwPerKwh) : Maybe<decimal>.None("전기요금 단가가 음수입니다.");
        }

        isEstimate = true;
        if (tariff.Tiers.Count == 0 || tariff.HouseholdMonthlyKwh is not double household || household < 0)
        {
            basis = "누진 단계 단순 추정 (조건 부족)";
            return Maybe<decimal>.None("누진 단계 추정에 필요한 단계표 또는 가구 월 사용량이 없습니다.");
        }

        var work = tariff.WorkloadMonthlyKwh is double m && m > 0 ? m : sessionKwh is double s && s > 0 ? s : 0;
        decimal rate;
        if (work > 0)
        {
            rate = (MonthlyBill(tariff, household + work) - MonthlyBill(tariff, household)) / ToDecimal(work);
        }
        else
        {
            rate = MarginalRate(tariff, household) * tariff.SurchargeMultiplier;
        }

        basis = $"누진 단계 단순 추정: 가구 월 {household:0.#} kWh에 작업 {work:0.###} kWh가 더해질 때의 증분 요금 ÷ 작업 kWh = {rate:0.##} 원/kWh"
                + (tariff.SurchargeMultiplier != 1m ? $" (세금·기금 배율 {tariff.SurchargeMultiplier:0.###} 포함)" : "")
                + ". 실제 고지서는 검침 기간·계절 단계·할인·절사에 따라 달라집니다.";
        return Maybe<decimal>.Of(rate);
    }

    /// <summary>The month's bill for a total consumption under the tier table (base charge of the tier reached + energy charges) × surcharge.</summary>
    public static decimal MonthlyBill(ElectricityTariff tariff, double totalKwh)
    {
        if (tariff.Tiers.Count == 0)
        {
            return 0;
        }

        decimal energy = 0;
        double previous = 0;
        TariffTier reached = tariff.Tiers[0];
        foreach (var tier in tariff.Tiers)
        {
            var upper = tier.UpToKwh ?? double.PositiveInfinity;
            if (totalKwh > previous)
            {
                reached = tier;
                energy += ToDecimal(Math.Min(totalKwh, upper) - previous) * tier.EnergyRateKrwPerKwh;
            }

            previous = upper;
            if (double.IsPositiveInfinity(upper))
            {
                break;
            }
        }

        if (totalKwh > previous)
        {
            // Consumption above the last bounded tier: charged at the last tier's rate.
            var last = tariff.Tiers[^1];
            energy += ToDecimal(totalKwh - previous) * last.EnergyRateKrwPerKwh;
            reached = last;
        }

        return (reached.BaseChargeKrw + energy) * tariff.SurchargeMultiplier;
    }

    private static decimal MarginalRate(ElectricityTariff tariff, double kwh)
    {
        foreach (var tier in tariff.Tiers)
        {
            if (kwh < (tier.UpToKwh ?? double.PositiveInfinity))
            {
                return tier.EnergyRateKrwPerKwh;
            }
        }

        return tariff.Tiers[^1].EnergyRateKrwPerKwh;
    }

    /// <summary>double → decimal keeping the double's precision (about 15 significant digits). Non-finite values are a bug.</summary>
    internal static decimal ToDecimal(double value)
        => double.IsFinite(value) ? (decimal)value : throw new ArgumentOutOfRangeException(nameof(value), "유한한 값이 아닙니다.");
}
