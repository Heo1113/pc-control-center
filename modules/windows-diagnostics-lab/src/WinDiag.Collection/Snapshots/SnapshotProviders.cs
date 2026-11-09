using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalAiPerf.Core.Hardware;
using Microsoft.Win32;
using WinDiag.Core;
using WinDiag.Core.Snapshots;

namespace WinDiag.Collection.Snapshots;

public interface ISnapshotProvider
{
    /// <summary>Reads the current configuration. Parts that cannot be read are listed in Issues, never guessed.</summary>
    SystemSnapshot Collect(bool includeDrivers, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the system configuration with documented, read-only, non-elevated interfaces: the registry (Windows version,
/// BIOS and board identification) and WMI/CIM (memory modules, display adapters, disks, signed drivers, hotfixes).
/// GPU driver and memory totals come from project A's sensor module. No serial numbers or user data are read.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSnapshotProvider(Func<HardwareInfo?> hardware) : ISnapshotProvider
{
    /// <summary>Device classes whose drivers change with plugging devices in and out (not configuration changes).</summary>
    private static readonly HashSet<string> VolatileClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "USB", "HIDCLASS", "WPD", "PRINTER", "PRINTQUEUE", "MONITOR", "VOLUME", "VOLUMESNAPSHOT", "KEYBOARD", "MOUSE", "IMAGE",
        "SMARTCARDREADER", "BIOMETRIC", "CAMERA", "AUDIOENDPOINT", "PRINTFQUEUE", "SENSOR", "BLUETOOTH",
    };

    public SystemSnapshot Collect(bool includeDrivers, CancellationToken cancellationToken)
    {
        var issues = new List<SnapshotIssue>();
        var info = hardware();
        var snapshot = new SystemSnapshot
        {
            CollectedAt = DateTimeOffset.UtcNow,
            AppVersion = AppInfo.Version,
            Os = Try("Windows 버전", issues, ReadOs),
            Bios = Try("BIOS/메인보드", issues, ReadBios),
            Cpu = new CpuInfo { Name = info?.CpuName ?? Try("CPU", issues, ReadCpuName), LogicalProcessors = Environment.ProcessorCount },
            Memory = new MemoryInfo { TotalBytes = info?.RamTotalBytes, Modules = Try("메모리 모듈 (WMI)", issues, ReadMemoryModules) ?? [] },
            Gpus = Try("디스플레이 어댑터 (WMI)", issues, ReadGpus) ?? [],
            NvidiaDriverVersion = info?.GpuDriverVersion,
            Disks = Try("저장장치 (WMI)", issues, ReadDisks) ?? [],
            Hotfixes = Try("설치된 업데이트 (WMI)", issues, ReadHotfixes) ?? [],
        };
        cancellationToken.ThrowIfCancellationRequested();
        if (includeDrivers)
        {
            snapshot = snapshot with { Drivers = Try("장치 드라이버 목록 (WMI)", issues, ReadDrivers) ?? [] };
        }

        return snapshot with { Issues = issues };
    }

    private static T? Try<T>(string part, List<SnapshotIssue> issues, Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or System.Security.SecurityException
                                       or IOException or InvalidOperationException or PlatformNotSupportedException or TimeoutException)
        {
            issues.Add(new SnapshotIssue(part, $"읽을 수 없음: {ex.Message}"));
            return null;
        }
    }

    private static OsInfo ReadOs()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")
                        ?? throw new IOException("레지스트리 키가 없습니다.");
        return new OsInfo
        {
            ProductName = key.GetValue("ProductName") as string,
            DisplayVersion = key.GetValue("DisplayVersion") as string,
            CurrentBuild = key.GetValue("CurrentBuild") as string ?? key.GetValue("CurrentBuildNumber") as string,
            Ubr = key.GetValue("UBR") is int ubr ? ubr : null,
            EditionId = key.GetValue("EditionID") as string,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
        };
    }

    private static BiosInfo ReadBios()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS") ?? throw new IOException("레지스트리 키가 없습니다.");
        string? Value(string name) => (key.GetValue(name) as string)?.Trim();
        return new BiosInfo
        {
            Vendor = Value("BIOSVendor"),
            Version = Value("BIOSVersion"),
            ReleaseDate = Value("BIOSReleaseDate"),
            SystemManufacturer = Value("SystemManufacturer"),
            SystemProductName = Value("SystemProductName"),
            BoardManufacturer = Value("BaseBoardManufacturer"),
            BoardProduct = Value("BaseBoardProduct"),
            BoardVersion = Value("BaseBoardVersion"),
        };
    }

    private static string? ReadCpuName()
        => (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string)?.Trim();

    private static List<MemoryModuleInfo> ReadMemoryModules()
        => Query("SELECT BankLabel, DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, ConfiguredVoltage, Manufacturer, PartNumber FROM Win32_PhysicalMemory",
            o => new MemoryModuleInfo
            {
                Slot = Str(o, "DeviceLocator"),
                Bank = Str(o, "BankLabel"),
                CapacityBytes = Long(o, "Capacity"),
                SpeedMts = (int?)Long(o, "Speed"),
                ConfiguredSpeedMts = (int?)Long(o, "ConfiguredClockSpeed"),
                ConfiguredVoltageMv = Long(o, "ConfiguredVoltage") is long mv and > 0 ? (int)mv : null,
                Manufacturer = Str(o, "Manufacturer"),
                PartNumber = Str(o, "PartNumber"),
            });

    private static List<GpuAdapterInfo> ReadGpus()
        => Query("SELECT Name, DriverVersion, DriverDate, VideoProcessor, Status FROM Win32_VideoController",
            o => new GpuAdapterInfo
            {
                Name = Str(o, "Name"),
                DriverVersion = Str(o, "DriverVersion"),
                DriverDate = CimDate(Str(o, "DriverDate")),
                VideoProcessor = Str(o, "VideoProcessor"),
                Status = Str(o, "Status"),
            });

    private static List<DiskInfo> ReadDisks()
        => Query("SELECT Model, InterfaceType, MediaType, Size, FirmwareRevision, Status FROM Win32_DiskDrive",
            o => new DiskInfo
            {
                Model = Str(o, "Model"),
                InterfaceType = Str(o, "InterfaceType"),
                MediaType = Str(o, "MediaType"),
                SizeBytes = Long(o, "Size"),
                FirmwareRevision = Str(o, "FirmwareRevision"),
                Status = Str(o, "Status"),
            });

    private static List<HotfixInfo> ReadHotfixes()
        => Query("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering",
            o => new HotfixInfo { HotFixId = Str(o, "HotFixID"), Description = Str(o, "Description"), InstalledOn = Str(o, "InstalledOn") });

    private static List<DriverInfo> ReadDrivers()
        => Query("SELECT DeviceName, DeviceClass, DriverVersion, DriverDate, DriverProviderName, InfName FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL",
                o => new DriverInfo
                {
                    DeviceName = Str(o, "DeviceName"),
                    DeviceClass = Str(o, "DeviceClass"),
                    DriverVersion = Str(o, "DriverVersion"),
                    DriverDate = CimDate(Str(o, "DriverDate")),
                    Provider = Str(o, "DriverProviderName"),
                    InfName = Str(o, "InfName"),
                }, TimeSpan.FromSeconds(60))
            .Where(d => d.DeviceClass is null || !VolatileClasses.Contains(d.DeviceClass))
            .DistinctBy(d => (d.Key, d.DriverVersion))
            .OrderBy(d => d.Key, StringComparer.Ordinal)
            .ToList();

    private static List<T> Query<T>(string wql, Func<ManagementBaseObject, T> map, TimeSpan? timeout = null)
    {
        var options = new System.Management.EnumerationOptions { Timeout = timeout ?? TimeSpan.FromSeconds(20), ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery(wql), options);
        using var results = searcher.Get();
        var list = new List<T>();
        foreach (var item in results)
        {
            using (item)
            {
                list.Add(map(item));
            }
        }

        return list;
    }

    private static string? Str(ManagementBaseObject o, string name) => (o[name] as string)?.Trim() is { Length: > 0 } s ? s : null;

    private static long? Long(ManagementBaseObject o, string name)
        => o[name] switch
        {
            null => null,
            ulong u => (long)Math.Min(u, long.MaxValue),
            uint u => u,
            ushort u => u,
            int i => i,
            long l => l,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
            _ => null,
        };

    /// <summary>CIM datetime "20250101000000.000000-000" to "2025-01-01".</summary>
    internal static string? CimDate(string? value)
        => value is { Length: >= 8 } && DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value;
}

/// <summary>Snapshot source for mock mode and non-Windows runs: clearly marked as mock, no values invented for real parts.</summary>
public sealed class MockSnapshotProvider(Func<HardwareInfo?> hardware) : ISnapshotProvider
{
    public SystemSnapshot Collect(bool includeDrivers, CancellationToken cancellationToken)
    {
        var info = hardware();
        return new SystemSnapshot
        {
            CollectedAt = DateTimeOffset.UtcNow,
            AppVersion = AppInfo.Version,
            IsMock = true,
            Os = new OsInfo { ProductName = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.OSArchitecture.ToString() },
            Cpu = new CpuInfo { Name = info?.CpuName, LogicalProcessors = Environment.ProcessorCount },
            Memory = new MemoryInfo { TotalBytes = info?.RamTotalBytes },
            NvidiaDriverVersion = info?.GpuDriverVersion,
            Issues = [new SnapshotIssue("전체", OperatingSystem.IsWindows()
                ? "MOCK 모드: 실제 구성 정보를 읽지 않았습니다."
                : "Windows가 아니어서 레지스트리·WMI 구성 정보를 읽을 수 없습니다.")],
        };
    }
}
