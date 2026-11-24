namespace GpuProfit.Core.Domain;

public enum TariffMode
{
    /// <summary>One effective price per kWh chosen by the user (exact for that price).</summary>
    FlatRate = 0,

    /// <summary>
    /// Progressive (block) tariff with tiers entered by the user and the household's other monthly consumption.
    /// Always shown as an estimate: the real bill depends on the whole household's meter, the billing period,
    /// seasonal tiers, discounts and rounding, which this app does not know.
    /// </summary>
    TieredEstimate = 1,
}

public sealed record TariffTier
{
    /// <summary>Upper bound of the tier in kWh per month; null for the last (open) tier.</summary>
    public double? UpToKwh { get; init; }

    public decimal EnergyRateKrwPerKwh { get; init; }

    /// <summary>Fixed monthly charge that applies when the month's total falls in this tier.</summary>
    public decimal BaseChargeKrw { get; init; }
}

/// <summary>Electricity price used to turn kWh into KRW.</summary>
public sealed record ElectricityTariff
{
    public TariffMode Mode { get; init; } = TariffMode.FlatRate;

    /// <summary>Effective price per kWh (all taxes and funds included, as the user chooses).</summary>
    public decimal FlatRateKrwPerKwh { get; init; } = 200m;

    public IReadOnlyList<TariffTier> Tiers { get; init; } = [];

    /// <summary>The household's monthly consumption without this work, in kWh (needed for the tiered estimate).</summary>
    public double? HouseholdMonthlyKwh { get; init; }

    /// <summary>The work's assumed monthly consumption, in kWh. When null, each session is priced on its own.</summary>
    public double? WorkloadMonthlyKwh { get; init; }

    /// <summary>Multiplier for taxes and funds on top of the tier charges (for example 1.137), entered by the user.</summary>
    public decimal SurchargeMultiplier { get; init; } = 1m;

    public string? Note { get; init; }
}

/// <summary>Accounting defaults. Stored inside the database so a backup carries the conditions with the data.</summary>
public sealed record AccountingProfile
{
    public ElectricityTariff Tariff { get; init; } = new();
    public EnergyBasis DefaultEnergyBasis { get; init; } = EnergyBasis.GpuMeasured;

    /// <summary>Assumed power of the rest of the system (CPU, board, RAM, fans, PSU losses) in W.</summary>
    public double ExtraSystemPowerW { get; init; }

    /// <summary>Assumed GPU idle board power in W (for the incremental basis).</summary>
    public double IdleGpuPowerW { get; init; } = 20;

    /// <summary>
    /// Two samples further apart than this are not connected: the time between them is unobserved and no energy is
    /// interpolated for it.
    /// </summary>
    public double MaxSampleGapSeconds { get; init; } = 10;

    public UnobservedPolicy UnobservedPolicy { get; init; } = UnobservedPolicy.Exclude;

    /// <summary>GPU utilisation at or above this counts as "GPU active" in the observed activity figure.</summary>
    public double GpuActiveUtilizationPercent { get; init; } = 10;

    public const double HoursPerMonth = 730.5; // 365.25 days × 24 h / 12

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Tariff.Mode == TariffMode.FlatRate && Tariff.FlatRateKrwPerKwh < 0)
        {
            problems.Add("전기요금 단가는 0 이상이어야 합니다.");
        }

        if (Tariff.Mode == TariffMode.TieredEstimate)
        {
            if (Tariff.Tiers.Count == 0)
            {
                problems.Add("누진 단계 추정에는 단계가 하나 이상 필요합니다.");
            }

            if (Tariff.Tiers.Any(t => t.EnergyRateKrwPerKwh < 0 || t.BaseChargeKrw < 0))
            {
                problems.Add("누진 단계 단가와 기본요금은 0 이상이어야 합니다.");
            }

            var bounds = Tariff.Tiers.Select(t => t.UpToKwh).ToList();
            if (bounds.Take(bounds.Count - 1).Any(b => b is null) || bounds.Where(b => b is not null).Zip(bounds.Where(b => b is not null).Skip(1)).Any(p => p.Second <= p.First))
            {
                problems.Add("누진 단계 상한은 오름차순이어야 하며, 상한이 없는 단계는 마지막에만 둘 수 있습니다.");
            }

            if (Tariff.HouseholdMonthlyKwh is not double h || h < 0)
            {
                problems.Add("누진 단계 추정에는 작업을 제외한 가구 월 사용량(kWh)이 필요합니다.");
            }

            if (Tariff.SurchargeMultiplier <= 0)
            {
                problems.Add("세금·기금 배율은 0보다 커야 합니다.");
            }
        }

        if (ExtraSystemPowerW is < 0 or > 5000 || !double.IsFinite(ExtraSystemPowerW))
        {
            problems.Add("시스템 추가 전력 가정은 0~5000 W 사이여야 합니다.");
        }

        if (IdleGpuPowerW is < 0 or > 2000 || !double.IsFinite(IdleGpuPowerW))
        {
            problems.Add("GPU 유휴 전력 가정은 0~2000 W 사이여야 합니다.");
        }

        if (MaxSampleGapSeconds is < 1 or > 3600 || !double.IsFinite(MaxSampleGapSeconds))
        {
            problems.Add("최대 샘플 간격은 1~3600초 사이여야 합니다.");
        }

        if (GpuActiveUtilizationPercent is < 0 or > 100)
        {
            problems.Add("GPU 활성 판정 사용률은 0~100% 사이여야 합니다.");
        }

        return problems;
    }
}
