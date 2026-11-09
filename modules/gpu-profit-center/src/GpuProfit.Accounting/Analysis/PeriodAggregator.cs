using GpuProfit.Accounting.Costs;
using GpuProfit.Accounting.Energy;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Core.Domain;

namespace GpuProfit.Accounting.Analysis;

public enum PeriodGranularity
{
    Day = 0,
    Week = 1,
    Month = 2,
}

public sealed record PeriodItem(WorkSession Session, IReadOnlyList<SessionSegment> Segments, SessionAccountingResult Result);

public sealed record PeriodBucket
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required string Label { get; init; }
    public int Sessions { get; init; }
    public double Hours { get; init; }
    public double MeasuredKwh { get; init; }
    public double EstimatedKwh { get; init; }
    public double UserEnteredKwh { get; init; }
    public double TotalKwh { get; init; }

    /// <summary>Sessions (share) whose energy could not be stated; they add nothing to the energy and cost sums.</summary>
    public int SessionsWithoutEnergy { get; init; }

    public decimal ElectricityCost { get; init; }
    public decimal OtherCost { get; init; }

    /// <summary>One-time workload/global costs that occurred in the period (not allocated to sessions).</summary>
    public decimal PeriodOneTimeCost { get; init; }

    public decimal RealizedRevenue { get; init; }
    public decimal UnconfirmedRevenue { get; init; }
    public decimal ExpectedRevenue { get; init; }

    /// <summary>Realized net of the sessions whose realized net is defined, minus period one-time costs.</summary>
    public decimal RealizedNet { get; init; }
    public int SessionsWithoutRealizedNet { get; init; }

    public double ObservedSeconds { get; init; }
    public double WindowSeconds { get; init; }
    public double? Coverage => WindowSeconds > 0 ? ObservedSeconds / WindowSeconds : null;

    /// <summary>Share of the period's energy that is estimated (assumptions), not measured or entered.</summary>
    public double EstimatedShare
    {
        get
        {
            var positive = MeasuredKwh + Math.Max(0, EstimatedKwh) + UserEnteredKwh;
            return positive > 0 ? Math.Max(0, EstimatedKwh) / positive : 0;
        }
    }
}

/// <summary>
/// Daily, weekly (Monday start) and monthly totals. A session's figures are split between periods in proportion to
/// its recorded time in each (a session over midnight counts in both days); a session with no recorded time counts in
/// the period it started.
/// </summary>
public static class PeriodAggregator
{
    public static IReadOnlyList<PeriodBucket> Aggregate(IReadOnlyList<PeriodItem> items, PeriodGranularity granularity, TimeZoneInfo zone,
        DateTimeOffset now, IReadOnlyList<CostEntry>? oneTimeCosts = null, IReadOnlyList<ExchangeRate>? rates = null)
    {
        var buckets = new Dictionary<DateTimeOffset, Accumulator>();

        Accumulator Get(DateTimeOffset start)
        {
            if (!buckets.TryGetValue(start, out var acc))
            {
                acc = new Accumulator(start, Next(start, granularity, zone));
                buckets[start] = acc;
            }

            return acc;
        }

        foreach (var item in items)
        {
            var windows = TimeWindow.Normalize(item.Segments.Select(s => TimeWindow.Of(s.Start, s.End ?? now)));
            var total = windows.Sum(w => w.Seconds);
            var shares = new Dictionary<DateTimeOffset, double>();
            if (total <= 0)
            {
                shares[PeriodStart(item.Session.Started, granularity, zone)] = 1;
            }
            else
            {
                foreach (var w in windows)
                {
                    var cursor = DateTimeOffset.FromUnixTimeMilliseconds(w.StartMs);
                    var end = DateTimeOffset.FromUnixTimeMilliseconds(w.EndMs);
                    while (cursor < end)
                    {
                        var start = PeriodStart(cursor, granularity, zone);
                        var next = Next(start, granularity, zone);
                        var until = next < end ? next : end;
                        shares[start] = shares.GetValueOrDefault(start) + (until - cursor).TotalSeconds / total;
                        cursor = until;
                    }
                }
            }

            foreach (var (start, share) in shares)
            {
                Get(start).Add(item, share);
            }
        }

        if (oneTimeCosts is not null)
        {
            var converter = new CurrencyConverter(rates ?? []);
            foreach (var c in oneTimeCosts.Where(c => c.Active && c.Basis == CostBasis.OneTime && c.SessionId is null))
            {
                var krw = converter.ToKrw(c.Amount, c.Currency, c.Occurred);
                if (krw.HasValue)
                {
                    Get(PeriodStart(c.Occurred, granularity, zone)).PeriodOneTime += krw.Value!.Value;
                }
            }
        }

        return buckets.Values.OrderBy(b => b.Start).Select(b => b.ToBucket(granularity, zone)).ToList();
    }

    public static DateTimeOffset PeriodStart(DateTimeOffset time, PeriodGranularity granularity, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(time, zone).DateTime.Date;
        var date = granularity switch
        {
            PeriodGranularity.Week => local.AddDays(-(((int)local.DayOfWeek + 6) % 7)),
            PeriodGranularity.Month => new DateTime(local.Year, local.Month, 1),
            _ => local,
        };
        return new DateTimeOffset(date, zone.GetUtcOffset(date));
    }

    private static DateTimeOffset Next(DateTimeOffset start, PeriodGranularity granularity, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(start, zone).DateTime.Date;
        var next = granularity switch
        {
            PeriodGranularity.Week => local.AddDays(7),
            PeriodGranularity.Month => local.AddMonths(1),
            _ => local.AddDays(1),
        };
        return new DateTimeOffset(next, zone.GetUtcOffset(next));
    }

    private sealed class Accumulator(DateTimeOffset start, DateTimeOffset end)
    {
        public DateTimeOffset Start { get; } = start;
        public decimal PeriodOneTime { get; set; }
        private readonly HashSet<long> _sessions = [];
        private readonly HashSet<long> _noEnergy = [];
        private readonly HashSet<long> _noNet = [];
        private double _hours, _measured, _estimated, _user, _total, _observed, _window;
        private decimal _electricity, _other, _realized, _unconfirmed, _expected, _net;

        public void Add(PeriodItem item, double share)
        {
            var r = item.Result;
            var s = (decimal)share;
            _sessions.Add(item.Session.Id);
            _hours += r.BasisHours * share;
            _window += r.Energy.WindowSeconds * share;
            _observed += (r.Energy.Coverage ?? 0) * r.Energy.WindowSeconds * share;
            if (r.Energy.TotalWh.HasValue)
            {
                foreach (var c in r.Energy.Components)
                {
                    var kwh = c.Wh / 1000 * share;
                    switch (c.Origin)
                    {
                        case ValueOrigin.Measured: _measured += kwh; break;
                        case ValueOrigin.UserEntered: _user += kwh; break;
                        default: _estimated += kwh; break;
                    }
                }

                _total += r.Energy.TotalWh.Value!.Value / 1000 * share;
            }
            else
            {
                _noEnergy.Add(item.Session.Id);
            }

            _electricity += (r.Electricity.CostKrw.Value ?? 0) * s;
            _other += r.OtherCosts.Sum(l => l.AmountKrw.Value ?? 0) * s;
            _realized += (r.View(RevenueView.Realized).Revenue.Value ?? 0) * s;
            _unconfirmed += (r.RevenueByKind.Where(k => k.Kind is RevenueKind.Pending or RevenueKind.ExternalReported).Sum(k => k.AmountKrw.Value ?? 0)) * s;
            _expected += (r.View(RevenueView.Expected).Revenue.Value ?? 0) * s;
            if (r.View(RevenueView.Realized).NetProfit.Value is decimal net)
            {
                _net += net * s;
            }
            else
            {
                _noNet.Add(item.Session.Id);
            }
        }

        public PeriodBucket ToBucket(PeriodGranularity granularity, TimeZoneInfo zone)
        {
            var local = TimeZoneInfo.ConvertTime(Start, zone);
            var label = granularity switch
            {
                PeriodGranularity.Month => local.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
                PeriodGranularity.Week => $"{local:yyyy-MM-dd} 주",
                _ => local.ToString("yyyy-MM-dd (ddd)", System.Globalization.CultureInfo.GetCultureInfo("ko-KR")),
            };
            return new PeriodBucket
            {
                Start = Start,
                End = end,
                Label = label,
                Sessions = _sessions.Count,
                Hours = _hours,
                MeasuredKwh = _measured,
                EstimatedKwh = _estimated,
                UserEnteredKwh = _user,
                TotalKwh = _total,
                SessionsWithoutEnergy = _noEnergy.Count,
                ElectricityCost = _electricity,
                OtherCost = _other,
                PeriodOneTimeCost = PeriodOneTime,
                RealizedRevenue = _realized,
                UnconfirmedRevenue = _unconfirmed,
                ExpectedRevenue = _expected,
                RealizedNet = _net - PeriodOneTime,
                SessionsWithoutRealizedNet = _noNet.Count,
                ObservedSeconds = _observed,
                WindowSeconds = _window,
            };
        }
    }
}
