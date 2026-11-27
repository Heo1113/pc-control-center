using CommunityToolkit.Mvvm.ComponentModel;

namespace Iron.App.Presentation;

/// <summary>The four states of the design package (docs/04 §2.6).</summary>
public enum MockPreset
{
    Normal,
    Alert,
    Disconnected,
    Loading,
}

/// <summary>
/// What the simulated sensors report. Each condition is independent, so combinations can be tried (for example a hot
/// RTX 5080 while the WireView port is released). The presets only set these switches.
/// </summary>
public sealed partial class MockState : ObservableObject
{
    [ObservableProperty] public partial SensorPhase Phase { get; set; } = SensorPhase.Ready;

    /// <summary>RTX 5080 under heavy load at 76 °C (above the default 72 °C UI threshold).</summary>
    [ObservableProperty] public partial bool GpuOverheat { get; set; }

    /// <summary>Uneven 12 V pin currents: P3 7.94 A (above the default 7.0 A UI threshold).</summary>
    [ObservableProperty] public partial bool PinImbalance { get; set; }

    /// <summary>The RTX 5060 Ti reports no board power (the total must become "일부 미확인").</summary>
    [ObservableProperty] public partial bool Gpu1PowerMissing { get; set; }

    [ObservableProperty] public partial bool FluxDisconnected { get; set; }

    /// <summary>Antec iUnity is running (IRON would hold its display output, never end iUnity).</summary>
    [ObservableProperty] public partial bool IUnityRunning { get; set; }

    /// <summary>Another program (the official WireView app, HWiNFO) holds the COM port when IRON resumes.</summary>
    [ObservableProperty] public partial bool WireViewPortBusy { get; set; }

    public void Apply(MockPreset preset)
    {
        GpuOverheat = preset == MockPreset.Alert;
        PinImbalance = preset == MockPreset.Alert;
        Gpu1PowerMissing = preset == MockPreset.Alert;
        FluxDisconnected = false;
        IUnityRunning = false;
        Phase = preset switch
        {
            MockPreset.Disconnected => SensorPhase.NoDevice,
            MockPreset.Loading => SensorPhase.Initializing,
            _ => SensorPhase.Ready,
        };
    }

    public MockPreset? CurrentPreset => Phase switch
    {
        SensorPhase.NoDevice => MockPreset.Disconnected,
        SensorPhase.Initializing => MockPreset.Loading,
        _ when GpuOverheat && PinImbalance && Gpu1PowerMissing => MockPreset.Alert,
        _ when !GpuOverheat && !PinImbalance && !Gpu1PowerMissing => MockPreset.Normal,
        _ => null,
    };
}

/// <summary>
/// Simulated sensor readings for Stage 1 (the values of the design package's prototype, with a small random walk so the
/// screen is alive). Nothing here touches a real sensor or device.
/// </summary>
public sealed class MockTelemetryProvider(int seed = 20261124, bool jitter = true) : ITelemetrySource
{
    bool ITelemetrySource.IsMock => true;

    string ITelemetrySource.Describe() => "모의 데이터 (Stage 1) — 상태 전환: F5 정상 · F6 오류/불균형 · F7 미연결 · F8 로딩 · F9 USB 해제/재개 · Shift+F9 1.5초 플래시";

    public static readonly double[] NormalPins = [1.31, 1.34, 1.29, 1.35, 1.32, 1.20];
    public static readonly double[] ImbalancedPins = [1.82, 2.15, 7.94, 5.10, 3.85, 3.33];

    /// <summary>During a short spike (the "1.5s 불균형 플래시" test) P3 carries this current.</summary>
    public const double SpikePinA = 7.6;

    private readonly Random _random = new(seed);
    private DateTimeOffset? _spikeUntil;

    public MockState State { get; } = new();
    public bool Jitter { get; set; } = jitter;

    public void StartPinSpike(DateTimeOffset now, TimeSpan duration) => _spikeUntil = now + duration;

    public bool IsSpikeActive(DateTimeOffset now) => _spikeUntil is { } until && now < until;

    /// <summary>The simulated cards' VRAM (both 16 GB). Only the mock states a size; real data shows N/A when NVML does not.</summary>
    private const double MockVramGb = 16;

    public TelemetryFrame Next(DateTimeOffset now)
    {
        var s = State;
        if (s.Phase == SensorPhase.Initializing)
        {
            return new TelemetryFrame
            {
                Timestamp = now,
                Phase = SensorPhase.Initializing,
                Gpus = [new GpuReading { Index = 0, Name = "RTX 5080", VramTotalGb = MockVramGb }, new GpuReading { Index = 1, Name = "RTX 5060 Ti", VramTotalGb = MockVramGb }],
            };
        }

        if (s.Phase == SensorPhase.NoDevice)
        {
            return new TelemetryFrame
            {
                Timestamp = now,
                Phase = SensorPhase.NoDevice,
                Gpus = [new GpuReading { Index = 0, Name = "RTX 5080", VramTotalGb = MockVramGb }, new GpuReading { Index = 1, Name = "RTX 5060 Ti", VramTotalGb = MockVramGb }],
                Cpu = new CpuReading(J(6, 1.5, 0), null, null, null),
                Ram = new RamReading(J(21, 0.2, 0), 128),
            };
        }

        var hot = s.GpuOverheat;
        var gpu0 = new GpuReading
        {
            Index = 0,
            Name = "RTX 5080",
            VramTotalGb = MockVramGb,
            TemperatureC = hot ? J(76, 0.4, 0) : J(48, 0.4, 0),
            PowerW = hot ? J(318, 4, 0) : J(112, 2.5, 0),
            UtilizationPercent = Clamp(hot ? J(98, 1, 0) : J(66, 3, 0), 0, 100),
            VramUsedGb = hot ? J(14.6, 0.05, 1) : J(10.8, 0.05, 1),
            ClockMhz = hot ? J(2790, 15, -1) : J(2610, 15, -1),
        };
        var gpu1 = new GpuReading
        {
            Index = 1,
            Name = "RTX 5060 Ti",
            VramTotalGb = MockVramGb,
            TemperatureC = hot ? J(64, 0.4, 0) : J(36, 0.4, 0),
            PowerW = s.Gpu1PowerMissing ? null : hot ? J(142, 3, 0) : J(14, 1, 0),
            UtilizationPercent = Clamp(hot ? J(84, 2, 0) : J(29, 2, 0), 0, 100),
            VramUsedGb = hot ? J(11.2, 0.05, 1) : J(4.2, 0.05, 1),
            ClockMhz = hot ? J(2540, 15, -1) : J(1890, 15, -1),
        };

        var tctl = hot ? J(71.4, 0.3, 1) : J(52.4, 0.3, 1);
        var cpu = new CpuReading(Clamp(hot ? J(42, 2, 0) : J(9, 1.5, 0), 0, 100), tctl, tctl + 0.6, tctl + 3.4);
        var ram = new RamReading(hot ? J(68, 0.3, 0) : J(22, 0.2, 0), 128);
        var ssd = new SsdReading(hot ? J(47, 0.4, 0) : J(39, 0.4, 0), hot ? J(1840, 60, -1) : J(142, 12, 0));

        double[] pins = s.PinImbalance ? [.. ImbalancedPins] : [.. NormalPins];
        for (var i = 0; i < pins.Length; i++)
        {
            pins[i] = Math.Max(0, J(pins[i], 0.03, 2));
        }

        if (IsSpikeActive(now))
        {
            pins[2] = SpikePinA;
        }

        var volts = s.PinImbalance ? J(11.84, 0.02, 2) : J(12.12, 0.02, 2);
        var wireView = new WireViewReading
        {
            Connected = true,
            PinCurrentsA = pins,
            VoltageV = volts,
            PowerW = Math.Round(pins.Sum() * volts, 1),
            ConnectorInC = s.PinImbalance ? J(68.5, 0.3, 1) : J(41.0, 0.3, 1),
            ConnectorOutC = s.PinImbalance ? J(72.1, 0.3, 1) : J(42.4, 0.3, 1),
            FanDutyPercent = s.PinImbalance ? 85 : 0,
            CableRatingW = 600,
        };

        return new TelemetryFrame
        {
            Timestamp = now,
            Phase = SensorPhase.Ready,
            Gpus = [gpu0, gpu1],
            Cpu = cpu,
            Ram = ram,
            Ssd = ssd,
            WireView = wireView,
            Flux = new FluxReading(!s.FluxDisconnected, s.IUnityRunning),
            Processes = Processes(hot),
        };
    }

    private IReadOnlyList<ProcessReading> Processes(bool heavy)
    {
        const string gpu0 = "RTX 5080";
        const string gpu1 = "RTX 5060 Ti";
        return
        [
            new(18420, "basalt-server.exe", gpu0, heavy ? 11.86 : 9.42, 0.18, U(heavy ? 97 : 64, 3), "Compute_0",
                @"C:\AI\basalt\basalt-server.exe --profile dual-quality --port 8080"),
            new(18421, "basalt-worker-1.exe", gpu1, heavy ? 9.64 : 3.85, 0.09, U(heavy ? 82 : 28, 3), "Compute_0",
                @"C:\AI\basalt\basalt-worker.exe --device 1 --offload-layers 18"),
            new(2028, "dwm.exe", gpu0, 1.12, 0.04, U(3, 1), "3D", @"C:\Windows\System32\dwm.exe"),
            new(14196, "chrome.exe", gpu0, 0.24, 0.08, U(2, 1), "VideoDecode",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe --type=gpu-process"),
            new(9912, "Antigravity.exe", gpu1, 0.31, 0.02, U(1, 1), "3D", @"%LOCALAPPDATA%\Programs\Antigravity\Antigravity.exe"),
            new(6120, "Code.exe", gpu0, 0.18, 0.05, U(1, 1), "3D", @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe --type=gpu-process"),
            new(7716, "msedgewebview2.exe", gpu1, 0.07, 0.02, U(0, 1), "3D",
                @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe --type=gpu-process"),
            new(10988, "WindowsTerminal.exe", gpu0, 0.09, 0.03, U(0, 1), "3D", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe"),
            new(13776, "explorer.exe", gpu0, 0.06, 0.01, U(0, 1), "3D", @"C:\Windows\explorer.exe"),
            new(5532, "ShellExperienceHost.exe", gpu0, 0.05, 0.01, U(0, 1), "3D",
                @"C:\Windows\SystemApps\ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe"),
            new(8840, "steamwebhelper.exe", gpu1, 0.04, 0.02, U(0, 1), "3D", @"C:\Program Files (x86)\Steam\bin\cef\cef.win64\steamwebhelper.exe --type=gpu-process"),
            new(11204, "Discord.exe", gpu1, 0.04, 0.01, U(0, 1), "3D", @"%LOCALAPPDATA%\Discord\app\Discord.exe --type=gpu-process"),
            new(6408, "SearchHost.exe", gpu0, 0.03, 0.01, U(0, 1), "3D", @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe"),
            new(7312, "TextInputHost.exe", gpu0, 0.02, 0.01, U(0, 1), "3D", @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe"),
            new(9020, "OneDrive.exe", gpu0, 0.02, 0.00, U(0, 1), "3D", @"%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.exe"),
            new(3964, "PhoneExperienceHost.exe", gpu0, 0.01, 0.00, U(0, 1), "3D", @"C:\Program Files\WindowsApps\Microsoft.YourPhone\PhoneExperienceHost.exe"),
            new(4, "Basic Render Driver", "Microsoft Basic Render Driver", 0.00, 0.02, 0, "3D", "(소프트웨어 어댑터 — 실제 GPU 아님)", IsSoftwareAdapter: true),
        ];
    }

    private double U(double baseline, double amplitude) => Math.Clamp(J(baseline, amplitude, 0), 0, 100);

    /// <summary>Baseline with a small symmetric random offset, rounded to <paramref name="digits"/> (negative = tens).</summary>
    private double J(double baseline, double amplitude, int digits)
    {
        var value = Jitter ? baseline + (_random.NextDouble() * 2 - 1) * amplitude : baseline;
        return digits >= 0 ? Math.Round(value, digits) : Math.Round(value / 10) * 10;
    }

    private static double Clamp(double value, double min, double max) => Math.Clamp(value, min, max);
}
