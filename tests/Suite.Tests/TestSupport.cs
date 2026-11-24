using LocalAiPerf.Core.Hardware;

namespace Suite.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "suite-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => _now += by;
}

internal static class Gpus
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    public static GpuSample Rtx5080(double? powerW = 312.4) => new()
    {
        Index = 0, Name = "NVIDIA GeForce RTX 5080", PowerW = powerW, TemperatureC = 61, UtilizationPercent = 97,
        MemoryUsedBytes = 14L << 30, MemoryTotalBytes = 16L << 30, GraphicsClockMhz = 2820, DeviceUuid = "GPU-5080",
    };

    public static GpuSample Rtx5060Ti(double? powerW = 151.8) => new()
    {
        Index = 1, Name = "NVIDIA GeForce RTX 5060 Ti", PowerW = powerW, TemperatureC = 55, UtilizationPercent = 88,
        MemoryUsedBytes = 12L << 30, MemoryTotalBytes = 16L << 30, GraphicsClockMhz = 2700, DeviceUuid = "GPU-5060TI",
    };

    public static HardwareSnapshot Snapshot(params GpuSample[] gpus) => new() { Timestamp = T0, Gpus = gpus };
}
