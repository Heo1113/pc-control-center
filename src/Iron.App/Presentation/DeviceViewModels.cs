using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Iron.App.Presentation;

/// <summary>Who holds the WireView COM port (Stage 1: simulated; no port is opened).</summary>
public enum PortState
{
    /// <summary>IRON reads the sensors (read-only commands only).</summary>
    Active,

    /// <summary>RELEASE USB pressed: the reader is being stopped and the port closed (asynchronous).</summary>
    Releasing,

    /// <summary>The port is free for the official WireView app; no warning color is left on screen.</summary>
    Released,

    /// <summary>RESUME pressed: reopening the port.</summary>
    Resuming,

    /// <summary>Another program holds the port; IRON retries every 3 s without crashing.</summary>
    Busy,
}

/// <summary>
/// WireView Pro II as IRON shows it: connector power, voltage, temperatures, the six pin currents and the RELEASE USB /
/// RESUME handover. The pin warning lives here and is cleared the moment the port is released; the GPU warning is not.
/// </summary>
public sealed partial class WireViewViewModel : ObservableObject
{
    public static readonly TimeSpan ReleaseDelay = TimeSpan.FromMilliseconds(450);
    public static readonly TimeSpan ResumeDelay = TimeSpan.FromMilliseconds(650);
    public static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(3);
    public const double PinScaleA = 9.5;

    private readonly IUiScheduler _scheduler;
    private readonly Func<bool> _portBusy;
    private readonly Action _changed;
    private IDisposable? _pending;

    public WireViewViewModel(IUiScheduler scheduler, Func<bool> portBusy, Action changed)
    {
        _scheduler = scheduler;
        _portBusy = portBusy;
        _changed = changed;
        for (var i = 0; i < 6; i++)
        {
            Pins.Add(new PinViewModel(i));
        }
    }

    public ObservableCollection<PinViewModel> Pins { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReleasedOrBusy), nameof(HandoverButtonText), nameof(IsHandoverBusy), nameof(PortOwnerText), nameof(QuickText))]
    public partial PortState Port { get; private set; } = PortState.Active;

    /// <summary>The device answers (not the port state: a released port is still a connected device).</summary>
    [ObservableProperty] public partial bool DeviceConnected { get; private set; }

    /// <summary>Values are being read (connected and the port is IRON's).</summary>
    [ObservableProperty] public partial bool IsReading { get; private set; }

    [ObservableProperty] public partial bool PinAlert { get; private set; }
    [ObservableProperty] public partial string PowerText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial string VoltageText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial string ConnectorText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial string FanText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial string DeltaText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial string SummaryText { get; private set; } = "미연결";
    [ObservableProperty] public partial string StateText { get; private set; } = "미연결";
    [ObservableProperty] public partial StatusKind StateKind { get; private set; } = StatusKind.Disconnected;
    [ObservableProperty] public partial string HandoverText { get; private set; } = "";
    [ObservableProperty] public partial string ConfirmText { get; private set; } = "";
    [ObservableProperty] public partial double? PowerW { get; private set; }
    [ObservableProperty] public partial string ThresholdText { get; private set; } = "";

    public bool IsReleasedOrBusy => Port is PortState.Released or PortState.Busy or PortState.Resuming;
    public bool IsHandoverBusy => Port is PortState.Releasing or PortState.Resuming;

    public string HandoverButtonText => Port switch
    {
        PortState.Active => "RELEASE USB",
        PortState.Releasing => "해제 중...",
        PortState.Resuming => "재연결 중...",
        _ => "RESUME TELEMETRY",
    };

    /// <summary>The overview's FLUX bar button.</summary>
    public string QuickText => Port switch
    {
        PortState.Active => "Release USB",
        PortState.Releasing => "해제 중...",
        PortState.Resuming => "재연결 중...",
        _ => "WireView 재개",
    };

    public string PortOwnerText => Port switch
    {
        PortState.Active => "IRON 조회 중 (읽기 전용)",
        PortState.Releasing => "해제 중...",
        PortState.Released => "해제됨 — 공식 앱 사용 가능",
        PortState.Resuming => "재연결 중...",
        _ => "다른 프로그램이 사용 중 — 3초마다 재시도",
    };

    [RelayCommand]
    public void ToggleHandover()
    {
        if (Port == PortState.Active)
        {
            Release();
        }
        else if (Port is PortState.Released or PortState.Busy)
        {
            Resume();
        }
    }

    /// <summary>Stops reading at once (warning colors cleared now), the port is closed asynchronously (simulated).</summary>
    public void Release()
    {
        if (Port != PortState.Active)
        {
            return;
        }

        Cancel();
        Port = PortState.Releasing;
        _changed();
        _pending = _scheduler.Schedule(ReleaseDelay, () =>
        {
            Port = PortState.Released;
            _changed();
        });
    }

    public void Resume()
    {
        if (Port is not (PortState.Released or PortState.Busy))
        {
            return;
        }

        Cancel();
        Port = PortState.Resuming;
        _changed();
        _pending = _scheduler.Schedule(ResumeDelay, TryOpen);
    }

    /// <summary>Puts the port straight into a state (command line, screenshots, tests).</summary>
    public void SetPortImmediately(PortState state)
    {
        Cancel();
        Port = state;
        _changed();
    }

    public void Apply(WireViewReading reading, SensorPhase phase, AlertMonitor alerts, double thresholdA)
    {
        DeviceConnected = reading.Connected;
        IsReading = reading.Connected && Port == PortState.Active;
        PinAlert = IsReading && alerts.PinAlert;
        ThresholdText = $"주의 기준 {thresholdA.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} A (설정) · 최대 {PinScaleA:0.0} A/핀";
        for (var i = 0; i < Pins.Count; i++)
        {
            var current = i < reading.PinCurrentsA.Count ? reading.PinCurrentsA[i] : (double?)null;
            Pins[i].Set(current, current is double a ? AlertMonitor.LevelOf(a, thresholdA, IsReading) : PinLevel.Inactive);
        }

        var pins = IsReading ? reading.PinCurrentsA : [];
        PowerW = IsReading ? reading.PowerW : null;
        PowerText = IsReading ? Fmt.One(reading.PowerW) : Fmt.Missing;
        VoltageText = IsReading && reading.VoltageV is double v ? v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " V" : Fmt.Missing;
        ConnectorText = IsReading ? $"{Fmt.Int(reading.ConnectorInC)}/{Fmt.Int(reading.ConnectorOutC)}°C" : Fmt.Missing;
        FanText = IsReading ? $"{Fmt.Int(reading.FanDutyPercent)}% · {reading.CableRatingW?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Fmt.Missing}W" : Fmt.Missing;
        DeltaText = pins.Count > 0 ? (pins.Max() - pins.Min()).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " A" : Fmt.Missing;
        var pending = IsReading && !alerts.PinAlert && alerts.PinOverReadings > 0;
        ConfirmText = pending ? $"주의 기준 초과 확인 중 {alerts.PinOverReadings}/{AlertMonitor.PinLatchReadings}" : "";

        (StateKind, StateText) = (phase, Port, reading.Connected) switch
        {
            (SensorPhase.Initializing, _, _) => (StatusKind.Loading, "확인 중..."),
            (_, PortState.Releasing, _) => (StatusKind.Loading, "USB 해제 중..."),
            (_, PortState.Released, _) => (StatusKind.Disconnected, "USB 해제됨 (공식 앱 대기)"),
            (_, PortState.Resuming, _) => (StatusKind.Loading, "재연결 중..."),
            (_, PortState.Busy, _) => (StatusKind.Disconnected, "포트 사용 중 — 3초마다 재시도"),
            (_, _, false) => (StatusKind.Disconnected, "미연결"),
            _ when PinAlert => (StatusKind.Alert, "핀 불균형 주의 (3회 연속 확인)"),
            _ => (StatusKind.Online, "연결됨 · 읽기 전용"),
        };

        SummaryText = IsReading
            ? $"{Fmt.One(reading.PowerW)}W · {Fmt.Int(reading.ConnectorInC)}°C · Δ {DeltaText}"
            : StateText;
        HandoverText = Port switch
        {
            PortState.Active => "읽기 전용 조회 · 장치 자체 하드웨어 보호 기능은 IRON과 독립 동작",
            PortState.Releasing => "진행 중인 읽기를 멈추고 COM 포트를 닫는 중 (모의)",
            PortState.Released => "COM 포트 해제됨 — 공식 WireView 앱을 실행할 수 있습니다 (모의)",
            PortState.Resuming => "COM 포트를 다시 여는 중 (모의)",
            _ => "다른 프로그램이 COM 포트를 쓰고 있습니다 — 3초마다 다시 시도 (모의)",
        };
    }

    private void TryOpen()
    {
        if (_portBusy())
        {
            Port = PortState.Busy;
            _changed();
            _pending = _scheduler.Schedule(BusyRetry, TryOpen);
            return;
        }

        Port = PortState.Active;
        _pending = null;
        _changed();
    }

    private void Cancel()
    {
        _pending?.Dispose();
        _pending = null;
    }
}

public enum FluxState
{
    Sending,
    Conflict,
    Disconnected,
    Checking,
}

/// <summary>
/// Antec FLUX PRO (Vortex View side-panel LCD): what IRON would show on the case display, the CPU sensor and GPU
/// target, and the iUnity conflict hold. Stage 1 never opens the HID device; it only shows the report it would send.
/// </summary>
public sealed partial class FluxViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> CpuSensors = ["Ryzen Tctl/Tdie", "CPU Package", "Core Max"];
    public static readonly IReadOnlyList<string> GpuTargets = ["RTX 5080", "RTX 5060 Ti", "자동 (더 뜨거운 GPU)"];

    private TelemetryFrame? _frame;

    [ObservableProperty] public partial string CpuSensor { get; set; } = CpuSensors[0];
    [ObservableProperty] public partial string GpuTarget { get; set; } = GpuTargets[0];
    [ObservableProperty] public partial FluxState State { get; private set; } = FluxState.Checking;
    [ObservableProperty] public partial string StateText { get; private set; } = "";
    [ObservableProperty] public partial string BadgeText { get; private set; } = "";
    [ObservableProperty] public partial string CpuText { get; private set; } = "--.-°";
    [ObservableProperty] public partial string GpuText { get; private set; } = "--.-°";
    [ObservableProperty] public partial string CpuCaption { get; private set; } = "";
    [ObservableProperty] public partial string GpuCaption { get; private set; } = "";
    [ObservableProperty] public partial string BarText { get; private set; } = "";
    [ObservableProperty] public partial string PacketText { get; private set; } = "";

    /// <summary>The tray shows one FLUX PRO line only when it is disconnected or iUnity conflicts.</summary>
    [ObservableProperty] public partial string? TrayWarning { get; private set; }

    partial void OnCpuSensorChanged(string value) => Recompute();
    partial void OnGpuTargetChanged(string value) => Recompute();

    public void Apply(TelemetryFrame frame)
    {
        _frame = frame;
        Recompute();
    }

    /// <summary>The GPU shown on the display: the chosen one, or the hotter of the two (auto).</summary>
    public static (double? Celsius, string Label) TargetTemperature(IReadOnlyList<GpuReading> gpus, string target)
    {
        if (target == GpuTargets[1] && gpus.Count > 1)
        {
            return (gpus[1].TemperatureC, "5060 Ti");
        }

        if (target == GpuTargets[2])
        {
            var hottest = gpus.Where(g => g.TemperatureC is not null).OrderByDescending(g => g.TemperatureC).FirstOrDefault();
            return hottest is null ? (null, "자동") : (hottest.TemperatureC, "자동 " + ProcessRowViewModel.Short(hottest.Name));
        }

        return (gpus.Count > 0 ? gpus[0].TemperatureC : null, "5080");
    }

    private void Recompute()
    {
        if (_frame is not { } frame)
        {
            return;
        }

        var cpu = CpuSensor == CpuSensors[1] ? frame.Cpu.PackageC : CpuSensor == CpuSensors[2] ? frame.Cpu.CoreMaxC : frame.Cpu.TctlC;
        var (gpu, gpuLabel) = TargetTemperature(frame.Gpus, GpuTarget);
        State = frame.Phase == SensorPhase.Initializing ? FluxState.Checking
            : !frame.Flux.Connected ? FluxState.Disconnected
            : frame.Flux.IUnityRunning ? FluxState.Conflict
            : FluxState.Sending;
        var sending = State == FluxState.Sending;
        CpuCaption = $"CPU ({CpuSensor})";
        GpuCaption = GpuTarget == GpuTargets[2] ? $"GPU ({gpuLabel})" : $"GPU ({GpuTarget})";
        CpuText = sending && cpu is double c ? c.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "°" : "--.-°";
        GpuText = sending && gpu is double g ? g.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "°" : "--.-°";
        (BadgeText, StateText) = State switch
        {
            FluxState.Sending => ("송신 중 · 1Hz (모의)", $"표시 송신 중 (CPU {Deg(cpu)} / {gpuLabel} {Deg(gpu)})"),
            FluxState.Conflict => ("iUnity 충돌 — 송신 보류", "iUnity 충돌 감지 — 표시 송신 보류 (iUnity를 종료하지 않음)"),
            FluxState.Disconnected => ("미연결", "미연결 — 케이스 디스플레이 USB를 찾지 못함"),
            _ => ("확인 중", "장치 확인 중..."),
        };
        BarText = "Antec FLUX PRO · " + StateText;
        PacketText = FluxPacket.Hex(FluxPacket.Build(cpu, gpu));
        TrayWarning = State switch
        {
            FluxState.Conflict => "iUnity 충돌 — 송신 보류",
            FluxState.Disconnected => "미연결",
            _ => null,
        };
    }

    private static string Deg(double? c) => c is double v ? v.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "°" : Fmt.Missing;
}
