using LocalAiPerf.Analytics;

namespace GpuProfit.Accounting.Energy;

/// <summary>Sample statistics of one GPU's power inside the windows (project A's <see cref="Statistics"/>).</summary>
public static class PowerStatistics
{
    /// <summary>Statistics of the valid power readings (not time-weighted: the energy uses the time-weighted integral).</summary>
    public static SummaryStats ForDevice(IEnumerable<PowerSample> samples, IReadOnlyList<TimeWindow> windows, int deviceIndex)
    {
        var ws = TimeWindow.Normalize(windows);
        return Statistics.Summarize(samples
            .Where(s => s.DeviceIndex == deviceIndex && EnergyIntegrator.IsValid(s.PowerW) && ws.Any(w => s.TimeMs >= w.StartMs && s.TimeMs < w.EndMs))
            .Select(s => s.PowerW!.Value));
    }
}
