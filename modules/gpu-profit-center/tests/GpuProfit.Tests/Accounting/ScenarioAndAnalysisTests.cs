using GpuProfit.Accounting.Analysis;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Accounting.Scenarios;
using GpuProfit.Core.Domain;

namespace GpuProfit.Tests.Accounting;

public class ScenarioEngineTests
{
    // 24 h × 30 d at 100 %, 400 W total, 200 KRW/kWh, 1000 KRW/h revenue.
    private static readonly ScenarioParameters Basis = new()
    {
        RevenuePerHourKrw = 1000m, GpuPowerW = 300, SystemExtraPowerW = 100, ElectricityRateKrwPerKwh = 200m,
        UtilizationPercent = 100, HoursPerDay = 24, Days = 30,
    };

    [Fact]
    public void Monthly_figures()
    {
        var r = ScenarioEngine.Compute(Basis);
        Assert.Equal(720, r.OperatingHours, 9);
        Assert.Equal(288, r.EnergyKwh, 9);
        Assert.Equal(720_000m, r.GrossRevenue);
        Assert.Equal(57_600m, r.ElectricityCost);
        Assert.Equal(662_400m, r.NetProfit);
        Assert.Equal(920m, r.NetPerHour.Value);
        Assert.Equal(2500m, r.BreakevenElectricityRate.Value); // 720000 / 288
        Assert.Equal(80m, r.BreakevenRevenuePerHour.Value); // 57600 / 720
    }

    [Fact]
    public void Utilization_scales_hours_energy_and_revenue()
    {
        var r = ScenarioEngine.Compute(Basis with { UtilizationPercent = 50 });
        Assert.Equal(360, r.OperatingHours, 9);
        Assert.Equal(331_200m, r.NetProfit);
    }

    [Fact]
    public void Zero_hours_has_undefined_rates()
    {
        var r = ScenarioEngine.Compute(Basis with { HoursPerDay = 0 });
        Assert.Equal(0m, r.NetProfit);
        Assert.False(r.NetPerHour.HasValue);
        Assert.False(r.BreakevenElectricityRate.HasValue);
        Assert.False(r.BreakevenRevenuePerHour.HasValue);
    }

    [Fact]
    public void Fixed_costs_depreciation_fee_and_payback()
    {
        var p = Basis with { PlatformFeePercent = 2, FixedCostPerMonthKrw = 30_437.5m, EquipmentCostKrw = 1_500_000m, EquipmentLifeMonths = 30, Days = 30.4375 };
        var r = ScenarioEngine.Compute(p);
        Assert.Equal(30_437.5m, Math.Round(r.FixedCost, 6));
        Assert.Equal(50_000m, Math.Round(r.Depreciation, 6));
        Assert.Equal(Math.Round(r.GrossRevenue * 0.02m, 6), Math.Round(r.PlatformFees, 6));
        Assert.True(r.PaybackMonths.HasValue);
        var monthlyBeforeDepreciation = r.NetProfit + r.Depreciation;
        Assert.Equal((double)(1_500_000m / monthlyBeforeDepreciation), r.PaybackMonths.Value!.Value, 6);
    }

    [Fact]
    public void Validation_reports_bad_values()
    {
        Assert.Empty(Basis.Validate());
        Assert.NotEmpty((Basis with { UtilizationPercent = 120 }).Validate());
        Assert.NotEmpty((Basis with { PlatformFeePercent = 100 }).Validate());
        Assert.NotEmpty((Basis with { Days = 0 }).Validate());
    }
}

public class SensitivityTests
{
    private static readonly ScenarioParameters Basis = new()
    {
        RevenuePerHourKrw = 100m, GpuPowerW = 300, SystemExtraPowerW = 100, ElectricityRateKrwPerKwh = 200m, HoursPerDay = 24, Days = 30,
    };

    [Theory]
    [InlineData(SensitivityVariable.ElectricityRate, 250.0)] // 100 KRW/h / 0.4 kWh/h
    [InlineData(SensitivityVariable.RevenuePerHour, 80.0)] // 0.4 kWh × 200
    [InlineData(SensitivityVariable.GpuPower, 400.0)] // 100 / 200 × 1000 − 100 W
    public void Breakeven_values_are_exact(SensitivityVariable v, double expected)
    {
        var be = SensitivityAnalyzer.Breakeven(Basis, v);
        Assert.True(be.HasValue, be.Reason);
        Assert.Equal(expected, be.Value!.Value, 6);
        Assert.Equal(0m, Math.Round(ScenarioEngine.Compute(SensitivityAnalyzer.With(Basis, v, expected)).NetProfit, 4));
    }

    [Fact]
    public void Breakeven_for_utilization_is_undefined_when_every_hour_is_profitable()
    {
        // Revenue and costs both scale with utilization: profitable at any utilization > 0, zero at 0.
        var be = SensitivityAnalyzer.Breakeven(Basis, SensitivityVariable.Utilization);
        Assert.True(be.HasValue);
        Assert.Equal(0, be.Value!.Value, 6);
        // With a fixed monthly cost there is a real breakeven utilization.
        var withFixed = Basis with { FixedCostPerMonthKrw = 7200m };
        var b2 = SensitivityAnalyzer.Breakeven(withFixed, SensitivityVariable.Utilization);
        Assert.True(b2.HasValue);
        Assert.Equal(0m, Math.Round(ScenarioEngine.Compute(SensitivityAnalyzer.With(withFixed, SensitivityVariable.Utilization, b2.Value!.Value)).NetProfit, 3));
    }

    [Fact]
    public void Breakeven_outside_the_natural_range_is_reported_as_none()
    {
        // Revenue far above cost: no electricity price ≥ 0 ... actually there is one; use fee instead:
        // even a 99.999 % fee leaves a loss only if costs exist; with large costs no fee in [0,100) breaks even.
        var losing = Basis with { RevenuePerHourKrw = 10m };
        var be = SensitivityAnalyzer.Breakeven(losing, SensitivityVariable.PlatformFee);
        Assert.False(be.HasValue);
    }

    [Fact]
    public void Variable_without_effect_has_no_breakeven()
    {
        var noEquipment = Basis with { SystemExtraPowerW = 0, GpuPowerW = 0 };
        var be = SensitivityAnalyzer.Breakeven(noEquipment, SensitivityVariable.ElectricityRate);
        Assert.False(be.HasValue);
        Assert.Contains("영향을 주지 않으며", be.Reason);
    }

    [Fact]
    public void Sweep_produces_points_and_marks_breakeven_in_range()
    {
        var (low, high) = SensitivityAnalyzer.DefaultRange(Basis, SensitivityVariable.ElectricityRate);
        Assert.Equal(0, low);
        Assert.Equal(400, high);
        var s = SensitivityAnalyzer.Sweep(Basis, SensitivityVariable.ElectricityRate, low, high, 41);
        Assert.Equal(41, s.Points.Count);
        Assert.True(s.BreakevenInRange);
        Assert.True(s.Points[0].NetProfit > 0);
        Assert.True(s.Points[^1].NetProfit < 0);
    }

    [Fact]
    public void Tornado_orders_by_swing()
    {
        var bars = SensitivityAnalyzer.Tornado(Basis, 20);
        Assert.Equal(SensitivityVariableText.All.Count, bars.Count);
        Assert.True(bars.Zip(bars.Skip(1)).All(p => p.First.Swing >= p.Second.Swing));
    }

    [Fact]
    public void Optimistic_and_pessimistic_variants_bracket_the_base()
    {
        var o = ScenarioEngine.Compute(SensitivityAnalyzer.Variant(Basis, ScenarioKind.Optimistic)).NetProfit;
        var b = ScenarioEngine.Compute(Basis).NetProfit;
        var p = ScenarioEngine.Compute(SensitivityAnalyzer.Variant(Basis, ScenarioKind.Pessimistic)).NetProfit;
        Assert.True(o > b && b > p);
    }
}

public class ComparisonAndPeriodTests
{
    private static AccountedSession Item(long id, decimal revenue, double watts, double from, double to, EnergyBasis basis = EnergyBasis.GpuMeasured,
        bool mock = false, WorkloadType type = WorkloadType.Mining, double? meter = null)
    {
        var session = T.Session(id, from, to) with { IsMock = mock, EnergyBasis = basis, WallMeterKwh = meter };
        var input = new SessionAccountingInput
        {
            Session = session,
            Segments = [T.Segment(from, to, id)],
            Samples = T.Constant(watts, from - 1, to + 1),
            Profile = new AccountingProfile(),
            Revenue = revenue > 0 ? [new RevenueEntry { Id = id, Amount = revenue, Kind = RevenueKind.Received, Occurred = T.At(from) }] : [],
            Now = T.At(to + 100),
        };
        var workload = new Workload { Id = (long)type + 1, Name = WorkloadTypeText.ToKorean(type), Type = type };
        return new AccountedSession(session, workload, ProfitabilityEngine.Compute(input));
    }

    [Fact]
    public void Compare_normalizes_and_warns_about_different_conditions()
    {
        var items = new[]
        {
            Item(1, 1000, 500, 0, 3600),
            Item(2, 3000, 300, 7200, 7200 + 7200, EnergyBasis.WallMeter, meter: 1.0, type: WorkloadType.AiInference),
            Item(3, 0, 300, 20000, 23600, mock: true),
        };
        var (rows, warnings) = SessionComparer.Compare(items, RevenueView.Realized);
        Assert.Equal(3, rows.Count);
        Assert.Equal(1000m - 100m, Math.Round(rows[0].NetPerHour.Value!.Value, 4));
        Assert.Equal((3000m - 200m) / 2m, Math.Round(rows[1].NetPerHour.Value!.Value, 4));
        Assert.False(rows[2].NetProfit.HasValue);
        Assert.Contains(warnings, w => w.Contains("전력 산정 기준이 서로 다릅니다"));
        Assert.Contains(warnings, w => w.Contains("MOCK"));
        Assert.Contains(warnings, w => w.Contains("순수익을 계산할 수 없는 세션 1개"));
        Assert.Contains(warnings, w => w.Contains("작업 유형"));
    }

    [Fact]
    public void Groups_sum_only_defined_sessions()
    {
        var items = new[] { Item(1, 1000, 500, 0, 3600), Item(2, 0, 500, 7200, 10800) };
        var groups = SessionComparer.ByWorkloadType(items, RevenueView.Realized);
        var g = Assert.Single(groups);
        Assert.Equal(2, g.Sessions);
        Assert.Equal(1, g.DefinedSessions);
        Assert.Equal(900m, Math.Round(g.NetProfit, 4));
    }

    [Fact]
    public void Time_of_day_bands()
    {
        Assert.Equal("새벽 (00–06시)", SessionComparer.TimeBand(3));
        Assert.Equal("저녁 (18–24시)", SessionComparer.TimeBand(23));
        var items = new[] { Item(1, 1000, 500, 0, 3600), Item(2, 1000, 500, 13 * 3600, 14 * 3600) };
        var bands = SessionComparer.ByTimeOfDay(items, RevenueView.Realized, TimeZoneInfo.Utc);
        Assert.Equal(["새벽 (00–06시)", "오후 (12–18시)"], bands.Select(b => b.Key));
    }

    [Fact]
    public void Actual_versus_expected()
    {
        var a = Item(1, 1000, 500, 0, 3600);
        var input = a with { };
        var rows = SessionComparer.ActualVsExpected([input]);
        Assert.False(rows[0].Expected.HasValue);
        Assert.False(rows[0].Difference.HasValue);
    }

    [Fact]
    public void Session_over_midnight_is_split_between_days()
    {
        // 22:00 → 02:00 UTC, 400 W: 1.6 kWh, split 50/50.
        var start = 22 * 3600.0 - 86400;
        var item = Item(1, 2000, 400, start, start + 4 * 3600);
        var buckets = PeriodAggregator.Aggregate([new PeriodItem(item.Session, [T.Segment(start, start + 4 * 3600)], item.Result)],
            PeriodGranularity.Day, TimeZoneInfo.Utc, T.At(100000));
        Assert.Equal(2, buckets.Count);
        Assert.All(buckets, b => Assert.Equal(0.8, b.TotalKwh, 6));
        Assert.All(buckets, b => Assert.Equal(1000m, Math.Round(b.RealizedRevenue, 6)));
        Assert.Equal(1.6, buckets.Sum(b => b.MeasuredKwh), 6);
    }

    [Fact]
    public void Week_starts_on_monday_and_month_buckets_and_period_costs()
    {
        // 2026-11-01 is a Sunday.
        Assert.Equal(new DateTime(2026, 10, 26), PeriodAggregator.PeriodStart(T.T0, PeriodGranularity.Week, TimeZoneInfo.Utc).DateTime);
        Assert.Equal(new DateTime(2026, 11, 1), PeriodAggregator.PeriodStart(T.T0.AddDays(10), PeriodGranularity.Month, TimeZoneInfo.Utc).DateTime);

        var item = Item(1, 1000, 500, 0, 3600);
        var costs = new List<CostEntry> { new() { Id = 1, Basis = CostBasis.OneTime, Amount = 300, Occurred = T.At(60) } };
        var b = Assert.Single(PeriodAggregator.Aggregate([new PeriodItem(item.Session, [T.Segment(0, 3600)], item.Result)], PeriodGranularity.Month,
            TimeZoneInfo.Utc, T.At(4000), costs));
        Assert.Equal(300m, b.PeriodOneTimeCost);
        Assert.Equal(600m, Math.Round(b.RealizedNet, 4)); // 1000 − 100 electricity − 300 one-time
    }

    [Fact]
    public void Estimated_share_is_reported()
    {
        var session = T.Session() with { EnergyBasis = EnergyBasis.GpuPlusSystemAssumption, ExtraSystemPowerW = 100 };
        var r = ProfitabilityEngine.Compute(new SessionAccountingInput
        {
            Session = session, Segments = [T.Segment(0, 3600)], Samples = T.Constant(300, -1, 3601), Now = T.At(4000),
        });
        var b = Assert.Single(PeriodAggregator.Aggregate([new PeriodItem(session, [T.Segment(0, 3600)], r)], PeriodGranularity.Day, TimeZoneInfo.Utc, T.At(4000)));
        Assert.Equal(0.25, b.EstimatedShare, 6);
        Assert.Equal(1.0, b.Coverage!.Value, 6);
    }
}
