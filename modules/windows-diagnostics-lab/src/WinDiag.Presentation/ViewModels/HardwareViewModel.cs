using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Core.Hardware;
using WinDiag.Analysis;
using WinDiag.Core.Events;
using WinDiag.Core.Snapshots;
using WinDiag.Export;
using WinDiag.Storage;

namespace WinDiag.Presentation.ViewModels;

/// <summary>
/// Live and recorded sensor values from project A's sensor module, with problem events marked on the charts, and an
/// event-centred view (values before and after an event). Values a sensor does not provide are shown as N/A.
/// </summary>
public sealed partial class HardwareViewModel : PageViewModel
{
    private DateTimeOffset _lastLive;

    public HardwareViewModel(ViewContext context) : base(context)
    {
        Ranges = [new("실시간 (최근 60분)", 0), new("최근 6시간", 6), new("최근 24시간", 24), new("최근 7일", 168), new("최근 30일", 720)];
        SelectedRange = Ranges[0];
    }

    public override string Title => "하드웨어";
    public override string Glyph => "";

    public IReadOnlyList<Option<int>> Ranges { get; }
    public ObservableCollection<NameValueRow> Live { get; } = [];
    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];
    public ObservableCollection<NameValueRow> Info { get; } = [];
    public ObservableCollection<MetricStatsRow> FocusStats { get; } = [];

    [ObservableProperty] public partial Option<int> SelectedRange { get; set; }
    [ObservableProperty] public partial ChartData? PowerChart { get; set; }
    [ObservableProperty] public partial ChartData? TempChart { get; set; }
    [ObservableProperty] public partial ChartData? UtilChart { get; set; }
    [ObservableProperty] public partial ChartData? ClockChart { get; set; }
    [ObservableProperty] public partial ChartData? CpuChart { get; set; }
    [ObservableProperty] public partial ChartData? RamChart { get; set; }
    [ObservableProperty] public partial bool IsFocused { get; set; }
    [ObservableProperty] public partial string FocusTitle { get; set; } = "";
    [ObservableProperty] public partial string FocusNote { get; set; } = "";
    [ObservableProperty] public partial string ChartNote { get; set; } = "";

    private DateTimeOffset? _focusTime;

    partial void OnSelectedRangeChanged(Option<int> value)
    {
        if (value is not null && IsActive)
        {
            IsFocused = false;
            _focusTime = null;
            _ = RefreshChartsAsync();
        }
    }

    public override async Task OnActivatedAsync()
    {
        await LoadOnce(() => Busy(LoadInfoAsync));
        await RefreshChartsAsync();
    }

    /// <summary>Shows the sensor values around an event (navigation from Event Explorer or Diagnostics).</summary>
    public void Focus(DateTimeOffset time, string title)
    {
        _focusTime = time;
        FocusTitle = $"{Fmt.Time(time)} · {title}";
        IsFocused = true;
        if (IsActive)
        {
            _ = RefreshChartsAsync();
        }
    }

    [RelayCommand]
    private Task ClearFocus()
    {
        _focusTime = null;
        IsFocused = false;
        return RefreshChartsAsync();
    }

    [RelayCommand]
    private Task Refresh() => RefreshChartsAsync();

    [RelayCommand]
    private async Task ExportCsv()
    {
        var (from, to) = CurrentRange();
        var path = Context.Dialogs.SaveFile("센서 기록 내보내기", "CSV (*.csv)|*.csv", $"sensors-{Context.Now.ToLocalTime():yyyyMMdd-HHmm}.csv", Context.Engine.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        await Busy(async () =>
        {
            var series = await Task.Run(() => Context.Store.QuerySensors(from, to, maxPoints: 200_000));
            await Task.Run(() => SensorExporter.WriteCsv(series, path, new ExportOptions { UseUtc = Context.Settings.Ui.UseUtc }));
            StatusText = $"센서 기록 {series.System.Count}행을 내보냈습니다.";
        });
    }

    public override void OnSensor(HardwareSnapshot snapshot)
    {
        ShowLive(snapshot);
        if (SelectedRange.Value == 0 && !IsFocused && snapshot.Timestamp - _lastLive > TimeSpan.FromSeconds(10))
        {
            _lastLive = snapshot.Timestamp;
            _ = RefreshChartsAsync();
        }
    }

    private void ShowLive(HardwareSnapshot snapshot)
    {
        var gpu = snapshot.Gpus.FirstOrDefault();
        var rows = new List<NameValueRow>
        {
            new("측정 시각", Fmt.Clock(snapshot.Timestamp), snapshot.IsMock ? "MOCK(가상) 값" : "실측 (NVML·Windows API)"),
            new("GPU 사용률", Fmt.Percent(gpu?.UtilizationPercent, "0")),
            new("GPU 온도", Fmt.Num(gpu?.TemperatureC, "0", "°C")),
            new("GPU 전력 / 제한", gpu?.PowerW is double p ? $"{Fmt.Num(p, "0.0", "W")} / {Fmt.Num(gpu.PowerLimitW, "0", "W")}" : Fmt.NA),
            new("VRAM", Fmt.GibPair(gpu?.MemoryUsedBytes, gpu?.MemoryTotalBytes)),
            new("GPU 그래픽 / 메모리 클럭", gpu is null ? Fmt.NA : $"{Fmt.Num(gpu.GraphicsClockMhz, "0", "MHz")} / {Fmt.Num(gpu.MemoryClockMhz, "0", "MHz")}"),
            new("GPU 팬", Fmt.Percent(gpu?.FanSpeedPercent, "0")),
            new("클럭 제한 사유", gpu?.ThrottleReasons is ulong mask ? GpuThrottleReasonText.Describe(mask) : Fmt.NA),
            new("CPU 사용률", Fmt.Percent(snapshot.System.CpuUtilizationPercent)),
            new("RAM 사용 / 전체", Fmt.GibPair(snapshot.System.MemoryUsedBytes, snapshot.System.MemoryTotalBytes)),
        };
        foreach (var issue in snapshot.Issues.Take(3))
        {
            rows.Add(new NameValueRow($"센서 문제 ({issue.Scope})", SensorStatusText.ToKorean(issue.Status), issue.Message));
        }

        Replace(Live, rows);
    }

    private (DateTimeOffset From, DateTimeOffset To) CurrentRange()
    {
        if (_focusTime is { } focus)
        {
            var window = TimeSpan.FromMinutes(Context.Settings.Analysis.SensorWindowMinutes);
            return (focus - window, focus + window);
        }

        var now = Context.Now;
        return (now.AddHours(-(SelectedRange.Value == 0 ? 1 : SelectedRange.Value)), now);
    }

    private Task RefreshChartsAsync() => Busy(async () =>
    {
        var (from, to) = CurrentRange();
        var focus = _focusTime;
        var interval = Context.Settings.Sensors.IntervalSeconds;
        var window = TimeSpan.FromMinutes(Context.Settings.Analysis.SensorWindowMinutes);
        var store = Context.Store;
        var data = await Task.Run(() =>
        {
            var series = store.QuerySensors(from, to, maxPoints: 1500);
            var events = store.LoadEventPoints(new EventQuery { From = from, To = to, MinSeverity = DiagSeverity.Warning, Limit = 5000 });
            var context = focus is { } t ? SensorContext.Build(store, t, window) : null;
            return (series, events, context);
        });

        var markers = DiagCharts.Markers(data.events);
        if (focus is { } f)
        {
            markers = [new ChartMarker(DiagCharts.X(f), LocalAiPerf.Presentation.Charts.ChartPalette.PrimaryInk, "선택한 이벤트"), .. markers];
        }

        var gap = Math.Max(interval * 3, data.series.BucketMs / 1000.0 * 2.5);
        PowerChart = DiagCharts.Gpu("GPU 전력", "W", data.series, g => g.PowerW, markers, gap, 0);
        TempChart = DiagCharts.Gpu("GPU 온도", "°C", data.series, g => g.TempC, markers, gap, 0, null, "0");
        UtilChart = DiagCharts.Gpu("GPU 사용률", "%", data.series, g => g.Util, markers, gap, 0, 100, "0");
        ClockChart = DiagCharts.Gpu("GPU 그래픽 클럭", "MHz", data.series, g => g.ClockGfxMhz, markers, gap, 0, null, "0");
        CpuChart = DiagCharts.System("CPU 사용률", "%", data.series, s => s.CpuUtil, markers, gap, 0, 100);
        RamChart = DiagCharts.System("RAM 사용 비율", "%", data.series, s => s.RamUsedPercent, markers, gap, 0, 100);
        ChartNote = (data.series.IsDownsampled ? "기간이 길어 구간 평균으로 표시합니다 (순간 최고값은 낮게 보일 수 있음). " : "")
                    + (markers.Count > 0 ? $"세로선: 경고 이상 이벤트 {data.events.Count}건 (빨강 오류 이상, 노랑 경고)." : "")
                    + " 센서 값은 앱이 실행 중일 때만 기록됩니다.";
        if (data.context is { } ctx)
        {
            FocusNote = ctx.CoverageNote;
            Replace(FocusStats, ctx.Stats.Select(s => new MetricStatsRow($"{s.Name} ({s.Unit})", Fmt.Num(s.BeforeMean), Fmt.Num(s.BeforeMax), Fmt.Num(s.AfterMean),
                Fmt.Num(s.AfterMax), Fmt.Num(s.AtEvent))));
        }
    });

    private async Task LoadInfoAsync()
    {
        var engine = Context.Engine;
        var info = engine.Hardware.Info;
        var latest = await Task.Run(() => Context.Store.ListSnapshots().LastOrDefault());
        var snapshot = latest is null ? null : SystemSnapshot.FromJson(latest.Json);
        var rows = new List<NameValueRow>
        {
            new("CPU", info.CpuName ?? Fmt.NA, $"논리 프로세서 {info.LogicalProcessors}"),
            new("RAM 전체", Fmt.Gib(info.RamTotalBytes)),
            new("GPU", string.Join(", ", info.Gpus.Select(g => g.Name)) is { Length: > 0 } names ? names : Fmt.NA,
                engine.Hardware.Gpu.IsAvailable ? null : engine.Hardware.Gpu.UnavailableReason),
            new("GPU 드라이버 (NVML)", info.GpuDriverVersion ?? Fmt.NA, info.NvmlVersion is { } v ? $"NVML {v}" : null),
            new("운영체제", snapshot?.Os is { } os ? $"{os.DisplayName} {os.DisplayVersion} (빌드 {os.BuildText})" : info.OsDescription),
            new("BIOS/UEFI", snapshot?.Bios is { } b ? $"{b.Vendor} {b.Version} ({b.ReleaseDate})" : Fmt.NA, "레지스트리 읽기 (설정값은 읽지 않음)"),
            new("메인보드", snapshot?.Bios is { } board ? $"{board.BoardManufacturer} {board.BoardProduct} {board.BoardVersion}".Trim() : Fmt.NA),
            new("메모리 모듈", snapshot?.Memory is { Modules.Count: > 0 } m
                ? string.Join(", ", m.Modules.Select(x => $"{x.Slot}: {Fmt.Gib(x.CapacityBytes, "0")} {x.ConfiguredSpeedMts ?? x.SpeedMts} MT/s"))
                : Fmt.NA, "SMBIOS 보고값 (구성 속도·전압은 실측이 아님)"),
            new("관리자 권한", info.IsElevated ? "예" : "아니요 (일반 사용자 권한으로 동작)"),
        };
        Replace(Info, rows);
        Replace(Capabilities, engine.Hardware.Capabilities.Select(c => new CapabilityRow(c.DisplayName, c.Source, SensorStatusText.ToKorean(c.Status),
            c.RequiresElevation ? "필요" : "불필요", c.Detail ?? "")));
        if (engine.Recorder?.Live.Latest is { } snapshotNow)
        {
            // Values only: the charts are refreshed by the activation that runs this load.
            ShowLive(snapshotNow);
        }
    }
}
