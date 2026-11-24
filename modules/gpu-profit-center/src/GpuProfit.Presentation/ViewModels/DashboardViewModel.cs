using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuProfit.Accounting.Analysis;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Core.Domain;
using GpuProfit.Storage;
using LocalAiPerf.Core.Hardware;

namespace GpuProfit.Presentation.ViewModels;

/// <summary>Current devices, recording sessions, and today's / this month's energy, cost and revenue.</summary>
public sealed partial class DashboardViewModel(ViewContext context) : PageViewModel(context)
{
    public const string BoardPowerNote =
        "GPU 전력은 NVML이 보고하는 그래픽카드 보드 전력입니다. CPU·메인보드·메모리·팬·전원공급장치 손실이 빠져 있어 콘센트에서 재는 PC 전체 전력보다 작습니다. "
        + "PC 전체 전력은 외부 전력계 값을 세션에 입력하거나 '시스템 추가 전력 가정'으로 반영하세요.";

    private DateTimeOffset _lastActive;
    private DateTimeOffset _lastTotals;
    private bool _subscribed;

    public override string Title => "대시보드";
    public override string Glyph => "";

    public ObservableCollection<GpuCardRow> Gpus { get; } = [];
    public ObservableCollection<NameValueRow> SystemRows { get; } = [];
    public ObservableCollection<ActiveSessionRow> Active { get; } = [];
    public ObservableCollection<PeriodSummaryRow> Totals { get; } = [];
    public ObservableCollection<Workload> Workloads { get; } = [];

    [ObservableProperty] public partial Workload? SelectedWorkload { get; set; }
    [ObservableProperty] public partial ActiveSessionRow? SelectedActive { get; set; }
    [ObservableProperty] public partial string NewTitle { get; set; } = "";
    [ObservableProperty] public partial string SensorNote { get; set; } = "";
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
    [ObservableProperty] public partial string TotalsNote { get; set; } = "";

    public string PowerNote => BoardPowerNote;

    public override async Task OnActivatedAsync()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            Context.DataChanged += () =>
            {
                if (IsActive)
                {
                    _ = RefreshAsync();
                }
            };
        }

        if (Context.Engine.Recorder?.Live.Latest is { } latest)
        {
            OnSensor(latest);
        }

        await RefreshAsync();
    }

    public Task RefreshAsync() => Busy(async () =>
    {
        var selected = SelectedWorkload?.Id;
        Replace(Workloads, Context.Store.ListWorkloads(includeArchived: false));
        SelectedWorkload = Workloads.FirstOrDefault(w => w.Id == selected) ?? Workloads.FirstOrDefault();
        await RefreshActiveAsync();
        await RefreshTotalsAsync();
        EmptyText = Workloads.Count == 0
            ? "아직 작업이 없습니다. '세션' 화면에서 작업(예: 채굴, AI 추론 서비스)을 만든 뒤 세션을 시작하세요."
            : Context.Store.GetStats().Sessions == 0 ? "아직 기록한 세션이 없습니다. 아래에서 작업을 고르고 '기록 시작'을 누르세요." : "";
    });

    private async Task RefreshActiveAsync()
    {
        _lastActive = Context.Now;
        var open = Context.Sessions.OpenSessions();
        var items = await Context.AccountAsync(open);
        var selected = SelectedActive?.Id;
        Replace(Active, items.Select(i => new ActiveSessionRow(
            i.Session.Id,
            i.Session.Title ?? $"세션 #{i.Session.Id}",
            i.Workload?.Name ?? "",
            SessionStateText.ToKorean(i.Session.State),
            Fmt.Duration(i.Result.RecordedSeconds),
            Fmt.Kwh(i.Result.Energy.TotalKwh),
            Fmt.Krw(i.Result.Electricity.CostKrw),
            Fmt.Percent(i.Result.Energy.Coverage),
            i.Result.ExecutionConfirmed ? "사용자 확인" : i.Result.Energy.GpuActiveShare is double a ? $"GPU 활동 {a:P0} (실행 미확인)" : "실행 근거 없음")));
        SelectedActive = Active.FirstOrDefault(a => a.Id == selected) ?? Active.FirstOrDefault();
    }

    private async Task RefreshTotalsAsync()
    {
        _lastTotals = Context.Now;
        var zone = Fmt.Zone;
        var now = Context.Now;
        var monthStart = PeriodAggregator.PeriodStart(now, PeriodGranularity.Month, zone);
        var dayStart = PeriodAggregator.PeriodStart(now, PeriodGranularity.Day, zone);
        var sessions = Context.Store.ListSessions(new SessionQuery { From = monthStart, Limit = 5000 });
        var items = await Context.AccountAsync(sessions);
        var segments = Context.Store.ListAllSegments();
        var periodItems = items.Select(i => new PeriodItem(i.Session, segments.GetValueOrDefault(i.Session.Id, []), i.Result)).ToList();
        var costs = Context.Store.ListCosts();
        var rates = Context.Store.ListRates();
        var days = PeriodAggregator.Aggregate(periodItems, PeriodGranularity.Day, zone, now, costs, rates);
        var months = PeriodAggregator.Aggregate(periodItems, PeriodGranularity.Month, zone, now, costs, rates);
        var rows = new List<PeriodSummaryRow>
        {
            Summary("오늘", days.FirstOrDefault(b => b.Start == dayStart)),
            Summary("이번 달", months.FirstOrDefault(b => b.Start == monthStart)),
        };
        Replace(Totals, rows);
        TotalsNote = "매출·순수익은 실제 수령분만 반영한 실현 기준입니다. 미수령·예상 수익은 수익성 화면에서 따로 봅니다. 세션 값은 기록 시간 비율로 기간에 나눕니다.";
    }

    private static PeriodSummaryRow Summary(string label, PeriodBucket? b)
    {
        if (b is null || b.Sessions == 0)
        {
            return new PeriodSummaryRow(label, "0", "기록 없음", "-", "-", "-", "-", "이 기간에 기록한 세션이 없습니다");
        }

        var notes = new List<string>();
        if (b.SessionsWithoutEnergy > 0)
        {
            notes.Add($"에너지를 계산할 수 없는 세션 {b.SessionsWithoutEnergy}개 제외");
        }

        if (b.SessionsWithoutRealizedNet > 0)
        {
            notes.Add($"순수익 미정의 세션 {b.SessionsWithoutRealizedNet}개 제외");
        }

        if (b.Coverage is < 0.9)
        {
            notes.Add($"전력 관측률 {b.Coverage:P0}");
        }

        return new PeriodSummaryRow(label, b.Sessions.ToString(System.Globalization.CultureInfo.InvariantCulture), Fmt.Kwh(b.TotalKwh), Fmt.Percent(b.EstimatedShare),
            Fmt.Krw(b.ElectricityCost + b.OtherCost + b.PeriodOneTimeCost), Fmt.Krw(b.RealizedRevenue), Fmt.SignedKrw(Maybe<decimal>.Of(b.RealizedNet)),
            string.Join(" · ", notes));
    }

    public override void OnSensor(HardwareSnapshot snapshot)
    {
        if (!IsActive)
        {
            return;
        }

        var info = Context.Engine.Hardware?.Info;
        Replace(Gpus, snapshot.Gpus.Count == 0
            ? [new GpuCardRow(-1, Context.Engine.Hardware?.Gpu.UnavailableReason ?? "GPU 없음", Fmt.NA, Fmt.NA, Fmt.NA, Fmt.NA, Fmt.NA, "GPU 센서 사용 불가 — 값은 N/A", false)]
            : snapshot.Gpus.Select(g => new GpuCardRow(g.Index, g.Name ?? $"GPU {g.Index}", Fmt.Watts(g.PowerW), Fmt.PercentValue(g.UtilizationPercent), Fmt.Celsius(g.TemperatureC),
                $"{Fmt.Gib(g.MemoryUsedBytes)} / {Fmt.Gib(g.MemoryTotalBytes)}", $"{Fmt.Mhz(g.GraphicsClockMhz)} / {Fmt.Mhz(g.MemoryClockMhz)}",
                g.PowerW is null ? "전력 N/A" : snapshot.IsMock ? "MOCK 가상 값" : "측정 중", g.PowerW is not null)));
        Replace(SystemRows,
        [
            new NameValueRow("CPU", info?.CpuName ?? Fmt.NA, Fmt.PercentValue(snapshot.System.CpuUtilizationPercent)),
            new NameValueRow("시스템 RAM", $"{Fmt.Gib(snapshot.System.MemoryUsedBytes)} / {Fmt.Gib(snapshot.System.MemoryTotalBytes)}", Fmt.Percent(snapshot.System.MemoryUsedPercent / 100)),
            new NameValueRow("측정 시각", Fmt.Time(snapshot.Timestamp), snapshot.IsMock ? "MOCK" : ""),
        ]);
        SensorNote = snapshot.Issues.Count == 0 ? "" : string.Join(" · ", snapshot.Issues.Select(i => $"{i.Scope}: {i.Message}").Distinct().Take(3));
    }

    public override void Tick(DateTimeOffset now)
    {
        if (IsBusy)
        {
            return;
        }

        if (Active.Count > 0 && now - _lastActive >= TimeSpan.FromSeconds(10))
        {
            _ = Busy(RefreshActiveAsync);
        }
        else if (now - _lastTotals >= TimeSpan.FromSeconds(60))
        {
            _ = Busy(RefreshTotalsAsync);
        }
    }

    [RelayCommand]
    private Task Start() => Act(async () =>
    {
        if (SelectedWorkload is null)
        {
            throw new InvalidOperationException("작업을 먼저 고르세요 (작업이 없으면 세션 화면에서 만드세요).");
        }

        var s = await Task.Run(() => Context.Sessions.Start(SelectedWorkload.Id, NewTitle, null));
        NewTitle = "";
        await RefreshActiveAsync();
        StatusText = $"세션 #{s.Id} 기록을 시작했습니다. 기록 버튼만으로 작업이 실제 실행됐다고 보지 않으며, 실행 여부는 세션 화면에서 확인 표시할 수 있습니다.";
    });

    [RelayCommand]
    private Task Pause() => SessionAction(id => Context.Sessions.Pause(id), "일시정지했습니다.");

    [RelayCommand]
    private Task Resume() => SessionAction(id => Context.Sessions.Resume(id), "재개했습니다.");

    [RelayCommand]
    private Task Stop() => SessionAction(id => Context.Sessions.Stop(id), "종료하고 계산 결과를 저장했습니다.");

    [RelayCommand]
    private void OpenAccounting()
    {
        if (SelectedActive is { } a)
        {
            Context.Main?.ShowProfitability(a.Id);
        }
    }

    private Task SessionAction(Action<long> action, string done) => Act(async () =>
    {
        if (SelectedActive is not { } a)
        {
            throw new InvalidOperationException("기록 중인 세션을 고르세요.");
        }

        await Task.Run(() => action(a.Id));
        await RefreshActiveAsync();
        StatusText = $"세션 #{a.Id}: {done}";
    });
}
