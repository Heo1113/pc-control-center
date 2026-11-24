using System.Text.Json;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Metrics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Sessions;
using LocalAiPerf.Storage;

namespace LocalAiPerf.Analytics;

public sealed record MetricSummary(MetricDefinition Metric, SummaryStats Stats, TrendInfo Trend);

/// <summary>Measured GPU energy over the session from NVML's energy counter, divided by reported output tokens.</summary>
public sealed record EnergySummary(double? GpuEnergyJoules, long? OutputTokens, double? JoulesPerOutputToken, double? AveragePowerW, string Basis);

public sealed record SessionReport(
    SessionInfo Session,
    HardwareInfo? Host,
    IReadOnlyList<MetricSummary> Metrics,
    IReadOnlyList<DegradationCandidate> Degradations,
    IReadOnlyList<ThrottleInterval> Throttles,
    EnergySummary Energy,
    RuntimeConditions? Conditions,
    IReadOnlyList<RuntimeConditions> AllConditions,
    int WarningEvents,
    int ErrorEvents,
    int GapEvents,
    int RequestCount,
    int FailedRequestCount)
{
    public MetricSummary? Get(string key) => Metrics.FirstOrDefault(m => m.Metric.Key == key);

    /// <summary>True when a known condition (model, engine version, context, slots, settings) changed inside the session.</summary>
    public bool ConditionsChangedWithinSession => AllConditions.Zip(AllConditions.Skip(1)).Any(p => RuntimeConditions.IsMaterialChange(p.First, p.Second));
}

public static class SessionAnalyzer
{
    public static SessionReport Analyze(SessionData data, DetectionOptions options, int gpuIndex = 0)
    {
        var series = SeriesExtractor.Extract(data, gpuIndex);
        var metrics = SeriesExtractor.SummaryMetricKeys
            .Select(key => (Definition: MetricCatalog.Get(key), Series: series.TryGetValue(key, out var s) ? s : MetricSeries.Empty(key)))
            .Select(x => new MetricSummary(x.Definition, Statistics.Summarize(x.Series.Points.Select(p => p.Value)), Statistics.Trend(x.Series.Points)))
            .ToList();

        var degradations = new List<DegradationCandidate>();
        foreach (var (key, minPoints) in new[] { (MetricKeys.RtAnswerTokS, 3), (MetricKeys.RtAggregateTokS, 3), (MetricKeys.ReqDecodeTokS, 3) })
        {
            if (series.TryGetValue(key, out var s))
            {
                var detectionOptions = key == MetricKeys.ReqDecodeTokS
                    ? options with { MinDuration = TimeSpan.Zero, BaselineWindow = TimeSpan.FromHours(1) }
                    : options;
                degradations.AddRange(DegradationDetector.Detect(s, detectionOptions, minPoints)
                    .Select(c => DegradationDetector.Annotate(c, data, gpuIndex, options.HighGpuTempC)));
            }
        }

        var throttles = ThrottleDetector.Detect(data.Gpu, gpuIndex, options.HighGpuTempC, data.Session.SampleIntervalMs);
        HardwareInfo? host = null;
        if (data.Session.HostJson is string json)
        {
            try
            {
                host = JsonSerializer.Deserialize<HardwareInfo>(json);
            }
            catch (JsonException)
            {
                host = null;
            }
        }

        var requests = data.Requests.Select(r => r.ToCompletedRequest()).ToList();
        return new SessionReport(
            data.Session,
            host,
            metrics,
            degradations.OrderBy(d => d.StartMs).ToList(),
            throttles,
            Energy(data, gpuIndex, requests),
            data.RuntimeInfo.LastOrDefault()?.Conditions,
            data.RuntimeInfo.Select(r => r.Conditions).ToList(),
            data.Events.Count(e => e.Severity == EventSeverity.Warning),
            data.Events.Count(e => e.Severity == EventSeverity.Error),
            data.Events.Count(e => e.Category == EventCategories.Gap),
            requests.Count,
            requests.Count(r => r.IsError));
    }

    private static EnergySummary Energy(SessionData data, int gpuIndex, IReadOnlyList<CompletedRequest> requests)
    {
        var gpu = data.Gpu.Where(g => g.GpuIndex == gpuIndex).OrderBy(g => g.TimeMs).ToList();
        double? joules = null;
        var counter = gpu.Where(g => g.EnergyMj is not null).ToList();
        if (counter.Count >= 2)
        {
            // Sum positive steps only: a driver reload resets the counter.
            long total = 0;
            for (var i = 1; i < counter.Count; i++)
            {
                var step = counter[i].EnergyMj!.Value - counter[i - 1].EnergyMj!.Value;
                if (step > 0)
                {
                    total += step;
                }
            }

            joules = total / 1000.0;
        }

        var power = gpu.Where(g => g.PowerW is not null).Select(g => g.PowerW!.Value).ToList();
        long? tokens = requests.Count > 0 && requests.Any(r => r.OutputTokens is not null) ? requests.Sum(r => r.OutputTokens ?? 0) : null;
        double? perToken = joules is double j && tokens is long t && t > 0 ? j / t : null;
        return new EnergySummary(joules, tokens, perToken, power.Count > 0 ? power.Average() : null,
            "세션 전체 GPU 에너지(NVML 누적 카운터 차이, 유휴 시간 포함) ÷ 세션 중 완료된 요청의 출력 토큰 합. 계산값입니다.");
    }
}

public sealed record ComparisonWarning(string Kind, string Message);

public sealed record ComparisonRow(MetricDefinition Metric, IReadOnlyList<SummaryStats> PerSession, IReadOnlyList<double?> DeltaPercent, string? Note)
{
    /// <summary>Whether the change of session <paramref name="index"/> is an improvement, a regression or neutral.</summary>
    public int Direction(int index)
    {
        if (Metric.HigherIsBetter is not bool higherIsBetter || DeltaPercent[index] is not double delta || Math.Abs(delta) < 1)
        {
            return 0;
        }

        return (delta > 0) == higherIsBetter ? 1 : -1;
    }
}

public sealed record ComparisonReport(IReadOnlyList<SessionReport> Sessions, int BaselineIndex, IReadOnlyList<ComparisonRow> Rows, IReadOnlyList<ComparisonWarning> Warnings);

public static class SessionComparer
{
    public const int SmallSample = 30;

    public static ComparisonReport Compare(IReadOnlyList<SessionReport> sessions, int baselineIndex = 0)
    {
        if (sessions.Count < 2)
        {
            throw new ArgumentException("비교하려면 세션이 2개 이상 필요합니다.", nameof(sessions));
        }

        baselineIndex = Math.Clamp(baselineIndex, 0, sessions.Count - 1);
        var rows = new List<ComparisonRow>();
        foreach (var key in SeriesExtractor.SummaryMetricKeys)
        {
            var stats = sessions.Select(s => s.Get(key)?.Stats ?? SummaryStats.Empty).ToList();
            if (stats.All(s => s.Count == 0))
            {
                continue;
            }

            var deltas = stats.Select(s => Statistics.PercentChange(stats[baselineIndex].Mean, s.Mean)).ToList();
            string? note = null;
            if (stats.Any(s => s.Count is > 0 and < SmallSample))
            {
                note = $"표본 {SmallSample}개 미만 세션 있음";
            }
            else if (stats.Any(s => s.Count == 0))
            {
                note = "일부 세션에 값 없음";
            }

            rows.Add(new ComparisonRow(MetricCatalog.Get(key), stats, deltas, note));
        }

        return new ComparisonReport(sessions, baselineIndex, rows, Warnings(sessions, baselineIndex));
    }

    private static List<ComparisonWarning> Warnings(IReadOnlyList<SessionReport> sessions, int baselineIndex)
    {
        var warnings = new List<ComparisonWarning>();
        var baseline = sessions[baselineIndex];
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var label = $"#{s.Session.Id} '{s.Session.Name}'";
            if (s.Conditions is null)
            {
                warnings.Add(new ComparisonWarning("condition-unknown", $"{label}: AI 엔진 조건(모델·버전)을 확인할 수 없습니다 (Strata 미연결 상태로 기록됨)."));
            }

            if (s.ConditionsChangedWithinSession)
            {
                warnings.Add(new ComparisonWarning("condition-mixed", $"{label}: 세션 안에서 엔진/모델 조건이 바뀌었습니다. 평균에 서로 다른 조건이 섞여 있습니다."));
            }

            if (i == baselineIndex)
            {
                continue;
            }

            var differences = RuntimeConditions.Describe(baseline.Conditions, s.Conditions);
            if (differences.Count > 0)
            {
                warnings.Add(new ComparisonWarning("condition-different",
                    $"{label}: 기준 세션과 조건이 다릅니다 - {string.Join("; ", differences)}. 차이가 이 조건 때문일 수 있습니다."));
            }

            if (baseline.Host is not null && s.Host is not null)
            {
                if (!SameNames(baseline.Host.Gpus.Select(g => g.Name), s.Host.Gpus.Select(g => g.Name)))
                {
                    warnings.Add(new ComparisonWarning("gpu-different", $"{label}: GPU 구성이 다릅니다."));
                }

                if (baseline.Host.GpuDriverVersion != s.Host.GpuDriverVersion)
                {
                    warnings.Add(new ComparisonWarning("driver-different",
                        $"{label}: GPU 드라이버 버전이 다릅니다 ({baseline.Host.GpuDriverVersion ?? "알 수 없음"} → {s.Host.GpuDriverVersion ?? "알 수 없음"})."));
                }
            }

            var basePower = baseline.Get(MetricKeys.GpuPowerLimit)?.Stats.Mean;
            var power = s.Get(MetricKeys.GpuPowerLimit)?.Stats.Mean;
            if (basePower is double bp && power is double p && Math.Abs(bp - p) >= 1)
            {
                warnings.Add(new ComparisonWarning("power-limit-different", $"{label}: GPU 전력 제한이 다릅니다 ({bp:0} W → {p:0} W)."));
            }

            if (baseline.Session.SampleIntervalMs != s.Session.SampleIntervalMs)
            {
                warnings.Add(new ComparisonWarning("interval-different",
                    $"{label}: 기록 간격이 다릅니다 ({baseline.Session.SampleIntervalMs} ms → {s.Session.SampleIntervalMs} ms). 최댓값/최솟값 비교에 영향을 줍니다."));
            }

            if (baseline.Session.IsMock != s.Session.IsMock)
            {
                warnings.Add(new ComparisonWarning("mock-mixed", $"{label}: 실측 세션과 테스트용 가상(MOCK) 세션을 비교하고 있습니다."));
            }
        }

        if (sessions.Any(s => s.Session.IsMock))
        {
            warnings.Add(new ComparisonWarning("mock", "가상 센서(MOCK)로 기록된 세션이 포함되어 있습니다. 실제 성능 데이터가 아닙니다."));
        }

        warnings.Add(new ComparisonWarning("correlation",
            "비교 결과는 시간적으로 함께 나타난 차이일 뿐입니다. 차이의 원인은 위의 조건 차이와 작업 부하(프롬프트 길이, 동시 요청 수)를 함께 보고 판단하세요."));
        return warnings;
    }

    private static bool SameNames(IEnumerable<string?> a, IEnumerable<string?> b) => a.SequenceEqual(b, StringComparer.Ordinal);
}
