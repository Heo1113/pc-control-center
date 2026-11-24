using CommunityToolkit.Mvvm.ComponentModel;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Presentation.Charts;
using LocalAiPerf.Recording;

namespace LocalAiPerf.Presentation.ViewModels;

public enum StatusLevel
{
    Neutral,
    Good,
    Warning,
    Critical,
}

/// <summary>Live overview: hardware cards, AI runtime card and four small charts.</summary>
public sealed partial class DashboardViewModel(MonitoringService service) : PageViewModel
{
    public override string Title => "대시보드";
    public override string Glyph => "";

    [ObservableProperty] public partial string GpuName { get; set; } = "GPU";
    [ObservableProperty] public partial string GpuUtil { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string GpuTemp { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string GpuPower { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string GpuVram { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string GpuVramPercent { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string GpuClock { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string ThrottleText { get; set; } = "";
    [ObservableProperty] public partial StatusLevel ThrottleLevel { get; set; }
    [ObservableProperty] public partial string CpuUtil { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string RamUsed { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string RamPercent { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string GpuNote { get; set; } = "";
    [ObservableProperty] public partial string RuntimeName { get; set; } = "AI 엔진";
    [ObservableProperty] public partial string RuntimeState { get; set; } = "-";
    [ObservableProperty] public partial StatusLevel RuntimeLevel { get; set; }
    [ObservableProperty] public partial string RuntimeModel { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string RuntimeEngine { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string AnswerTokS { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string AnswerBasis { get; set; } = "";
    [ObservableProperty] public partial string PrefillTokS { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string AggregateTokS { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string Requests { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string Context { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string RuntimeMessage { get; set; } = "";
    [ObservableProperty] public partial string MeasuredAt { get; set; } = Fmt.NA;
    [ObservableProperty] public partial ChartModel? UtilizationChart { get; set; }
    [ObservableProperty] public partial ChartModel? PowerChart { get; set; }
    [ObservableProperty] public partial ChartModel? VramChart { get; set; }
    [ObservableProperty] public partial ChartModel? ThroughputChart { get; set; }

    public override void OnFrame(TelemetryFrame frame)
    {
        var gpuIndex = service.Settings.Ui.SelectedGpuIndex;
        var hw = frame.Hardware;
        var gpu = hw.Gpu(gpuIndex) ?? hw.Gpus.FirstOrDefault();
        MeasuredAt = Fmt.Time(frame.Timestamp);
        GpuName = gpu?.Name ?? "GPU";
        GpuUtil = Fmt.Percent(gpu?.UtilizationPercent, "0");
        GpuTemp = Fmt.Num(gpu?.TemperatureC, "0", "°C");
        GpuPower = gpu?.PowerW is double w ? $"{w:0.0} W" + (gpu.PowerLimitW is double l ? $" / {l:0} W" : "") : Fmt.NA;
        GpuVram = Fmt.GibPair(gpu?.MemoryUsedBytes, gpu?.MemoryTotalBytes);
        GpuVramPercent = Fmt.Percent(gpu?.MemoryUsedPercent);
        GpuClock = Fmt.Num(gpu?.GraphicsClockMhz, "0", "MHz");
        var limiting = (gpu?.ThrottleReasons ?? 0) & (ulong)GpuThrottleReasonText.Limiting;
        ThrottleText = gpu?.ThrottleReasons is null ? "" : limiting != 0 ? $"클럭 제한: {GpuThrottleReasonText.Describe(limiting)}" : "클럭 제한 없음";
        ThrottleLevel = limiting != 0 ? StatusLevel.Warning : StatusLevel.Neutral;
        GpuNote = gpu is null ? service.Hardware?.Gpu.UnavailableReason ?? "GPU 센서 값 없음" : "";
        CpuUtil = Fmt.Percent(hw.System.CpuUtilizationPercent);
        RamUsed = Fmt.GibPair(hw.System.MemoryUsedBytes, hw.System.MemoryTotalBytes);
        RamPercent = Fmt.Percent(hw.System.MemoryUsedPercent);
        UpdateRuntime(service.LatestRuntime);

        var minutes = Math.Clamp(service.Settings.Ui.LiveChartMinutes, 1, 60);
        var frames = Window(service.Live.Snapshot(), frame.Timestamp, TimeSpan.FromMinutes(minutes));
        var interval = service.Interval;
        UtilizationChart = ChartBuilder.Utilization(frames, gpu?.Index ?? gpuIndex, interval);
        PowerChart = ChartBuilder.GpuPower(frames, gpu?.Index ?? gpuIndex, interval);
        VramChart = ChartBuilder.Vram(frames, gpu?.Index ?? gpuIndex, interval);
        ThroughputChart = ChartBuilder.Throughput(frames, interval);
    }

    internal static IReadOnlyList<TelemetryFrame> Window(IReadOnlyList<TelemetryFrame> frames, DateTimeOffset end, TimeSpan window)
    {
        var start = end - window;
        return frames.Where(f => f.Timestamp >= start).ToList();
    }

    private void UpdateRuntime(RuntimeSnapshot? rt)
    {
        RuntimeName = service.Runtime?.DisplayName ?? "AI 엔진";
        if (service.Runtime is null)
        {
            RuntimeState = "꺼짐 (설정에서 사용 안 함)";
            RuntimeLevel = StatusLevel.Neutral;
            RuntimeMessage = "";
            return;
        }

        if (rt is null)
        {
            RuntimeState = "연결 중";
            RuntimeLevel = StatusLevel.Neutral;
            return;
        }

        RuntimeState = RuntimeStateText.ToKorean(rt.State);
        RuntimeLevel = rt.State switch
        {
            RuntimeConnectionState.Connected => StatusLevel.Good,
            RuntimeConnectionState.Limited or RuntimeConnectionState.Timeout or RuntimeConnectionState.AuthRequired => StatusLevel.Warning,
            RuntimeConnectionState.Unreachable => StatusLevel.Neutral,
            _ => StatusLevel.Critical,
        };
        RuntimeMessage = rt.Error is { } e ? $"[{RuntimeStateText.ToKorean(e.Category)}] {e.Message}" : "";
        var reachable = rt.IsReachable;
        RuntimeModel = rt.Model?.Id is string id ? id + (reachable ? "" : " (마지막 확인)") : Fmt.NA;
        RuntimeEngine = rt.Server?.EngineVersion
            ?? (rt.Server?.MetadataRestricted == true ? "" : reachable ? $"{Fmt.NA} - {rt.Server?.EngineVersionNote}" : Fmt.NA);
        var last = reachable ? rt.LastCompletedRequest : null;
        if (reachable && rt.Rates?.AnswerTokPerSec is double live)
        {
            AnswerTokS = Fmt.Num(live, "0.0", "tok/s");
            AnswerBasis = "생성 중 · 엔진 보고";
        }
        else if (last?.DecodeTokPerSec is double lastRate)
        {
            AnswerTokS = Fmt.Num(lastRate, "0.0", "tok/s");
            AnswerBasis = $"마지막 완료 요청 {Fmt.Unix(last.StartedUnix)} · 계산값 (지금은 생성 중 아님)";
        }
        else
        {
            AnswerTokS = Fmt.NA;
            AnswerBasis = reachable ? "생성 중이 아니고 완료된 요청 기록도 없음" : "";
        }

        PrefillTokS = !reachable ? Fmt.NA
            : rt.Rates?.PrefillTokPerSec is double prefill ? Fmt.Num(prefill, "0.0", "tok/s")
            : last?.PrefillTokPerSec is double lastPrefill ? $"{Fmt.Num(lastPrefill, "0.0", "tok/s")} (마지막 요청, 계산값)"
            : Fmt.NA;
        AggregateTokS = reachable ? Fmt.Num(rt.Rates?.AggregateOutputTokPerSec, "0.0", "tok/s") : Fmt.NA;
        Requests = reachable && rt.Work is { } work
            ? $"처리 중 {Fmt.Int(work.Active)} · 대기 {Fmt.Int(work.Queued)}"
            : Fmt.NA;
        Context = !reachable || rt.Model?.ContextTokens is not long ctx ? Fmt.NA
            : rt.MaxSlotContextUsed is long used ? $"{used:N0} / {ctx:N0} tokens ({Fmt.Percent(rt.ContextFillPercent)})"
            : rt.Work?.Active == 0 ? $"사용 중 아님 · 최대 {ctx:N0} tokens"
            : $"{Fmt.NA} / {ctx:N0} tokens";
    }
}
