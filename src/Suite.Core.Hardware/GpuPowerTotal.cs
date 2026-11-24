using LocalAiPerf.Core.Hardware;

namespace Suite.Core.Hardware;

/// <summary>Whether a total over all GPUs could be formed from one reading.</summary>
public enum PowerTotalState
{
    /// <summary>Every expected GPU reported a valid board power; <see cref="GpuPowerTotal.TotalW"/> is their sum.</summary>
    Complete,

    /// <summary>Some, but not all, expected GPUs reported power. No total is formed (shown as PARTIAL, never as a smaller sum).</summary>
    Partial,

    /// <summary>No GPU reported power (or no GPU is known). No total.</summary>
    Unavailable,
}

/// <summary>
/// Board power summed over all GPUs of one shared reading (NVML measured). The sum exists only when every expected GPU
/// reported a finite, non-negative <see cref="GpuSample.PowerW"/>: with a dual GPU system (RTX 5080 + RTX 5060 Ti), one
/// GPU without power makes the total and everything derived from it (cost per hour) PARTIAL instead of a too small number.
/// Same rule as the PC Control Center dashboard (ModuleSummaries.Economics); a missing value is never taken as 0 W.
/// </summary>
public sealed record GpuPowerTotal
{
    public const string PartialLabel = "PARTIAL";

    public required PowerTotalState State { get; init; }

    /// <summary>The sum in watts; null unless <see cref="State"/> is <see cref="PowerTotalState.Complete"/>.</summary>
    public double? TotalW { get; init; }

    /// <summary>GPUs with a valid power value in this reading.</summary>
    public int Reported { get; init; }

    /// <summary>GPUs that should have reported: the larger of the reading's GPU count and the reader's device count.</summary>
    public int Expected { get; init; }

    public DateTimeOffset? MeasuredAt { get; init; }
    public bool IsMock { get; init; }

    /// <summary>Always <see cref="MetricSource.Nvml"/> (or <see cref="MetricSource.Mock"/>): the hub's GPU readings only.</summary>
    public MetricSource Source => IsMock ? MetricSource.Mock : MetricSource.Nvml;

    public static bool IsValidPower(double? watts) => watts is double w && double.IsFinite(w) && w >= 0;

    /// <param name="snapshot">One reading of the shared hub (null before the first reading).</param>
    /// <param name="expectedDevices">GPUs the reader knows (<see cref="SharedHardwareHub.GpuDevices"/>).Count.</param>
    public static GpuPowerTotal From(HardwareSnapshot? snapshot, int expectedDevices)
    {
        var gpus = snapshot?.Gpus ?? [];
        var expected = Math.Max(gpus.Count, Math.Max(0, expectedDevices));
        var valid = gpus.Where(g => IsValidPower(g.PowerW)).Select(g => g.PowerW!.Value).ToList();
        var state = expected == 0 || valid.Count == 0 ? PowerTotalState.Unavailable
            : valid.Count == expected ? PowerTotalState.Complete
            : PowerTotalState.Partial;
        return new GpuPowerTotal
        {
            State = state,
            TotalW = state == PowerTotalState.Complete ? valid.Sum() : null,
            Reported = valid.Count,
            Expected = expected,
            MeasuredAt = snapshot?.Timestamp,
            IsMock = snapshot?.IsMock ?? false,
        };
    }

    /// <summary>
    /// Electricity cost of one hour at this power: an estimate (board power only, not the whole PC). Null unless the total
    /// is <see cref="PowerTotalState.Complete"/> and a rate is set; a PARTIAL total never produces a cost.
    /// </summary>
    public decimal? EstimatedCostPerHour(decimal? ratePerKwh)
        => State == PowerTotalState.Complete && TotalW is double w && ratePerKwh is decimal rate && rate >= 0
            ? Math.Round((decimal)(w / 1000.0) * rate, 0, MidpointRounding.AwayFromZero)
            : null;
}
