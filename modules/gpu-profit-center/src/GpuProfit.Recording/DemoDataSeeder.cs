using System.Text.Json;
using GpuProfit.Accounting.Scenarios;
using GpuProfit.Core;
using GpuProfit.Core.Domain;
using GpuProfit.Storage;
using LocalAiPerf.Core.Hardware;

namespace GpuProfit.Recording;

/// <summary>
/// Demo data for screenshots, smoke tests and trying the app on a PC without a GPU. Every sample and session is
/// flagged MOCK and every title starts with "[데모]"; the UI shows a banner while such data exists. The numbers are
/// synthetic and say nothing about any real coin, pool or service.
/// </summary>
public static class DemoDataSeeder
{
    public const string TitlePrefix = "[데모]";

    public static bool HasDemoData(ProfitStore store) => store.ListSessions(new SessionQuery { IsMock = true, Limit = 1, Text = TitlePrefix }).Count > 0;

    public static int Seed(ProfitStore store, SessionService sessions, DateTimeOffset now)
    {
        if (HasDemoData(store))
        {
            return 0;
        }

        var mining = FindOrAdd(store, "[데모] GPU 채굴 (가상)", WorkloadType.Mining);
        var inference = FindOrAdd(store, "[데모] AI 추론 서비스 (가상)", WorkloadType.AiInference);
        var compute = FindOrAdd(store, "[데모] 렌더링 작업 (가상)", WorkloadType.GpuCompute);

        var random = new Random(20261103);
        var created = 0;
        var plan = new (long Workload, int DaysAgo, int StartHour, double Hours, double BaseW, decimal? Received, decimal? Pending, decimal? Expected, bool Gap)[]
        {
            (mining, 6, 1, 3.0, 285, 5200m, null, 5600m, false),
            (mining, 5, 14, 2.0, 290, null, 3100m, 3700m, false),
            (inference, 4, 9, 2.5, 210, 9800m, null, 9000m, false),
            (mining, 3, 22, 3.0, 300, 4100m, 900m, 5400m, true),
            (compute, 2, 19, 1.5, 330, null, null, null, false),
            (inference, 1, 10, 2.0, 190, 6400m, 1200m, 8000m, false),
        };

        var run = store.StartRun(now - TimeSpan.FromDays(7), AppInfo.Version + "-demo", isMock: true, 5000);
        foreach (var p in plan)
        {
            var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-p.DaysAgo).AddHours(p.StartHour);
            var end = start.AddHours(p.Hours);
            var batch = new List<HardwareSnapshot>();
            for (var t = start.AddSeconds(-5); t <= end.AddSeconds(5); t = t.AddSeconds(5))
            {
                if (p.Gap && t > start.AddMinutes(70) && t < start.AddMinutes(85))
                {
                    continue; // a 15-minute recording gap, to show how unobserved time is reported
                }

                var w = p.BaseW + 25 * Math.Sin((t - start).TotalMinutes / 9) + random.NextDouble() * 8;
                batch.Add(new HardwareSnapshot
                {
                    Timestamp = t,
                    IsMock = true,
                    System = new SystemSample { CpuUtilizationPercent = 15 + random.NextDouble() * 10, MemoryUsedBytes = 20L << 30, MemoryTotalBytes = 64L << 30 },
                    Gpus = [new GpuSample
                    {
                        Index = 0, Name = "MOCK GPU (테스트용 가상 센서)", PowerW = Math.Round(w, 1), UtilizationPercent = 92 + random.NextDouble() * 6,
                        TemperatureC = 63 + random.NextDouble() * 6, MemoryUsedBytes = 9L << 30, MemoryTotalBytes = 16L << 30,
                        GraphicsClockMhz = 2600, MemoryClockMhz = 15000,
                    }],
                });
            }

            store.WriteSnapshots(run, batch); // one transaction per session: one disk sync, not thousands
            var id = store.CreateSession(new WorkSession
            {
                WorkloadId = p.Workload,
                Title = $"{TitlePrefix} 세션 {created + 1}",
                Notes = "가상 데이터로 만든 데모 세션입니다. 실제 측정이 아닙니다.",
                State = SessionState.Ended,
                Source = SessionSource.Live,
                Started = start,
                Ended = end,
                EndReason = SegmentEndReasons.Stopped,
                RunId = run,
                IsMock = true,
                ExecutionConfirmed = created % 2 == 0,
                NoRevenueConfirmed = p.Received is null && p.Pending is null && p.Expected is null,
            }, end, SegmentEndReasons.Stopped);
            if (p.Received is decimal received)
            {
                store.AddRevenue(new RevenueEntry { SessionId = id, Kind = RevenueKind.Received, Amount = received, IsGross = true, Occurred = end, Provider = "데모 서비스", Note = "가상" });
            }

            if (p.Pending is decimal pending)
            {
                store.AddRevenue(new RevenueEntry { SessionId = id, Kind = RevenueKind.Pending, Amount = pending, IsGross = true, Occurred = end, Provider = "데모 서비스", Note = "가상" });
            }

            if (p.Expected is decimal expected)
            {
                store.AddRevenue(new RevenueEntry { SessionId = id, Kind = RevenueKind.Expected, Amount = expected, Occurred = start, Note = "가상 예상" });
            }

            created++;
        }

        store.EndRun(run, now - TimeSpan.FromHours(1), RunEndReasons.Normal);
        store.AddCost(new CostEntry { WorkloadId = mining, Category = CostCategory.PlatformFee, Basis = CostBasis.PercentOfRevenue, Amount = 1m, Note = "[데모] 풀 수수료", Occurred = now });
        store.AddCost(new CostEntry { Category = CostCategory.Network, Basis = CostBasis.PerMonth, Amount = 33000m, Note = "[데모] 인터넷 요금 일부", Occurred = now });
        store.AddEquipment(new EquipmentAsset { Name = "[데모] GPU", PriceKrw = 1_600_000m, SalvageKrw = 400_000m, Method = DepreciationMethod.StraightLineMonths, LifeMonths = 36, Purchased = now.AddMonths(-3) });

        var basis = new ScenarioParameters
        {
            RevenuePerHourKrw = 1700m, GpuPowerW = 290, SystemExtraPowerW = 90, ElectricityRateKrwPerKwh = 200m, HoursPerDay = 20, Days = 30,
            PlatformFeePercent = 1, FixedCostPerMonthKrw = 33000m, EquipmentCostKrw = 1_600_000m, EquipmentSalvageKrw = 400_000m, EquipmentLifeMonths = 36,
        };
        foreach (var kind in new[] { ScenarioKind.Base, ScenarioKind.Optimistic, ScenarioKind.Pessimistic })
        {
            store.SaveScenario(new ScenarioRecord
            {
                Name = $"[데모] {ScenarioKindText.ToKorean(kind)}",
                Kind = (int)kind,
                ParamsJson = JsonSerializer.Serialize(kind == ScenarioKind.Base ? basis : SensitivityAnalyzer.Variant(basis, kind)),
                Notes = "가상 조건입니다. 예측이 아닌 조건부 계산입니다.",
            });
        }

        foreach (var s in store.ListSessions(new SessionQuery { IsMock = true, Text = TitlePrefix }))
        {
            sessions.Recalculate(s.Id, "데모 데이터");
        }

        return created;
    }

    private static long FindOrAdd(ProfitStore store, string name, WorkloadType type)
        => store.ListWorkloads().FirstOrDefault(w => w.Name == name)?.Id ?? store.AddWorkload(new Workload { Name = name, Type = type, Notes = "데모용 가상 작업" });
}
