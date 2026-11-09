using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuProfit.Accounting.Analysis;
using GpuProfit.Accounting.Energy;
using GpuProfit.Accounting.Profitability;
using GpuProfit.Core.Domain;
using GpuProfit.Export;
using GpuProfit.Storage;
using LocalAiPerf.Presentation.Charts;

namespace GpuProfit.Presentation.ViewModels;

/// <summary>Work items, session recording (start/pause/resume/stop/abort), manual sessions, search, editing and session detail.</summary>
public sealed partial class SessionsViewModel : PageViewModel
{
    private long? _pendingSelect;
    private bool _loading;
    private bool _subscribed;
    private AccountedSession? _detail;

    public SessionsViewModel(ViewContext context) : base(context)
    {
        Types = WorkloadTypeText.All.Select(t => new Option<WorkloadType>(WorkloadTypeText.ToKorean(t), t)).ToList();
        TypeFilters = [new("모든 유형", null), .. WorkloadTypeText.All.Select(t => new Option<WorkloadType?>(WorkloadTypeText.ToKorean(t), t))];
        StateFilters = [new("모든 상태", null), .. Enum.GetValues<SessionState>().Select(s => new Option<SessionState?>(SessionStateText.ToKorean(s), s))];
        Ranges = [new("전체 기간", 0), new("최근 7일", 7), new("최근 30일", 30), new("최근 90일", 90), new("최근 1년", 365)];
        Bases = [new("회계 조건 기본값 사용", null), .. EnergyBasisText.All.Select(b => new Option<EnergyBasis?>(EnergyBasisText.ToKorean(b), b))];
        Policies = [new("회계 조건 기본값 사용", null), new("미관측 구간 제외 (보간 안 함)", UnobservedPolicy.Exclude), new("관측 평균으로 추정해 별도 표시", UnobservedPolicy.EstimateFromObservedAverage)];
        NewWorkloadType = Types[0];
        FilterType = TypeFilters[0];
        FilterState = StateFilters[0];
        FilterRange = Ranges[0];
        EditBasis = Bases[0];
        EditPolicy = Policies[0];
        ManualStart = Fmt.InputTime(context.Now.AddHours(-2));
        ManualEnd = Fmt.InputTime(context.Now);
    }

    public override string Title => "세션";
    public override string Glyph => "";

    public IReadOnlyList<Option<WorkloadType>> Types { get; }
    public IReadOnlyList<Option<WorkloadType?>> TypeFilters { get; }
    public IReadOnlyList<Option<SessionState?>> StateFilters { get; }
    public IReadOnlyList<Option<int>> Ranges { get; }
    public IReadOnlyList<Option<EnergyBasis?>> Bases { get; }
    public IReadOnlyList<Option<UnobservedPolicy?>> Policies { get; }

    public ObservableCollection<Workload> Workloads { get; } = [];
    public ObservableCollection<Option<long?>> WorkloadFilters { get; } = [];
    public ObservableCollection<SessionRow> Sessions { get; } = [];
    public ObservableCollection<SegmentRow> Segments { get; } = [];
    public ObservableCollection<GapRow> Gaps { get; } = [];
    public ObservableCollection<DeviceEnergyRow> Devices { get; } = [];
    public ObservableCollection<BreakdownRow> EnergyComponents { get; } = [];
    public ObservableCollection<NameValueRow> Summary { get; } = [];

    // ---- work items
    [ObservableProperty] public partial Workload? SelectedWorkload { get; set; }
    [ObservableProperty] public partial string NewWorkloadName { get; set; } = "";
    [ObservableProperty] public partial Option<WorkloadType> NewWorkloadType { get; set; }
    [ObservableProperty] public partial string NewWorkloadNotes { get; set; } = "";

    // ---- filters
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial Option<long?>? FilterWorkload { get; set; }
    [ObservableProperty] public partial Option<WorkloadType?> FilterType { get; set; }
    [ObservableProperty] public partial Option<SessionState?> FilterState { get; set; }
    [ObservableProperty] public partial Option<int> FilterRange { get; set; }

    // ---- start and manual entry
    [ObservableProperty] public partial string StartTitle { get; set; } = "";
    [ObservableProperty] public partial string StartNotes { get; set; } = "";
    [ObservableProperty] public partial string StartGpus { get; set; } = "";
    [ObservableProperty] public partial string ManualTitle { get; set; } = "";
    [ObservableProperty] public partial string ManualStart { get; set; }
    [ObservableProperty] public partial string ManualEnd { get; set; }
    [ObservableProperty] public partial string ManualWallKwh { get; set; } = "";

    // ---- selected session
    [ObservableProperty] public partial SessionRow? SelectedSession { get; set; }
    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial string DetailTitle { get; set; } = "";
    [ObservableProperty] public partial string Evidence { get; set; } = "";
    [ObservableProperty] public partial string EnergyNotes { get; set; } = "";
    [ObservableProperty] public partial ChartModel? PowerChart { get; set; }
    [ObservableProperty] public partial string EditTitle { get; set; } = "";
    [ObservableProperty] public partial string EditNotes { get; set; } = "";
    [ObservableProperty] public partial bool EditConfirmed { get; set; }
    [ObservableProperty] public partial bool EditNoRevenue { get; set; }
    [ObservableProperty] public partial Option<EnergyBasis?> EditBasis { get; set; }
    [ObservableProperty] public partial Option<UnobservedPolicy?> EditPolicy { get; set; }
    [ObservableProperty] public partial string EditExtraW { get; set; } = "";
    [ObservableProperty] public partial string EditIdleW { get; set; } = "";
    [ObservableProperty] public partial string EditRate { get; set; } = "";
    [ObservableProperty] public partial string EditWallKwh { get; set; } = "";
    [ObservableProperty] public partial string EditWorkHours { get; set; } = "";
    [ObservableProperty] public partial string EditGpus { get; set; } = "";
    [ObservableProperty] public partial string EmptyText { get; set; } = "";

    public string ExecutionNote =>
        "세션은 사용자가 기록을 켠 구간입니다. 이 앱은 채굴기·추론 서버 같은 작업 프로그램이 실제로 돌았는지 직접 확인하지 않습니다. "
        + "작업이 실행된 것을 확인했다면 '작업 실행 확인'을 체크하세요. 확인하지 않은 세션은 GPU 사용률만 참고로 보여 줍니다.";

    partial void OnSelectedSessionChanged(SessionRow? value)
    {
        HasSelection = value is not null;
        if (value is not null && !_loading)
        {
            _ = Busy(() => LoadDetailAsync(value.Id));
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
        var selectedWorkload = SelectedWorkload?.Id;
        var filterWorkload = FilterWorkload?.Value;
        Replace(Workloads, Context.Store.ListWorkloads());
        SelectedWorkload = Workloads.FirstOrDefault(w => w.Id == selectedWorkload) ?? Workloads.FirstOrDefault(w => !w.Archived);
        _loading = true;
        try
        {
            Replace(WorkloadFilters, [new Option<long?>("모든 작업", null), .. Workloads.Select(w => new Option<long?>(w.Name + (w.Archived ? " (보관됨)" : ""), w.Id))]);
            FilterWorkload = WorkloadFilters.FirstOrDefault(f => f.Value == filterWorkload) ?? WorkloadFilters[0];
        }
        finally
        {
            _loading = false;
        }

        await SearchCoreAsync();
    });

    [RelayCommand]
    private Task Search() => Busy(SearchCoreAsync);

    private async Task SearchCoreAsync()
    {
        var query = new SessionQuery
        {
            Text = SearchText,
            WorkloadId = FilterWorkload?.Value,
            Type = FilterType?.Value,
            State = FilterState?.Value,
            From = FilterRange?.Value is > 0 ? Context.Now.AddDays(-FilterRange.Value) : null,
            Limit = 2000,
        };
        var (sessions, results, workloads) = await Task.Run(() => (
            Context.Store.ListSessions(query),
            Context.Store.ListResults(latestOnly: true).ToDictionary(r => r.SessionId),
            Context.Store.ListWorkloads().ToDictionary(w => w.Id)));
        var keep = _pendingSelect ?? SelectedSession?.Id;
        _pendingSelect = null;
        _loading = true;
        try
        {
            Replace(Sessions, sessions.Select(s =>
            {
                var r = results.GetValueOrDefault(s.Id)?.Summary;
                var w = workloads.GetValueOrDefault(s.WorkloadId);
                var flags = new List<string>();
                if (s.IsMock) flags.Add("MOCK");
                if (s.Source == SessionSource.Manual) flags.Add("직접 입력");
                flags.Add(s.ExecutionConfirmed ? "실행 확인" : "실행 미확인");
                if (r?.EstimatedShare > 0) flags.Add($"추정 {r.EstimatedShare:P0}");
                return new SessionRow(s.Id, s.Title ?? $"세션 #{s.Id}", w?.Name ?? "", w is null ? "" : WorkloadTypeText.ToKorean(w.Type), SessionStateText.ToKorean(s.State),
                    Fmt.ShortTime(s.Started), s.IsOpen ? "기록 중" : Fmt.Hours(r?.BasisHours), s.IsOpen ? "-" : Fmt.Kwh(r?.TotalKwh),
                    s.IsOpen ? "-" : r?.RealizedNet is decimal n ? Fmt.SignedKrw(Maybe<decimal>.Of(n)) : Fmt.NA, string.Join(", ", flags), s.IsOpen, s.IsMock);
            }));

            // Selected inside the loading block: the detail is loaded once, below, not again by the selection change.
            SelectedSession = Sessions.FirstOrDefault(s => s.Id == keep) ?? Sessions.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        HasSelection = SelectedSession is not null;
        EmptyText = Sessions.Count > 0 ? ""
            : Workloads.Count == 0 ? "작업이 없습니다. 왼쪽에서 작업을 만든 뒤 세션을 시작하거나 직접 입력하세요."
            : "조건에 맞는 세션이 없습니다.";
        StatusText = $"세션 {Sessions.Count}개";
        if (SelectedSession is { } sel)
        {
            await LoadDetailAsync(sel.Id);
        }
        else
        {
            ClearDetail();
        }
    }

    private void ClearDetail()
    {
        _detail = null;
        DetailTitle = "";
        Replace(Segments, []);
        Replace(Gaps, []);
        Replace(Devices, []);
        Replace(EnergyComponents, []);
        Replace(Summary, []);
        PowerChart = null;
    }

    private async Task LoadDetailAsync(long id)
    {
        var (detail, samples, segments, gaps) = await Task.Run(() =>
        {
            var input = Context.Sessions.BuildInput(id);
            var result = ProfitabilityEngine.Compute(input);
            var w = Context.Store.GetWorkload(input.Session.WorkloadId);
            var segs = input.Segments;
            var from = segs.Count > 0 ? segs.Min(g => g.Start) : input.Session.Started;
            var to = segs.Count > 0 ? segs.Max(g => g.End ?? input.Now) : input.Now;
            return (new AccountedSession(input.Session, w, result), input.Samples, segs, Context.Store.ListGaps(from, to, 200));
        });
        if (SelectedSession?.Id != id)
        {
            return;
        }

        _detail = detail;
        var s = detail.Session;
        var r = detail.Result;
        DetailTitle = $"#{s.Id} {s.Title} — {detail.Workload?.Name} · {SessionStateText.ToKorean(s.State)}{(s.IsMock ? " · MOCK" : "")}";
        Evidence = r.ExecutionEvidence;
        EnergyNotes = string.Join(" ", r.Energy.Notes.Concat(r.Warnings).Distinct());
        Replace(Segments, segments.Select(g => new SegmentRow(Fmt.Time(g.Start), g.End is null ? "기록 중" : Fmt.Time(g.End),
            Fmt.Duration(g.Duration(Context.Now).TotalSeconds), EndReasonText(g.EndReason))));
        Replace(Gaps, r.Energy.Devices.SelectMany(d => d.Gaps.Select(g => new GapRow(Fmt.Time(DateTimeOffset.FromUnixTimeMilliseconds(g.FromMs)),
                Fmt.Time(DateTimeOffset.FromUnixTimeMilliseconds(g.ToMs)), Fmt.Duration(g.Seconds), $"GPU {d.DeviceIndex}: {GapReasons.ToKorean(g.Reason)}", "에너지 보간 안 함")))
            .Concat(gaps.Select(g => new GapRow(Fmt.Time(g.From), Fmt.Time(g.To), g.From is { } f && g.To is { } t ? Fmt.Duration((t - f).TotalSeconds) : "",
                GapKinds.ToKorean(g.Kind), g.Detail ?? "")))
            .Take(300));
        var windows = segments.Select(g => TimeWindow.Of(g.Start, g.End ?? Context.Now)).ToList();
        Replace(Devices, r.Energy.Devices.Select(d => (d, stats: PowerStatistics.ForDevice(samples, windows, d.DeviceIndex))).Select(x => (x.d, x.stats)).Select(t => new DeviceEnergyRow($"GPU {t.d.DeviceIndex}",
            Fmt.Wh(t.d.MeasuredWh), Fmt.Duration(t.d.ObservedSeconds), Fmt.Percent(t.d.Coverage, 1), Fmt.Watts(t.d.AveragePowerW),
            $"{Fmt.Watts(t.d.MinPowerW)} / {Fmt.Watts(t.d.MaxPowerW)}", $"{Fmt.Watts(t.stats.P50)} / {Fmt.Watts(t.stats.P95)}", $"{t.d.ValidPowerSamples:N0} / {t.d.SampleCount:N0}",
            t.d.CounterWh is double c ? $"{Fmt.Wh(c)}{(r.Energy.CounterDifference is double diff ? $" (적분 대비 {diff:+0.0%;-0.0%})" : "")}" : "카운터 없음",
            t.d.LongestGapSeconds is double lg ? Fmt.Duration(lg) : "없음")));
        Replace(EnergyComponents, r.Energy.Components.Select(c => new BreakdownRow(c.Label, Fmt.Wh(c.Wh), Fmt.Origin(c.Origin), c.Basis)));
        Replace(Summary,
        [
            new NameValueRow("기록 시간", Fmt.Duration(r.RecordedSeconds), $"일시정지 {Fmt.Duration(r.PausedSeconds)}, 구간 {r.SegmentCount}개"),
            new NameValueRow("시간 기준", Fmt.Hours(r.BasisHours), r.HoursSource),
            new NameValueRow("전력 산정 기준", EnergyBasisText.ToKorean(r.Energy.Conditions.Basis)),
            new NameValueRow("총 에너지", Fmt.Kwh(r.Energy.TotalKwh, withReason: true), $"실측 {Fmt.Percent(r.Energy.MeasuredShare)} · 추정 {Fmt.Percent(r.Energy.EstimatedShare)} · 사용자 입력 {Fmt.Percent(r.Energy.UserEnteredShare)}"),
            new NameValueRow("GPU 실측 에너지", Fmt.Wh(r.Energy.GpuMeasuredWh), $"관측률 {Fmt.Percent(r.Energy.Coverage, 1)}"),
            new NameValueRow("전력비", Fmt.Krw(r.Electricity.CostKrw, withReason: true), r.Electricity.Basis),
            new NameValueRow("실현 순수익", Fmt.SignedKrw(r.View(RevenueView.Realized).NetProfit, withReason: true), "실제 수령분만 반영"),
        ]);
        PowerChart = Charts.PowerSamples("세션 GPU 보드 전력", samples, r.Energy.Conditions.MaxSampleGapSeconds,
            segments.Count > 0 ? segments.Min(g => g.Start) : null, segments.Count > 0 ? segments.Max(g => g.End ?? Context.Now) : null);

        EditTitle = s.Title ?? "";
        EditNotes = s.Notes ?? "";
        EditConfirmed = s.ExecutionConfirmed;
        EditNoRevenue = s.NoRevenueConfirmed;
        EditBasis = Bases.FirstOrDefault(b => b.Value == s.EnergyBasis) ?? Bases[0];
        EditPolicy = Policies.FirstOrDefault(p => p.Value == s.UnobservedPolicy) ?? Policies[0];
        EditExtraW = Fmt.Number(s.ExtraSystemPowerW);
        EditIdleW = Fmt.Number(s.IdleGpuPowerW);
        EditRate = s.ElectricityRateKrwPerKwh is decimal rate ? Fmt.Number(rate) : "";
        EditWallKwh = Fmt.Number(s.WallMeterKwh, "0.######");
        EditWorkHours = Fmt.Number(s.WorkHoursOverride, "0.####");
        EditGpus = s.DeviceIndices is { Count: > 0 } d ? string.Join(",", d) : "";
    }

    private static string EndReasonText(string? reason) => reason switch
    {
        null => "",
        SegmentEndReasons.Paused => "일시정지",
        SegmentEndReasons.Stopped => "종료",
        SegmentEndReasons.Aborted => "중단",
        SegmentEndReasons.AppExit => "앱 종료로 일시정지",
        SegmentEndReasons.RecoveredAfterCrash => "비정상 종료 — 마지막 기록에서 닫음",
        SegmentEndReasons.WindowsSessionEnd => "Windows 로그오프·종료",
        SegmentEndReasons.Manual => "직접 입력",
        _ => reason,
    };

    // ------------------------------------------------------------------ work items

    [RelayCommand]
    private Task AddWorkload() => Act(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewWorkloadName))
        {
            throw new ArgumentException("작업 이름을 입력하세요.");
        }

        if (Workloads.Any(w => string.Equals(w.Name, NewWorkloadName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("같은 이름의 작업이 이미 있습니다.");
        }

        var id = Context.Store.AddWorkload(new Workload { Name = NewWorkloadName.Trim(), Type = NewWorkloadType.Value, Notes = Blank(NewWorkloadNotes) });
        NewWorkloadName = "";
        NewWorkloadNotes = "";
        await RefreshAsync();
        SelectedWorkload = Workloads.FirstOrDefault(w => w.Id == id);
        Context.NotifyDataChanged();
    }, "작업을 추가했습니다.");

    [RelayCommand]
    private Task ToggleArchive() => Act(async () =>
    {
        if (SelectedWorkload is not { } w)
        {
            throw new InvalidOperationException("작업을 고르세요.");
        }

        Context.Store.UpdateWorkload(w with { Archived = !w.Archived });
        await RefreshAsync();
        Context.NotifyDataChanged();
    }, "작업 보관 상태를 바꿨습니다 (기록은 삭제하지 않음).");

    // ------------------------------------------------------------------ recording

    [RelayCommand]
    private Task Start() => Act(async () =>
    {
        if (SelectedWorkload is not { } w)
        {
            throw new InvalidOperationException("작업을 먼저 고르거나 만드세요.");
        }

        var gpus = ParseGpus(StartGpus);
        var s = await Task.Run(() => Context.Sessions.Start(w.Id, StartTitle, StartNotes, gpus));
        StartTitle = "";
        StartNotes = "";
        _pendingSelect = s.Id;
        await RefreshAsync();
        StatusText = $"세션 #{s.Id} 기록을 시작했습니다.";
    });

    [RelayCommand]
    private Task Pause() => OnSelected(id => Context.Sessions.Pause(id), "일시정지했습니다.");

    [RelayCommand]
    private Task Resume() => OnSelected(id => Context.Sessions.Resume(id), "재개했습니다. 앱이 꺼져 있던 시간은 세션에 포함하지 않습니다.");

    [RelayCommand]
    private Task Stop() => OnSelected(id => Context.Sessions.Stop(id), "종료하고 계산 결과를 저장했습니다.");

    [RelayCommand]
    private Task Abort() => OnSelected(id => Context.Sessions.Stop(id, aborted: true), "중단으로 종료했습니다.",
        "세션 중단", "이 세션을 '중단'(작업 실패·취소)으로 끝냅니다. 기록은 삭제하지 않고 중단 표시와 함께 보존합니다. 계속할까요?");

    [RelayCommand]
    private Task Delete() => OnSelected(id => Context.Sessions.Delete(id), "삭제했습니다.",
        "세션 삭제", "이 세션과 세션의 수익·비용 항목, 계산 이력을 삭제합니다. 센서 기록은 남습니다. 되돌릴 수 없으니 필요하면 먼저 백업하세요. 삭제할까요?");

    [RelayCommand]
    private Task CreateManual() => Act(async () =>
    {
        if (SelectedWorkload is not { } w)
        {
            throw new InvalidOperationException("작업을 먼저 고르세요.");
        }

        var start = ParseTime(ManualStart, "시작");
        var end = ParseTime(ManualEnd, "종료");
        var kwh = ParseOptionalNumber(ManualWallKwh, "전력계 kWh");
        var s = await Task.Run(() => Context.Sessions.CreateManual(w.Id, ManualTitle, start, end, kwh));
        ManualTitle = "";
        ManualWallKwh = "";
        _pendingSelect = s.Id;
        await RefreshAsync();
    }, "직접 입력 세션을 만들었습니다. 그 시간에 기록된 센서 값이 있으면 함께 사용합니다.");

    [RelayCommand]
    private Task SaveDetails() => Act(async () =>
    {
        if (_detail?.Session is not { } s)
        {
            throw new InvalidOperationException("세션을 고르세요.");
        }

        var edited = s with
        {
            Title = Blank(EditTitle),
            Notes = Blank(EditNotes),
            ExecutionConfirmed = EditConfirmed,
            NoRevenueConfirmed = EditNoRevenue,
            EnergyBasis = EditBasis?.Value,
            UnobservedPolicy = EditPolicy?.Value,
            ExtraSystemPowerW = ParseOptionalNumber(EditExtraW, "시스템 추가 전력 W"),
            IdleGpuPowerW = ParseOptionalNumber(EditIdleW, "GPU 유휴 전력 W"),
            ElectricityRateKrwPerKwh = ParseOptionalMoney(EditRate, "세션 전기요금 원/kWh"),
            WallMeterKwh = ParseOptionalNumber(EditWallKwh, "외부 전력계 kWh"),
            WorkHoursOverride = ParseOptionalNumber(EditWorkHours, "작업 시간"),
            DeviceIndices = ParseGpus(EditGpus),
        };
        if (edited.ExtraSystemPowerW is < 0 || edited.IdleGpuPowerW is < 0 || edited.WallMeterKwh is < 0 || edited.WorkHoursOverride is < 0 || edited.ElectricityRateKrwPerKwh is < 0)
        {
            throw new ArgumentException("전력·요금·시간 값은 0 이상이어야 합니다.");
        }

        if (edited.EnergyBasis == EnergyBasis.WallMeter && edited.WallMeterKwh is null)
        {
            throw new ArgumentException("외부 전력계 기준을 쓰려면 전력계 kWh 값을 입력하세요.");
        }

        await Task.Run(() => Context.Sessions.UpdateDetails(edited));
        _pendingSelect = s.Id;
        await SearchCoreAsync();
    }, "세션 설정을 저장하고 다시 계산했습니다.");

    [RelayCommand]
    private void OpenAccounting()
    {
        if (SelectedSession is { } s)
        {
            Context.Main?.ShowProfitability(s.Id);
        }
    }

    [RelayCommand]
    private Task ExportEnergy() => Act(async () =>
    {
        if (_detail is not { } d)
        {
            throw new InvalidOperationException("세션을 고르세요.");
        }

        var path = Context.Dialogs.SaveFile("세션 에너지 내보내기", "CSV (*.csv)|*.csv", $"gpc-session-{d.Session.Id}-energy.csv", Context.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        await Task.Run(() => Exporters.SessionEnergyCsv(path, d));
        var samplesPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-samples.csv");
        await Task.Run(() => Exporters.SamplesCsv(samplesPath, Context.Sessions.BuildInput(d.Session.Id).Samples));
        StatusText = $"내보냈습니다: {Path.GetFileName(path)}, {Path.GetFileName(samplesPath)}";
    });

    private Task OnSelected(Action<long> action, string done, string? confirmTitle = null, string? confirmMessage = null) => Act(async () =>
    {
        if (SelectedSession is not { } s)
        {
            throw new InvalidOperationException("세션을 고르세요.");
        }

        // Confirmation on the UI thread, before any work starts.
        if (confirmTitle is not null && !Context.Dialogs.Confirm(confirmTitle, $"세션 #{s.Id}: {confirmMessage}"))
        {
            StatusText = "취소했습니다.";
            return;
        }

        await Task.Run(() => action(s.Id));

        _pendingSelect = s.Id;
        await SearchCoreAsync();
        StatusText = $"세션 #{s.Id}: {done}";
    });

    internal static IReadOnlyList<int>? ParseGpus(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var list = new List<int>();
        foreach (var part in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) || i is < 0 or > 15)
            {
                throw new ArgumentException("GPU 번호는 0~15를 쉼표로 구분해 입력하세요 (비우면 모든 GPU).");
            }

            list.Add(i);
        }

        return list.Distinct().Order().ToList();
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
