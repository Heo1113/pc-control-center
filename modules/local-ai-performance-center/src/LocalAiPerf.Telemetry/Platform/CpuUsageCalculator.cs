namespace LocalAiPerf.Telemetry.Platform;

/// <summary>
/// Turns two cumulative CPU time readings into a utilisation percentage.
/// The first reading has no predecessor, so it yields no value (shown as N/A) instead of a guess.
/// </summary>
public sealed class CpuUsageCalculator
{
    private ulong? _lastIdle;
    private ulong? _lastTotal;

    /// <param name="idle">Cumulative idle time.</param>
    /// <param name="total">Cumulative total time (idle + busy) in the same unit.</param>
    public double? Update(ulong idle, ulong total)
    {
        double? result = null;
        if (_lastIdle is ulong lastIdle && _lastTotal is ulong lastTotal && total > lastTotal && idle >= lastIdle)
        {
            var totalDelta = total - lastTotal;
            var idleDelta = Math.Min(idle - lastIdle, totalDelta);
            result = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
        }

        _lastIdle = idle;
        _lastTotal = total;
        return result;
    }
}
