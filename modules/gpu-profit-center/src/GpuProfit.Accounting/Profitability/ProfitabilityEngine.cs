using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GpuProfit.Accounting.Costs;
using GpuProfit.Accounting.Energy;
using GpuProfit.Core.Domain;

namespace GpuProfit.Accounting.Profitability;

/// <summary>Everything needed to account one session. Built from the database by the app, or by hand in tests.</summary>
public sealed record SessionAccountingInput
{
    public required WorkSession Session { get; init; }
    public Workload? Workload { get; init; }
    public IReadOnlyList<SessionSegment> Segments { get; init; } = [];
    public IReadOnlyList<PowerSample> Samples { get; init; } = [];
    public AccountingProfile Profile { get; init; } = new();
    public IReadOnlyList<RevenueEntry> Revenue { get; init; } = [];
    public IReadOnlyList<CostEntry> Costs { get; init; } = [];
    public IReadOnlyList<EquipmentAsset> Equipment { get; init; } = [];
    public IReadOnlyList<ExchangeRate> Rates { get; init; } = [];

    /// <summary>End time used for a segment that is still open.</summary>
    public required DateTimeOffset Now { get; init; }
}

/// <summary>Which revenue amounts a profit figure counts.</summary>
public enum RevenueView
{
    /// <summary>Only amounts actually received: the confirmed result.</summary>
    Realized = 0,

    /// <summary>Received plus pending plus externally reported amounts: not confirmed.</summary>
    IncludingUnconfirmed = 1,

    /// <summary>The user's expected revenue (an assumption), to compare with what actually happened.</summary>
    Expected = 2,
}

public static class RevenueViewText
{
    public static string ToKorean(RevenueView view) => view switch
    {
        RevenueView.Realized => "실현 (실제 수령분만)",
        RevenueView.IncludingUnconfirmed => "미확정 포함 (수령+미수령+외부 보고, 확정 아님)",
        RevenueView.Expected => "예상 (사용자 가정)",
        _ => view.ToString(),
    };

    public static IReadOnlyList<RevenueKind> Kinds(RevenueView view) => view switch
    {
        RevenueView.Realized => [RevenueKind.Received],
        RevenueView.IncludingUnconfirmed => [RevenueKind.Received, RevenueKind.Pending, RevenueKind.ExternalReported],
        _ => [RevenueKind.Expected],
    };
}

public sealed record RevenueKindTotal(RevenueKind Kind, int Count, Maybe<decimal> AmountKrw, IReadOnlyList<string> UnconvertedCurrencies);

/// <summary>The profit figures for one revenue view.</summary>
public sealed record ProfitView
{
    public required RevenueView View { get; init; }
    public Maybe<decimal> Revenue { get; init; }
    public Maybe<decimal> PlatformFees { get; init; }
    public Maybe<decimal> TotalCost { get; init; }
    public Maybe<decimal> NetProfit { get; init; }
    public Maybe<decimal> NetPerHour { get; init; }
    public Maybe<decimal> NetPerKwh { get; init; }

    /// <summary>Net profit ÷ revenue (fraction; may be negative).</summary>
    public Maybe<decimal> Margin { get; init; }

    /// <summary>Net profit ÷ total cost (fraction).</summary>
    public Maybe<decimal> ReturnOnCost { get; init; }

    /// <summary>The highest electricity price (KRW/kWh) at which this revenue still covers every cost.</summary>
    public Maybe<decimal> BreakevenElectricityRate { get; init; }

    /// <summary>Operating hours needed for the profit before depreciation to repay the equipment price.</summary>
    public Maybe<double> PaybackHours { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record SessionAccountingResult
{
    public const int EngineVersion = 1;

    public required long SessionId { get; init; }
    public required DateTimeOffset ComputedAt { get; init; }

    public double RecordedSeconds { get; init; }
    public double PausedSeconds { get; init; }
    public int SegmentCount { get; init; }

    /// <summary>Hours that per-hour figures use: the user's work hours if entered, otherwise the recorded hours.</summary>
    public double BasisHours { get; init; }
    public required string HoursSource { get; init; }

    public bool ExecutionConfirmed { get; init; }
    public required string ExecutionEvidence { get; init; }

    public required SessionEnergyResult Energy { get; init; }
    public required ElectricityCostResult Electricity { get; init; }
    public IReadOnlyList<CostLine> OtherCosts { get; init; } = [];
    public decimal PlatformFeePercent { get; init; }

    /// <summary>All costs except percentage fees (electricity + other lines).</summary>
    public Maybe<decimal> CostBeforeFees { get; init; }

    /// <summary>Revenue needed (all of it gross, before percentage fees) to cover every cost.</summary>
    public Maybe<decimal> BreakevenRevenue { get; init; }

    public IReadOnlyList<RevenueKindTotal> RevenueByKind { get; init; } = [];
    public IReadOnlyList<ProfitView> Views { get; init; } = [];
    public IReadOnlyList<UsedRate> RatesUsed { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Calculation conditions, in display order (label → value). Stored with the result.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Conditions { get; init; } = [];

    /// <summary>SHA-256 of the inputs. A stored result whose hash differs from a fresh one is out of date.</summary>
    public required string InputsHash { get; init; }

    public ProfitView View(RevenueView view) => Views.First(v => v.View == view);
}

/// <summary>
/// The one economics engine used by the GUI, the command line and the tests. Pure: no database, no clock, no I/O.
/// </summary>
public static class ProfitabilityEngine
{
    public static SessionAccountingResult Compute(SessionAccountingInput input)
    {
        var session = input.Session;
        var profile = input.Profile;
        var warnings = new List<string>();

        // ---- time
        var windows = input.Segments.Select(s => TimeWindow.Of(s.Start, s.End ?? input.Now)).ToList();
        var normalized = TimeWindow.Normalize(windows);
        var recorded = normalized.Sum(w => w.Seconds);
        var spanEnd = input.Segments.Count == 0 ? session.Started : input.Segments.Max(s => s.End ?? input.Now);
        var span = Math.Max(0, (spanEnd - session.Started).TotalSeconds);
        var paused = Math.Max(0, span - recorded);
        var hours = session.WorkHoursOverride is double h && h >= 0 && double.IsFinite(h) ? h : recorded / 3600.0;
        var hoursSource = session.WorkHoursOverride is not null ? "사용자 입력 작업 시간" : "세션 기록 시간 (일시정지 제외)";

        // ---- energy
        var conditions = EnergyConditions.From(profile, session);
        var energy = SessionEnergyCalculator.Compute(input.Samples, normalized, session.DeviceIndices, conditions);
        if (energy.Coverage is double coverage && coverage < 0.9 && conditions.Basis != EnergyBasis.WallMeter && recorded > 0)
        {
            warnings.Add($"GPU 전력 관측률이 {coverage:P0}입니다. 미관측 구간의 에너지는 {(conditions.UnobservedPolicy == UnobservedPolicy.Exclude ? "포함되지 않았습니다" : "추정값으로 따로 더했습니다")}.");
        }

        if (energy.CounterDifference is double diff && Math.Abs(diff) > 0.1)
        {
            warnings.Add($"NVML 누적 에너지 카운터와 전력 적분 값이 {diff:P0} 차이 납니다. 센서 갱신 주기나 샘플 간격을 확인하세요.");
        }

        if (session.IsMock)
        {
            warnings.Add("MOCK(가상) 센서로 기록한 세션입니다. 실제 하드웨어 측정이 아닙니다.");
        }

        // ---- electricity
        var electricity = ElectricityCostCalculator.Compute(energy.TotalKwh, profile.Tariff, session.ElectricityRateKrwPerKwh);
        if (electricity.IsEstimate)
        {
            warnings.Add("누진 단계 전기요금은 단순 추정입니다. 가구 전체 사용량·검침 기간·할인에 따라 실제 요금과 다릅니다.");
        }

        // ---- other costs
        var converter = new CurrencyConverter(input.Rates);
        var (lines, feePercent, costNotes) = CostAllocator.Allocate(session, hours, input.Costs, input.Equipment, converter);
        warnings.AddRange(costNotes);
        if (feePercent is < 0 or >= 100)
        {
            warnings.Add($"수수료 비율 합계 {feePercent}%는 계산할 수 없는 값입니다 (0 이상 100 미만이어야 함).");
        }

        var costBeforeFees = Sum([electricity.CostKrw, .. lines.Select(l => l.AmountKrw)]);
        var nonElectricity = Sum(lines.Select(l => l.AmountKrw));
        var depreciation = Sum(lines.Where(l => l.IsDepreciation).Select(l => l.AmountKrw));
        var equipmentPrice = input.Equipment.Where(a => a.AppliesTo(session)).Sum(a => a.PriceKrw);

        var fee = feePercent / 100m;
        var breakevenRevenue = !costBeforeFees.HasValue
            ? Maybe<decimal>.None(costBeforeFees.Reason!)
            : fee is < 0 or >= 1
                ? Maybe<decimal>.None("수수료 비율이 100% 이상이라 손익분기 매출이 정의되지 않습니다.")
                : Maybe<decimal>.Of(costBeforeFees.Value!.Value / (1 - fee));

        // ---- revenue
        var byKind = new List<RevenueKindTotal>();
        foreach (var kind in RevenueKindText.All)
        {
            var entries = input.Revenue.Where(r => r.Kind == kind).ToList();
            var unconverted = new List<string>();
            decimal total = 0;
            foreach (var r in entries)
            {
                var krw = converter.ToKrw(r.Amount, r.Currency, r.Occurred, r.KrwPerUnit, r.RateAsOf, r.RateSource);
                if (krw.HasValue)
                {
                    total += krw.Value!.Value;
                }
                else if (!unconverted.Contains(CurrencyCodes.Normalize(r.Currency) ?? r.Currency))
                {
                    unconverted.Add(CurrencyCodes.Normalize(r.Currency) ?? r.Currency);
                }
            }

            byKind.Add(new RevenueKindTotal(kind, entries.Count,
                unconverted.Count > 0 ? Maybe<decimal>.None($"{string.Join(", ", unconverted)} 환율이 없어 원화 합계를 낼 수 없습니다.") : Maybe<decimal>.Of(total),
                unconverted));
        }

        if (converter.Used.Any(u => u.AfterOccurrence))
        {
            warnings.Add("일부 수익은 발생 시각 이후의 환율로 환산했습니다 (그 이전 환율이 없음).");
        }

        var views = Enum.GetValues<RevenueView>()
            .Select(v => BuildView(v, input, energy, electricity, nonElectricity, costBeforeFees, depreciation, equipmentPrice, fee, hours, converter))
            .ToList();

        // ---- evidence of execution
        string evidence;
        if (session.ExecutionConfirmed)
        {
            evidence = "사용자가 작업 실행을 확인함.";
        }
        else if (energy.GpuActiveShare is double active)
        {
            evidence = $"작업 실행 미확인. 관측 시간 중 GPU 사용률 {conditions.GpuActiveUtilizationPercent:0}% 이상이었던 비율 {active:P0} (작업 실행의 증거가 아니라 GPU 활동 관측값).";
        }
        else
        {
            evidence = "작업 실행 여부를 확인할 근거가 없습니다. 기록 구간은 사용자가 기록을 켠 시간일 뿐입니다.";
        }

        if (recorded <= 0)
        {
            warnings.Add("기록 시간이 0인 세션입니다. 시간당·에너지당 값은 정의되지 않습니다.");
        }

        var conditionsList = BuildConditions(input, conditions, electricity, hours, hoursSource, feePercent, converter.Used);
        return new SessionAccountingResult
        {
            SessionId = session.Id,
            ComputedAt = input.Now,
            RecordedSeconds = recorded,
            PausedSeconds = paused,
            SegmentCount = input.Segments.Count,
            BasisHours = hours,
            HoursSource = hoursSource,
            ExecutionConfirmed = session.ExecutionConfirmed,
            ExecutionEvidence = evidence,
            Energy = energy,
            Electricity = electricity,
            OtherCosts = lines,
            PlatformFeePercent = feePercent,
            CostBeforeFees = costBeforeFees,
            BreakevenRevenue = breakevenRevenue,
            RevenueByKind = byKind,
            Views = views,
            RatesUsed = [.. converter.Used],
            Warnings = warnings,
            Conditions = conditionsList,
            InputsHash = HashInputs(input),
        };
    }

    private static ProfitView BuildView(RevenueView view, SessionAccountingInput input, SessionEnergyResult energy, ElectricityCostResult electricity,
        Maybe<decimal> nonElectricity, Maybe<decimal> costBeforeFees, Maybe<decimal> depreciation, decimal equipmentPrice, decimal fee, double hours,
        CurrencyConverter converter)
    {
        var notes = new List<string>();
        var kinds = RevenueViewText.Kinds(view);
        var entries = input.Revenue.Where(r => kinds.Contains(r.Kind)).ToList();
        Maybe<decimal> revenue;
        decimal grossBase = 0;
        if (entries.Count == 0)
        {
            var anyRevenue = input.Revenue.Count > 0;
            if (input.Session.NoRevenueConfirmed)
            {
                revenue = Maybe<decimal>.Of(0);
                notes.Add("사용자가 '수익 없음'을 확인한 세션입니다 (비용만 발생).");
            }
            else if (view == RevenueView.Realized && anyRevenue)
            {
                revenue = Maybe<decimal>.Of(0);
                notes.Add("아직 실제로 수령한 수익이 없습니다. 미수령·보고·예상 금액은 확정 수익에 넣지 않았습니다.");
            }
            else
            {
                revenue = Maybe<decimal>.None(view switch
                {
                    RevenueView.Expected => "예상 수익이 입력되지 않았습니다.",
                    RevenueView.Realized => "수익이 입력되지 않았습니다 (0원으로 간주하지 않음).",
                    _ => "수령·미수령·외부 보고 수익이 입력되지 않았습니다.",
                });
            }
        }
        else
        {
            decimal total = 0;
            var missing = new List<string>();
            foreach (var r in entries)
            {
                var krw = converter.ToKrw(r.Amount, r.Currency, r.Occurred, r.KrwPerUnit, r.RateAsOf, r.RateSource);
                if (krw.HasValue)
                {
                    total += krw.Value!.Value;
                    if (r.IsGross)
                    {
                        grossBase += krw.Value!.Value;
                    }
                }
                else if (!missing.Contains(CurrencyCodes.Normalize(r.Currency) ?? r.Currency))
                {
                    missing.Add(CurrencyCodes.Normalize(r.Currency) ?? r.Currency);
                }
            }

            revenue = missing.Count > 0 ? Maybe<decimal>.None($"{string.Join(", ", missing)} 환율이 없어 매출을 원화로 계산할 수 없습니다.") : Maybe<decimal>.Of(total);
            if (view == RevenueView.IncludingUnconfirmed && entries.Any(r => r.Kind != RevenueKind.Received))
            {
                notes.Add("미수령·외부 보고 금액이 포함되어 있습니다. 확정 수익이 아닙니다.");
            }
        }

        var fees = revenue.HasValue && fee is >= 0 and < 1
            ? Maybe<decimal>.Of(grossBase * fee)
            : Maybe<decimal>.None(revenue.HasValue ? "수수료 비율이 올바르지 않습니다." : revenue.Reason!);
        // Without revenue the percentage fees are unknown; the total cost shown then excludes them.
        var totalCost = revenue.HasValue ? Sum([costBeforeFees, fees]) : costBeforeFees;
        if (!revenue.HasValue && fee > 0)
        {
            notes.Add("수익이 없어 비율 수수료는 총비용에 포함되지 않았습니다.");
        }

        var net = Subtract(revenue, totalCost);
        var kwh = energy.TotalKwh;
        Maybe<decimal> netPerHour = !net.HasValue ? Maybe<decimal>.None(net.Reason!)
            : hours <= 0 ? Maybe<decimal>.None("작업 시간이 0시간이라 시간당 값이 정의되지 않습니다.")
            : Maybe<decimal>.Of(net.Value!.Value / ElectricityCostCalculator.ToDecimal(hours));
        Maybe<decimal> netPerKwh = !net.HasValue ? Maybe<decimal>.None(net.Reason!)
            : !kwh.HasValue ? Maybe<decimal>.None(kwh.Reason!)
            : kwh.Value <= 0 ? Maybe<decimal>.None("에너지가 0 kWh라 에너지당 값이 정의되지 않습니다.")
            : Maybe<decimal>.Of(net.Value!.Value / ElectricityCostCalculator.ToDecimal(kwh.Value!.Value));
        Maybe<decimal> margin = !net.HasValue ? Maybe<decimal>.None(net.Reason!)
            : revenue.Value is not > 0 ? Maybe<decimal>.None("매출이 0이라 수익률이 정의되지 않습니다.")
            : Maybe<decimal>.Of(net.Value!.Value / revenue.Value!.Value);
        Maybe<decimal> roc = !net.HasValue ? Maybe<decimal>.None(net.Reason!)
            : totalCost.Value is not > 0 ? Maybe<decimal>.None("총비용이 0이라 비용 대비 수익률이 정의되지 않습니다.")
            : Maybe<decimal>.Of(net.Value!.Value / totalCost.Value!.Value);

        Maybe<decimal> breakevenRate;
        if (!revenue.HasValue || !fees.HasValue)
        {
            breakevenRate = Maybe<decimal>.None(revenue.Reason ?? fees.Reason!);
        }
        else if (!kwh.HasValue)
        {
            breakevenRate = Maybe<decimal>.None(kwh.Reason!);
        }
        else if (kwh.Value <= 0)
        {
            breakevenRate = Maybe<decimal>.None("에너지가 0 kWh라 손익분기 전력 단가가 정의되지 않습니다 (전기요금과 무관).");
        }
        else if (!nonElectricity.HasValue)
        {
            breakevenRate = Maybe<decimal>.None(nonElectricity.Reason!);
        }
        else
        {
            breakevenRate = Maybe<decimal>.Of((revenue.Value!.Value - fees.Value!.Value - nonElectricity.Value!.Value)
                                              / ElectricityCostCalculator.ToDecimal(kwh.Value!.Value));
            if (breakevenRate.Value < 0)
            {
                notes.Add("손익분기 전력 단가가 음수입니다: 전기요금이 0원이어도 다른 비용 때문에 손실입니다.");
            }
        }

        Maybe<double> payback;
        if (equipmentPrice <= 0)
        {
            payback = Maybe<double>.None("이 세션에 걸린 장비 비용이 없습니다.");
        }
        else if (!net.HasValue || !depreciation.HasValue)
        {
            payback = Maybe<double>.None(net.Reason ?? depreciation.Reason!);
        }
        else if (hours <= 0)
        {
            payback = Maybe<double>.None("작업 시간이 0시간이라 회수 기간을 계산할 수 없습니다.");
        }
        else
        {
            var perHour = (net.Value!.Value + depreciation.Value!.Value) / ElectricityCostCalculator.ToDecimal(hours);
            payback = perHour > 0
                ? Maybe<double>.Of((double)(equipmentPrice / perHour))
                : Maybe<double>.None("감가상각 전 이익이 0 이하라 장비 비용을 회수할 수 없습니다.");
        }

        return new ProfitView
        {
            View = view,
            Revenue = revenue,
            PlatformFees = fees,
            TotalCost = totalCost,
            NetProfit = net,
            NetPerHour = netPerHour,
            NetPerKwh = netPerKwh,
            Margin = margin,
            ReturnOnCost = roc,
            BreakevenElectricityRate = breakevenRate,
            PaybackHours = payback,
            Notes = notes,
        };
    }

    /// <summary>Sum of defined values; undefined (with the first reason) as soon as one value is undefined.</summary>
    public static Maybe<decimal> Sum(IEnumerable<Maybe<decimal>> values)
    {
        decimal total = 0;
        foreach (var v in values)
        {
            if (!v.HasValue)
            {
                return Maybe<decimal>.None(v.Reason!);
            }

            total += v.Value!.Value;
        }

        return Maybe<decimal>.Of(total);
    }

    private static Maybe<decimal> Subtract(Maybe<decimal> a, Maybe<decimal> b)
        => !a.HasValue ? Maybe<decimal>.None(a.Reason!) : !b.HasValue ? Maybe<decimal>.None(b.Reason!) : Maybe<decimal>.Of(a.Value!.Value - b.Value!.Value);

    private static List<KeyValuePair<string, string>> BuildConditions(SessionAccountingInput input, EnergyConditions c, ElectricityCostResult electricity,
        double hours, string hoursSource, decimal feePercent, IEnumerable<UsedRate> rates)
    {
        var list = new List<KeyValuePair<string, string>>
        {
            new("계산 엔진 버전", SessionAccountingResult.EngineVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("전력 산정 기준", EnergyBasisText.ToKorean(c.Basis)),
            new("최대 샘플 간격", $"{c.MaxSampleGapSeconds:0.#} 초 (초과 구간은 보간하지 않음)"),
            new("미관측 구간 처리", c.UnobservedPolicy == UnobservedPolicy.Exclude ? "제외 (에너지 미포함)" : "관측 평균 전력으로 추정해 별도 표시"),
            new("대상 GPU", input.Session.DeviceIndices is { Count: > 0 } d ? string.Join(", ", d.Select(i => $"GPU {i}")) : "기록된 모든 GPU"),
            new("시간 기준", $"{hoursSource}: {hours:0.####} 시간"),
            new("전기요금", electricity.Basis),
        };
        if (c.Basis is EnergyBasis.GpuPlusSystemAssumption or EnergyBasis.IncrementalGpu)
        {
            list.Add(new("시스템 추가 전력 가정", $"{c.ExtraSystemPowerW:0.#} W"));
        }

        if (c.Basis == EnergyBasis.IncrementalGpu)
        {
            list.Add(new("GPU 유휴 전력 가정", $"{c.IdleGpuPowerW:0.#} W"));
        }

        if (c.Basis == EnergyBasis.WallMeter)
        {
            list.Add(new("외부 전력계 입력", c.WallMeterKwh is double k ? $"{k:0.###} kWh" : "없음"));
        }

        list.Add(new("수수료 비율 합계", $"{feePercent:0.###} %"));
        foreach (var r in rates)
        {
            list.Add(new($"환율 {r.Currency}", $"{r.KrwPerUnit:0.########} 원 ({r.Source}{(r.AsOf is { } at ? $", {at:yyyy-MM-dd HH:mm}" : "")})"));
        }

        return list;
    }

    /// <summary>Hash of every input that can change the result (not of the clock, unless a segment is still open).</summary>
    public static string HashInputs(SessionAccountingInput input)
    {
        var s = input.Session;
        var payload = new
        {
            v = SessionAccountingResult.EngineVersion,
            session = new
            {
                s.Id, s.WorkloadId, s.State, s.Started, s.DeviceIndices, s.ExecutionConfirmed, s.NoRevenueConfirmed, s.EnergyBasis, s.ExtraSystemPowerW,
                s.IdleGpuPowerW, s.UnobservedPolicy, s.ElectricityRateKrwPerKwh, s.WallMeterKwh, s.WorkHoursOverride, s.IsMock,
            },
            segments = input.Segments.Select(g => new { g.Start, End = g.End ?? input.Now }),
            profile = input.Profile,
            samples = new
            {
                n = input.Samples.Count,
                first = input.Samples.Count > 0 ? input.Samples.Min(x => x.TimeMs) : 0,
                last = input.Samples.Count > 0 ? input.Samples.Max(x => x.TimeMs) : 0,
                sum = Math.Round(input.Samples.Sum(x => x.PowerW ?? 0), 3),
                nulls = input.Samples.Count(x => x.PowerW is null),
            },
            revenue = input.Revenue.OrderBy(r => r.Id).Select(r => new { r.Id, r.Kind, r.Amount, r.Currency, r.IsGross, r.KrwPerUnit, r.Occurred }),
            costs = input.Costs.Where(c => c.AppliesTo(s)).OrderBy(c => c.Id).Select(c => new { c.Id, c.Category, c.Basis, c.Amount, c.Currency }),
            equipment = input.Equipment.Where(a => a.AppliesTo(s)).OrderBy(a => a.Id).Select(a => new { a.Id, a.PriceKrw, a.SalvageKrw, a.Method, a.LifeMonths, a.LifeHours }),
            rates = input.Rates.OrderBy(r => r.Currency).ThenBy(r => r.AsOf).Select(r => new { r.Currency, r.KrwPerUnit, r.AsOf }),
        };
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
