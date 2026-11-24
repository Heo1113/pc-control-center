using System.Globalization;
using System.Runtime.Versioning;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Metrics;

namespace LocalAiPerf.Telemetry.Platform;

/// <summary>
/// CPU and memory from /proc on Linux. The app targets Windows; this provider exists so that the
/// cross-platform recorder and CLI can be exercised with real readings on Linux build machines.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxProcSystemProvider : ISystemSensorProvider
{
    private const string Source = "Linux /proc";
    private readonly CpuUsageCalculator _cpu = new();
    private readonly string _procRoot;

    public LinuxProcSystemProvider(string procRoot = "/proc")
    {
        _procRoot = procRoot;
        CpuName = ReadCpuName();
        LogicalProcessors = Environment.ProcessorCount;
        _ = ReadCpu(new List<SensorIssue>());
        Capabilities =
        [
            new SensorCapability(MetricKeys.CpuUtil, "CPU 사용률", "/proc/stat", SensorStatus.Ok, false),
            new SensorCapability(MetricKeys.RamUsedGiB, "시스템 RAM 사용량/전체", "/proc/meminfo", SensorStatus.Ok, false),
        ];
    }

    public string Name => "Linux /proc";
    public string? CpuName { get; }
    public int LogicalProcessors { get; }
    public IReadOnlyList<SensorCapability> Capabilities { get; }

    public SystemSample ReadSystem(ICollection<SensorIssue> issues)
    {
        var cpu = ReadCpu(issues);
        long? total = null, used = null;
        try
        {
            long? memTotal = null, memAvailable = null;
            foreach (var line in File.ReadLines(Path.Combine(_procRoot, "meminfo")))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    memTotal = ParseKb(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    memAvailable = ParseKb(line);
                }
            }

            if (memTotal is long t && memAvailable is long a)
            {
                total = t;
                used = Math.Max(0, t - a);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            issues.Add(new SensorIssue(Source, MetricKeys.RamUsedGiB, SensorStatus.Error, ex.Message));
        }

        return new SystemSample { CpuUtilizationPercent = cpu, MemoryUsedBytes = used, MemoryTotalBytes = total };
    }

    public void Dispose()
    {
    }

    private double? ReadCpu(ICollection<SensorIssue> issues)
    {
        try
        {
            var first = File.ReadLines(Path.Combine(_procRoot, "stat")).First();
            var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "cpu")
            {
                issues.Add(new SensorIssue(Source, MetricKeys.CpuUtil, SensorStatus.Error, "/proc/stat 형식을 읽을 수 없습니다."));
                return null;
            }

            ulong total = 0;
            for (var i = 1; i < parts.Length && i <= 8; i++)
            {
                total += ulong.Parse(parts[i], CultureInfo.InvariantCulture);
            }

            var idle = ulong.Parse(parts[4], CultureInfo.InvariantCulture)
                + (parts.Length > 5 ? ulong.Parse(parts[5], CultureInfo.InvariantCulture) : 0);
            return _cpu.Update(idle, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException or InvalidOperationException)
        {
            issues.Add(new SensorIssue(Source, MetricKeys.CpuUtil, SensorStatus.Error, ex.Message));
            return null;
        }
    }

    private static long ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return long.Parse(parts[1], CultureInfo.InvariantCulture) * 1024;
    }

    private string? ReadCpuName()
    {
        try
        {
            var line = File.ReadLines(Path.Combine(_procRoot, "cpuinfo")).FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
            return line?.Split(':', 2)[1].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
