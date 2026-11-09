using System.Runtime.InteropServices;

namespace LocalAiPerf.Telemetry.Nvml;

/// <summary>nvmlReturn_t values from nvml.h (subset used here).</summary>
internal enum NvmlReturn
{
    Success = 0,
    Uninitialized = 1,
    InvalidArgument = 2,
    NotSupported = 3,
    NoPermission = 4,
    AlreadyInitialized = 5,
    NotFound = 6,
    InsufficientSize = 7,
    InsufficientPower = 8,
    DriverNotLoaded = 9,
    Timeout = 10,
    LibraryNotFound = 12,
    FunctionNotFound = 13,
    GpuIsLost = 15,
    ResetRequired = 16,
    OperatingSystem = 17,
    LibRmVersionMismatch = 18,
    NoData = 21,
    Unknown = 999,
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlUtilization
{
    public uint Gpu;
    public uint Memory;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlMemory
{
    public ulong Total;
    public ulong Free;
    public ulong Used;
}

/// <summary>
/// Thin binding to NVIDIA's NVML library, which ships with the NVIDIA display driver (nvml.dll on Windows,
/// libnvidia-ml.so.1 on Linux). Only read functions are bound; nothing here changes GPU settings.
/// The library is loaded from the system directory only, never from the application folder.
/// </summary>
internal sealed unsafe class NvmlApi : IDisposable
{
    public const int TemperatureGpu = 0;
    public const int ClockGraphics = 0;
    public const int ClockMemory = 2;
    public const int PcieTxBytes = 0;
    public const int PcieRxBytes = 1;

    private IntPtr _library;

    private NvmlApi(IntPtr library, string loadedFrom)
    {
        _library = library;
        LoadedFrom = loadedFrom;
        Init = (delegate* unmanaged<int>)Required("nvmlInit_v2", "nvmlInit");
        Shutdown = (delegate* unmanaged<int>)Required("nvmlShutdown");
        DeviceGetCount = (delegate* unmanaged<uint*, int>)Required("nvmlDeviceGetCount_v2", "nvmlDeviceGetCount");
        DeviceGetHandleByIndex = (delegate* unmanaged<uint, IntPtr*, int>)Required("nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex");
        ErrorString = (delegate* unmanaged<int, IntPtr>)Optional("nvmlErrorString");
        SystemGetDriverVersion = (delegate* unmanaged<byte*, uint, int>)Optional("nvmlSystemGetDriverVersion");
        SystemGetNvmlVersion = (delegate* unmanaged<byte*, uint, int>)Optional("nvmlSystemGetNVMLVersion");
        DeviceGetName = (delegate* unmanaged<IntPtr, byte*, uint, int>)Optional("nvmlDeviceGetName");
        DeviceGetUtilizationRates = (delegate* unmanaged<IntPtr, NvmlUtilization*, int>)Optional("nvmlDeviceGetUtilizationRates");
        DeviceGetTemperature = (delegate* unmanaged<IntPtr, int, uint*, int>)Optional("nvmlDeviceGetTemperature");
        DeviceGetPowerUsage = (delegate* unmanaged<IntPtr, uint*, int>)Optional("nvmlDeviceGetPowerUsage");
        DeviceGetEnforcedPowerLimit = (delegate* unmanaged<IntPtr, uint*, int>)Optional("nvmlDeviceGetEnforcedPowerLimit");
        DeviceGetMemoryInfo = (delegate* unmanaged<IntPtr, NvmlMemory*, int>)Optional("nvmlDeviceGetMemoryInfo");
        DeviceGetClockInfo = (delegate* unmanaged<IntPtr, int, uint*, int>)Optional("nvmlDeviceGetClockInfo");
        DeviceGetFanSpeed = (delegate* unmanaged<IntPtr, uint*, int>)Optional("nvmlDeviceGetFanSpeed");
        DeviceGetPcieThroughput = (delegate* unmanaged<IntPtr, int, uint*, int>)Optional("nvmlDeviceGetPcieThroughput");
        DeviceGetTotalEnergyConsumption = (delegate* unmanaged<IntPtr, ulong*, int>)Optional("nvmlDeviceGetTotalEnergyConsumption");
        DeviceGetClocksEventReasons = (delegate* unmanaged<IntPtr, ulong*, int>)Optional(
            "nvmlDeviceGetCurrentClocksEventReasons", "nvmlDeviceGetCurrentClocksThrottleReasons");
    }

    public string LoadedFrom { get; }

    public delegate* unmanaged<int> Init { get; }
    public delegate* unmanaged<int> Shutdown { get; }
    public delegate* unmanaged<uint*, int> DeviceGetCount { get; }
    public delegate* unmanaged<uint, IntPtr*, int> DeviceGetHandleByIndex { get; }
    public delegate* unmanaged<int, IntPtr> ErrorString { get; }
    public delegate* unmanaged<byte*, uint, int> SystemGetDriverVersion { get; }
    public delegate* unmanaged<byte*, uint, int> SystemGetNvmlVersion { get; }
    public delegate* unmanaged<IntPtr, byte*, uint, int> DeviceGetName { get; }
    public delegate* unmanaged<IntPtr, NvmlUtilization*, int> DeviceGetUtilizationRates { get; }
    public delegate* unmanaged<IntPtr, int, uint*, int> DeviceGetTemperature { get; }
    public delegate* unmanaged<IntPtr, uint*, int> DeviceGetPowerUsage { get; }
    public delegate* unmanaged<IntPtr, uint*, int> DeviceGetEnforcedPowerLimit { get; }
    public delegate* unmanaged<IntPtr, NvmlMemory*, int> DeviceGetMemoryInfo { get; }
    public delegate* unmanaged<IntPtr, int, uint*, int> DeviceGetClockInfo { get; }
    public delegate* unmanaged<IntPtr, uint*, int> DeviceGetFanSpeed { get; }
    public delegate* unmanaged<IntPtr, int, uint*, int> DeviceGetPcieThroughput { get; }
    public delegate* unmanaged<IntPtr, ulong*, int> DeviceGetTotalEnergyConsumption { get; }
    public delegate* unmanaged<IntPtr, ulong*, int> DeviceGetClocksEventReasons { get; }

    /// <summary>Loads NVML from trusted system locations. Returns null with a reason when it is not present.</summary>
    public static NvmlApi? TryLoad(out string reason)
    {
        foreach (var candidate in Candidates())
        {
            IntPtr handle;
            bool loaded = candidate.SearchPath is DllImportSearchPath searchPath
                ? NativeLibrary.TryLoad(candidate.Name, typeof(NvmlApi).Assembly, searchPath, out handle)
                : NativeLibrary.TryLoad(candidate.Name, out handle);
            if (!loaded)
            {
                continue;
            }

            try
            {
                reason = "";
                return new NvmlApi(handle, candidate.Name);
            }
            catch (EntryPointNotFoundException ex)
            {
                NativeLibrary.Free(handle);
                reason = $"NVML 라이브러리의 필수 함수가 없습니다: {ex.Message}";
                return null;
            }
        }

        reason = OperatingSystem.IsWindows()
            ? "nvml.dll을 찾을 수 없습니다 (NVIDIA 드라이버가 설치되어 있지 않거나 NVIDIA GPU가 없습니다)."
            : "libnvidia-ml.so.1을 찾을 수 없습니다 (NVIDIA 드라이버 없음).";
        return null;
    }

    public string Describe(int code)
    {
        if (ErrorString != null)
        {
            var text = Marshal.PtrToStringAnsi(ErrorString(code));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return $"{text} ({code})";
            }
        }

        return $"NVML 오류 {code}";
    }

    public void Dispose()
    {
        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }
    }

    private IntPtr Required(params string[] names)
    {
        foreach (var name in names)
        {
            if (NativeLibrary.TryGetExport(_library, name, out var address))
            {
                return address;
            }
        }

        throw new EntryPointNotFoundException(string.Join(" / ", names));
    }

    private IntPtr Optional(params string[] names)
    {
        foreach (var name in names)
        {
            if (NativeLibrary.TryGetExport(_library, name, out var address))
            {
                return address;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<(string Name, DllImportSearchPath? SearchPath)> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            // nvml.dll is installed into System32 by the driver. Search System32 only (no DLL planting from the app folder).
            yield return ("nvml.dll", DllImportSearchPath.System32);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
            {
                yield return (Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll"), null);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return ("libnvidia-ml.so.1", DllImportSearchPath.SafeDirectories);
        }
    }
}
