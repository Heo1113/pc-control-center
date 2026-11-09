using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Presentation.Charts;
using LocalAiPerf.Recording;
using LocalAiPerf.Telemetry;

namespace LocalAiPerf.Presentation.ViewModels;

/// <summary>Detailed GPU/CPU/RAM charts, device facts and the sensor support table (admin-only sensors listed separately).</summary>
public sealed partial class HardwareViewModel(MonitoringService service) : PageViewModel
{
    public override string Title => "하드웨어";
    public override string Glyph => "";

    public IReadOnlyList<int> WindowOptions { get; } = [1, 5, 15, 60];

    [ObservableProperty] public partial int WindowMinutes { get; set; } = 5;
    [ObservableProperty] public partial ChartModel? GpuUtilChart { get; set; }
    [ObservableProperty] public partial ChartModel? TempChart { get; set; }
    [ObservableProperty] public partial ChartModel? PowerChart { get; set; }
    [ObservableProperty] public partial ChartModel? VramChart { get; set; }
    [ObservableProperty] public partial ChartModel? ClockChart { get; set; }
    [ObservableProperty] public partial ChartModel? PcieChart { get; set; }
    [ObservableProperty] public partial ChartModel? CpuChart { get; set; }
    [ObservableProperty] public partial ChartModel? RamChart { get; set; }
    [ObservableProperty] public partial string ElevationText { get; set; } = "";

    public ObservableCollection<ValueRow> DeviceInfo { get; } = [];
    public ObservableCollection<ValueRow> CurrentValues { get; } = [];
    public ObservableCollection<SensorRow> Sensors { get; } = [];

    public override Task OnActivatedAsync()
    {
        RefreshStatic();
        return Task.CompletedTask;
    }

    public override void OnFrame(TelemetryFrame frame)
    {
        var gpuIndex = service.Settings.Ui.SelectedGpuIndex;
        var hw = frame.Hardware;
        var gpu = hw.Gpu(gpuIndex) ?? hw.Gpus.FirstOrDefault();
        var index = gpu?.Index ?? gpuIndex;
        var frames = DashboardViewModel.Window(service.Live.Snapshot(), frame.Timestamp, TimeSpan.FromMinutes(WindowMinutes));
        var interval = service.Interval;
        GpuUtilChart = new ChartModel
        {
            Title = "GPU 사용률",
            Unit = "%",
            Series =
            [
                ChartBuilder.Live(frames, "GPU 코어", 0, f => f.Hardware.Gpu(index)?.UtilizationPercent),
                ChartBuilder.Live(frames, "메모리 컨트롤러", 1, f => f.Hardware.Gpu(index)?.MemoryControllerUtilizationPercent),
            ],
            YMin = 0,
            YMax = 100,
            ValueFormat = "0",
            GapThreshold = ChartBuilder.TimeGap(interval),
        };
        TempChart = ChartBuilder.GpuTemperature(frames, index, interval, service.Settings.Analysis.HighGpuTempC);
        PowerChart = ChartBuilder.GpuPower(frames, index, interval);
        VramChart = ChartBuilder.Vram(frames, index, interval);
        ClockChart = ChartBuilder.GpuClock(frames, index, interval);
        PcieChart = ChartBuilder.Pcie(frames, index, interval);
        CpuChart = ChartBuilder.Cpu(frames, interval);
        RamChart = ChartBuilder.Ram(frames, interval);

        CurrentValues.Clear();
        const string measured = "실측";
        CurrentValues.Add(new ValueRow("GPU 사용률", Fmt.Percent(gpu?.UtilizationPercent, "0"), measured));
        CurrentValues.Add(new ValueRow("GPU 메모리 컨트롤러", Fmt.Percent(gpu?.MemoryControllerUtilizationPercent, "0"), measured));
        CurrentValues.Add(new ValueRow("GPU 온도", Fmt.Num(gpu?.TemperatureC, "0", "°C"), measured));
        CurrentValues.Add(new ValueRow("GPU 전력 / 제한", gpu?.PowerW is double w ? $"{w:0.0} W / {Fmt.Num(gpu.PowerLimitW, "0", "W")}" : Fmt.NA, measured));
        CurrentValues.Add(new ValueRow("VRAM", Fmt.GibPair(gpu?.MemoryUsedBytes, gpu?.MemoryTotalBytes), measured));
        CurrentValues.Add(new ValueRow("VRAM 사용 비율", Fmt.Percent(gpu?.MemoryUsedPercent), "계산값"));
        CurrentValues.Add(new ValueRow("그래픽 / 메모리 클럭", $"{Fmt.Num(gpu?.GraphicsClockMhz, "0")} / {Fmt.Num(gpu?.MemoryClockMhz, "0")} MHz", measured));
        CurrentValues.Add(new ValueRow("팬 속도", Fmt.Percent(gpu?.FanSpeedPercent, "0"), measured));
        CurrentValues.Add(new ValueRow("PCIe 수신 / 송신", $"{Fmt.Num(gpu?.PcieRxKBps / 1024.0)} / {Fmt.Num(gpu?.PcieTxKBps / 1024.0)} MB/s", measured));
        CurrentValues.Add(new ValueRow("클럭 제한 사유", gpu?.ThrottleReasons is ulong r ? GpuThrottleReasonText.Describe(r) : Fmt.NA, measured));
        CurrentValues.Add(new ValueRow("CPU 사용률", Fmt.Percent(hw.System.CpuUtilizationPercent), measured));
        CurrentValues.Add(new ValueRow("시스템 RAM", Fmt.GibPair(hw.System.MemoryUsedBytes, hw.System.MemoryTotalBytes), measured));
        if (hw.IsMock)
        {
            CurrentValues.Add(new ValueRow("주의", "MOCK 가상 센서 값 (실제 측정 아님)", ""));
        }

        if (DeviceInfo.Count == 0)
        {
            RefreshStatic();
        }
    }

    partial void OnWindowMinutesChanged(int value)
    {
        if (service.Live.Latest is { } latest)
        {
            OnFrame(latest);
        }
    }

    private void RefreshStatic()
    {
        var info = service.HardwareInfo;
        DeviceInfo.Clear();
        Sensors.Clear();
        if (info is null)
        {
            return;
        }

        DeviceInfo.Add(new ValueRow("CPU", info.CpuName ?? Fmt.NA));
        DeviceInfo.Add(new ValueRow("논리 프로세서", info.LogicalProcessors.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        DeviceInfo.Add(new ValueRow("RAM 전체", Fmt.Gib(info.RamTotalBytes, "0.0")));
        DeviceInfo.Add(new ValueRow("운영체제", info.OsDescription));
        foreach (var gpu in info.Gpus)
        {
            DeviceInfo.Add(new ValueRow($"GPU {gpu.Index}", $"{gpu.Name} · VRAM {Fmt.Gib(gpu.MemoryTotalBytes, "0.0")} · 전력 제한 {Fmt.Num(gpu.PowerLimitW, "0", "W")}"));
        }

        DeviceInfo.Add(new ValueRow("GPU 드라이버", info.GpuDriverVersion ?? Fmt.NA));
        DeviceInfo.Add(new ValueRow("NVML 버전", info.NvmlVersion ?? Fmt.NA));
        if (service.Hardware?.Gpu.IsAvailable == false)
        {
            DeviceInfo.Add(new ValueRow("GPU 센서", service.Hardware.Gpu.UnavailableReason ?? "사용 불가"));
        }

        ElevationText = info.IsElevated
            ? "관리자 권한으로 실행 중입니다. 이 앱은 관리자 권한이 필요하지 않습니다."
            : "일반 사용자 권한으로 실행 중입니다. 아래 표의 [관리자 권한 필요] 항목만 수집하지 않습니다.";
        foreach (var c in service.Hardware?.Capabilities ?? HardwareMonitor.NotCollected)
        {
            Sensors.Add(new SensorRow(c.DisplayName, SensorStatusText.ToKorean(c.Status), c.Source, c.RequiresElevation, c.Detail ?? "", c.Status == SensorStatus.Ok));
        }
    }
}
