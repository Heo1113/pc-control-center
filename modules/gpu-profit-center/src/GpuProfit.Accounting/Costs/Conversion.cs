using GpuProfit.Core.Domain;

namespace GpuProfit.Accounting.Costs;

/// <summary>The rate actually used to convert an amount, kept for the calculation record.</summary>
public sealed record UsedRate(string Currency, decimal KrwPerUnit, DateTimeOffset? AsOf, string Source, bool AfterOccurrence);

/// <summary>Converts amounts to KRW with user-entered (or imported) exchange rates. Nothing is fetched from the network.</summary>
public sealed class CurrencyConverter(IReadOnlyList<ExchangeRate> rates)
{
    private readonly Dictionary<string, List<ExchangeRate>> _byCurrency = rates
        .Where(r => r.KrwPerUnit > 0 && CurrencyCodes.Normalize(r.Currency) is not null)
        .GroupBy(r => CurrencyCodes.Normalize(r.Currency)!)
        .ToDictionary(g => g.Key, g => g.OrderBy(r => r.AsOf).ToList());

    public List<UsedRate> Used { get; } = [];

    /// <summary>
    /// The rate fixed on the entry wins; otherwise the latest rate at or before <paramref name="at"/>; otherwise the
    /// earliest later rate (flagged). KRW converts 1:1. Without any rate the amount cannot be converted.
    /// </summary>
    public Maybe<decimal> ToKrw(decimal amount, string? currency, DateTimeOffset at, decimal? fixedKrwPerUnit = null, DateTimeOffset? fixedAsOf = null, string? fixedSource = null)
    {
        var code = CurrencyCodes.Normalize(currency);
        if (code is null)
        {
            return Maybe<decimal>.None($"통화 코드 '{currency}'를 알 수 없습니다.");
        }

        if (code == CurrencyCodes.Krw)
        {
            return Maybe<decimal>.Of(amount);
        }

        if (fixedKrwPerUnit is decimal f && f > 0)
        {
            Remember(new UsedRate(code, f, fixedAsOf, fixedSource ?? "항목에 지정한 환율", false));
            return Maybe<decimal>.Of(amount * f);
        }

        if (!_byCurrency.TryGetValue(code, out var list) || list.Count == 0)
        {
            return Maybe<decimal>.None($"{code} → KRW 환율이 없어 원화로 바꿀 수 없습니다.");
        }

        var before = list.LastOrDefault(r => r.AsOf <= at);
        var chosen = before ?? list[0];
        Remember(new UsedRate(code, chosen.KrwPerUnit, chosen.AsOf, chosen.Source ?? ValueOriginText.ToKorean(chosen.Origin), before is null));
        return Maybe<decimal>.Of(amount * chosen.KrwPerUnit);
    }

    private void Remember(UsedRate rate)
    {
        if (!Used.Contains(rate))
        {
            Used.Add(rate);
        }
    }
}

/// <summary>A non-electricity cost line of a session.</summary>
public sealed record CostLine
{
    public required string Label { get; init; }
    public required string Category { get; init; }
    public Maybe<decimal> AmountKrw { get; init; }
    public ValueOrigin Origin { get; init; }
    public required string Basis { get; init; }
    public long? CostEntryId { get; init; }
    public long? EquipmentId { get; init; }
    public bool IsDepreciation { get; init; }
}

/// <summary>Allocates cost entries and equipment depreciation to one session.</summary>
public static class CostAllocator
{
    public static (List<CostLine> Lines, decimal FeePercent, List<string> Notes) Allocate(WorkSession session, double hours,
        IEnumerable<CostEntry> entries, IEnumerable<EquipmentAsset> equipment, CurrencyConverter converter)
    {
        var lines = new List<CostLine>();
        var notes = new List<string>();
        decimal feePercent = 0;
        var periodOnly = 0;
        foreach (var e in entries.Where(e => e.AppliesTo(session)).OrderBy(e => e.Id))
        {
            var label = string.IsNullOrWhiteSpace(e.Note) ? CostCategoryText.ToKorean(e.Category) : $"{CostCategoryText.ToKorean(e.Category)}: {e.Note}";
            switch (e.Basis)
            {
                case CostBasis.PercentOfRevenue:
                    feePercent += e.Amount;
                    break;
                case CostBasis.OneTime when e.SessionId is null:
                    periodOnly++; // a workload/global one-time amount belongs to its period, not to a single session
                    break;
                case CostBasis.OneTime:
                    lines.Add(new CostLine
                    {
                        Label = label, Category = CostCategoryText.ToKorean(e.Category), CostEntryId = e.Id, Origin = e.Origin,
                        AmountKrw = converter.ToKrw(e.Amount, e.Currency, e.Occurred),
                        Basis = $"세션에 직접 입력한 금액 {e.Amount:0.########} {e.Currency}",
                    });
                    break;
                case CostBasis.PerHour:
                    lines.Add(new CostLine
                    {
                        Label = label, Category = CostCategoryText.ToKorean(e.Category), CostEntryId = e.Id, Origin = ValueOrigin.Calculated,
                        AmountKrw = Times(converter.ToKrw(e.Amount, e.Currency, session.Started), hours),
                        Basis = $"시간당 {e.Amount:0.########} {e.Currency} × {hours:0.###} 시간",
                    });
                    break;
                case CostBasis.PerMonth:
                    lines.Add(new CostLine
                    {
                        Label = label, Category = CostCategoryText.ToKorean(e.Category), CostEntryId = e.Id, Origin = ValueOrigin.Estimated,
                        AmountKrw = Times(converter.ToKrw(e.Amount, e.Currency, session.Started), hours, HoursPerMonth),
                        Basis = $"월 {e.Amount:0.########} {e.Currency} × {hours:0.###} 시간 / 월 {AccountingProfile.HoursPerMonth} 시간 (시간 비례 배분, 추정)",
                    });
                    break;
            }
        }

        if (periodOnly > 0)
        {
            notes.Add($"작업 또는 전체에 걸린 일회성 비용 {periodOnly}건은 세션에 배분하지 않고 기간별 합계(History)에만 반영합니다.");
        }

        foreach (var asset in equipment.Where(a => a.AppliesTo(session)).OrderBy(a => a.Id))
        {
            var depreciable = Math.Max(0, asset.PriceKrw - asset.SalvageKrw);
            Maybe<decimal> amount;
            string basis;
            if (asset.Method == DepreciationMethod.UsageHours)
            {
                if (asset.LifeHours is double life && life > 0)
                {
                    amount = Maybe<decimal>.Of(depreciable * ElectricityCostCalculator.ToDecimal(hours) / ElectricityCostCalculator.ToDecimal(life));
                    basis = $"({asset.PriceKrw:N0} − 잔존 {asset.SalvageKrw:N0})원 / 수명 {life:0.#} 시간 × {hours:0.###} 시간";
                }
                else
                {
                    amount = Maybe<decimal>.None("사용 시간 기준 감가상각에 수명(시간)이 없습니다.");
                    basis = "사용 시간 기준 (수명 미입력)";
                }
            }
            else if (asset.LifeMonths > 0)
            {
                amount = Maybe<decimal>.Of(depreciable * ElectricityCostCalculator.ToDecimal(hours)
                                           / (ElectricityCostCalculator.ToDecimal(asset.LifeMonths) * HoursPerMonth));
                basis = $"({asset.PriceKrw:N0} − 잔존 {asset.SalvageKrw:N0})원 / {asset.LifeMonths:0.#}개월, 세션 {hours:0.###} 시간만큼 배분 (추정)";
            }
            else
            {
                amount = Maybe<decimal>.None("감가상각 기간(개월)이 0입니다.");
                basis = "정액법 (기간 미입력)";
            }

            lines.Add(new CostLine
            {
                Label = $"감가상각: {asset.Name}", Category = "장비 감가상각", EquipmentId = asset.Id, Origin = ValueOrigin.Estimated,
                AmountKrw = amount, Basis = basis, IsDepreciation = true,
            });
        }

        return (lines, feePercent, notes);
    }

    private static readonly decimal HoursPerMonth = (decimal)AccountingProfile.HoursPerMonth;

    /// <summary>value × factor ÷ divisor, multiplying before dividing so that decimal precision is kept.</summary>
    private static Maybe<decimal> Times(Maybe<decimal> value, double factor, decimal divisor = 1m)
        => value.HasValue ? Maybe<decimal>.Of(value.Value!.Value * ElectricityCostCalculator.ToDecimal(factor) / divisor) : value;
}
