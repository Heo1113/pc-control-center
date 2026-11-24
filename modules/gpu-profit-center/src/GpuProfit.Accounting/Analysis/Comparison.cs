using GpuProfit.Accounting.Profitability;
using GpuProfit.Core.Domain;

namespace GpuProfit.Accounting.Analysis;

public sealed record AccountedSession(WorkSession Session, Workload? Workload, SessionAccountingResult Result);

public sealed record CompareRow
{
    public required long SessionId { get; init; }
    public required string Label { get; init; }
    public string WorkloadName { get; init; } = "";
    public WorkloadType? Type { get; init; }
    public DateTimeOffset Started { get; init; }
    public double Hours { get; init; }
    public Maybe<double> Kwh { get; init; }
    public double? AveragePowerW { get; init; }
    public double? Coverage { get; init; }
    public Maybe<decimal> Revenue { get; init; }
    public Maybe<decimal> TotalCost { get; init; }
    public Maybe<decimal> NetProfit { get; init; }
    public Maybe<decimal> NetPerHour { get; init; }
    public Maybe<decimal> NetPerKwh { get; init; }
    public Maybe<decimal> CostPerHour { get; init; }
    public Maybe<decimal> RevenuePerHour { get; init; }
    public string EnergyBasis { get; init; } = "";
    public Maybe<decimal> ElectricityRate { get; init; }
    public bool IsMock { get; init; }
    public bool ExecutionConfirmed { get; init; }
}

public sealed record GroupRow
{
    public required string Key { get; init; }
    public int Sessions { get; init; }

    /// <summary>Sessions whose net profit is defined (only they enter the sums below).</summary>
    public int DefinedSessions { get; init; }

    public double Hours { get; init; }
    public double Kwh { get; init; }
    public decimal Revenue { get; init; }
    public decimal TotalCost { get; init; }
    public decimal NetProfit { get; init; }
    public Maybe<decimal> NetPerHour { get; init; }
    public Maybe<decimal> NetPerKwh { get; init; }
}

public sealed record ActualVsExpectedRow(long SessionId, string Label, Maybe<decimal> Actual, Maybe<decimal> Expected, Maybe<decimal> Difference, Maybe<decimal> Ratio);

public static class SessionComparer
{
    public static (IReadOnlyList<CompareRow> Rows, IReadOnlyList<string> Warnings) Compare(IReadOnlyList<AccountedSession> items, RevenueView view)
    {
        var rows = items.Select(i => Row(i, view)).ToList();
        var warnings = new List<string>();
        if (items.Count < 2)
        {
            return (rows, warnings);
        }

        var bases = items.Select(i => i.Result.Energy.Conditions.Basis).Distinct().ToList();
        if (bases.Count > 1)
        {
            warnings.Add($"전력 산정 기준이 서로 다릅니다 ({string.Join(", ", bases.Select(EnergyBasisText.ToKorean))}). 에너지·전력비를 직접 비교하기 어렵습니다.");
        }

        var rates = items.Select(i => i.Result.Electricity.RateKrwPerKwh.Value).Distinct().ToList();
        if (rates.Count > 1)
        {
            warnings.Add("전기요금 단가가 세션마다 다릅니다. 전력비 차이의 일부는 단가 차이 때문입니다.");
        }

        if (items.Any(i => i.Result.Electricity.IsEstimate))
        {
            warnings.Add("일부 세션의 전기요금은 누진 단계 단순 추정입니다.");
        }

        if (items.Select(i => i.Session.IsMock).Distinct().Count() > 1)
        {
            warnings.Add("MOCK(가상) 센서 세션과 실제 측정 세션이 섞여 있습니다.");
        }
        else if (items.All(i => i.Session.IsMock))
        {
            warnings.Add("모든 세션이 MOCK(가상) 센서 기록입니다. 실제 측정이 아닙니다.");
        }

        var lowCoverage = items.Where(i => i.Result.Energy.Coverage is < 0.9 && i.Result.Energy.Conditions.Basis != EnergyBasis.WallMeter).ToList();
        if (lowCoverage.Count > 0)
        {
            warnings.Add($"전력 관측률이 90% 미만인 세션이 {lowCoverage.Count}개 있습니다: {string.Join(", ", lowCoverage.Select(i => $"#{i.Session.Id}"))}.");
        }

        var undefinedNet = rows.Where(r => !r.NetProfit.HasValue).ToList();
        if (undefinedNet.Count > 0)
        {
            warnings.Add($"순수익을 계산할 수 없는 세션 {undefinedNet.Count}개는 합계·평균에서 빠집니다 (예: 수익 미입력·환율 없음).");
        }

        var deviceSets = items.Select(i => string.Join(",", i.Result.Energy.Devices.Select(d => d.DeviceIndex))).Distinct().ToList();
        if (deviceSets.Count > 1)
        {
            warnings.Add("세션마다 집계한 GPU가 다릅니다.");
        }

        if (items.Select(i => i.Result.HoursSource).Distinct().Count() > 1)
        {
            warnings.Add("일부 세션은 사용자 입력 작업 시간을, 일부는 기록 시간을 시간 기준으로 씁니다.");
        }

        if (view != RevenueView.Realized)
        {
            warnings.Add(view == RevenueView.Expected ? "예상(가정) 수익 기준 비교입니다." : "미확정 수익을 포함한 비교입니다. 확정 결과가 아닙니다.");
        }

        if (items.Select(i => i.Workload?.Type).Distinct().Count() > 1)
        {
            warnings.Add("작업 유형이 서로 다른 세션입니다. 시간당·에너지당 값으로 정규화해 비교하세요.");
        }

        return (rows, warnings);
    }

    public static CompareRow Row(AccountedSession item, RevenueView view)
    {
        var r = item.Result;
        var v = r.View(view);
        var hours = r.BasisHours;
        return new CompareRow
        {
            SessionId = item.Session.Id,
            Label = string.IsNullOrWhiteSpace(item.Session.Title) ? $"세션 #{item.Session.Id}" : item.Session.Title!,
            WorkloadName = item.Workload?.Name ?? "",
            Type = item.Workload?.Type,
            Started = item.Session.Started,
            Hours = hours,
            Kwh = r.Energy.TotalKwh,
            AveragePowerW = r.Energy.Devices.Any(d => d.AveragePowerW is not null) ? r.Energy.Devices.Sum(d => d.AveragePowerW ?? 0) : null,
            Coverage = r.Energy.Coverage,
            Revenue = v.Revenue,
            TotalCost = v.TotalCost,
            NetProfit = v.NetProfit,
            NetPerHour = v.NetPerHour,
            NetPerKwh = v.NetPerKwh,
            CostPerHour = PerHour(v.TotalCost, hours),
            RevenuePerHour = PerHour(v.Revenue, hours),
            EnergyBasis = EnergyBasisText.ToKorean(r.Energy.Conditions.Basis),
            ElectricityRate = r.Electricity.RateKrwPerKwh,
            IsMock = item.Session.IsMock,
            ExecutionConfirmed = item.Session.ExecutionConfirmed,
        };
    }

    public static IReadOnlyList<GroupRow> ByWorkloadType(IReadOnlyList<AccountedSession> items, RevenueView view)
        => Group(items, i => i.Workload is null ? "작업 없음" : WorkloadTypeText.ToKorean(i.Workload.Type), view);

    public static IReadOnlyList<GroupRow> ByWorkload(IReadOnlyList<AccountedSession> items, RevenueView view)
        => Group(items, i => i.Workload?.Name ?? "작업 없음", view);

    /// <summary>Same work at different times of day: sessions grouped by the local hour they started.</summary>
    public static IReadOnlyList<GroupRow> ByTimeOfDay(IReadOnlyList<AccountedSession> items, RevenueView view, TimeZoneInfo zone)
        => Group(items, i => TimeBand(TimeZoneInfo.ConvertTime(i.Session.Started, zone).Hour), view)
            .OrderBy(g => Array.IndexOf(Bands, g.Key)).ToList();

    private static readonly string[] Bands = ["새벽 (00–06시)", "오전 (06–12시)", "오후 (12–18시)", "저녁 (18–24시)"];

    public static string TimeBand(int hour) => Bands[Math.Clamp(hour / 6, 0, 3)];

    public static IReadOnlyList<ActualVsExpectedRow> ActualVsExpected(IReadOnlyList<AccountedSession> items)
        => items.Select(i =>
        {
            var actual = i.Result.View(RevenueView.Realized).Revenue;
            var expected = i.Result.View(RevenueView.Expected).Revenue;
            var diff = actual.HasValue && expected.HasValue ? Maybe<decimal>.Of(actual.Value!.Value - expected.Value!.Value) : Maybe<decimal>.None(actual.Reason ?? expected.Reason!);
            var ratio = actual.HasValue && expected.Value is > 0 ? Maybe<decimal>.Of(actual.Value!.Value / expected.Value!.Value) : Maybe<decimal>.None("예상 수익이 0이거나 없습니다.");
            return new ActualVsExpectedRow(i.Session.Id, string.IsNullOrWhiteSpace(i.Session.Title) ? $"세션 #{i.Session.Id}" : i.Session.Title!, actual, expected, diff, ratio);
        }).ToList();

    private static List<GroupRow> Group(IReadOnlyList<AccountedSession> items, Func<AccountedSession, string> key, RevenueView view)
        => items.GroupBy(key).Select(g =>
        {
            var defined = g.Where(i => i.Result.View(view).NetProfit.HasValue && i.Result.Energy.TotalKwh.HasValue).ToList();
            var hours = defined.Sum(i => i.Result.BasisHours);
            var kwh = defined.Sum(i => i.Result.Energy.TotalKwh.Value!.Value);
            var net = defined.Sum(i => i.Result.View(view).NetProfit.Value!.Value);
            return new GroupRow
            {
                Key = g.Key,
                Sessions = g.Count(),
                DefinedSessions = defined.Count,
                Hours = hours,
                Kwh = kwh,
                Revenue = defined.Sum(i => i.Result.View(view).Revenue.Value ?? 0),
                TotalCost = defined.Sum(i => i.Result.View(view).TotalCost.Value ?? 0),
                NetProfit = net,
                NetPerHour = hours > 0 ? Maybe<decimal>.Of(net / (decimal)hours) : Maybe<decimal>.None(defined.Count == 0 ? "순수익이 정의된 세션이 없습니다." : "작업 시간 합계가 0입니다."),
                NetPerKwh = kwh > 0 ? Maybe<decimal>.Of(net / (decimal)kwh) : Maybe<decimal>.None(defined.Count == 0 ? "순수익이 정의된 세션이 없습니다." : "에너지 합계가 0입니다."),
            };
        }).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();

    private static Maybe<decimal> PerHour(Maybe<decimal> value, double hours)
        => !value.HasValue ? value : hours > 0 ? Maybe<decimal>.Of(value.Value!.Value / (decimal)hours) : Maybe<decimal>.None("작업 시간이 0시간입니다.");
}
