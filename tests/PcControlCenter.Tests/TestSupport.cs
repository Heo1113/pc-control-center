using LocalAiPerf.Core.Hardware;

namespace PcControlCenter.Tests;

/// <summary>A clock the test moves by hand (same as the helpers of projects A, B and C).</summary>
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
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

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

/// <summary>A GPU provider that counts how often the device is really read (to prove that consumers share readings).</summary>
internal sealed class CountingGpuProvider(TimeProvider time) : IGpuSensorProvider
{
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);
    public string Name => "counting-gpu";
    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public string? DriverVersion => "test-driver";
    public string? LibraryVersion => "test-nvml";
    public IReadOnlyList<GpuInfo> Devices { get; } = [new GpuInfo(0, "Test GPU", 16L << 30, 360)];

    public IReadOnlyList<SensorCapability> Capabilities { get; } =
        [new SensorCapability("gpu.power", "GPU 전력", "counting-gpu", SensorStatus.Ok, false)];

    /// <summary>Issue to report with the next readings (simulates a partial sensor failure).</summary>
    public SensorIssue? Issue { get; set; }

    public IReadOnlyList<GpuSample> ReadGpus(ICollection<SensorIssue> issues)
    {
        var n = Interlocked.Increment(ref _reads);
        if (Issue is { } issue)
        {
            issues.Add(issue);
        }

        return
        [
            new GpuSample
            {
                Index = 0,
                Name = "Test GPU",
                PowerW = 100 + n,
                UtilizationPercent = 50,
                TemperatureC = 60,
                MemoryUsedBytes = 4L << 30,
                MemoryTotalBytes = 16L << 30,
                TotalEnergyMilliJoules = 1_000_000L + n * 100_000L,
                GraphicsClockMhz = 2000,
                MemoryClockMhz = 10000,
                PowerLimitW = 360,
            },
        ];
    }

    public void Dispose()
    {
    }

    public DateTimeOffset Now => time.GetUtcNow();
}

internal sealed class CountingSystemProvider : ISystemSensorProvider
{
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);
    public string Name => "counting-system";
    public string? CpuName => "Test CPU";
    public int LogicalProcessors => 16;
    public IReadOnlyList<SensorCapability> Capabilities { get; } =
        [new SensorCapability("cpu.util", "CPU 사용률", "counting-system", SensorStatus.Ok, false)];

    public SystemSample ReadSystem(ICollection<SensorIssue> issues)
    {
        Interlocked.Increment(ref _reads);
        return new SystemSample { CpuUtilizationPercent = 12.5, MemoryUsedBytes = 8L << 30, MemoryTotalBytes = 32L << 30 };
    }

    public void Dispose()
    {
    }
}
