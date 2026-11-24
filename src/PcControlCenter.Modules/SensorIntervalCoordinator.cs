using PcControlCenter.Hardware;

namespace PcControlCenter.Modules;

/// <summary>
/// Runs the shared reader at the fastest interval any running module asks for, bounded by the shell setting, so that
/// every module still gets a reading per own tick (A 1 s, B 1 s, C 5 s by default) from a single NVML reader.
/// </summary>
public sealed class SensorIntervalCoordinator(SharedHardwareHub hub, TimeSpan maxInterval)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TimeSpan> _requests = [];

    public TimeSpan MaxInterval { get; } = maxInterval;

    public TimeSpan Effective
    {
        get
        {
            lock (_gate)
            {
                return Compute();
            }
        }
    }

    public IReadOnlyDictionary<string, TimeSpan> Requests
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, TimeSpan>(_requests);
            }
        }
    }

    public Task RequestAsync(string moduleId, TimeSpan interval)
    {
        TimeSpan effective;
        lock (_gate)
        {
            _requests[moduleId] = interval;
            effective = Compute();
        }

        return hub.SetIntervalAsync(effective);
    }

    public Task ReleaseAsync(string moduleId)
    {
        TimeSpan effective;
        lock (_gate)
        {
            _requests.Remove(moduleId);
            effective = Compute();
        }

        return hub.SetIntervalAsync(effective);
    }

    private TimeSpan Compute() => _requests.Count == 0 ? MaxInterval : TimeSpan.FromTicks(Math.Min(MaxInterval.Ticks, _requests.Values.Min(v => v.Ticks)));
}
