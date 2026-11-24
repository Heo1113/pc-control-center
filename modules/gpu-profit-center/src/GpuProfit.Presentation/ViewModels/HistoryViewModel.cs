using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuProfit.Accounting.Analysis;
using GpuProfit.Core.Domain;
using GpuProfit.Export;
using GpuProfit.Storage;
using LocalAiPerf.Presentation.Charts;

namespace GpuProfit.Presentation.ViewModels;

public sealed record PeriodDisplayRow(string Period, string Sessions, string Hours, string Measured, string Estimated, string Entered, string Total, string EstimatedShare,
    string Coverage, string Electricity, string OtherCost, string Revenue, string Unconfirmed, string Expected, string Net, string Notes);

/// <summary>Daily / weekly / monthly records (energy by provenance, cost, revenue, net) and the recorded sensor time series.</summary>
public sealed partial class HistoryViewModel : PageViewModel
{
    private IReadOnlyList<PeriodBucket> _buckets = [];

    public HistoryViewModel(ViewContext context) : base(context)
    {
        Granularities = [new("일별", PeriodGranularity.Day), new("주별 (월요일 시작)", PeriodGranularity.Week), new("월별", PeriodGranularity.Month)];
        Ranges = [new("최근 30일", 30), new("최근 90일", 90), new("최근 1년", 365), new("전체", 0)];
        SeriesRanges = [new("최근 24시간", 24), new("최근 7일", 168), new("최근 30일", 720)];
        SelectedGranularity = Granularities[0];
        SelectedRange = Ranges[0];
        SelectedSeriesRange = SeriesRanges[0];
    }

    public override string Title => "기록";
    public override string Glyph => "";

    public IReadOnlyList<Option<PeriodGranularity>> Granularities { get; }
    public IReadOnlyList<Option<int>> Ranges { get; }
    public IReadOnlyList<Option<int>> SeriesRanges { get; }
    public ObservableCollection<Option<long?>> WorkloadFilters { get; } = [];
    public ObservableCollection<PeriodDisplayRow> Periods { get; } = [];
    public ObservableCollection<GapRow> Gaps { get; } = [];

    [ObservableProperty] public partial Option<PeriodGranularity> SelectedGranularity { get; set; }
    [ObservableProperty] public partial Option<int> SelectedRange { get; set; }
    [ObservableProperty] public partial Option<int> SelectedSeriesRange { get; set; }
    [ObservableProperty] public partial Option<long?>? SelectedWorkload { get; set; }
    [ObservableProperty] public partial ChartModel? EnergyChart { get; set; }
    [ObservableProperty] public partial ChartModel? MoneyChart { get; set; }
    [ObservableProperty] public partial ChartModel? PowerChart { get; set; }
    [ObservableProperty] public partial ChartModel? PowerPeakChart { get; set; }
    [ObservableProperty] public partial ChartModel? TempChart { get; set; }
    [ObservableProperty] public partial ChartModel? UtilChart { get; set; }
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
    [ObservableProperty] public partial string SeriesNote { get; set; } = "";

    public string PeriodNote =>
        "세션 값은 각 기간의 기록 시간 비율로 나눕니다(자정을 넘긴 세션은 두 날짜에 나뉨). 순수익은 실제 수령분 기준이며, 순수익을 정할 수 없는 세션(수익 미입력 등)은 합계에서 빠지고 비고에 표시합니다. "
        + "작업·전체에 걸린 일회성 비용은 발생한 기간의 순수익에서 뺍니다.";

    partial void OnSelectedGranularityChanged(Option<PeriodGranularity> value) => Reload();

    partial void OnSelectedRangeChanged(Option<int> value) => Reload();

    partial void OnSelectedWorkloadChanged(Option<long?>? value) => Reload();

    partial void OnSelectedSeriesRangeChanged(Option<int> value)
    {
        if (IsActive)
        {
            _ = Busy(LoadSeriesAsync);
        }
    }

    private void Reload()
    {
        if (IsActive && !IsBusy)
        {
            _ = Busy(LoadPeriodsAsync);
        }
    }

    public override Task OnActivatedAsync() => Busy(async () =>
    {
        var keep = SelectedWorkload?.Value;
        Replace(WorkloadFilters, [new Option<long?>("모든 작업", null), .. Context.Store.ListWorkloads().Select(w => new Option<long?>(w.Name, w.Id))]);
        SelectedWorkload = WorkloadFilters.FirstOrDefault(w => w.Value == keep) ?? WorkloadFilters[0];
        await LoadPeriodsAsync();
        await LoadSeriesAsync();
    });

    private async Task LoadPeriodsAsync()
    {
        var from = SelectedRange.Value > 0 ? Context.Now.AddDays(-SelectedRange.Value) : (DateTimeOffset?)null;
        var sessions = Context.Store.ListSessions(new SessionQuery { From = from, WorkloadId = SelectedWorkload?.Value, Limit = 100_000 });
        var items = await Context.AccountAsync(sessions);
        var segments = Context.Store.ListAllSegments();
        var costs = Context.Store.ListCosts().Where(c => SelectedWorkload?.Value is not long w || c.WorkloadId == w).ToList();
        var granularity = SelectedGranularity.Value;
        _buckets = PeriodAggregator.Aggregate(items.Select(i => new PeriodItem(i.Session, segments.GetValueOrDefault(i.Session.Id, []), i.Result)).ToList(),
            granularity, Fmt.Zone, Context.Now, costs, Context.Store.ListRates());
        if (from is { } f)
        {
            var start = PeriodAggregator.PeriodStart(f, granularity, Fmt.Zone);
            _buckets = _buckets.Where(b => b.Start >= start).ToList();
        }

        Replace(Periods, _buckets.OrderByDescending(b => b.Start).Select(b =>
        {
            var notes = new List<string>();
            if (b.SessionsWithoutEnergy > 0) notes.Add($"에너지 미정 {b.SessionsWithoutEnergy}");
            if (b.SessionsWithoutRealizedNet > 0) notes.Add($"순수익 미정 {b.SessionsWithoutRealizedNet}");
            if (b.PeriodOneTimeCost > 0) notes.Add($"기간 일회성 비용 {Fmt.Krw(b.PeriodOneTimeCost)}");
            return new PeriodDisplayRow(b.Label, b.Sessions.ToString(System.Globalization.CultureInfo.InvariantCulture), Fmt.Hours(b.Hours), Fmt.Kwh(b.MeasuredKwh),
                Fmt.Kwh(b.EstimatedKwh), Fmt.Kwh(b.UserEnteredKwh), Fmt.Kwh(b.TotalKwh), Fmt.Percent(b.EstimatedShare), Fmt.Percent(b.Coverage), Fmt.Krw(b.ElectricityCost),
                Fmt.Krw(b.OtherCost + b.PeriodOneTimeCost), Fmt.Krw(b.RealizedRevenue), Fmt.Krw(b.UnconfirmedRevenue), Fmt.Krw(b.ExpectedRevenue),
                Fmt.SignedKrw(Maybe<decimal>.Of(b.RealizedNet)), string.Join(" · ", notes));
        }));
        EnergyChart = Charts.PeriodLines("기간별 에너지", "kWh", _buckets, ("실측", b => b.MeasuredKwh), ("추정·가정", b => b.EstimatedKwh), ("사용자 입력(전력계)", b => b.UserEnteredKwh));
        MoneyChart = Charts.PeriodLines("기간별 비용·수익", "원", _buckets, ("총비용", b => (double)(b.ElectricityCost + b.OtherCost + b.PeriodOneTimeCost)),
            ("실현 매출", b => (double)b.RealizedRevenue), ("실현 순수익", b => (double)b.RealizedNet));
        EmptyText = _buckets.Count == 0 ? "이 기간에 기록된 세션이 없습니다." : "";
        StatusText = $"{_buckets.Count}개 기간, 세션 {items.Count}개";
    }

    private async Task LoadSeriesAsync()
    {
        var hours = SelectedSeriesRange.Value;
        var to = Context.Now;
        var from = to.AddHours(-hours);
        var bucket = hours <= 24 ? TimeSpan.FromMinutes(1) : hours <= 168 ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(30);
        var mock = Context.Engine.IsMock ? (bool?)null : false;
        var (gpu, gaps) = await Task.Run(() => (Context.Store.QueryGpuSeries(from, to, bucket, mock), Context.Store.ListGaps(from, to, 300)));
        PowerChart = Charts.GpuHistory("GPU 보드 전력 (평균)", "W", gpu, p => p.PowerW, bucket);
        PowerPeakChart = Charts.GpuHistory("GPU 보드 전력 (구간 최대)", "W", gpu, p => p.PowerMaxW, bucket);
        TempChart = Charts.WithMinimumSpan(Charts.GpuHistory("GPU 온도 (평균)", "°C", gpu, p => p.TemperatureC, bucket, "0"), 10);
        UtilChart = Charts.GpuHistory("GPU 사용률 (평균)", "%", gpu, p => p.Utilization, bucket, "0");
        SeriesNote = $"{bucket.TotalMinutes:0}분 단위 평균. 기록이 없는 구간은 선을 잇지 않습니다." + (mock is null ? " (MOCK 값 포함)" : " (실제 센서 값만)");
        Replace(Gaps, gaps.Select(g => new GapRow(Fmt.Time(g.From ?? g.Detected), Fmt.Time(g.To), g.From is { } f && g.To is { } t ? Fmt.Duration((t - f).TotalSeconds) : "",
            GapKinds.ToKorean(g.Kind), g.Detail ?? "")));
    }

    [RelayCommand]
    private Task Export() => Act(async () =>
    {
        if (_buckets.Count == 0)
        {
            throw new InvalidOperationException("내보낼 기간이 없습니다.");
        }

        var path = Context.Dialogs.SaveFile("기간별 기록 내보내기", "CSV (*.csv)|*.csv", $"gpc-periods-{SelectedGranularity.Value.ToString().ToLowerInvariant()}.csv", Context.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        var buckets = _buckets;
        await Task.Run(() => Exporters.PeriodsCsv(path, buckets));
        StatusText = $"내보냈습니다: {Path.GetFileName(path)}";
    });
}
