using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Telemetry.Mock;
using LocalAiPerf.Telemetry.Nvml;
using LocalAiPerf.Telemetry.Platform;

namespace LocalAiPerf.Telemetry;

/// <summary>
/// Combines a GPU provider and a system provider into hardware snapshots. Never throws while sampling.
/// Reusable outside this app (for example by a GPU profitability or Windows error analysis tool).
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    /// <summary>Sensors this version deliberately does not read, listed so the UI can explain the N/A.</summary>
    public static IReadOnlyList<SensorCapability> NotCollected { get; } =
    [
        new SensorCapability("cpu.temp", "CPU 온도", "-", SensorStatus.RequiresElevation, true,
            "AMD Ryzen 온도는 커널 드라이버(예: PawnIO)와 관리자 권한이 필요합니다. 이 버전은 일반 사용자 권한만 사용하므로 수집하지 않습니다."),
        new SensorCapability("cpu.power", "CPU 패키지 전력", "-", SensorStatus.RequiresElevation, true,
            "CPU 전력 레지스터 접근에 커널 드라이버와 관리자 권한이 필요해 수집하지 않습니다."),
        new SensorCapability("cpu.clock", "CPU 실제 클럭", "-", SensorStatus.NotImplemented, false,
            "이 버전에서는 수집하지 않습니다."),
    ];

    private readonly IGpuSensorProvider _gpu;
    private readonly ISystemSensorProvider _system;
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;

    public HardwareMonitor(IGpuSensorProvider gpu, ISystemSensorProvider system, TimeProvider? time = null, IAppLogger? log = null, bool isMock = false)
    {
        _gpu = gpu;
        _system = system;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        IsMock = isMock;
        Info = BuildInfo();
    }

    public bool IsMock { get; }
    public HardwareInfo Info { get; private set; }
    public IGpuSensorProvider Gpu => _gpu;
    public ISystemSensorProvider System => _system;

    public IReadOnlyList<SensorCapability> Capabilities => [.. _gpu.Capabilities, .. _system.Capabilities, .. NotCollected];

    public static HardwareMonitor Create(SamplingSettings settings, bool useMock, TimeProvider? time = null, IAppLogger? log = null)
    {
        time ??= TimeProvider.System;
        log ??= NullLogger.Instance;
        if (useMock)
        {
            return new HardwareMonitor(new MockGpuProvider(time), new MockSystemProvider(time), time, log, isMock: true);
        }

        IGpuSensorProvider gpu = settings.GpuEnabled ? new NvmlGpuProvider(time) : new DisabledGpuProvider();
        ISystemSensorProvider system;
        if (!settings.SystemEnabled)
        {
            system = new UnsupportedSystemProvider(SensorStatus.Disabled, "설정에서 꺼져 있습니다.");
        }
        else if (OperatingSystem.IsWindows())
        {
            system = new WindowsSystemProvider();
        }
        else if (OperatingSystem.IsLinux())
        {
            system = new LinuxProcSystemProvider();
        }
        else
        {
            system = new UnsupportedSystemProvider(SensorStatus.NotSupported, "이 운영체제는 지원하지 않습니다.");
        }

        if (!gpu.IsAvailable)
        {
            log.Warn("hardware", $"GPU 센서 사용 불가: {gpu.UnavailableReason}");
        }

        return new HardwareMonitor(gpu, system, time, log);
    }

    public HardwareSnapshot Sample()
    {
        var issues = new List<SensorIssue>();
        var timestamp = _time.GetUtcNow();
        SystemSample system;
        IReadOnlyList<GpuSample> gpus;
        try
        {
            system = _system.ReadSystem(issues);
        }
        catch (Exception ex)
        {
            _log.Error("hardware", "시스템 센서 읽기 실패", ex);
            issues.Add(new SensorIssue(_system.Name, "system", SensorStatus.Error, ex.Message));
            system = SystemSample.Empty;
        }

        try
        {
            gpus = _gpu.ReadGpus(issues);
        }
        catch (Exception ex)
        {
            _log.Error("hardware", "GPU 센서 읽기 실패", ex);
            issues.Add(new SensorIssue(_gpu.Name, "gpu", SensorStatus.Error, ex.Message));
            gpus = [];
        }

        if (gpus.Count > 0 && Info.Gpus.Count == 0)
        {
            Info = BuildInfo();
        }

        return new HardwareSnapshot { Timestamp = timestamp, System = system, Gpus = gpus, Issues = issues, IsMock = IsMock };
    }

    public void Dispose()
    {
        _gpu.Dispose();
        _system.Dispose();
    }

    private HardwareInfo BuildInfo()
    {
        long? ramTotal = null;
        try
        {
            ramTotal = _system.ReadSystem(new List<SensorIssue>()).MemoryTotalBytes;
        }
        catch (Exception ex)
        {
            _log.Warn("hardware", "RAM 용량 확인 실패", ex);
        }

        return new HardwareInfo
        {
            CpuName = _system.CpuName,
            LogicalProcessors = _system.LogicalProcessors,
            RamTotalBytes = ramTotal,
            OsDescription = global::System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            IsElevated = Environment.IsPrivilegedProcess,
            GpuDriverVersion = _gpu.DriverVersion,
            NvmlVersion = _gpu.LibraryVersion,
            Gpus = _gpu.Devices,
            IsMock = IsMock,
        };
    }
}
