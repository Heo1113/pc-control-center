using LocalAiPerf.Core.Hardware;

namespace GpuProfit.Tests;

/// <summary>A clock the test moves by hand (wall time and monotonic time move together unless told otherwise).</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    private long _ticks;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _ticks += by.Ticks;
    }

    /// <summary>Moves only the wall clock (a manual time change or a resume from sleep with a stopped monotonic clock).</summary>
    public void JumpWall(TimeSpan by) => _now += by;
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Several mock GPUs with scripted power (tests only; real multi-GPU hardware was not available). Each GPU's power
/// is a function of the clock; returning null simulates a missing reading, <see cref="Fail"/> simulates a provider
/// exception, <see cref="Lost"/> makes the provider return no GPUs (connection lost).
/// </summary>
internal sealed class ScriptedGpuProvider(TimeProvider time, params Func<DateTimeOffset, double?>[] power) : IGpuSensorProvider
{
    public bool Fail { get; set; }
    public bool Lost { get; set; }
    public string Name => "Scripted GPUs";
    public bool IsAvailable => !Lost;
    public string? UnavailableReason => Lost ? "scripted loss" : null;
    public string? DriverVersion => "test";
    public string? LibraryVersion => "test";
    public IReadOnlyList<GpuInfo> Devices => power.Select((_, i) => new GpuInfo(i, $"Scripted GPU {i}", 8L << 30, 300)).ToList();
    public IReadOnlyList<SensorCapability> Capabilities => [];

    public IReadOnlyList<GpuSample> ReadGpus(ICollection<SensorIssue> issues)
    {
        if (Fail)
        {
            throw new InvalidOperationException("scripted failure");
        }

        if (Lost)
        {
            issues.Add(new SensorIssue("scripted", "gpu", SensorStatus.Unavailable, "lost"));
            return [];
        }

        var now = time.GetUtcNow();
        return power.Select((f, i) => new GpuSample
        {
            Index = i,
            Name = $"Scripted GPU {i}",
            PowerW = f(now),
            UtilizationPercent = f(now) is double p && p > 50 ? 95 : 2,
            MemoryTotalBytes = 8L << 30,
            MemoryUsedBytes = 2L << 30,
        }).ToList();
    }

    public void Dispose()
    {
    }
}

internal sealed class FixedSystemProvider : ISystemSensorProvider
{
    public string Name => "Fixed";
    public string? CpuName => "Test CPU";
    public int LogicalProcessors => 8;
    public IReadOnlyList<SensorCapability> Capabilities => [];

    public SystemSample ReadSystem(ICollection<SensorIssue> issues) => new() { CpuUtilizationPercent = 10, MemoryUsedBytes = 4L << 30, MemoryTotalBytes = 16L << 30 };

    public void Dispose()
    {
    }
}
