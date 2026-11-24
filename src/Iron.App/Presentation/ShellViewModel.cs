using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Iron.App.Presentation;

/// <summary>
/// The whole IRON window (and the tray popup): five tabs over one stream of readings. Stage 1: the readings come from
/// <see cref="MockTelemetryProvider"/>; every device action is simulated. WPF-free; the app calls <see cref="Tick"/>
/// on its timer and tests call it directly.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public const double GpuBoardScaleW = 540; // RTX 5080 360 W + RTX 5060 Ti 180 W: the board power meter's full scale

    private readonly MockTelemetryProvider _mock;
    private readonly IUiScheduler _scheduler;
    private readonly AlertMonitor _alerts = new();
    private readonly History _nvmlPower = new();
    private readonly History _wireViewPower = new();
    private IDisposable? _flashEnd;

    public ShellViewModel(MockTelemetryProvider mock, IUiScheduler scheduler)
    {
        _mock = mock;
        _scheduler = scheduler;
        Gpus = new[] { new GpuCardViewModel(0), new GpuCardViewModel(1) }; // an array: XAML binds Gpus[0], Gpus[1]
        Cpu = new SystemTileViewModel("CPU (9800X3D)");
        Ram = new SystemTileViewModel("시스템 RAM");
        Ssd = new SystemTileViewModel("NVMe SSD");
        Board = new SystemTileViewModel("GPU 보드 전력");
        SystemTiles = [Cpu, Ram, Ssd, Board];
        WireView = new WireViewViewModel(scheduler, () => _mock.State.WireViewPortBusy, Reevaluate);
        GpuTemperatureLimit = new NumericSetting("GPU 온도 UI 주의 기준", "°C", 55, 95, 72, 0, _ => Reevaluate());
        PinCurrentLimit = new NumericSetting("WireView 핀 전류 UI 주의 기준", "A", 4.0, 9.0, 7.0, 1, _ => Reevaluate());
        Tariff = new NumericSetting("전기요금 단가", "₩/kWh", 0, 2000, 185, 0, _ => Reevaluate());
        IntervalMs = new NumericSetting("센서 수집 주기", "ms", 500, 5000, 1000, 0, v => IntervalChanged?.Invoke(TimeSpan.FromMilliseconds(v)));
        _mock.State.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MockSummary));
            RefreshNow();
        };
    }

    /// <summary>The collection interval changed (the app restarts its timer).</summary>
    public event Action<TimeSpan>? IntervalChanged;

    public MockState Mock => _mock.State;

    public IReadOnlyList<GpuCardViewModel> Gpus { get; }
    public SystemTileViewModel Cpu { get; }
    public SystemTileViewModel Ram { get; }
    public SystemTileViewModel Ssd { get; }
    public SystemTileViewModel Board { get; }
    public IReadOnlyList<SystemTileViewModel> SystemTiles { get; }
    public ProcessListViewModel Processes { get; } = new();
    public WireViewViewModel WireView { get; }
    public FluxViewModel Flux { get; } = new();

    public NumericSetting GpuTemperatureLimit { get; }
    public NumericSetting PinCurrentLimit { get; }
    public NumericSetting Tariff { get; }
    public NumericSetting IntervalMs { get; }

    public TelemetryFrame? Latest { get; private set; }

    // ---- navigation and layout ---------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsProcesses), nameof(IsPower), nameof(IsDevices), nameof(IsSettings))]
    public partial IronTab Tab { get; set; } = IronTab.Overview;

    public bool IsOverview => Tab == IronTab.Overview;
    public bool IsProcesses => Tab == IronTab.Processes;
    public bool IsPower => Tab == IronTab.Power;
    public bool IsDevices => Tab == IronTab.Devices;
    public bool IsSettings => Tab == IronTab.Settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuColumns), nameof(SystemColumns), nameof(WorkSideBySide), nameof(WorkFirstShare), nameof(PanelsSideBySide),
        nameof(IsFull), nameof(IsCompact), nameof(IsNarrow), nameof(PanelColumns))]
    public partial LayoutMode Layout { get; set; } = LayoutMode.Full;

    public int GpuColumns => LayoutRules.GpuColumns(Layout);
    public int SystemColumns => LayoutRules.SystemColumns(Layout);
    public bool WorkSideBySide => LayoutRules.WorkAreaSideBySide(Layout);
    public double WorkFirstShare => LayoutRules.WorkAreaFirstShare(Layout);
    public bool PanelsSideBySide => LayoutRules.PanelsSideBySide(Layout);
    public int PanelColumns => PanelsSideBySide ? 2 : 1;
    public bool IsFull => Layout == LayoutMode.Full;
    public bool IsCompact => Layout == LayoutMode.Compact;
    public bool IsNarrow => Layout == LayoutMode.Narrow;

    /// <summary>The window fills the work area (true) or its half (false); the title bar button switches.</summary>
    [ObservableProperty] public partial bool IsHalfWidth { get; set; }

    // ---- status ------------------------------------------------------------------------------------------------------

    [ObservableProperty] public partial StatusKind Status { get; private set; } = StatusKind.Loading;
    [ObservableProperty] public partial string StatusText { get; private set; } = StatusRules.LoadingText;

    // ---- power -------------------------------------------------------------------------------------------------------

    [ObservableProperty] public partial string TotalPowerText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial bool TotalPowerPartial { get; private set; }
    [ObservableProperty] public partial string CostPerHourText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial string PowerNote { get; private set; } = "";
    [ObservableProperty] public partial string NvmlBoardText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial string SlotDeltaText { get; private set; } = Fmt.Missing;
    [ObservableProperty] public partial double?[] NvmlPowerTrend { get; private set; } = [];
    [ObservableProperty] public partial double?[] WireViewPowerTrend { get; private set; } = [];

    // ---- settings (Stage 1: kept in memory only) ---------------------------------------------------------------------

    /// <summary>Closing the window (✕) keeps IRON in the tray. Default on.</summary>
    [ObservableProperty] public partial bool CloseToTray { get; set; } = true;

    /// <summary>Start in the tray at Windows sign-in. Stage 1 only remembers the switch; nothing is registered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoStartNote))]
    public partial bool AutoStartMock { get; set; }

    public string AutoStartNote => AutoStartMock
        ? "모의: 켜짐으로 표시만 합니다. Stage 1은 시작 프로그램(Run 키·작업 스케줄러)에 아무것도 등록하지 않습니다."
        : "기본 꺼짐. 실기 검증을 마친 뒤 켜세요. Stage 1은 시작 프로그램에 아무것도 등록하지 않습니다.";

    // ---- tray --------------------------------------------------------------------------------------------------------

    [ObservableProperty] public partial string TrayToolTip { get; private set; } = "IRON";
    [ObservableProperty] public partial string TrayWireViewText { get; private set; } = Fmt.Missing;

    // ---- mock --------------------------------------------------------------------------------------------------------

    public string MockSummary
    {
        get
        {
            var preset = Mock.CurrentPreset switch
            {
                MockPreset.Normal => "정상",
                MockPreset.Alert => "오류/불균형",
                MockPreset.Disconnected => "미연결",
                MockPreset.Loading => "로딩",
                _ => "사용자 조합",
            };
            return $"모의 상태: {preset}" + (WireView.Port != PortState.Active ? " · WireView USB 해제" : "");
        }
    }

    public ObservableCollection<string> RecentActions { get; } = [];

    [RelayCommand]
    public void SelectTab(string? tab)
    {
        if (tab is not null && AppOptions.TryTab(tab, out var t))
        {
            Tab = t;
        }
    }

    [RelayCommand]
    public void ApplyPreset(string? preset)
    {
        var p = preset?.ToLowerInvariant() switch
        {
            "alert" => MockPreset.Alert,
            "disconnected" => MockPreset.Disconnected,
            "loading" => MockPreset.Loading,
            _ => MockPreset.Normal,
        };
        Mock.Apply(p);
        Log(p switch
        {
            MockPreset.Alert => "오류/불균형 적용 (5080 76°C · P3 7.94 A · 5060 Ti 전력 누락)",
            MockPreset.Disconnected => "미연결 적용",
            MockPreset.Loading => "로딩 적용",
            _ => "정상 적용",
        });
    }

    [RelayCommand]
    public void ToggleUsb()
    {
        WireView.ToggleHandover();
        Log(WireView.Port is PortState.Releasing or PortState.Released ? "WireView USB 해제 (모의)" : "WireView 재개 (모의)");
    }

    /// <summary>A 1.5 s pin spike: the pin flashes red while it lasts, but the warning never latches (3 readings needed).</summary>
    [RelayCommand]
    public void FlashImbalance()
    {
        var now = _scheduler.Now;
        _mock.StartPinSpike(now, TimeSpan.FromSeconds(1.5));
        _flashEnd?.Dispose();
        _flashEnd = _scheduler.Schedule(TimeSpan.FromSeconds(1.5) + TimeSpan.FromMilliseconds(20), RefreshNow);
        Log("1.5초 불균형 플래시 (P3 7.6 A, 3회 연속이 아니므로 경고 LED는 켜지지 않음)");
        RefreshNow();
    }

    /// <summary>One reading of the sensors (the app's timer). Counts toward the 3-reading pin confirmation.</summary>
    public void Tick() => Ingest(_mock.Next(_scheduler.Now), counts: true);

    /// <summary>
    /// Shows the effect of a switch at once (a mock state, the USB handover): a fresh reading of the same moment that
    /// does not count as one of the three readings that confirm a pin warning.
    /// </summary>
    public void RefreshNow() => Ingest(_mock.Next(_scheduler.Now), counts: false);

    /// <summary>Re-applies the last reading (a threshold or the tariff changed): colors follow the new value at once.</summary>
    public void Reevaluate()
    {
        if (Latest is { } frame)
        {
            Apply(frame, counts: false);
        }
    }

    private void Ingest(TelemetryFrame frame, bool counts)
    {
        Latest = frame;
        if (counts)
        {
            var g0 = frame.Gpus.Count > 0 ? frame.Gpus[0].PowerW : null;
            _nvmlPower.Add(g0);
            _wireViewPower.Add(frame.WireView.Connected && WireView.Port == PortState.Active ? frame.WireView.PowerW : null);
            Processes.Apply(frame.Processes, frame.Phase);
        }
        else if (frame.Phase != SensorPhase.Ready || Processes.Rows.Count == 0)
        {
            Processes.Apply(frame.Processes, frame.Phase);
        }

        Apply(frame, counts);
    }

    private void Apply(TelemetryFrame frame, bool counts)
    {
        var tempLimit = GpuTemperatureLimit.Value;
        var pinLimit = PinCurrentLimit.Value;
        var measuring = frame.Phase == SensorPhase.Ready;

        // GPU temperature warning (NVML): independent of WireView.
        _alerts.EvaluateGpus(frame.Gpus, tempLimit);
        for (var i = 0; i < Gpus.Count && i < frame.Gpus.Count; i++)
        {
            Gpus[i].Apply(frame.Gpus[i], _alerts.GpuHot[i], measuring);
        }

        // WireView pin warning: only while IRON reads the device; a released or missing port clears it at once.
        var reading = frame.WireView.Connected && WireView.Port == PortState.Active;
        _alerts.AddPinReading(reading ? frame.WireView.PinCurrentsA : null, pinLimit, counts);
        WireView.Apply(frame.WireView, frame.Phase, _alerts, pinLimit);

        var status = StatusRules.For(frame.Phase, _alerts.AnyGpuHot, WireView.PinAlert);
        Status = status.Kind;
        StatusText = status.Text;

        // System modules.
        Cpu.Set(Fmt.Percent2(frame.Cpu.LoadPercent), Fmt.Celsius(frame.Cpu.TctlC), frame.Cpu.LoadPercent / 100.0);
        Ram.Set(frame.Ram.UsedGb is double used ? $"{Fmt.Int(used)} / {Fmt.Int(frame.Ram.TotalGb)} GB" : Fmt.Missing, "", frame.Ram.UsedGb / frame.Ram.TotalGb);
        Ssd.Set(Fmt.Celsius(frame.Ssd.TemperatureC), frame.Ssd.ReadMBps is double mb ? Fmt.Int(mb) + "MB/s" : "", frame.Ssd.TemperatureC / 100.0);

        var total = PowerTotal.Of(frame.Gpus);
        var rate = (decimal)Tariff.Value;
        if (!measuring)
        {
            Board.Set(Fmt.Missing, "", null);
            TotalPowerText = Fmt.Missing;
            TotalPowerPartial = false;
            CostPerHourText = Fmt.Missing;
            PowerNote = frame.Phase == SensorPhase.Initializing ? "센서 초기화 중입니다." : "GPU 센서가 연결되지 않아 전력을 표시하지 않습니다.";
        }
        else if (total.TotalW is double w)
        {
            Board.Set($"{Fmt.Int(w)} W", "", w / GpuBoardScaleW, toolTip: "NVML 보드 전력 합계 (RTX 5080 + RTX 5060 Ti)");
            TotalPowerText = $"{Fmt.Int(w)} W";
            TotalPowerPartial = false;
            CostPerHourText = $"{Fmt.Krw(total.CostPerHour(rate)!.Value)} ₩/h";
            PowerNote = "시간당 요금은 GPU 보드 전력 × 단가로 낸 추정치입니다 (PC 전체 전력 아님).";
        }
        else
        {
            Board.Set("일부 미확인", "", null, warning: true,
                toolTip: $"전력을 보고한 GPU {total.Reported}/{total.Expected}개 — 합계를 만들지 않습니다 (0 W로 간주하지 않음).");
            TotalPowerText = "일부 미확인";
            TotalPowerPartial = true;
            CostPerHourText = "미합산";
            PowerNote = $"GPU {total.Expected}개 중 {total.Expected - total.Reported}개의 전력 값이 없어 합계 전력과 요금을 계산하지 않습니다.";
        }

        var gpu0 = frame.Gpus.Count > 0 ? frame.Gpus[0] : null;
        NvmlBoardText = measuring ? Fmt.Watts(gpu0?.PowerW).Replace("W", " W", StringComparison.Ordinal) : Fmt.Missing;
        SlotDeltaText = measuring && WireView.IsReading && gpu0?.PowerW is double nv && WireView.PowerW is double wv
            ? (nv - wv).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " W"
            : Fmt.Missing;
        NvmlPowerTrend = _nvmlPower.Snapshot();
        WireViewPowerTrend = _wireViewPower.Snapshot();

        Flux.Apply(frame);

        // Tray.
        TrayWireViewText = WireView.IsReading ? $"{WireView.PowerText} W · {Fmt.Celsius(frame.WireView.ConnectorInC)}" : WireView.StateText;
        var g = Gpus;
        TrayToolTip = Truncate($"IRON · {StatusText} · 5080 {g[0].TemperatureText} · 5060 Ti {g[1].TemperatureText}" + " · 모의 데이터", 120);
        OnPropertyChanged(nameof(MockSummary));
    }

    private void Log(string action)
    {
        RecentActions.Insert(0, $"{_scheduler.Now.ToLocalTime():HH:mm:ss}  {action}");
        while (RecentActions.Count > 6)
        {
            RecentActions.RemoveAt(RecentActions.Count - 1);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
