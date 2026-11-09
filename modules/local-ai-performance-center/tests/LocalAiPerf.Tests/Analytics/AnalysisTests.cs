using LocalAiPerf.Analytics;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Metrics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Sessions;
using LocalAiPerf.Storage;
using Xunit;
using static LocalAiPerf.Tests.FrameFactory;

namespace LocalAiPerf.Tests.Analytics;

public class AnalysisTests
{
    private static long Record(TelemetryStore store, string name, int seconds, Func<int, double?> tok, string model = "model-a",
        Func<int, double>? temp = null, Func<int, ulong>? throttle = null, string engine = "0.1.55")
    {
        var start = T0.AddHours(store.ListSessions().Count);
        var session = store.StartSession(Start(start, name));
        RuntimeConditions? last = null;
        for (var i = 0; i < seconds; i++)
        {
            var frame = Frame(start.AddSeconds(i), tokS: tok(i), seq: i, model: model, engine: engine, temp: temp?.Invoke(i) ?? 65,
                throttle: throttle?.Invoke(i) ?? 0, power: 200 + (tok(i) ?? 0), energyMj: i * 250_000L);
            var conditions = RuntimeConditions.FromSnapshot(frame.Runtime!);
            store.WriteFrame(new FrameWrite
            {
                SessionId = session.Id,
                Frame = frame,
                Conditions = conditions?.Fingerprint != last?.Fingerprint ? conditions : null,
                NewRequests = i % 10 == 9 ? [new CompletedRequest { Id = $"{i:x12}", OutputTokens = 400, DecodeMs = 10_000, StartedUnix = start.AddSeconds(i).ToUnixTimeSeconds() }] : [],
                InstanceId = "inst",
            });
            last = conditions;
        }

        store.EndSession(session.Id, start.AddSeconds(seconds), SessionEndReasons.UserStopped);
        return session.Id;
    }

    [Fact]
    public void A_sustained_drop_is_a_degradation_candidate_with_concurrent_observations_not_causes()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var id = Record(store, "drop", 300, i => i is >= 150 and < 200 ? 20 : 40,
            temp: i => i is >= 150 and < 200 ? 88 : 65, throttle: i => i is >= 150 and < 200 ? (ulong)GpuThrottleReasons.SwThermalSlowdown : 0);

        var report = SessionAnalyzer.Analyze(store.LoadSessionData(id)!, DetectionOptions.Default);

        var candidate = Assert.Single(report.Degradations, d => d.MetricKey == MetricKeys.RtAnswerTokS);
        Assert.InRange(candidate.DropPercent, 45, 55);
        Assert.InRange(candidate.Duration.TotalSeconds, 40, 60);
        Assert.Contains(candidate.ConcurrentObservations, o => o.Contains("온도"));
        Assert.Contains(candidate.ConcurrentObservations, o => o.Contains("클럭 제한"));
        Assert.NotEmpty(report.Throttles);
    }

    [Fact]
    public void Steady_series_has_no_candidates_and_short_dips_are_ignored()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var id = Record(store, "steady", 200, i => i == 100 || i == 101 ? 10 : 40 + (i % 3));

        var report = SessionAnalyzer.Analyze(store.LoadSessionData(id)!, DetectionOptions.Default);

        Assert.DoesNotContain(report.Degradations, d => d.MetricKey == MetricKeys.RtAnswerTokS);
        var tok = report.Get(MetricKeys.RtAnswerTokS)!;
        Assert.Equal(200, tok.Stats.Count);
        Assert.Equal(MetricOrigin.Reported, tok.Metric.Origin);
    }

    [Fact]
    public void Energy_per_token_uses_the_measured_counter_and_is_labelled_derived()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var id = Record(store, "energy", 100, _ => 40);

        var energy = SessionAnalyzer.Analyze(store.LoadSessionData(id)!, DetectionOptions.Default).Energy;

        Assert.Equal(99 * 250.0, energy.GpuEnergyJoules); // 99 steps of 250 J
        Assert.Equal(10 * 400, energy.OutputTokens);
        Assert.Equal(99 * 250.0 / 4000, energy.JoulesPerOutputToken!.Value, 9);
        Assert.Contains("계산값", energy.Basis);
    }

    [Fact]
    public void Comparison_reports_deltas_and_warns_about_different_conditions()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var before = Record(store, "before", 120, _ => 40, engine: "0.1.54");
        var after = Record(store, "after", 120, _ => 30, engine: "0.1.55");
        var options = DetectionOptions.Default;

        var comparison = SessionComparer.Compare([
            SessionAnalyzer.Analyze(store.LoadSessionData(before)!, options),
            SessionAnalyzer.Analyze(store.LoadSessionData(after)!, options),
        ]);

        var row = comparison.Rows.Single(r => r.Metric.Key == MetricKeys.RtAnswerTokS);
        Assert.Equal(-25, row.DeltaPercent[1]!.Value, 6);
        Assert.Equal(-1, row.Direction(1)); // lower tok/s is a regression
        Assert.Contains(comparison.Warnings, w => w.Kind == "condition-different" && w.Message.Contains("0.1.54"));
        Assert.Contains(comparison.Warnings, w => w.Kind == "correlation");
    }

    [Fact]
    public void Comparison_needs_two_sessions()
    {
        using var dir = new TempDir();
        using var store = TelemetryStore.Open(dir.File("lapc.db"));
        var id = Record(store, "only", 10, _ => 40);
        Assert.Throws<ArgumentException>(() => SessionComparer.Compare([SessionAnalyzer.Analyze(store.LoadSessionData(id)!, DetectionOptions.Default)]));
    }
}
