using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDiag.Core.Snapshots;

public sealed record OsInfo
{
    public string? ProductName { get; init; }
    public string? DisplayVersion { get; init; }
    public string? CurrentBuild { get; init; }
    public int? Ubr { get; init; }
    public string? EditionId { get; init; }
    public string? Architecture { get; init; }

    /// <summary>
    /// The registry ProductName of Windows 11 still says "Windows 10"; Microsoft's documented way to tell them apart is
    /// the build number (22000 or later is Windows 11).
    /// </summary>
    public string DisplayName
    {
        get
        {
            var name = ProductName ?? "Windows";
            if (int.TryParse(CurrentBuild, NumberStyles.Integer, CultureInfo.InvariantCulture, out var build) && build >= 22000)
            {
                name = name.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            }

            return name;
        }
    }

    public string BuildText => CurrentBuild is null ? "N/A" : Ubr is int ubr ? $"{CurrentBuild}.{ubr}" : CurrentBuild;
}

public sealed record BiosInfo
{
    public string? Vendor { get; init; }
    public string? Version { get; init; }
    public string? ReleaseDate { get; init; }
    public string? SystemManufacturer { get; init; }
    public string? SystemProductName { get; init; }
    public string? BoardManufacturer { get; init; }
    public string? BoardProduct { get; init; }
    public string? BoardVersion { get; init; }
}

public sealed record CpuInfo
{
    public string? Name { get; init; }
    public int LogicalProcessors { get; init; }
}

/// <summary>One memory module as reported by SMBIOS through WMI (Win32_PhysicalMemory). Serial numbers are not read.</summary>
public sealed record MemoryModuleInfo
{
    public string? Slot { get; init; }
    public string? Bank { get; init; }
    public long? CapacityBytes { get; init; }
    public int? SpeedMts { get; init; }

    /// <summary>Configured speed reported by the firmware (for example the EXPO speed). Not a measurement.</summary>
    public int? ConfiguredSpeedMts { get; init; }

    /// <summary>Configured voltage in mV reported by the firmware through SMBIOS. Not a live measurement.</summary>
    public int? ConfiguredVoltageMv { get; init; }

    public string? Manufacturer { get; init; }
    public string? PartNumber { get; init; }
}

public sealed record MemoryInfo
{
    public long? TotalBytes { get; init; }
    public IReadOnlyList<MemoryModuleInfo> Modules { get; init; } = [];
}

public sealed record GpuAdapterInfo
{
    public string? Name { get; init; }
    public string? DriverVersion { get; init; }
    public string? DriverDate { get; init; }
    public string? VideoProcessor { get; init; }
    public string? Status { get; init; }
}

public sealed record DiskInfo
{
    public string? Model { get; init; }
    public string? InterfaceType { get; init; }
    public string? MediaType { get; init; }
    public long? SizeBytes { get; init; }
    public string? FirmwareRevision { get; init; }
    public string? Status { get; init; }
}

public sealed record DriverInfo
{
    public string? DeviceName { get; init; }
    public string? DeviceClass { get; init; }
    public string? DriverVersion { get; init; }
    public string? DriverDate { get; init; }
    public string? Provider { get; init; }
    public string? InfName { get; init; }

    [JsonIgnore]
    public string Key => $"{DeviceClass}|{DeviceName}".ToUpperInvariant();
}

public sealed record HotfixInfo
{
    public string? HotFixId { get; init; }
    public string? Description { get; init; }
    public string? InstalledOn { get; init; }
}

/// <summary>A part of the snapshot that could not be read, with the reason (shown instead of a guessed value).</summary>
public sealed record SnapshotIssue(string Part, string Message);

/// <summary>
/// The system configuration at one point in time. Contains no user name, computer name or serial number.
/// </summary>
public sealed record SystemSnapshot
{
    public DateTimeOffset CollectedAt { get; init; }
    public string AppVersion { get; init; } = "";
    public bool IsMock { get; init; }
    public OsInfo? Os { get; init; }
    public BiosInfo? Bios { get; init; }
    public CpuInfo? Cpu { get; init; }
    public MemoryInfo? Memory { get; init; }
    public IReadOnlyList<GpuAdapterInfo> Gpus { get; init; } = [];

    /// <summary>Driver version reported by NVIDIA NVML (project A's sensor module), when an NVIDIA driver is present.</summary>
    public string? NvidiaDriverVersion { get; init; }

    public IReadOnlyList<DiskInfo> Disks { get; init; } = [];
    public IReadOnlyList<DriverInfo> Drivers { get; init; } = [];
    public IReadOnlyList<HotfixInfo> Hotfixes { get; init; } = [];
    public IReadOnlyList<SnapshotIssue> Issues { get; init; } = [];

    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static SystemSnapshot FromJson(string json) => JsonSerializer.Deserialize<SystemSnapshot>(json, Json) ?? new SystemSnapshot();

    /// <summary>
    /// Hash of the configuration only: collection time, app version and read issues are excluded, so an unchanged
    /// system gives the same hash and is stored once (its "last confirmed" time moves forward instead).
    /// </summary>
    public string ContentHash()
    {
        var normalized = this with
        {
            CollectedAt = default,
            AppVersion = "",
            Issues = [],
            Drivers = Drivers.OrderBy(d => d.Key, StringComparer.Ordinal).ThenBy(d => d.DriverVersion, StringComparer.Ordinal).ToList(),
            Hotfixes = Hotfixes.OrderBy(h => h.HotFixId, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToJson()))).ToLowerInvariant();
    }
}
