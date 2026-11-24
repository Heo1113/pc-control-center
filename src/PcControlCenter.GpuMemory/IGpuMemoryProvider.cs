using LocalAiPerf.Core.Hardware;

namespace PcControlCenter.GpuMemory;

/// <summary>
/// Reads GPU memory per adapter and per process. Implementations are created and called on the monitor's own thread only,
/// must not throw for API failures (they report issues and leave values null) and never open NVML.
/// </summary>
public interface IGpuMemoryProvider : IDisposable
{
    /// <summary>Short source name for the status line.</summary>
    string Name { get; }

    bool IsMock { get; }

    /// <summary>Null when the provider can read; otherwise why every value is N/A (shown to the user).</summary>
    string? UnavailableReason { get; }

    /// <summary>One reading. <see cref="GpuMemorySnapshot.ExpectedInterval"/> is filled in by the monitor.</summary>
    GpuMemorySnapshot Read(GpuMemoryReadRequest request);
}

/// <summary>
/// What a reading should include besides the memory counters. <see cref="WatchedPorts"/>: local TCP ports whose listening
/// process should be identified (for example the port of the Strata server the local AI module is connected to); other
/// listeners are not looked up. <see cref="IncludeDetails"/>: engine activity and the libraries / model files of the larger
/// GPU processes, read only while the GPU memory page is visible (they cost more than the memory counters).
/// </summary>
public sealed record GpuMemoryReadRequest(IReadOnlySet<int> WatchedPorts, bool IncludeDetails = false)
{
    public static GpuMemoryReadRequest None { get; } = new(new HashSet<int>());
}

/// <summary>Where per-process GPU memory is not available (not Windows, or switched off in the settings).</summary>
public sealed class UnavailableGpuMemoryProvider(string reason, TimeProvider? time = null) : IGpuMemoryProvider
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => "없음";
    public bool IsMock => false;
    public string? UnavailableReason { get; } = reason;

    public GpuMemorySnapshot Read(GpuMemoryReadRequest request) => new()
    {
        Timestamp = _time.GetUtcNow(),
        Source = Name,
        Issues = [new SensorIssue("GPU 메모리", "provider", SensorStatus.Unavailable, UnavailableReason!)],
    };

    public void Dispose()
    {
    }
}
