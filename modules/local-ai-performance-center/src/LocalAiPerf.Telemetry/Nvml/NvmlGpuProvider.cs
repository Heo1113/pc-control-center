using System.Text;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Metrics;

namespace LocalAiPerf.Telemetry.Nvml;

/// <summary>
/// NVIDIA GPU sensors through NVML. Runs as a normal user (no administrator rights, no extra driver).
/// A metric NVML does not support on a card is reported as NotSupported and shown as N/A.
/// </summary>
public sealed unsafe class NvmlGpuProvider : IGpuSensorProvider
{
    private const string Source = "NVML";
    private static readonly TimeSpan ReinitInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private NvmlApi? _api;
    private bool _initialized;
    private IntPtr[] _handles = [];
    private string?[] _names = [];
    private DateTimeOffset _lastInitAttempt;
    private List<SensorCapability> _capabilities = [];
    private List<GpuInfo> _devices = [];

    public NvmlGpuProvider(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        TryInitialize();
    }

    public string Name => "NVIDIA NVML";
    public bool IsAvailable => _initialized && _handles.Length > 0;
    public string? UnavailableReason { get; private set; }
    public string? DriverVersion { get; private set; }
    public string? LibraryVersion { get; private set; }
    public IReadOnlyList<GpuInfo> Devices => _devices;
    public IReadOnlyList<SensorCapability> Capabilities => _capabilities;

    public IReadOnlyList<GpuSample> ReadGpus(ICollection<SensorIssue> issues)
    {
        if (!_initialized)
        {
            if (_time.GetUtcNow() - _lastInitAttempt >= ReinitInterval && _api is null)
            {
                TryInitialize();
            }

            if (!_initialized)
            {
                return [];
            }
        }

        var samples = new List<GpuSample>(_handles.Length);
        var lost = false;
        for (var i = 0; i < _handles.Length; i++)
        {
            var sample = ReadDevice(i, _handles[i], issues, ref lost);
            samples.Add(sample);
        }

        if (lost)
        {
            issues.Add(new SensorIssue(Source, "gpu", SensorStatus.Error, "GPU 연결이 끊겼습니다 (NVML GPU_IS_LOST). 다시 초기화합니다."));
            Reset();
        }

        return samples;
    }

    public void Dispose() => Reset();

    private GpuSample ReadDevice(int index, IntPtr handle, ICollection<SensorIssue> issues, ref bool lost)
    {
        var api = _api!;
        double? util = null, memUtil = null, temp = null, power = null, powerLimit = null, gfx = null, memClock = null, fan = null, tx = null, rx = null;
        long? used = null, total = null, energy = null;
        ulong? reasons = null;

        if (api.DeviceGetUtilizationRates != null)
        {
            NvmlUtilization u;
            if (Check(api.DeviceGetUtilizationRates(handle, &u), MetricKeys.GpuUtil, index, issues, ref lost))
            {
                util = u.Gpu;
                memUtil = u.Memory;
            }
        }

        if (api.DeviceGetTemperature != null)
        {
            uint value;
            if (Check(api.DeviceGetTemperature(handle, NvmlApi.TemperatureGpu, &value), MetricKeys.GpuTemp, index, issues, ref lost))
            {
                temp = value;
            }
        }

        if (api.DeviceGetPowerUsage != null)
        {
            uint mw;
            if (Check(api.DeviceGetPowerUsage(handle, &mw), MetricKeys.GpuPower, index, issues, ref lost))
            {
                power = mw / 1000.0;
            }
        }

        if (api.DeviceGetEnforcedPowerLimit != null)
        {
            uint mw;
            if (Check(api.DeviceGetEnforcedPowerLimit(handle, &mw), MetricKeys.GpuPowerLimit, index, issues, ref lost))
            {
                powerLimit = mw / 1000.0;
            }
        }

        if (api.DeviceGetMemoryInfo != null)
        {
            NvmlMemory memory;
            if (Check(api.DeviceGetMemoryInfo(handle, &memory), MetricKeys.GpuVramUsedGiB, index, issues, ref lost))
            {
                used = (long)Math.Min(memory.Used, long.MaxValue);
                total = (long)Math.Min(memory.Total, long.MaxValue);
            }
        }

        if (api.DeviceGetClockInfo != null)
        {
            uint mhz;
            if (Check(api.DeviceGetClockInfo(handle, NvmlApi.ClockGraphics, &mhz), MetricKeys.GpuClockGraphics, index, issues, ref lost))
            {
                gfx = mhz;
            }

            if (Check(api.DeviceGetClockInfo(handle, NvmlApi.ClockMemory, &mhz), MetricKeys.GpuClockMemory, index, issues, ref lost))
            {
                memClock = mhz;
            }
        }

        if (api.DeviceGetFanSpeed != null)
        {
            uint pct;
            if (Check(api.DeviceGetFanSpeed(handle, &pct), MetricKeys.GpuFan, index, issues, ref lost))
            {
                fan = pct;
            }
        }

        if (api.DeviceGetPcieThroughput != null)
        {
            uint kbps;
            if (Check(api.DeviceGetPcieThroughput(handle, NvmlApi.PcieTxBytes, &kbps), MetricKeys.GpuPcieTx, index, issues, ref lost))
            {
                tx = kbps;
            }

            if (Check(api.DeviceGetPcieThroughput(handle, NvmlApi.PcieRxBytes, &kbps), MetricKeys.GpuPcieRx, index, issues, ref lost))
            {
                rx = kbps;
            }
        }

        if (api.DeviceGetTotalEnergyConsumption != null)
        {
            ulong mj;
            if (Check(api.DeviceGetTotalEnergyConsumption(handle, &mj), "gpu.energy", index, issues, ref lost))
            {
                energy = (long)Math.Min(mj, long.MaxValue);
            }
        }

        if (api.DeviceGetClocksEventReasons != null)
        {
            ulong mask;
            if (Check(api.DeviceGetClocksEventReasons(handle, &mask), "gpu.throttle", index, issues, ref lost))
            {
                reasons = mask;
            }
        }

        return new GpuSample
        {
            Index = index,
            Name = _names[index],
            UtilizationPercent = util,
            MemoryControllerUtilizationPercent = memUtil,
            TemperatureC = temp,
            PowerW = power,
            PowerLimitW = powerLimit,
            MemoryUsedBytes = used,
            MemoryTotalBytes = total,
            GraphicsClockMhz = gfx,
            MemoryClockMhz = memClock,
            FanSpeedPercent = fan,
            PcieTxKBps = tx,
            PcieRxKBps = rx,
            TotalEnergyMilliJoules = energy,
            ThrottleReasons = reasons,
        };
    }

    private bool Check(int code, string metric, int index, ICollection<SensorIssue> issues, ref bool lost)
    {
        var result = (NvmlReturn)code;
        if (result == NvmlReturn.Success)
        {
            return true;
        }

        if (result is NvmlReturn.NotSupported or NvmlReturn.NoData)
        {
            return false;
        }

        if (result == NvmlReturn.GpuIsLost)
        {
            lost = true;
        }

        issues.Add(new SensorIssue(Source, $"{metric}[{index}]",
            result == NvmlReturn.NoPermission ? SensorStatus.RequiresElevation : SensorStatus.Error,
            _api?.Describe(code) ?? $"NVML 오류 {code}"));
        return false;
    }

    private void TryInitialize()
    {
        _lastInitAttempt = _time.GetUtcNow();
        var loadReason = "";
        if (_api is null)
        {
            _api = NvmlApi.TryLoad(out loadReason);
        }

        if (_api is null)
        {
            UnavailableReason = string.IsNullOrEmpty(loadReason) ? "NVML을 불러올 수 없습니다." : loadReason;
            _capabilities = BuildCapabilities(probeHandle: IntPtr.Zero, SensorStatus.Unavailable, UnavailableReason);
            return;
        }

        var init = (NvmlReturn)_api.Init();
        if (init != NvmlReturn.Success && init != NvmlReturn.AlreadyInitialized)
        {
            UnavailableReason = $"NVML 초기화 실패: {_api.Describe((int)init)}";
            _capabilities = BuildCapabilities(IntPtr.Zero, SensorStatus.Unavailable, UnavailableReason);
            _api.Dispose();
            _api = null;
            return;
        }

        uint count;
        if ((NvmlReturn)_api.DeviceGetCount(&count) != NvmlReturn.Success)
        {
            count = 0;
        }

        var handles = new List<IntPtr>();
        var names = new List<string?>();
        for (uint i = 0; i < Math.Min(count, 16u); i++)
        {
            IntPtr handle;
            if ((NvmlReturn)_api.DeviceGetHandleByIndex(i, &handle) == NvmlReturn.Success)
            {
                handles.Add(handle);
                names.Add(ReadString(_api.DeviceGetName, handle, 96));
            }
        }

        _handles = [.. handles];
        _names = [.. names];
        DriverVersion = ReadSystemString(_api.SystemGetDriverVersion, 80);
        LibraryVersion = ReadSystemString(_api.SystemGetNvmlVersion, 80);
        _initialized = true;
        UnavailableReason = _handles.Length == 0 ? "NVIDIA GPU가 감지되지 않았습니다." : null;
        _capabilities = _handles.Length == 0
            ? BuildCapabilities(IntPtr.Zero, SensorStatus.Unavailable, UnavailableReason)
            : BuildCapabilities(_handles[0], SensorStatus.Ok, null);

        var devices = new List<GpuInfo>();
        var throwaway = new List<SensorIssue>();
        var lostDuringProbe = false;
        for (var i = 0; i < _handles.Length; i++)
        {
            var sample = ReadDevice(i, _handles[i], throwaway, ref lostDuringProbe);
            devices.Add(new GpuInfo(i, _names[i], sample.MemoryTotalBytes, sample.PowerLimitW));
        }

        _devices = devices;
    }

    private void Reset()
    {
        if (_api is not null && _initialized)
        {
            _api.Shutdown();
        }

        _initialized = false;
        _handles = [];
        _names = [];
        // The library is released too, so that a later re-initialisation starts from a clean state.
        _api?.Dispose();
        _api = null;
    }

    private List<SensorCapability> BuildCapabilities(IntPtr probeHandle, SensorStatus fallback, string? detail)
    {
        var rows = new List<SensorCapability>();
        var api = _api;

        void Add(string key, string name, bool exported, Func<IntPtr, int> probe)
        {
            SensorStatus status;
            string? note;
            if (api is null || probeHandle == IntPtr.Zero)
            {
                status = fallback;
                note = detail;
            }
            else if (!exported)
            {
                status = SensorStatus.NotSupported;
                note = "이 드라이버의 NVML에 해당 함수가 없습니다.";
            }
            else
            {
                var code = (NvmlReturn)probe(probeHandle);
                status = code switch
                {
                    NvmlReturn.Success => SensorStatus.Ok,
                    NvmlReturn.NotSupported => SensorStatus.NotSupported,
                    NvmlReturn.NoPermission => SensorStatus.RequiresElevation,
                    _ => SensorStatus.Error,
                };
                note = status == SensorStatus.Ok ? null : api.Describe((int)code);
            }

            rows.Add(new SensorCapability(key, name, Source, status, RequiresElevation: false, note));
        }

        if (api is null)
        {
            foreach (var (key, name) in CapabilityNames)
            {
                rows.Add(new SensorCapability(key, name, Source, fallback, RequiresElevation: false, detail));
            }

            return rows;
        }

        Add(MetricKeys.GpuUtil, "GPU 사용률", api.DeviceGetUtilizationRates != null, h => { NvmlUtilization u; return api.DeviceGetUtilizationRates(h, &u); });
        Add(MetricKeys.GpuMemUtil, "GPU 메모리 컨트롤러 사용률", api.DeviceGetUtilizationRates != null, h => { NvmlUtilization u; return api.DeviceGetUtilizationRates(h, &u); });
        Add(MetricKeys.GpuTemp, "GPU 온도", api.DeviceGetTemperature != null, h => { uint v; return api.DeviceGetTemperature(h, NvmlApi.TemperatureGpu, &v); });
        Add(MetricKeys.GpuPower, "GPU 전력", api.DeviceGetPowerUsage != null, h => { uint v; return api.DeviceGetPowerUsage(h, &v); });
        Add(MetricKeys.GpuPowerLimit, "GPU 전력 제한", api.DeviceGetEnforcedPowerLimit != null, h => { uint v; return api.DeviceGetEnforcedPowerLimit(h, &v); });
        Add(MetricKeys.GpuVramUsedGiB, "VRAM 사용량/전체", api.DeviceGetMemoryInfo != null, h => { NvmlMemory m; return api.DeviceGetMemoryInfo(h, &m); });
        Add(MetricKeys.GpuClockGraphics, "GPU 그래픽 클럭", api.DeviceGetClockInfo != null, h => { uint v; return api.DeviceGetClockInfo(h, NvmlApi.ClockGraphics, &v); });
        Add(MetricKeys.GpuClockMemory, "GPU 메모리 클럭", api.DeviceGetClockInfo != null, h => { uint v; return api.DeviceGetClockInfo(h, NvmlApi.ClockMemory, &v); });
        Add(MetricKeys.GpuFan, "GPU 팬 속도", api.DeviceGetFanSpeed != null, h => { uint v; return api.DeviceGetFanSpeed(h, &v); });
        Add(MetricKeys.GpuPcieTx, "PCIe 송신", api.DeviceGetPcieThroughput != null, h => { uint v; return api.DeviceGetPcieThroughput(h, NvmlApi.PcieTxBytes, &v); });
        Add(MetricKeys.GpuPcieRx, "PCIe 수신", api.DeviceGetPcieThroughput != null, h => { uint v; return api.DeviceGetPcieThroughput(h, NvmlApi.PcieRxBytes, &v); });
        Add("gpu.energy", "GPU 누적 에너지 카운터", api.DeviceGetTotalEnergyConsumption != null, h => { ulong v; return api.DeviceGetTotalEnergyConsumption(h, &v); });
        Add("gpu.throttle", "클럭 제한(스로틀) 사유", api.DeviceGetClocksEventReasons != null, h => { ulong v; return api.DeviceGetClocksEventReasons(h, &v); });
        return rows;
    }

    private static readonly (string Key, string Name)[] CapabilityNames =
    [
        (MetricKeys.GpuUtil, "GPU 사용률"),
        (MetricKeys.GpuMemUtil, "GPU 메모리 컨트롤러 사용률"),
        (MetricKeys.GpuTemp, "GPU 온도"),
        (MetricKeys.GpuPower, "GPU 전력"),
        (MetricKeys.GpuPowerLimit, "GPU 전력 제한"),
        (MetricKeys.GpuVramUsedGiB, "VRAM 사용량/전체"),
        (MetricKeys.GpuClockGraphics, "GPU 그래픽 클럭"),
        (MetricKeys.GpuClockMemory, "GPU 메모리 클럭"),
        (MetricKeys.GpuFan, "GPU 팬 속도"),
        (MetricKeys.GpuPcieTx, "PCIe 송신"),
        (MetricKeys.GpuPcieRx, "PCIe 수신"),
        ("gpu.energy", "GPU 누적 에너지 카운터"),
        ("gpu.throttle", "클럭 제한(스로틀) 사유"),
    ];

    private static string? ReadString(delegate* unmanaged<IntPtr, byte*, uint, int> fn, IntPtr handle, int size)
    {
        if (fn == null)
        {
            return null;
        }

        var buffer = stackalloc byte[size];
        return (NvmlReturn)fn(handle, buffer, (uint)size) == NvmlReturn.Success ? Decode(buffer, size) : null;
    }

    private static string? ReadSystemString(delegate* unmanaged<byte*, uint, int> fn, int size)
    {
        if (fn == null)
        {
            return null;
        }

        var buffer = stackalloc byte[size];
        return (NvmlReturn)fn(buffer, (uint)size) == NvmlReturn.Success ? Decode(buffer, size) : null;
    }

    private static string Decode(byte* buffer, int size)
    {
        var span = new ReadOnlySpan<byte>(buffer, size);
        var end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? span[..end] : span).Trim();
    }
}
