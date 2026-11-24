using GpuProfit.Accounting.Costs;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Core.Domain;

namespace GpuProfit.Tests.Accounting;

public class ProfitabilityEngineTests
{
    private static readonly AccountingProfile Flat200 = new() { Tariff = new ElectricityTariff { FlatRateKrwPerKwh = 200m } };

    /// <summary>One hour at 500 W measured on GPU 0 → 0.5 kWh → 100 KRW at 200 KRW/kWh.</summary>
    private static SessionAccountingInput OneHour(WorkSession? session = null, IReadOnlyList<RevenueEntry>? revenue = null,
        IReadOnlyList<CostEntry>? costs = null, AccountingProfile? profile = null, double watts = 500)
        => new()
        {
            Session = session ?? T.Session(),
            Segments = [T.Segment(0, 3600)],
            Samples = T.Constant(watts, -1, 3601),
            Profile = profile ?? Flat200,
            Revenue = revenue ?? [],
            Costs = costs ?? [],
            Now = T.At(4000),
        };

    private static RevenueEntry Rev(decimal amount, RevenueKind kind = RevenueKind.Received, string currency = "KRW", bool gross = false, long id = 1)
        => new() { Id = id, Amount = amount, Kind = kind, Currency = currency, IsGross = gross, Occurred = T.At(1800) };

    [Fact]
    public void Basic_profit_figures()
    {
        var r = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(1000)]));
        Assert.Equal(1.0, r.BasisHours, 9);
        Assert.Equal(0.5, r.Energy.TotalKwh.Value!.Value, 6);
        Assert.Equal(100m, Math.Round(r.Electricity.CostKrw.Value!.Value, 4));
        var v = r.View(RevenueView.Realized);
        Assert.Equal(1000m, v.Revenue.Value);
        Assert.Equal(100m, Math.Round(v.TotalCost.Value!.Value, 4));
        Assert.Equal(900m, Math.Round(v.NetProfit.Value!.Value, 4));
        Assert.Equal(900m, Math.Round(v.NetPerHour.Value!.Value, 4));
        Assert.Equal(1800m, Math.Round(v.NetPerKwh.Value!.Value, 4));
        Assert.Equal(0.9m, Math.Round(v.Margin.Value!.Value, 6));
        Assert.Equal(9m, Math.Round(v.ReturnOnCost.Value!.Value, 6));
        // Breakeven electricity price: 1000 KRW / 0.5 kWh = 2000 KRW/kWh.
        Assert.Equal(2000m, Math.Round(v.BreakevenElectricityRate.Value!.Value, 4));
        Assert.Equal(100m, Math.Round(r.BreakevenRevenue.Value!.Value, 4));
    }

    [Fact]
    public void Missing_revenue_is_not_zero()
    {
        var r = ProfitabilityEngine.Compute(OneHour());
        var v = r.View(RevenueView.Realized);
        Assert.False(v.Revenue.HasValue);
        Assert.False(v.NetProfit.HasValue);
        Assert.Contains("0원으로 간주하지 않음", v.Revenue.Reason);
        Assert.True(v.TotalCost.HasValue); // costs are still known
        Assert.True(r.BreakevenRevenue.HasValue); // breakeven revenue does not need revenue
        Assert.False(v.BreakevenElectricityRate.HasValue);
    }

    [Fact]
    public void Cost_only_session_confirmed_by_user_shows_a_loss()
    {
        var r = ProfitabilityEngine.Compute(OneHour(session: T.Session() with { NoRevenueConfirmed = true }));
        var v = r.View(RevenueView.Realized);
        Assert.Equal(0m, v.Revenue.Value);
        Assert.Equal(-100m, Math.Round(v.NetProfit.Value!.Value, 4));
        Assert.False(v.Margin.HasValue); // revenue 0 → margin undefined
        Assert.Equal(-1m, Math.Round(v.ReturnOnCost.Value!.Value, 6));
    }

    [Fact]
    public void Pending_and_reported_revenue_never_count_as_realized()
    {
        var r = ProfitabilityEngine.Compute(OneHour(revenue:
        [
            Rev(300, RevenueKind.Pending, id: 1), Rev(500, RevenueKind.ExternalReported, id: 2), Rev(2000, RevenueKind.Expected, id: 3),
        ]));
        Assert.Equal(0m, r.View(RevenueView.Realized).Revenue.Value);
        Assert.Contains(r.View(RevenueView.Realized).Notes, n => n.Contains("아직 실제로 수령한 수익이 없습니다"));
        Assert.Equal(800m, r.View(RevenueView.IncludingUnconfirmed).Revenue.Value);
        Assert.Contains(r.View(RevenueView.IncludingUnconfirmed).Notes, n => n.Contains("확정 수익이 아닙니다"));
        Assert.Equal(2000m, r.View(RevenueView.Expected).Revenue.Value);
    }

    [Fact]
    public void Loss_when_costs_exceed_revenue_gives_negative_figures_and_negative_breakeven_rate()
    {
        var costs = new List<CostEntry> { new() { Id = 1, SessionId = 1, Basis = CostBasis.OneTime, Category = CostCategory.Other, Amount = 500 } };
        var r = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(300)], costs: costs));
        var v = r.View(RevenueView.Realized);
        Assert.Equal(-300m, Math.Round(v.NetProfit.Value!.Value, 4)); // 300 − (100 + 500)
        Assert.True(v.Margin.Value < 0);
        // (300 − 500) / 0.5 = −400 KRW/kWh: a loss even with free electricity.
        Assert.Equal(-400m, Math.Round(v.BreakevenElectricityRate.Value!.Value, 4));
        Assert.Contains(v.Notes, n => n.Contains("음수"));
    }

    [Fact]
    public void Zero_hour_session_has_undefined_per_hour_and_per_energy_values()
    {
        var input = OneHour(revenue: [Rev(100)]) with { Segments = [T.Segment(0, 0)] };
        var r = ProfitabilityEngine.Compute(input);
        var v = r.View(RevenueView.Realized);
        Assert.Equal(0, r.BasisHours);
        Assert.Equal(100m, v.NetProfit.Value);
        Assert.False(v.NetPerHour.HasValue);
        Assert.False(v.NetPerKwh.HasValue);
        Assert.False(v.BreakevenElectricityRate.HasValue);
        Assert.Contains(r.Warnings, w => w.Contains("기록 시간이 0"));
    }

    [Fact]
    public void Zero_watts_gives_zero_kwh_and_undefined_breakeven_rate()
    {
        var r = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(100)], watts: 0));
        Assert.Equal(0, r.Energy.TotalKwh.Value);
        Assert.Equal(0m, r.Electricity.CostKrw.Value);
        var v = r.View(RevenueView.Realized);
        Assert.Equal(100m, v.NetProfit.Value);
        Assert.False(v.NetPerKwh.HasValue);
        Assert.Contains("0 kWh", v.BreakevenElectricityRate.Reason);
        Assert.False(v.ReturnOnCost.HasValue); // total cost 0
    }

    [Fact]
    public void No_gpu_sensor_makes_electricity_and_net_undefined_unless_a_wall_meter_value_is_entered()
    {
        var noGpu = OneHour(revenue: [Rev(1000)]) with { Samples = [] };
        var r = ProfitabilityEngine.Compute(noGpu);
        Assert.False(r.Electricity.CostKrw.HasValue);
        Assert.False(r.View(RevenueView.Realized).NetProfit.HasValue);

        var withMeter = noGpu with { Session = T.Session() with { EnergyBasis = EnergyBasis.WallMeter, WallMeterKwh = 0.8 } };
        var m = ProfitabilityEngine.Compute(withMeter);
        Assert.Equal(160m, Math.Round(m.Electricity.CostKrw.Value!.Value, 4));
        Assert.Equal(840m, Math.Round(m.View(RevenueView.Realized).NetProfit.Value!.Value, 4));
    }

    [Fact]
    public void Percentage_fees_apply_to_gross_revenue_and_breakeven_revenue_includes_them()
    {
        var costs = new List<CostEntry> { new() { Id = 1, Basis = CostBasis.PercentOfRevenue, Category = CostCategory.PlatformFee, Amount = 10 } };
        var r = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(1000, gross: true, id: 1), Rev(500, gross: false, id: 2)], costs: costs));
        var v = r.View(RevenueView.Realized);
        Assert.Equal(100m, v.PlatformFees.Value); // 10 % of the gross 1000 only
        Assert.Equal(1300m, Math.Round(v.NetProfit.Value!.Value, 4)); // 1500 − 100 fee − 100 electricity
        Assert.Equal(Math.Round(100m / 0.9m, 6), Math.Round(r.BreakevenRevenue.Value!.Value, 6)); // electricity 100 / (1 − 10 %)
    }

    [Fact]
    public void Fee_of_100_percent_or_more_makes_breakeven_revenue_undefined()
    {
        var costs = new List<CostEntry> { new() { Id = 1, Basis = CostBasis.PercentOfRevenue, Amount = 100 } };
        var r = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(1000, gross: true)], costs: costs));
        Assert.False(r.BreakevenRevenue.HasValue);
        Assert.Contains(r.Warnings, w => w.Contains("100 미만"));
    }

    [Fact]
    public void Foreign_currency_revenue_needs_an_exchange_rate()
    {
        var input = OneHour(revenue: [Rev(2m, currency: "usd")]);
        var r = ProfitabilityEngine.Compute(input);
        Assert.False(r.View(RevenueView.Realized).Revenue.HasValue);
        Assert.Contains("USD", r.View(RevenueView.Realized).Revenue.Reason);

        var rates = new List<ExchangeRate>
        {
            new() { Currency = "USD", KrwPerUnit = 1300m, AsOf = T.At(-86400), Source = "test-old" },
            new() { Currency = "USD", KrwPerUnit = 1400m, AsOf = T.At(1000), Source = "test-current" },
            new() { Currency = "USD", KrwPerUnit = 1500m, AsOf = T.At(5000), Source = "test-later" },
        };
        var converted = ProfitabilityEngine.Compute(input with { Rates = rates });
        Assert.Equal(2800m, converted.View(RevenueView.Realized).Revenue.Value); // latest rate at or before t=1800
        Assert.Contains(converted.RatesUsed, u => u.Source == "test-current");
        Assert.Contains(converted.Conditions, c => c.Key == "환율 USD");
    }

    [Fact]
    public void Rate_fixed_on_the_entry_wins_and_crypto_amounts_keep_precision()
    {
        var entry = Rev(0.00012345m, currency: "BTC") with { KrwPerUnit = 100_000_000m, RateSource = "지급일 환율" };
        var r = ProfitabilityEngine.Compute(OneHour(revenue: [entry]));
        Assert.Equal(12345m, r.View(RevenueView.Realized).Revenue.Value);
    }

    [Fact]
    public void Only_later_rate_available_is_used_with_a_warning()
    {
        var rates = new List<ExchangeRate> { new() { Currency = "USD", KrwPerUnit = 1500m, AsOf = T.At(5000) } };
        var r = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(1, currency: "USD")]) with { Rates = rates });
        Assert.Equal(1500m, r.View(RevenueView.Realized).Revenue.Value);
        Assert.Contains(r.Warnings, w => w.Contains("이후의 환율"));
    }

    [Fact]
    public void Cost_allocation_bases()
    {
        var costs = new List<CostEntry>
        {
            new() { Id = 1, SessionId = 1, Basis = CostBasis.OneTime, Category = CostCategory.Network, Amount = 50 },
            new() { Id = 2, WorkloadId = 1, Basis = CostBasis.PerHour, Category = CostCategory.Other, Amount = 30 },
            new() { Id = 3, Basis = CostBasis.PerMonth, Category = CostCategory.Fixed, Amount = 73050 },
            new() { Id = 4, WorkloadId = 99, Basis = CostBasis.PerHour, Amount = 1000 }, // other workload: ignored
            new() { Id = 5, Basis = CostBasis.OneTime, Amount = 9999 }, // global one-time: period only
            new() { Id = 6, SessionId = 1, Basis = CostBasis.OneTime, Amount = 7777, Active = false },
        };
        var r = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(1000)], costs: costs));
        Assert.Equal(3, r.OtherCosts.Count);
        Assert.Equal(50m, r.OtherCosts.Single(l => l.CostEntryId == 1).AmountKrw.Value);
        Assert.Equal(30m, r.OtherCosts.Single(l => l.CostEntryId == 2).AmountKrw.Value);
        var monthly = r.OtherCosts.Single(l => l.CostEntryId == 3);
        Assert.Equal(100m, Math.Round(monthly.AmountKrw.Value!.Value, 6)); // 73050 / 730.5 h × 1 h
        Assert.Equal(ValueOrigin.Estimated, monthly.Origin);
        Assert.Contains(r.Warnings, w => w.Contains("기간별 합계"));
        Assert.Equal(720m, Math.Round(r.View(RevenueView.Realized).NetProfit.Value!.Value, 4)); // 1000 − 100 − 50 − 30 − 100
    }

    [Fact]
    public void Equipment_depreciation_and_payback()
    {
        var equipment = new List<EquipmentAsset>
        {
            new() { Id = 1, Name = "GPU", PriceKrw = 1_461_000m, SalvageKrw = 0, Method = DepreciationMethod.StraightLineMonths, LifeMonths = 24 },
            new() { Id = 2, Name = "PSU", PriceKrw = 200_000m, Method = DepreciationMethod.UsageHours, LifeHours = 20_000 },
        };
        var r = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(1000)]) with { Equipment = equipment });
        var gpu = r.OtherCosts.Single(l => l.EquipmentId == 1);
        Assert.Equal(1_461_000m / 24m / 730.5m, Math.Round(gpu.AmountKrw.Value!.Value, 10), 6);
        Assert.Equal(10m, r.OtherCosts.Single(l => l.EquipmentId == 2).AmountKrw.Value);
        var v = r.View(RevenueView.Realized);
        // Profit before depreciation: 1000 − 100 = 900 KRW/h → 1,661,000 / 900 h.
        Assert.Equal(1_661_000.0 / 900.0, v.PaybackHours.Value!.Value, 6);
    }

    [Fact]
    public void Payback_is_undefined_when_losing_money()
    {
        var equipment = new List<EquipmentAsset> { new() { Id = 1, Name = "GPU", PriceKrw = 1_000_000m, LifeMonths = 12 } };
        var r = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(50)]) with { Equipment = equipment });
        Assert.False(r.View(RevenueView.Realized).PaybackHours.HasValue);
        Assert.Contains("회수할 수 없습니다", r.View(RevenueView.Realized).PaybackHours.Reason);
    }

    [Fact]
    public void Work_hours_override_changes_per_hour_values_only()
    {
        var r = ProfitabilityEngine.Compute(OneHour(session: T.Session() with { WorkHoursOverride = 0.5 }, revenue: [Rev(1000)]));
        Assert.Equal(0.5, r.BasisHours);
        Assert.Equal(1800m, Math.Round(r.View(RevenueView.Realized).NetPerHour.Value!.Value, 4));
        Assert.Equal(0.5, r.Energy.TotalKwh.Value!.Value, 6); // energy still from the recorded segments
        Assert.Equal("사용자 입력 작업 시간", r.HoursSource);
    }

    [Fact]
    public void Pauses_are_excluded_from_recorded_time()
    {
        var input = OneHour(revenue: [Rev(1000)]) with { Segments = [T.Segment(0, 1800), T.Segment(2700, 3600)] };
        var r = ProfitabilityEngine.Compute(input);
        Assert.Equal(2700, r.RecordedSeconds, 6);
        Assert.Equal(900, r.PausedSeconds, 6);
        Assert.Equal(0.375, r.Energy.TotalKwh.Value!.Value, 6);
    }

    [Fact]
    public void Open_segment_runs_until_now()
    {
        var input = OneHour() with { Segments = [T.Segment(0, null)], Now = T.At(1800), Samples = T.Constant(500, -1, 1800) };
        var r = ProfitabilityEngine.Compute(input);
        Assert.Equal(1800, r.RecordedSeconds, 6);
    }

    [Fact]
    public void Execution_is_not_claimed_without_confirmation()
    {
        var r = ProfitabilityEngine.Compute(OneHour());
        Assert.False(r.ExecutionConfirmed);
        Assert.Contains("미확인", r.ExecutionEvidence);
        var confirmed = ProfitabilityEngine.Compute(OneHour(session: T.Session() with { ExecutionConfirmed = true }));
        Assert.Contains("사용자가 작업 실행을 확인", confirmed.ExecutionEvidence);
        var noSensor = ProfitabilityEngine.Compute(OneHour() with { Samples = [] });
        Assert.Contains("근거가 없습니다", noSensor.ExecutionEvidence);
    }

    [Fact]
    public void Inputs_hash_changes_when_conditions_change_and_is_stable_otherwise()
    {
        var a = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(1000)]));
        var b = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(1000)]));
        Assert.Equal(a.InputsHash, b.InputsHash);
        var rate = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(1000)], profile: Flat200 with { Tariff = new ElectricityTariff { FlatRateKrwPerKwh = 250m } }));
        Assert.NotEqual(a.InputsHash, rate.InputsHash);
        var revenue = ProfitabilityEngine.Compute(OneHour(revenue: [Rev(1001)]));
        Assert.NotEqual(a.InputsHash, revenue.InputsHash);
    }

    [Fact]
    public void Session_rate_override_beats_profile()
    {
        var r = ProfitabilityEngine.Compute(OneHour(session: T.Session() with { ElectricityRateKrwPerKwh = 100m }));
        Assert.Equal(50m, Math.Round(r.Electricity.CostKrw.Value!.Value, 4));
        Assert.False(r.Electricity.IsEstimate);
    }

    [Fact]
    public void Mock_session_is_flagged()
        => Assert.Contains(ProfitabilityEngine.Compute(OneHour(session: T.Session() with { IsMock = true })).Warnings, w => w.Contains("MOCK"));

    [Fact]
    public void Low_coverage_is_warned()
    {
        var input = OneHour() with { Samples = T.Constant(500, 0, 1000) };
        Assert.Contains(ProfitabilityEngine.Compute(input).Warnings, w => w.Contains("관측률"));
    }
}

public class ElectricityTariffTests
{
    // Illustrative tier table (values typed in by the user, not an official tariff).
    private static ElectricityTariff Tiered(double household, double? workMonthly = null, decimal surcharge = 1m) => new()
    {
        Mode = TariffMode.TieredEstimate,
        Tiers =
        [
            new TariffTier { UpToKwh = 200, EnergyRateKrwPerKwh = 100m, BaseChargeKrw = 1000m },
            new TariffTier { UpToKwh = 400, EnergyRateKrwPerKwh = 200m, BaseChargeKrw = 2000m },
            new TariffTier { UpToKwh = null, EnergyRateKrwPerKwh = 300m, BaseChargeKrw = 7000m },
        ],
        HouseholdMonthlyKwh = household,
        WorkloadMonthlyKwh = workMonthly,
        SurchargeMultiplier = surcharge,
    };

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(100, 1000 + 10000)]
    [InlineData(200, 1000 + 20000)]
    [InlineData(300, 2000 + 20000 + 20000)]
    [InlineData(500, 7000 + 20000 + 40000 + 30000)]
    public void Monthly_bill_by_tiers(double kwh, double expected)
        => Assert.Equal((decimal)expected, ElectricityCostCalculator.MonthlyBill(Tiered(0), kwh));

    [Fact]
    public void Tiered_estimate_uses_the_incremental_bill_and_is_flagged()
    {
        // Household 150 kWh; work 100 kWh/month → bill(250) − bill(150) = (2000+20000+10000) − (1000+15000) = 16000 → 160 KRW/kWh.
        var r = ElectricityCostCalculator.Compute(Maybe<double>.Of(10), Tiered(150, 100));
        Assert.True(r.IsEstimate);
        Assert.Equal(160m, r.RateKrwPerKwh.Value);
        Assert.Equal(1600m, r.CostKrw.Value);
        Assert.Contains("추정", r.Basis);
    }

    [Fact]
    public void Tiered_estimate_applies_the_surcharge_multiplier()
    {
        var r = ElectricityCostCalculator.Compute(Maybe<double>.Of(10), Tiered(150, 100, 1.137m));
        Assert.Equal(160m * 1.137m, r.RateKrwPerKwh.Value);
    }

    [Fact]
    public void Tiered_estimate_without_household_consumption_is_undefined()
    {
        var r = ElectricityCostCalculator.Compute(Maybe<double>.Of(10), Tiered(0) with { HouseholdMonthlyKwh = null });
        Assert.False(r.CostKrw.HasValue);
        Assert.Contains("가구 월 사용량", r.CostKrw.Reason);
    }

    [Fact]
    public void Profile_validation_rejects_bad_tiers()
    {
        var bad = new AccountingProfile { Tariff = Tiered(100) with { Tiers = [new TariffTier { UpToKwh = null, EnergyRateKrwPerKwh = 1 }, new TariffTier { UpToKwh = 100, EnergyRateKrwPerKwh = 2 }] } };
        Assert.NotEmpty(bad.Validate());
        Assert.Empty(new AccountingProfile().Validate());
        Assert.Contains(new AccountingProfile { MaxSampleGapSeconds = 0 }.Validate(), p => p.Contains("최대 샘플 간격"));
    }
}

public class SensitiveTextGuardTests
{
    [Theory]
    [InlineData("-----BEGIN EC PRIVATE KEY-----\nabc")]
    [InlineData("5HueCGU8rMjxEXxiPuD5BDku4MkFqeZyd4dZ1jvhTVqvbTLvyTJ")]
    [InlineData("private key: e9873d79c6d87dc0fb6a5778633389f4453213303da61f20bd67fc233aa33262")]
    [InlineData("seed: abandon ability able about above absent absorb abstract absurd abuse access accident")]
    [InlineData("api_key=sk-12345678abcdef")]
    public void Secrets_are_refused(string text) => Assert.NotNull(SensitiveTextGuard.Check(text));

    [Theory]
    [InlineData("txid e9873d79c6d87dc0fb6a5778633389f4453213303da61f20bd67fc233aa33262")]
    [InlineData("지갑 주소 0x52908400098527886E0F7030069857D2E4169EE7 로 지급")]
    [InlineData("야간 작업, 풀 수수료 1%")]
    [InlineData(null)]
    public void Ordinary_text_is_allowed(string? text) => Assert.Null(SensitiveTextGuard.Check(text));
}
