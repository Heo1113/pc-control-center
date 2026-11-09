using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using GpuProfit.Accounting.Analysis;
using GpuProfit.Accounting.Energy;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Core;
using GpuProfit.Core.Domain;
using GpuProfit.Storage;
using LocalAiPerf.Export;

namespace GpuProfit.Export;

/// <summary>
/// CSV (UTF-8 with BOM, RFC 4180, formula-injection safe: project A's CsvWriter) and JSON exports. Undefined values
/// are written as empty cells with the reason in a separate column, never as 0. Exports contain no user name,
/// computer name or file path of this PC.
/// </summary>
public static class Exporters
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void SessionsCsv(string path, IReadOnlyList<AccountedSession> items, RevenueView view = RevenueView.Realized)
        => Atomic(path, p =>
        {
            using var csv = new CsvWriter(p);
            csv.Row("session_id", "title", "workload", "workload_type", "state", "source", "is_mock", "execution_confirmed", "started", "ended",
                "recorded_hours", "paused_hours", "basis_hours", "hours_source", "energy_basis", "gpu_measured_kwh", "total_kwh", "measured_share",
                "estimated_share", "user_entered_share", "gpu_coverage", "gpu_active_share", "electricity_rate_krw_kwh", "electricity_is_estimate",
                "electricity_cost_krw", "other_cost_krw", "platform_fee_percent", "revenue_view", "revenue_received_krw", "revenue_pending_krw",
                "revenue_reported_krw", "revenue_expected_krw", "revenue_krw", "total_cost_krw", "net_profit_krw", "net_per_hour_krw", "net_per_kwh_krw",
                "margin", "breakeven_revenue_krw", "breakeven_rate_krw_kwh", "payback_hours", "undefined_reasons", "warnings", "inputs_hash");
            foreach (var i in items)
            {
                var r = i.Result;
                var v = r.View(view);
                var reasons = new List<string>();
                object? M<T>(Maybe<T> m, string label) where T : struct
                {
                    if (m.HasValue)
                    {
                        return m.Value is decimal d ? R(d) : m.Value!.Value;
                    }

                    reasons.Add($"{label}: {m.Reason}");
                    return null;
                }

                decimal? Kind(RevenueKind k) => r.RevenueByKind.First(x => x.Kind == k) is { Count: > 0 } t ? t.AmountKrw.Value : null;
                csv.Row(i.Session.Id, i.Session.Title, i.Workload?.Name, i.Workload?.Type.ToString(), i.Session.State.ToString(), i.Session.Source.ToString(),
                    i.Session.IsMock, i.Session.ExecutionConfirmed, i.Session.Started, i.Session.Ended,
                    r.RecordedSeconds / 3600, r.PausedSeconds / 3600, r.BasisHours, r.HoursSource, r.Energy.Conditions.Basis.ToString(),
                    r.Energy.GpuMeasuredWh / 1000, M(r.Energy.TotalKwh, "total_kwh"), r.Energy.MeasuredShare, r.Energy.EstimatedShare, r.Energy.UserEnteredShare,
                    r.Energy.Coverage, r.Energy.GpuActiveShare, M(r.Electricity.RateKrwPerKwh, "rate"), r.Electricity.IsEstimate, M(r.Electricity.CostKrw, "electricity"),
                    r.OtherCosts.All(l => l.AmountKrw.HasValue) ? R(r.OtherCosts.Sum(l => l.AmountKrw.Value!.Value)) : null, r.PlatformFeePercent, view.ToString(),
                    Kind(RevenueKind.Received), Kind(RevenueKind.Pending), Kind(RevenueKind.ExternalReported), Kind(RevenueKind.Expected),
                    M(v.Revenue, "revenue"), M(v.TotalCost, "total_cost"), M(v.NetProfit, "net"), M(v.NetPerHour, "net_per_hour"), M(v.NetPerKwh, "net_per_kwh"),
                    M(v.Margin, "margin"), M(r.BreakevenRevenue, "breakeven_revenue"), M(v.BreakevenElectricityRate, "breakeven_rate"), M(v.PaybackHours, "payback"),
                    string.Join(" | ", reasons.Distinct()), string.Join(" | ", r.Warnings), r.InputsHash);
            }
        });

    /// <summary>The per-GPU energy detail and gaps of one session.</summary>
    public static void SessionEnergyCsv(string path, AccountedSession item)
        => Atomic(path, p =>
        {
            using var csv = new CsvWriter(p);
            var e = item.Result.Energy;
            csv.Row("section", "gpu_index", "label", "value_wh", "origin", "from", "to", "seconds", "detail");
            foreach (var c in e.Components)
            {
                csv.Row("component", null, c.Label, c.Wh, c.Origin.ToString(), null, null, null, c.Basis);
            }

            foreach (var d in e.Devices)
            {
                csv.Row("device", d.DeviceIndex, "measured", d.MeasuredWh, "Measured", null, null, d.ObservedSeconds,
                    $"coverage={d.Coverage:0.####}; samples={d.SampleCount}; valid={d.ValidPowerSamples}; avg_w={d.AveragePowerW:0.##}; counter_wh={d.CounterWh:0.###}");
                foreach (var g in d.Gaps)
                {
                    csv.Row("gap", d.DeviceIndex, GapReasons.ToKorean(g.Reason), null, null, DateTimeOffset.FromUnixTimeMilliseconds(g.FromMs),
                        DateTimeOffset.FromUnixTimeMilliseconds(g.ToMs), g.Seconds, g.Reason);
                }
            }

            foreach (var (key, value) in item.Result.Conditions)
            {
                csv.Row("condition", null, key, null, null, null, null, null, value);
            }
        });

    public static void SamplesCsv(string path, IReadOnlyList<PowerSample> samples)
        => Atomic(path, p =>
        {
            using var csv = new CsvWriter(p);
            csv.Row("time_utc", "gpu_index", "power_w", "utilization_percent", "nvml_energy_mj");
            foreach (var s in samples)
            {
                csv.Row(DateTimeOffset.FromUnixTimeMilliseconds(s.TimeMs), s.DeviceIndex, s.PowerW, s.UtilizationPercent, s.EnergyMilliJoules);
            }
        });

    public static void RevenueCsv(string path, IReadOnlyList<RevenueEntry> entries)
        => Atomic(path, p =>
        {
            using var csv = new CsvWriter(p);
            csv.Row("id", "session_id", "kind", "amount", "currency", "is_gross", "occurred", "provider", "external_id", "origin", "krw_per_unit", "rate_as_of", "rate_source", "note");
            foreach (var e in entries)
            {
                csv.Row(e.Id, e.SessionId, e.Kind.ToString(), e.Amount, e.Currency, e.IsGross, e.Occurred, e.Provider, e.ExternalId, e.Origin.ToString(),
                    e.KrwPerUnit, e.RateAsOf, e.RateSource, e.Note);
            }
        });

    public static void CostsCsv(string path, IReadOnlyList<CostEntry> entries, IReadOnlyList<EquipmentAsset> equipment)
        => Atomic(path, p =>
        {
            using var csv = new CsvWriter(p);
            csv.Row("type", "id", "session_id", "workload_id", "category", "basis", "amount", "currency", "occurred", "active", "origin", "note");
            foreach (var e in entries)
            {
                csv.Row("cost", e.Id, e.SessionId, e.WorkloadId, e.Category.ToString(), e.Basis.ToString(), e.Amount, e.Currency, e.Occurred, e.Active, e.Origin.ToString(), e.Note);
            }

            foreach (var a in equipment)
            {
                csv.Row("equipment", a.Id, null, a.WorkloadId, "Equipment", a.Method.ToString(), a.PriceKrw, "KRW", a.Purchased, a.Active, "UserEntered",
                    $"{a.Name}; salvage={a.SalvageKrw}; life_months={a.LifeMonths}; life_hours={a.LifeHours}; {a.Note}");
            }
        });

    public static void PeriodsCsv(string path, IReadOnlyList<PeriodBucket> buckets)
        => Atomic(path, p =>
        {
            using var csv = new CsvWriter(p);
            csv.Row("period", "start", "end", "sessions", "hours", "measured_kwh", "estimated_kwh", "user_entered_kwh", "total_kwh", "sessions_without_energy",
                "estimated_share", "coverage", "electricity_cost_krw", "other_cost_krw", "period_one_time_cost_krw", "realized_revenue_krw",
                "unconfirmed_revenue_krw", "expected_revenue_krw", "realized_net_krw", "sessions_without_realized_net");
            foreach (var b in buckets)
            {
                csv.Row(b.Label, b.Start, b.End, b.Sessions, b.Hours, b.MeasuredKwh, b.EstimatedKwh, b.UserEnteredKwh, b.TotalKwh, b.SessionsWithoutEnergy,
                    b.EstimatedShare, b.Coverage, R(b.ElectricityCost), R(b.OtherCost), R(b.PeriodOneTimeCost), R(b.RealizedRevenue), R(b.UnconfirmedRevenue),
                    R(b.ExpectedRevenue), R(b.RealizedNet), b.SessionsWithoutRealizedNet);
            }
        });

    /// <summary>Everything the user entered and every session's latest result, as one JSON document (no sensor samples).</summary>
    public static void LedgerJson(string path, ProfitStore store, IReadOnlyList<AccountedSession> sessions, DateTimeOffset now)
        => Atomic(path, p =>
        {
            var segments = store.ListAllSegments();
            var document = new
            {
                format = "gpu-profit-center/ledger",
                formatVersion = 1,
                app = AppInfo.ProductName,
                appVersion = AppInfo.Version,
                exportedAt = now,
                note = "실측(Measured), 사용자 입력(UserEntered), 외부 보고(ExternalReported), 추정(Estimated)을 구분해 기록합니다. 계산할 수 없는 값은 null이며 이유가 함께 있습니다.",
                accountingProfile = store.GetProfile(),
                workloads = store.ListWorkloads(),
                sessions = sessions.Select(i => new
                {
                    session = i.Session,
                    segments = segments.GetValueOrDefault(i.Session.Id, []).Select(g => new { g.Start, g.End, g.EndReason }),
                    result = ResultDocument(i.Result),
                }),
                revenue = store.ListRevenue(),
                costs = store.ListCosts(),
                equipment = store.ListEquipment(),
                exchangeRates = store.ListRates(),
                scenarios = store.ListScenarios().Select(s => new { s.Id, s.Name, s.Kind, parameters = JsonDocument.Parse(s.ParamsJson).RootElement, s.SourceSessionId, s.Notes, s.Updated }),
                gaps = store.ListGaps(limit: 100_000).Select(g => new { g.Kind, g.Scope, g.From, g.To, g.Detail }),
            };
            File.WriteAllText(p, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
        });

    public static object ResultDocument(SessionAccountingResult r) => new
    {
        r.ComputedAt,
        engineVersion = SessionAccountingResult.EngineVersion,
        r.InputsHash,
        recordedHours = r.RecordedSeconds / 3600,
        pausedHours = r.PausedSeconds / 3600,
        r.BasisHours,
        r.HoursSource,
        r.ExecutionConfirmed,
        r.ExecutionEvidence,
        energy = new
        {
            basis = r.Energy.Conditions.Basis,
            totalWh = M(r.Energy.TotalWh),
            gpuMeasuredWh = r.Energy.GpuMeasuredWh,
            coverage = r.Energy.Coverage,
            components = r.Energy.Components,
            devices = r.Energy.Devices.Select(d => new
            {
                d.DeviceIndex, d.MeasuredWh, d.ObservedSeconds, d.UnobservedSeconds, d.Coverage, d.AveragePowerW, d.MinPowerW, d.MaxPowerW, d.SampleCount,
                d.CounterWh, gaps = d.Gaps.Select(g => new { from = DateTimeOffset.FromUnixTimeMilliseconds(g.FromMs), to = DateTimeOffset.FromUnixTimeMilliseconds(g.ToMs), g.Reason }),
            }),
            notes = r.Energy.Notes,
        },
        electricity = new { cost = M(r.Electricity.CostKrw), rate = M(r.Electricity.RateKrwPerKwh), r.Electricity.IsEstimate, r.Electricity.Basis },
        otherCosts = r.OtherCosts.Select(l => new { l.Label, l.Category, amount = M(l.AmountKrw), l.Origin, l.Basis }),
        r.PlatformFeePercent,
        breakevenRevenue = M(r.BreakevenRevenue),
        revenueByKind = r.RevenueByKind.Select(k => new { k.Kind, k.Count, amount = M(k.AmountKrw), k.UnconvertedCurrencies }),
        views = r.Views.Select(v => new
        {
            v.View, revenue = M(v.Revenue), fees = M(v.PlatformFees), totalCost = M(v.TotalCost), net = M(v.NetProfit), netPerHour = M(v.NetPerHour),
            netPerKwh = M(v.NetPerKwh), margin = M(v.Margin), returnOnCost = M(v.ReturnOnCost), breakevenRate = M(v.BreakevenElectricityRate),
            paybackHours = M(v.PaybackHours), v.Notes,
        }),
        ratesUsed = r.RatesUsed,
        r.Warnings,
        conditions = r.Conditions.ToDictionary(c => c.Key, c => c.Value),
    };

    private static object M<T>(Maybe<T> m) where T : struct => m.HasValue ? new { value = m.Value } : new { value = (T?)null, reason = m.Reason };

    /// <summary>Calculated money in exports: 6 decimal places (stored amounts are exported exactly as entered).</summary>
    private static decimal R(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    /// <summary>Writes to a temporary file first and moves it into place, so a failed export never leaves a half file.</summary>
    private static void Atomic(string path, Action<string> write)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".partial";
        try
        {
            write(temp);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
