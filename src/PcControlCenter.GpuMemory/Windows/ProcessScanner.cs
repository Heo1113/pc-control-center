using System.Diagnostics;
using System.Runtime.Versioning;
using static PcControlCenter.GpuMemory.Windows.NativeMethods;

namespace PcControlCenter.GpuMemory.Windows;

/// <summary>
/// Lists the libraries and data files mapped into a process by walking its address space with VirtualQueryEx and asking
/// GetMappedFileName for the file behind each image or mapped view. Only file names are read: no memory contents, no
/// command line. GetMappedFileName requires the query and VM-read rights, which protected and elevated processes refuse.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class ProcessScanner
{
    private const int MaxRegions = 500_000;

    public static RawProcessScan Scan(int pid, DosDevices devices, TimeSpan budget)
    {
        var process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)pid);
        if (process == 0)
        {
            return RawProcessScan.Denied;
        }

        try
        {
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var started = Stopwatch.GetTimestamp();
            var buffer = stackalloc char[1024];
            nint address = 0;
            var partial = false;
            for (var regions = 0; ; regions++)
            {
                MemoryBasicInformation info;
                if (VirtualQueryEx(process, address, &info, (nuint)sizeof(MemoryBasicInformation)) == 0)
                {
                    break;
                }

                if (info.State != MemFree && info.BaseAddress == info.AllocationBase && info.Type is MemImage or MemMapped)
                {
                    var length = GetMappedFileName(process, info.BaseAddress, buffer, 1024);
                    if (length > 0)
                    {
                        var device = new string(buffer, 0, (int)length);
                        (info.Type == MemImage ? libraries : files).Add(devices.ToDosPath(device) ?? device);
                    }
                }

                var next = (nint)((nuint)info.BaseAddress + info.RegionSize);
                if (next <= address)
                {
                    break; // end of the user address space (wrap-around)
                }

                address = next;
                if (regions >= MaxRegions || Stopwatch.GetElapsedTime(started) > budget)
                {
                    partial = true;
                    break;
                }
            }

            return new RawProcessScan(false, partial, [.. libraries], [.. files]);
        }
        finally
        {
            CloseHandle(process);
        }
    }
}

/// <summary>Maps NT device paths (\Device\HarddiskVolume3\...) to drive-letter paths (C:\...).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class DosDevices
{
    private readonly List<(string Device, string Drive)> _map;

    private DosDevices(List<(string, string)> map) => _map = map;

    public DateTimeOffset LoadedAt { get; } = DateTimeOffset.UtcNow;

    public static DosDevices Load()
    {
        var map = new List<(string, string)>();
        var buffer = stackalloc char[1024];
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = $"{letter}:";
            var length = QueryDosDevice(drive, buffer, 1024);
            if (length == 0)
            {
                continue;
            }

            var target = new string(buffer, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(target))
            {
                map.Add((target, drive));
            }
        }

        // Longest device first, so \Device\HarddiskVolume10 never matches \Device\HarddiskVolume1.
        map.Sort((x, y) => y.Item1.Length.CompareTo(x.Item1.Length));
        return new DosDevices(map);
    }

    public string? ToDosPath(string devicePath)
    {
        foreach (var (device, drive) in _map)
        {
            if (devicePath.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return drive + devicePath[device.Length..];
            }
        }

        const string Mup = @"\Device\Mup\";
        return devicePath.StartsWith(Mup, StringComparison.OrdinalIgnoreCase) ? @"\\" + devicePath[Mup.Length..] : null;
    }
}
