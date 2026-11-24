using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Metrics;

namespace LocalAiPerf.Telemetry.Platform;

/// <summary>CPU utilisation and memory from documented kernel32 functions. Works as a normal user.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSystemProvider : ISystemSensorProvider
{
    private const string Source = "Windows API";
    private readonly CpuUsageCalculator _cpu = new();

    public WindowsSystemProvider()
    {
        CpuName = ReadCpuName();
        LogicalProcessors = Environment.ProcessorCount;
        var issues = new List<SensorIssue>();
        _ = ReadCpu(issues); // primes the calculator so the next reading has a delta
        Capabilities =
        [
            new SensorCapability(MetricKeys.CpuUtil, "CPU 사용률", "GetSystemTimes", SensorStatus.Ok, false, "전체 논리 프로세서 평균, 시간 기준"),
            new SensorCapability(MetricKeys.RamUsedGiB, "시스템 RAM 사용량/전체", "GlobalMemoryStatusEx", SensorStatus.Ok, false),
        ];
    }

    public string Name => "Windows";
    public string? CpuName { get; }
    public int LogicalProcessors { get; }
    public IReadOnlyList<SensorCapability> Capabilities { get; }

    public SystemSample ReadSystem(ICollection<SensorIssue> issues)
    {
        var cpu = ReadCpu(issues);
        long? used = null, total = null;
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            total = (long)Math.Min(status.TotalPhys, long.MaxValue);
            used = (long)Math.Min(status.TotalPhys - Math.Min(status.AvailPhys, status.TotalPhys), long.MaxValue);
        }
        else
        {
            issues.Add(new SensorIssue(Source, MetricKeys.RamUsedGiB, SensorStatus.Error, $"GlobalMemoryStatusEx 실패 (Win32 {Marshal.GetLastPInvokeError()})"));
        }

        return new SystemSample { CpuUtilizationPercent = cpu, MemoryUsedBytes = used, MemoryTotalBytes = total };
    }

    public void Dispose()
    {
    }

    private double? ReadCpu(ICollection<SensorIssue> issues)
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            issues.Add(new SensorIssue(Source, MetricKeys.CpuUtil, SensorStatus.Error, $"GetSystemTimes 실패 (Win32 {Marshal.GetLastPInvokeError()})"));
            return null;
        }

        // Kernel time includes idle time.
        return _cpu.Update((ulong)idle, (ulong)kernel + (ulong)user);
    }

    private static string? ReadCpuName()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
