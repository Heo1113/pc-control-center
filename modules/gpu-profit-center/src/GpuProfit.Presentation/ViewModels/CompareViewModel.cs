using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuProfit.Accounting.Analysis;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Core.Domain;
using GpuProfit.Export;
using GpuProfit.Storage;
using LocalAiPerf.Presentation.Charts;

namespace GpuProfit.Presentation.ViewModels;

public sealed partial class CompareCandidate(long id, string label) : ObservableObject
{
    public long Id { get; } = id;
    public string Label { get; } = label;

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

public sealed record CompareDisplayRow(string Session, string Workload, string Started, string Hours, string Energy, string AvgPower, string Coverage,
    string Revenue, string Cost, string Net, string NetPerHour, string NetPerKwh, string CostPerHour, string Basis, string Rate, string Flags);

public sealed record GroupDisplayRow(string Group, string Sessions, string Hours, string Energy, string Revenue, string Cost, string Net, string NetPerHour, string NetPerKwh);

public sealed record ActualExpectedDisplayRow(string Session, string Actual, string Expected, string Difference, string Ratio);

/// <summary>Session-to-session, workload-type, time-of-day and actual-vs-expected comparison, with warnings when conditions differ.</summary>
public sealed partial class CompareViewModel : PageViewModel
{
    private List<AccountedSession> _last = [];

    public CompareViewModel(ViewContext context) : base(context)
    {
        Views = Enum.GetValues<RevenueView>().Select(v => new Option<RevenueView>(RevenueViewText.ToKorean(v), v)).ToList();
        Ranges = [new("최근 30일", 30), new("최근 90일", 90), new("최근 1년", 365), new("전체", 0)];
        SelectedView = Views[0];
        SelectedRange = Ranges[0];
    }

    public override string Title => "비교";
    public override string Glyph => "";

    public IReadOnlyList<Option<RevenueView>> Views { get; }
    public IReadOnlyList<Option<int>> Ranges { get; }
    public ObservableCollection<CompareCandidate> Candidates { get; } = [];
    public ObservableCollection<CompareDisplayRow> Rows { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<GroupDisplayRow> ByType { get; } = [];
    public ObservableCollection<GroupDisplayRow> ByWorkload { get; } = [];
    public ObservableCollection<GroupDisplayRow> ByTimeOfDay { get; } = [];
    public ObservableCollection<ActualExpectedDisplayRow> ActualVsExpected { get; } = [];

    [ObservableProperty] public partial Option<RevenueView> SelectedView { get; set; }
    [ObservableProperty] public partial Option<int> SelectedRange { get; set; }
    [ObservableProperty] public partial ChartModel? NetChart { get; set; }
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial string EmptyText { get; set; } = "";

    partial void OnSelectedRangeChanged(Option<int> value)
    {
        if (IsActive)
        {
            _ = LoadCandidatesAsync();
        }
    }

    partial void OnSelectedViewChanged(Option<RevenueView> value)
    {
        if (_last.Count > 0)
        {
            Show(_last);
        }
    }

    public override Task OnActivatedAsync() => LoadCandidatesAsync();

    public Task LoadCandidatesAsync() => Busy(async () =>
    {
        var from = SelectedRange.Value > 0 ? Context.Now.AddDays(-SelectedRange.Value) : (DateTimeOffset?)null;
        var sessions = await Task.Run(() => Context.Store.ListSessions(new SessionQuery { From = from, Limit = 1000 }).Where(s => !s.IsOpen).ToList());
        var workloads = Context.Store.ListWorkloads().ToDictionary(w => w.Id);
        var selected = Candidates.Where(c => c.IsSelected).Select(c => c.Id).ToHashSet();
        Replace(Candidates, sessions.Select(s => new CompareCandidate(s.Id,
            $"#{s.Id} {s.Title} · {workloads.GetValueOrDefault(s.WorkloadId)?.Name} · {Fmt.ShortTime(s.Started)}{(s.IsMock ? " · MOCK" : "")}")
        {
            IsSelected = selected.Count == 0 || selected.Contains(s.Id),
        }));
        EmptyText = Candidates.Count == 0 ? "이 기간에 끝난 세션이 없습니다. 비교하려면 세션을 두 개 이상 기록하세요." : "";
    });

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var c in Candidates)
        {
            c.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var c in Candidates)
        {
            c.IsSelected = false;
        }
    }

    [RelayCommand]
    private Task Compare() => Act(async () =>
    {
        var ids = Candidates.Where(c => c.IsSelected).Select(c => c.Id).ToHashSet();
        if (ids.Count == 0)
        {
            throw new InvalidOperationException("비교할 세션을 고르세요.");
        }

        var sessions = Context.Store.ListSessions(new SessionQuery { Limit = 100_000 }).Where(s => ids.Contains(s.Id));
        _last = await Context.AccountAsync(sessions);
        Show(_last);
        StatusText = $"세션 {_last.Count}개를 같은 계산 엔진으로 비교했습니다.";
    });

    private void Show(IReadOnlyList<AccountedSession> items)
    {
        var view = SelectedView.Value;
        var (rows, warnings) = SessionComparer.Compare(items, view);
        Replace(Rows, rows.Select(r =>
        {
            var flags = new List<string>();
            if (r.IsMock) flags.Add("MOCK");
            flags.Add(r.ExecutionConfirmed ? "실행 확인" : "실행 미확인");
            return new CompareDisplayRow($"#{r.SessionId} {r.Label}", r.WorkloadName, Fmt.ShortTime(r.Started), Fmt.Hours(r.Hours), Fmt.Kwh(r.Kwh), Fmt.Watts(r.AveragePowerW),
                Fmt.Percent(r.Coverage), Fmt.Krw(r.Revenue), Fmt.Krw(r.TotalCost), Fmt.SignedKrw(r.NetProfit), Fmt.KrwPerHour(r.NetPerHour), Fmt.KrwPerKwh(r.NetPerKwh),
                Fmt.KrwPerHour(r.CostPerHour), r.EnergyBasis, Fmt.KrwPerKwh(r.ElectricityRate), string.Join(", ", flags));
        }));
        Replace(Warnings, warnings.Count == 0 && items.Count >= 2 ? ["비교 조건(전력 산정 기준·단가·센서 종류)이 같습니다."] : warnings);
        Replace(ByType, SessionComparer.ByWorkloadType(items, view).Select(Group));
        Replace(ByWorkload, SessionComparer.ByWorkload(items, view).Select(Group));
        Replace(ByTimeOfDay, SessionComparer.ByTimeOfDay(items, view, Fmt.Zone).Select(Group));
        Replace(ActualVsExpected, SessionComparer.ActualVsExpected(items).Select(a => new ActualExpectedDisplayRow($"#{a.SessionId} {a.Label}", Fmt.Krw(a.Actual),
            Fmt.Krw(a.Expected), Fmt.SignedKrw(a.Difference), Fmt.Ratio(a.Ratio))));
        NetChart = Charts.NetPerHourBySession(rows);
        HasResult = items.Count > 0;
    }

    private static GroupDisplayRow Group(GroupRow g) => new(g.Key, $"{g.DefinedSessions} / {g.Sessions}", Fmt.Hours(g.Hours), Fmt.Kwh(g.Kwh), Fmt.Krw(g.Revenue),
        Fmt.Krw(g.TotalCost), Fmt.SignedKrw(Maybe<decimal>.Of(g.NetProfit)), Fmt.KrwPerHour(g.NetPerHour), Fmt.KrwPerKwh(g.NetPerKwh));

    [RelayCommand]
    private Task Export() => Act(async () =>
    {
        if (_last.Count == 0)
        {
            throw new InvalidOperationException("먼저 비교를 실행하세요.");
        }

        var path = Context.Dialogs.SaveFile("비교 결과 내보내기", "CSV (*.csv)|*.csv", $"gpc-compare-{Context.Now:yyyyMMdd-HHmmss}.csv", Context.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        var items = _last;
        var view = SelectedView.Value;
        await Task.Run(() => Exporters.SessionsCsv(path, items, view));
        StatusText = $"내보냈습니다: {Path.GetFileName(path)}";
    });
}
