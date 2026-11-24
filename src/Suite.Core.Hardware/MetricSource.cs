namespace Suite.Core.Hardware;

/// <summary>
/// Where a value was measured. Values of different sources measure different things even when they share a unit
/// (NVML's per-GPU memory vs. the WDDM counters' dedicated memory; NVML board power vs. WireView's connector power), so
/// they are shown side by side with their source and never added, averaged or substituted for each other.
/// </summary>
public enum MetricSource
{
    /// <summary>NVIDIA driver through the shared hub (GPU temperature, board power, clocks, utilization, memory).</summary>
    Nvml,

    /// <summary>Windows GPU performance counters (PC Control Center's GPU memory page). Not used by IRON's numbers.</summary>
    WddmPdh,

    /// <summary>Windows CPU and RAM counters through the shared hub.</summary>
    WindowsSystem,

    /// <summary>Thermal Grizzly WireView Pro II over its USB serial port (connector power, pin currents, temperatures).</summary>
    WireView,

    /// <summary>The mock providers (tests and demos only).</summary>
    Mock,
}

/// <summary>A value together with the source and time of its measurement.</summary>
public readonly record struct Sourced<T>(T Value, MetricSource Source, DateTimeOffset MeasuredAt)
    where T : struct;

public static class SourcedValues
{
    /// <summary>
    /// Sums values of one source. Throws when the sources differ: a sum over sources would be a number nobody measured.
    /// Returns null for an empty list (no value is not 0).
    /// </summary>
    public static Sourced<double>? SumOfOneSource(IReadOnlyList<Sourced<double>> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var source = values[0].Source;
        if (values.Any(v => v.Source != source))
        {
            throw new InvalidOperationException(
                $"서로 다른 출처의 값은 합치지 않습니다 ({string.Join(", ", values.Select(v => v.Source).Distinct())}).");
        }

        return new Sourced<double>(values.Sum(v => v.Value), source, values.Min(v => v.MeasuredAt));
    }
}
