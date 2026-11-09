using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuProfit.Accounting.Costs;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Core.Domain;
using GpuProfit.Export;
using GpuProfit.Storage;

namespace GpuProfit.Presentation.ViewModels;

/// <summary>
/// Cost and revenue accounting of one session: revenue entries by kind, costs, payout import, the full breakdown with
/// provenance, undefined values with reasons, conditions, and the saved calculation history.
/// </summary>
public sealed partial class ProfitabilityViewModel : PageViewModel
{
    private long? _pendingSelect;
    private bool _loading;
    private bool _subscribed;
    private SessionAccountingResult? _result;
    private WorkSession? _session;

    public ProfitabilityViewModel(ViewContext context) : base(context)
    {
        Views = Enum.GetValues<RevenueView>().Select(v => new Option<RevenueView>(RevenueViewText.ToKorean(v), v)).ToList();
        RevenueKinds = RevenueKindText.All.Select(k => new Option<RevenueKind>(RevenueKindText.ToKorean(k), k)).ToList();
        ImportKinds = [RevenueKinds[(int)RevenueKind.ExternalReported], RevenueKinds[(int)RevenueKind.Received], RevenueKinds[(int)RevenueKind.Pending]];
        CostCategories = CostCategoryText.All.Select(c => new Option<CostCategory>(CostCategoryText.ToKorean(c), c)).ToList();
        CostBases = CostBasisText.All.Select(b => new Option<CostBasis>(CostBasisText.ToKorean(b), b)).ToList();
        CostScopes = [new("이 세션만", 0), new("이 작업의 모든 세션", 1), new("모든 세션", 2)];
        ImportTargets = [new("시각이 들어가는 세션에 자동 배정", 0), new("선택한 세션에 모두 배정", 1)];
        SelectedView = Views[0];
        RevKind = RevenueKinds[0];
        ImportKind = ImportKinds[0];
        SelectedCostCategory = CostCategories[0];
        SelectedCostBasis = CostBases[0];
        CostScope = CostScopes[0];
        ImportTarget = ImportTargets[0];
        RevOccurred = Fmt.InputTime(context.Now);
    }

    public override string Title => "수익성";
    public override string Glyph => "";

    public IReadOnlyList<Option<RevenueView>> Views { get; }
    public IReadOnlyList<Option<RevenueKind>> RevenueKinds { get; }
    public IReadOnlyList<Option<RevenueKind>> ImportKinds { get; }
    public IReadOnlyList<Option<CostCategory>> CostCategories { get; }
    public IReadOnlyList<Option<CostBasis>> CostBases { get; }
    public IReadOnlyList<Option<int>> CostScopes { get; }
    public IReadOnlyList<Option<int>> ImportTargets { get; }

    public ObservableCollection<Option<long>> SessionOptions { get; } = [];
    public ObservableCollection<NameValueRow> Headline { get; } = [];
    public ObservableCollection<BreakdownRow> Breakdown { get; } = [];
    public ObservableCollection<NameValueRow> ByKind { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<NameValueRow> Conditions { get; } = [];
    public ObservableCollection<RevenueRow> Revenue { get; } = [];
    public ObservableCollection<CostRow> Costs { get; } = [];
    public ObservableCollection<ResultHistoryRow> History { get; } = [];

    [ObservableProperty] public partial Option<long>? SelectedSession { get; set; }
    [ObservableProperty] public partial Option<RevenueView> SelectedView { get; set; }
    [ObservableProperty] public partial bool HasSession { get; set; }
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
    [ObservableProperty] public partial string Evidence { get; set; } = "";
    [ObservableProperty] public partial string StaleText { get; set; } = "";

    [ObservableProperty] public partial Option<RevenueKind> RevKind { get; set; }
    [ObservableProperty] public partial string RevAmount { get; set; } = "";
    [ObservableProperty] public partial string RevCurrency { get; set; } = "KRW";
    [ObservableProperty] public partial bool RevGross { get; set; }
    [ObservableProperty] public partial string RevOccurred { get; set; }
    [ObservableProperty] public partial string RevProvider { get; set; } = "";
    [ObservableProperty] public partial string RevNote { get; set; } = "";
    [ObservableProperty] public partial string RevRate { get; set; } = "";
    [ObservableProperty] public partial RevenueRow? SelectedRevenue { get; set; }

    [ObservableProperty] public partial Option<int> CostScope { get; set; }
    [ObservableProperty] public partial Option<CostCategory> SelectedCostCategory { get; set; }
    [ObservableProperty] public partial Option<CostBasis> SelectedCostBasis { get; set; }
    [ObservableProperty] public partial string CostAmount { get; set; } = "";
    [ObservableProperty] public partial string CostCurrency { get; set; } = "KRW";
    [ObservableProperty] public partial string CostNote { get; set; } = "";
    [ObservableProperty] public partial CostRow? SelectedCost { get; set; }

    [ObservableProperty] public partial string ImportProvider { get; set; } = "";
    [ObservableProperty] public partial Option<RevenueKind> ImportKind { get; set; }
    [ObservableProperty] public partial string ImportCurrency { get; set; } = "";
    [ObservableProperty] public partial Option<int> ImportTarget { get; set; }

    public string RevenueNote =>
        "실제 수령: 계좌·지갑에 들어온 금액. 미정산·미수령: 벌었지만 아직 받지 못한 금액. 외부 서비스 보고: 대시보드·명세가 보여 준 금액(수령 확인 전). "
        + "예상(가정): 사용자가 기대한 금액. 확정 손익은 실제 수령분만 씁니다. 수익을 입력하지 않으면 0원이 아니라 '수익 미입력'으로 표시합니다.";

    partial void OnSelectedSessionChanged(Option<long>? value)
    {
        HasSession = value is not null;
        if (value is not null && !_loading)
        {
            _ = Busy(() => LoadAsync(value.Value));
        }
    }

    partial void OnSelectedViewChanged(Option<RevenueView> value)
    {
        if (_result is not null)
        {
            ShowResult();
        }
    }

    public void Select(long sessionId)
    {
        _pendingSelect = sessionId;
        if (IsActive)
        {
            _ = RefreshAsync();
        }
    }

    public override async Task OnActivatedAsync()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            Context.DataChanged += () =>
            {
                if (IsActive && !IsBusy)
                {
                    _ = RefreshAsync();
                }
            };
        }

        await RefreshAsync();
    }

    public Task RefreshAsync() => Busy(async () =>
    {
        var sessions = await Task.Run(() => Context.Store.ListSessions(new SessionQuery { Limit = 2000 }));
        var workloads = Context.Store.ListWorkloads().ToDictionary(w => w.Id);
        var keep = _pendingSelect ?? SelectedSession?.Value;
        _pendingSelect = null;
        _loading = true;
        try
        {
            Replace(SessionOptions, sessions.Select(s => new Option<long>(
                $"#{s.Id} {s.Title ?? ""} · {workloads.GetValueOrDefault(s.WorkloadId)?.Name} · {Fmt.ShortTime(s.Started)} · {SessionStateText.ToKorean(s.State)}{(s.IsMock ? " · MOCK" : "")}", s.Id)));
            SelectedSession = SessionOptions.FirstOrDefault(o => o.Value == keep) ?? SessionOptions.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        EmptyText = SessionOptions.Count == 0 ? "계산할 세션이 없습니다. 세션 화면에서 세션을 기록하거나 직접 입력하세요." : "";
        if (SelectedSession is { } sel)
        {
            await LoadAsync(sel.Value);
        }
    });

    private async Task LoadAsync(long id)
    {
        var (session, result, revenue, costs, history, workload) = await Task.Run(() =>
        {
            var input = Context.Sessions.BuildInput(id);
            return (input.Session, ProfitabilityEngine.Compute(input), input.Revenue, input.Costs, Context.Store.ListResults(id),
                Context.Store.GetWorkload(input.Session.WorkloadId));
        });
        if (SelectedSession?.Value != id)
        {
            return;
        }

        _session = session;
        _result = result;
        ShowResult();
        var converter = new CurrencyConverter(Context.Store.ListRates());
        Replace(Revenue, revenue.Select(e => new RevenueRow(e.Id, RevenueKindText.ToKorean(e.Kind), $"{Fmt.Number(e.Amount)} {e.Currency}{(e.IsGross ? " (수수료 전)" : "")}",
            Fmt.Krw(converter.ToKrw(e.Amount, e.Currency, e.Occurred, e.KrwPerUnit, e.RateAsOf, e.RateSource)), Fmt.ShortTime(e.Occurred), e.Provider ?? "",
            Fmt.Origin(e.Origin), e.Note ?? "")));
        Replace(Costs, costs.Where(c => c.SessionId == id || (c.SessionId is null && (c.WorkloadId is null || c.WorkloadId == session.WorkloadId)))
            .Select(c => new CostRow(c.Id, c.SessionId is not null ? "이 세션" : c.WorkloadId is not null ? $"작업: {workload?.Name}" : "모든 세션",
                CostCategoryText.ToKorean(c.Category), CostBasisText.ToKorean(c.Basis),
                c.Basis == CostBasis.PercentOfRevenue ? $"{Fmt.Number(c.Amount)} %" : $"{Fmt.Number(c.Amount)} {c.Currency}",
                c.Active ? "적용" : "꺼짐", c.Note ?? "")));
        Replace(History, history.Select(h => new ResultHistoryRow(Fmt.Time(h.Computed), h.Reason, Fmt.Kwh(h.Summary.TotalKwh), Fmt.Krw(h.Summary.ElectricityCost),
            h.Summary.RealizedNet is decimal n ? Fmt.SignedKrw(Maybe<decimal>.Of(n)) : Fmt.NA, h.InputsHash[..10],
            string.Join(" · ", h.Conditions.Where(c => c.Key is "전력 산정 기준" or "전기요금").Select(c => c.Value)))));
        var latest = history.FirstOrDefault();
        StaleText = latest is null ? "이 세션의 계산 결과가 아직 저장되지 않았습니다. '계산 저장'을 누르면 조건과 함께 이력에 남습니다."
            : latest.InputsHash != result.InputsHash ? $"마지막 저장({Fmt.Time(latest.Computed)}) 이후 입력이나 조건이 바뀌었습니다. 아래 값은 현재 조건으로 다시 계산한 값입니다. '계산 저장'으로 이력에 남길 수 있습니다."
            : $"저장된 계산({Fmt.Time(latest.Computed)})과 현재 조건이 같습니다.";
    }

    private void ShowResult()
    {
        var r = _result!;
        var v = r.View(SelectedView.Value);
        Evidence = r.ExecutionEvidence;
        Replace(Headline,
        [
            new NameValueRow("매출", Fmt.Krw(v.Revenue, withReason: true), RevenueViewText.ToKorean(v.View)),
            new NameValueRow("전력비", Fmt.Krw(r.Electricity.CostKrw, withReason: true), r.Electricity.IsEstimate ? "추정 (누진 단계)" : "사용자 지정 단가 기준"),
            new NameValueRow("기타 비용", Fmt.Krw(ProfitabilityEngine.Sum(r.OtherCosts.Select(l => l.AmountKrw)), withReason: true), $"{r.OtherCosts.Count}개 항목"),
            new NameValueRow("플랫폼·풀 수수료", Fmt.Krw(v.PlatformFees, withReason: true), $"합계 비율 {Fmt.Number(r.PlatformFeePercent)} %"),
            new NameValueRow("총비용", Fmt.Krw(v.TotalCost, withReason: true)),
            new NameValueRow("순수익", Fmt.SignedKrw(v.NetProfit, withReason: true), v.View == RevenueView.Realized ? "확정" : "확정 아님"),
            new NameValueRow("시간당 순수익", Fmt.KrwPerHour(v.NetPerHour), $"{Fmt.Hours(r.BasisHours)} ({r.HoursSource}){Reason(v.NetPerHour)}"),
            new NameValueRow("에너지당 순수익", Fmt.KrwPerKwh(v.NetPerKwh), $"{Fmt.Kwh(r.Energy.TotalKwh)}{Reason(v.NetPerKwh)}"),
            new NameValueRow("수익률 (순수익 ÷ 매출)", Fmt.Ratio(v.Margin), Reason(v.Margin)),
            new NameValueRow("비용 대비 수익률", Fmt.Ratio(v.ReturnOnCost), Reason(v.ReturnOnCost)),
            new NameValueRow("손익분기 매출", Fmt.Krw(r.BreakevenRevenue, withReason: true), "모든 매출이 수수료 전 금액이라고 볼 때 비용을 덮는 매출"),
            new NameValueRow("손익분기 전력 단가", Fmt.KrwPerKwh(v.BreakevenElectricityRate), $"이 단가 이하이면 이익{Reason(v.BreakevenElectricityRate)}"),
            new NameValueRow("장비 회수 기간", Fmt.Payback(v.PaybackHours), Reason(v.PaybackHours)),
        ]);
        var rows = new List<BreakdownRow>();
        rows.AddRange(r.Energy.Components.Select(c => new BreakdownRow($"에너지: {c.Label}", Fmt.Wh(c.Wh), Fmt.Origin(c.Origin), c.Basis)));
        rows.Add(new BreakdownRow("전력비", Fmt.Krw(r.Electricity.CostKrw, withReason: true), r.Electricity.IsEstimate ? "추정·가정" : "계산", r.Electricity.Basis));
        rows.AddRange(r.OtherCosts.Select(l => new BreakdownRow(l.Label, Fmt.Krw(l.AmountKrw, withReason: true), Fmt.Origin(l.Origin), l.Basis)));
        if (r.PlatformFeePercent > 0)
        {
            rows.Add(new BreakdownRow("플랫폼·풀 수수료", Fmt.Krw(v.PlatformFees, withReason: true), "계산", $"수수료 전 매출 × {Fmt.Number(r.PlatformFeePercent)} %"));
        }

        Replace(Breakdown, rows);
        Replace(ByKind, r.RevenueByKind.Select(k => new NameValueRow(RevenueKindText.ToKorean(k.Kind), k.Count == 0 ? "입력 없음" : Fmt.Krw(k.AmountKrw, withReason: true), $"{k.Count}건")));
        Replace(Warnings, r.Warnings.Concat(r.Energy.Notes).Concat(v.Notes).Distinct());
        Replace(Conditions, r.Conditions.Select(c => new NameValueRow(c.Key, c.Value)));
    }

    private static string Reason<T>(Maybe<T> m) where T : struct => m.HasValue ? "" : $" — {m.Reason}";

    // ------------------------------------------------------------------ revenue

    [RelayCommand]
    private Task AddRevenue() => Act(async () =>
    {
        var s = RequireSession();
        var entry = new RevenueEntry
        {
            SessionId = s.Id,
            WorkloadId = s.WorkloadId,
            Kind = RevKind.Value,
            Amount = ParseMoney(RevAmount, "금액"),
            Currency = CurrencyCodes.Normalize(RevCurrency) ?? throw new ArgumentException("통화 코드를 입력하세요 (예: KRW, USD, BTC)."),
            IsGross = RevGross,
            Occurred = ParseTime(RevOccurred, "발생 시각"),
            Provider = Blank(RevProvider),
            Note = Blank(RevNote),
            Origin = RevKind.Value == RevenueKind.ExternalReported ? ValueOrigin.ExternalReported : RevKind.Value == RevenueKind.Expected ? ValueOrigin.Estimated : ValueOrigin.UserEntered,
            KrwPerUnit = ParseOptionalMoney(RevRate, "원화 환율"),
            RateSource = string.IsNullOrWhiteSpace(RevRate) ? null : "항목에 입력한 환율",
        };
        entry = entry with { RateAsOf = entry.KrwPerUnit is null ? null : entry.Occurred };
        await Task.Run(() => Context.Store.AddRevenue(entry));
        RevAmount = "";
        RevNote = "";
        RevRate = "";
        await SaveAndReload(s.Id, "수익 추가");
    }, "수익 항목을 추가했습니다.");

    [RelayCommand]
    private Task DeleteRevenue() => Act(async () =>
    {
        var s = RequireSession();
        if (SelectedRevenue is not { } row)
        {
            throw new InvalidOperationException("삭제할 수익 항목을 고르세요.");
        }

        if (!Context.Dialogs.Confirm("수익 항목 삭제", $"{row.Kind} {row.Amount} 항목을 삭제할까요?"))
        {
            return;
        }

        await Task.Run(() => Context.Store.DeleteRevenue(row.Id));
        await SaveAndReload(s.Id, "수익 삭제");
    });

    /// <summary>A pending or reported amount that has now arrived.</summary>
    [RelayCommand]
    private Task MarkReceived() => Act(async () =>
    {
        var s = RequireSession();
        if (SelectedRevenue is not { } row || Context.Store.ListRevenue(s.Id).FirstOrDefault(e => e.Id == row.Id) is not { } entry)
        {
            throw new InvalidOperationException("수령 처리할 항목을 고르세요.");
        }

        if (entry.Kind is not (RevenueKind.Pending or RevenueKind.ExternalReported))
        {
            throw new InvalidOperationException("미정산·미수령 또는 외부 보고 항목만 수령으로 바꿀 수 있습니다.");
        }

        await Task.Run(() => Context.Store.UpdateRevenue(entry with { Kind = RevenueKind.Received, Note = $"{entry.Note} (수령 확인 {Fmt.ShortTime(Context.Now)})".Trim() }));
        await SaveAndReload(s.Id, "수령 확인");
    }, "실제 수령으로 바꿨습니다.");

    [RelayCommand]
    private Task Import() => Act(async () =>
    {
        var s = RequireSession();
        var path = Context.Dialogs.OpenFile("지급 내역 CSV 가져오기", "CSV (*.csv)|*.csv|모든 파일 (*.*)|*.*", Context.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        var parsed = await Task.Run(() => PayoutCsvImporter.Parse(path, ImportProvider, ImportKind.Value, Blank(ImportCurrency), Fmt.Zone, Context.Now));
        if (parsed.Entries.Count == 0)
        {
            Context.Dialogs.Error("가져오기", "가져올 행이 없습니다.\n" + string.Join("\n", parsed.Problems.Take(10).Select(p => $"{p.Line}행: {p.Message}")));
            return;
        }

        var entries = ImportTarget.Value == 1
            ? parsed.Entries.Select(e => e with { SessionId = s.Id, WorkloadId = s.WorkloadId }).ToList()
            : PayoutCsvImporter.AssignToSessions(parsed.Entries, Context.Store.ListAllSegments(), Context.Now)
                .Select(e => e.SessionId is long sid ? e with { WorkloadId = Context.Store.GetSession(sid)?.WorkloadId } : e).ToList();
        var (inserted, duplicates) = await Task.Run(() => Context.Store.ImportRevenue(entries));
        var unassigned = entries.Count(e => e.SessionId is null);
        await Task.Run(() => Context.Sessions.RecalculateAll("지급 내역 가져오기"));
        Context.NotifyDataChanged();
        await LoadAsync(s.Id);
        StatusText = $"가져오기: {inserted}건 추가, 중복 {duplicates}건 건너뜀, 문제 {parsed.Problems.Count}행" + (unassigned > 0 ? $", 세션 미배정 {unassigned}건" : "")
                     + $" (공급자 {parsed.Source.Provider}, {parsed.Source.Validity})";
    });

    // ------------------------------------------------------------------ costs

    [RelayCommand]
    private Task AddCost() => Act(async () =>
    {
        var s = RequireSession();
        var basis = SelectedCostBasis.Value;
        if (CostScope.Value != 0 && basis == CostBasis.OneTime)
        {
            throw new ArgumentException("작업·전체에 거는 비용은 시간당, 월 금액, 매출 대비 비율로 입력하세요. 일회성 금액은 '이 세션만'에 넣습니다.");
        }

        var entry = new CostEntry
        {
            SessionId = CostScope.Value == 0 ? s.Id : null,
            WorkloadId = CostScope.Value == 1 ? s.WorkloadId : null,
            Category = basis == CostBasis.PercentOfRevenue ? CostCategory.PlatformFee : SelectedCostCategory.Value,
            Basis = basis,
            Amount = ParseMoney(CostAmount, basis == CostBasis.PercentOfRevenue ? "비율(%)" : "금액"),
            Currency = CurrencyCodes.Normalize(CostCurrency) ?? throw new ArgumentException("통화 코드를 입력하세요."),
            Occurred = Context.Now,
            Note = Blank(CostNote),
        };
        await Task.Run(() => Context.Store.AddCost(entry));
        CostAmount = "";
        CostNote = "";
        if (CostScope.Value == 0)
        {
            await SaveAndReload(s.Id, "비용 추가");
        }
        else
        {
            await Task.Run(() => Context.Sessions.RecalculateAll("공통 비용 추가"));
            Context.NotifyDataChanged();
            await LoadAsync(s.Id);
        }
    }, "비용 항목을 추가했습니다.");

    [RelayCommand]
    private Task ToggleCost() => Act(async () =>
    {
        var s = RequireSession();
        if (SelectedCost is not { } row || Context.Store.ListCosts().FirstOrDefault(c => c.Id == row.Id) is not { } entry)
        {
            throw new InvalidOperationException("비용 항목을 고르세요.");
        }

        await Task.Run(() => Context.Store.UpdateCost(entry with { Active = !entry.Active }));
        await Task.Run(() => Context.Sessions.RecalculateAll("비용 적용 변경"));
        Context.NotifyDataChanged();
        await LoadAsync(s.Id);
    }, "비용 적용 여부를 바꿨습니다.");

    [RelayCommand]
    private Task DeleteCost() => Act(async () =>
    {
        var s = RequireSession();
        if (SelectedCost is not { } row)
        {
            throw new InvalidOperationException("비용 항목을 고르세요.");
        }

        if (!Context.Dialogs.Confirm("비용 항목 삭제", $"'{row.Category} {row.Amount}' 항목을 삭제합니다. 다른 세션에도 걸린 항목이면 그 세션들의 계산도 바뀝니다. 삭제할까요?"))
        {
            return;
        }

        await Task.Run(() => Context.Store.DeleteCost(row.Id));
        await Task.Run(() => Context.Sessions.RecalculateAll("비용 삭제"));
        Context.NotifyDataChanged();
        await LoadAsync(s.Id);
    });

    // ------------------------------------------------------------------ results

    [RelayCommand]
    private Task SaveResult() => Act(async () =>
    {
        var s = RequireSession();
        var (_, changed) = await Task.Run(() => Context.Sessions.Recalculate(s.Id, "사용자 저장"));
        await LoadAsync(s.Id);
        StatusText = changed ? "계산 결과를 조건과 함께 이력에 저장했습니다." : "입력과 조건이 마지막 저장과 같아 새 이력은 만들지 않았습니다.";
    });

    [RelayCommand]
    private Task Export() => Act(async () =>
    {
        var s = RequireSession();
        var path = Context.Dialogs.SaveFile("세션 회계 내보내기", "JSON (*.json)|*.json", $"gpc-session-{s.Id}.json", Context.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        var items = await Context.AccountAsync([s]);
        await Task.Run(() => Exporters.LedgerJson(path, Context.Store, items, Context.Now));
        StatusText = $"내보냈습니다: {Path.GetFileName(path)}";
    });

    private async Task SaveAndReload(long id, string reason)
    {
        await Task.Run(() => Context.Sessions.Recalculate(id, reason));
        Context.NotifyDataChanged();
        await LoadAsync(id);
    }

    private WorkSession RequireSession() => _session ?? throw new InvalidOperationException("세션을 고르세요.");

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
