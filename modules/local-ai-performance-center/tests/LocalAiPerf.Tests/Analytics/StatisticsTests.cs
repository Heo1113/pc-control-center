using LocalAiPerf.Analytics;
using Xunit;

namespace LocalAiPerf.Tests.Analytics;

public class StatisticsTests
{
    [Fact]
    public void Summary_matches_hand_calculation()
    {
        var s = Statistics.Summarize([1, 2, 3, 4, 5, double.NaN, double.PositiveInfinity]);

        Assert.Equal(5, s.Count);
        Assert.Equal(3, s.Mean);
        Assert.Equal(1, s.Min);
        Assert.Equal(5, s.Max);
        Assert.Equal(3, s.P50);
        Assert.Equal(4.8, s.P95!.Value, 9);
        Assert.Equal(Math.Sqrt(2.5), s.StdDev!.Value, 9);
    }

    [Fact]
    public void Empty_input_yields_no_numbers()
    {
        var s = Statistics.Summarize([]);
        Assert.Equal(0, s.Count);
        Assert.Null(s.Mean);
    }

    [Fact]
    public void Trend_reports_first_versus_last_window_and_slope()
    {
        var points = Enumerable.Range(0, 100).Select(i => new SeriesPoint(i * 36_000L, 100 - i * 0.5)).ToList(); // ~1 hour

        var trend = Statistics.Trend(points);

        Assert.True(trend.ChangePercent < -40);
        Assert.Equal(-50, trend.SlopePerHour!.Value, 0);
    }

    [Fact]
    public void Percent_change_is_undefined_for_zero_baseline()
    {
        Assert.Null(Statistics.PercentChange(0, 5));
        Assert.Equal(-25, Statistics.PercentChange(40, 30));
    }
}
