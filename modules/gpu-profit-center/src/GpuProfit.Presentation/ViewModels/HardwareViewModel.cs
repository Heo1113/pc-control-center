using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Presentation.Charts;

namespace GpuProfit.Presentation.ViewModels;

/// <summary>Live GPU/CPU/RAM charts from project A's sensors. Unsupported values are N/A, never 0.</summary>
public sealed partial class HardwareViewModel : PageViewModel
{
    private DateTimeOffset _lastCharts;

    public HardwareViewModel(ViewContext context) : base(context)
    {
        Windows = [new("최근 10분", 10), new("최근 30분", 30), new("최근 60분", 60)];
        SelectedWindow = Windows[0];
    }

    public override string Title => "하드웨어";
    public override string Glyph => "";

    public IReadOnlyList<Option<int>> Windows { get; }
    public ObservableCollection<NameValueRow> Current { get; } = [];
    public ObservableCollection<NameValueRow> Info { get; } = [];
    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];

    [ObservableProperty] public partial Option<int> SelectedWindow { get; set; }
    [ObservableProperty] public partial ChartModel? PowerChart { get; set; }
    [ObservableProperty] public partial ChartModel? UtilChart { get; set; }
    [ObservableProperty] public partial ChartModel? TempChart { get; set; }
    [ObservableProperty] public partial ChartModel? VramChart { get; set; }
    [ObservableProperty] public partial ChartModel? ClockChart { get; set; }
    [ObservableProperty] public partial ChartModel? CpuChart { get; set; }
    [ObservableProperty] public partial ChartModel? RamChart { get; set; }
    [ObservableProperty] public partial string SamplingText { get; set; } = "";

    public string PowerNote => DashboardViewModel.BoardPowerNote;

    partial void OnSelectedWindowChanged(Option<int> value) => RefreshCharts();

    public override Task OnActivatedAsync()
    {
        LoadInfo();
        RefreshCharts();
        return Task.CompletedTask;
    }

    private void LoadInfo()
    {
        var hw = Context.Engine.Hardware;
        if (hw is null)
        {
            return;
        }

        var info = hw.Info;
        var rows = new List<NameValueRow>
        {
            new("GPU 센서", hw.Gpu.Name, hw.Gpu.IsAvailable ? "사용 가능" : $"사용 불가: {hw.Gpu.UnavailableReason}"),
            new("GPU 드라이버", info.GpuDriverVersion ?? Fmt.NA, info.NvmlVersion is { } n ? $"NVML {n}" : ""),
        };
        rows.AddRange(info.Gpus.Select(g => new NameValueRow($"GPU {g.Index}", g.Name ?? Fmt.NA,
            $"VRAM {Fmt.Gib(g.MemoryTotalBytes)}, 전력 제한 {Fmt.Watts(g.PowerLimitW)}")));
        rows.Add(new("CPU", info.CpuName ?? Fmt.NA, $"논리 프로세서 {info.LogicalProcessors}개"));
        rows.Add(new("시스템 RAM", Fmt.Gib(info.RamTotalBytes)));
        rows.Add(new("운영체제", info.OsDescription, info.IsElevated ? "관리자 권한으로 실행 중" : "일반 사용자 권한"));
        if (info.IsMock)
        {
            rows.Add(new("모드", "MOCK 가상 센서", "실제 하드웨어 측정이 아닙니다"));
        }

        Replace(Info, rows);
        Replace(Capabilities, hw.Capabilities.Select(c => new CapabilityRow(c.DisplayName, c.Source, SensorStatusText.ToKorean(c.Status), c.Detail ?? "")));
        UpdateSamplingText();
    }

    private void UpdateSamplingText() => SamplingText =
        $"샘플 간격 {Context.Settings.Sampling.IntervalSeconds}초 · 최대 샘플 간격 {Context.Engine.Profile.MaxSampleGapSeconds:0.#}초를 넘는 구간은 공백으로 둡니다 · 저장된 샘플 {Context.Engine.Recorder?.SamplesStored:N0}개(이번 실행)";

    public override void OnSensor(HardwareSnapshot snapshot)
    {
        if (!IsActive)
        {
            return;
        }

        var rows = new List<NameValueRow>();
        if (snapshot.Gpus.Count == 0)
        {
            rows.Add(new NameValueRow("GPU", Fmt.NA, Context.Engine.Hardware?.Gpu.UnavailableReason ?? "GPU 값을 읽지 못했습니다"));
        }

        foreach (var g in snapshot.Gpus)
        {
            var p = $"GPU {g.Index}";
            rows.Add(new NameValueRow($"{p} 보드 전력", Fmt.Watts(g.PowerW), g.PowerLimitW is double l ? $"제한 {Fmt.Watts(l)}" : ""));
            rows.Add(new NameValueRow($"{p} 사용률", Fmt.PercentValue(g.UtilizationPercent), $"메모리 컨트롤러 {Fmt.PercentValue(g.MemoryControllerUtilizationPercent)}"));
            rows.Add(new NameValueRow($"{p} 온도", Fmt.Celsius(g.TemperatureC), g.FanSpeedPercent is double f ? $"팬 {f:0}%" : ""));
            rows.Add(new NameValueRow($"{p} VRAM", $"{Fmt.Gib(g.MemoryUsedBytes)} / {Fmt.Gib(g.MemoryTotalBytes)}", Fmt.Percent(g.MemoryUsedPercent / 100)));
            rows.Add(new NameValueRow($"{p} 클럭 (코어/메모리)", $"{Fmt.Mhz(g.GraphicsClockMhz)} / {Fmt.Mhz(g.MemoryClockMhz)}",
                g.ThrottleReasons is ulong t ? "클럭 제한: " + GpuThrottleReasonText.Describe(t) : ""));
        }

        rows.Add(new NameValueRow("CPU 사용률", Fmt.PercentValue(snapshot.System.CpuUtilizationPercent)));
        rows.Add(new NameValueRow("RAM 사용량", $"{Fmt.Gib(snapshot.System.MemoryUsedBytes)} / {Fmt.Gib(snapshot.System.MemoryTotalBytes)}"));
        rows.Add(new NameValueRow("측정 시각", Fmt.Time(snapshot.Timestamp), snapshot.IsMock ? "MOCK 가상 값" : ""));
        foreach (var issue in snapshot.Issues.Take(5))
        {
            rows.Add(new NameValueRow("센서 문제", issue.Scope, $"{SensorStatusText.ToKorean(issue.Status)}: {issue.Message}"));
        }

        Replace(Current, rows);
        UpdateSamplingText();
        if (snapshot.Timestamp - _lastCharts >= TimeSpan.FromSeconds(2))
        {
            RefreshCharts();
        }
    }

    private void RefreshCharts()
    {
        if (Context.Engine.Recorder is not { } recorder || SelectedWindow is null)
        {
            return;
        }

        var all = recorder.Live.Snapshot();
        if (all.Count == 0)
        {
            return;
        }

        _lastCharts = all[^1].Timestamp;
        var cutoff = all[^1].Timestamp - TimeSpan.FromMinutes(SelectedWindow.Value);
        var s = all.Where(x => x.Timestamp >= cutoff).ToList();
        var gap = Math.Max(Context.Engine.Profile.MaxSampleGapSeconds, recorder.Interval.TotalSeconds * 3);
        PowerChart = Charts.Live("GPU 보드 전력", "W", s, g => g.PowerW, gap, yMin: 0);
        UtilChart = Charts.Live("GPU 사용률", "%", s, g => g.UtilizationPercent, gap, "0", 0, 100);
        TempChart = Charts.WithMinimumSpan(Charts.Live("GPU 온도", "°C", s, g => g.TemperatureC, gap, "0"), 10);
        VramChart = Charts.Live("VRAM 사용량", "GiB", s, g => g.MemoryUsedBytes / (1024.0 * 1024 * 1024), gap, "0.00", yMin: 0);
        ClockChart = Charts.Live("GPU 코어 클럭", "MHz", s, g => g.GraphicsClockMhz, gap, "0");
        CpuChart = Charts.LiveSystem("CPU 사용률", "%", s, x => x.CpuUtilizationPercent, gap, 100);
        RamChart = Charts.LiveSystem("RAM 사용량", "GiB", s, x => x.MemoryUsedBytes / (1024.0 * 1024 * 1024), gap);
    }
}
