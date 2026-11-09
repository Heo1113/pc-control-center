using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Core.Hardware;
using WinDiag.Analysis;
using WinDiag.Core.Events;
using WinDiag.Storage;

namespace WinDiag.Presentation.ViewModels;

/// <summary>Overall state: new problems since the last check, key warnings, live sensors, the error trend and collection status.</summary>
public sealed partial class DashboardViewModel(ViewContext context) : PageViewModel(context)
{
    private DateTimeOffset _lastRefresh;

    public override string Title => "대시보드";
    public override string Glyph => "";

    public ObservableCollection<EventRow> NewEvents { get; } = [];
    public ObservableCollection<NameValueRow> Warnings { get; } = [];
    public ObservableCollection<CrashRow> RecentCrashes { get; } = [];
    public ObservableCollection<ChannelRow> Channels { get; } = [];

    [ObservableProperty] public partial string OverallText { get; set; } = "불러오는 중…";
    [ObservableProperty] public partial StatusLevel OverallLevel { get; set; }
    [ObservableProperty] public partial string Last24h { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string Last7d { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string NewCountText { get; set; } = "";
    [ObservableProperty] public partial ChartData? Trend { get; set; }
    [ObservableProperty] public partial string GpuName { get; set; } = "GPU";
    [ObservableProperty] public partial string GpuUtil { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string GpuTemp { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string GpuPower { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string GpuVram { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string CpuUtil { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string RamUsed { get; set; } = Fmt.NA;
    [ObservableProperty] public partial string SensorNote { get; set; } = "";
    [ObservableProperty] public partial string MeasuredAt { get; set; } = Fmt.NA;

    public override Task OnActivatedAsync() => RefreshAsync();

    public override void Tick(DateTimeOffset now)
    {
        if (IsActive && !IsBusy && now - _lastRefresh > TimeSpan.FromSeconds(30))
        {
            _ = RefreshAsync();
        }
    }

    public override void OnSensor(HardwareSnapshot snapshot)
    {
        var gpu = snapshot.Gpus.FirstOrDefault();
        var engine = Context.Engine;
        GpuName = gpu?.Name ?? engine.Hardware.Info.Gpus.FirstOrDefault()?.Name ?? "GPU";
        GpuUtil = Fmt.Percent(gpu?.UtilizationPercent, "0");
        GpuTemp = Fmt.Num(gpu?.TemperatureC, "0", "°C");
        GpuPower = gpu?.PowerW is double p ? $"{Fmt.Num(p, "0", "W")} / {Fmt.Num(gpu.PowerLimitW, "0", "W")}" : Fmt.NA;
        GpuVram = Fmt.GibPair(gpu?.MemoryUsedBytes, gpu?.MemoryTotalBytes);
        CpuUtil = Fmt.Percent(snapshot.System.CpuUtilizationPercent);
        RamUsed = Fmt.GibPair(snapshot.System.MemoryUsedBytes, snapshot.System.MemoryTotalBytes);
        MeasuredAt = Fmt.Clock(snapshot.Timestamp);
        SensorNote = gpu is null
            ? $"GPU 값 없음 (N/A): {engine.Hardware.Gpu.UnavailableReason ?? "GPU 센서를 읽지 못했습니다."}"
            : snapshot.IsMock ? "MOCK(가상) 센서 값입니다." : "";
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    [RelayCommand]
    private async Task Acknowledge()
    {
        await Task.Run(() => Context.Engine.Acknowledge());
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenEvent(EventRow? row)
    {
        if (row is not null)
        {
            Context.Main?.ShowEvent(row.Id);
        }
    }

    [RelayCommand]
    private void OpenCrash(CrashRow? row)
    {
        if (row is not null && Context.Main is { } main)
        {
            main.CurrentPage = main.Diagnostics;
            _ = main.Diagnostics.SelectIncidentAsync(row.Id);
        }
    }

    public Task RefreshAsync() => Busy(async () =>
    {
        var now = Context.Now;
        _lastRefresh = now;
        var store = Context.Store;
        var data = await Task.Run(() =>
        {
            var points = store.LoadEventPoints(new EventQuery { From = now.AddDays(-30), To = now, Limit = 1_000_000 });
            var ack = store.LastAcknowledgedEventId;
            var fresh = store.QueryEvents(new EventQuery
            {
                AfterId = ack > 0 ? ack : null, From = ack > 0 ? null : now.AddDays(-1), MinSeverity = DiagSeverity.Warning, Limit = 30,
            }).Where(e => EventCategories.Get(e.Category).Kind is TimelineKind.Error or TimelineKind.ProgramCrash or TimelineKind.SystemCrash).ToList();
            var freshCount = store.CountEvents(new EventQuery { AfterId = ack > 0 ? ack : null, From = ack > 0 ? null : now.AddDays(-1), MinSeverity = DiagSeverity.Warning });
            var crashes = CrashAnalyzer.Analyze(store, new AnalysisRange(now.AddDays(-7), now));
            return (points, ack, fresh, freshCount, crashes, channels: store.GetChannelStates());
        });

        static string Counts(IEnumerable<EventPoint> points)
        {
            var problems = points.Where(p => EventCategories.Get(p.Category).Kind is TimelineKind.Error or TimelineKind.ProgramCrash or TimelineKind.SystemCrash).ToList();
            return $"심각 {problems.Count(p => p.Severity == DiagSeverity.Critical)} · 오류 {problems.Count(p => p.Severity == DiagSeverity.Error)} · 경고 {problems.Count(p => p.Severity == DiagSeverity.Warning)}";
        }

        Last24h = Counts(data.points.Where(p => p.Time >= now.AddDays(-1)));
        Last7d = Counts(data.points.Where(p => p.Time >= now.AddDays(-7)));
        NewCountText = data.ack > 0
            ? $"마지막 확인 이후 새 경고·오류 {data.freshCount}건"
            : $"아직 '확인함'을 누른 적이 없습니다. 최근 24시간 경고·오류 {data.freshCount}건";
        Replace(NewEvents, data.fresh.Select(e => EventRow.From(e, data.ack)));

        var week = data.points.Where(p => p.Time >= now.AddDays(-7)).ToList();
        int Count(Func<EventPoint, bool> predicate) => week.Count(predicate);
        var warnings = new List<NameValueRow>
        {
            new("시스템 충돌·비정상 종료", $"{data.crashes.Count}건", data.crashes.Count > 0 ? "진단 화면에서 사건별 상세를 확인하세요." : null),
            new("WHEA 하드웨어 오류", $"{Count(p => p.Provider == EventCatalog.WheaLogger)}건", "수정된 오류도 포함 (반복 빈도가 중요)"),
            new("GPU 드라이버 오류·재설정", $"{Count(p => p.Category == EventCategories.GpuDriver && p.Severity >= DiagSeverity.Warning)}건", null),
            new("응용프로그램 비정상 종료", $"{Count(p => p.Category is EventCategories.AppCrash or EventCategories.AppHang)}건", null),
            new("디스크·스토리지 오류", $"{Count(p => p.Category == EventCategories.Storage)}건", null),
            new("서비스 충돌·실패", $"{Count(p => p.Category == EventCategories.ServiceFailure)}건", null),
        };
        Replace(Warnings, warnings);
        Replace(RecentCrashes, data.crashes.OrderByDescending(c => c.ReferenceTime).Select(DiagnosticsViewModel.ToRow));
        Replace(Channels, data.channels.Select(c => new ChannelRow(c.Channel, ChannelStatusText.Name(c.Status), c.StatusMessage ?? "",
            Fmt.Time(c.LastScan), Fmt.Time(c.OldestEventTime),
            c.Status switch { ChannelStatus.Ok => StatusLevel.Good, ChannelStatus.AccessDenied or ChannelStatus.Error => StatusLevel.Warning, _ => StatusLevel.Neutral })));
        Trend = DiagCharts.DailyCounts(data.points, now.AddDays(-30), now);

        var critical = week.Count(p => p.Severity == DiagSeverity.Critical);
        (OverallText, OverallLevel) = data.crashes.Count > 0 || critical > 0
            ? ($"최근 7일 동안 심각한 문제 기록이 있습니다: 비정상 종료 {data.crashes.Count}건, 심각 이벤트 {critical}건.", StatusLevel.Critical)
            : week.Any(p => p.Severity == DiagSeverity.Error)
                ? ("최근 7일 동안 오류 기록이 있습니다. 반복 여부는 진단 화면에서 확인하세요.", StatusLevel.Warning)
                : ("최근 7일 동안 심각하거나 오류 수준의 기록이 없습니다 (수집된 로그 기준).", StatusLevel.Good);
        StatusText = $"갱신 {Fmt.Clock(now)}";
    });
}
