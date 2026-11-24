using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Analytics;
using LocalAiPerf.Core.Metrics;
using LocalAiPerf.Core.Sessions;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Export;
using LocalAiPerf.Presentation.Charts;
using LocalAiPerf.Presentation.Services;
using LocalAiPerf.Recording;
using LocalAiPerf.Storage;

namespace LocalAiPerf.Presentation.ViewModels;

/// <summary>A session in a list (History and Compare).</summary>
public sealed partial class SessionItem(SessionInfo session) : ObservableObject
{
    public SessionInfo Session { get; } = session;
    public long Id => Session.Id;
    public string Name => Session.Name;
    public string Started => Fmt.DateTime(Session.StartedAt);
    public string Duration => Fmt.Duration(Session.Duration);
    public string State => SessionText.State(Session);
    public string Kind => SessionText.Kind(Session);
    public string Model => Session.ModelSummary ?? Fmt.NA;
    public string Engine => Session.EngineVersionSummary ?? Fmt.NA;
    public string Samples => Session.SampleCount.ToString("N0", CultureInfo.InvariantCulture);
    public bool IsMock => Session.IsMock;
    public string Label => $"#{Id} {Name}";

    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>Series color while selected in Compare (stable per session).</summary>
    [ObservableProperty] public partial string? ColorHex { get; set; }
}

/// <summary>Past sessions: list, statistics table, chart with highlighted candidates, events, metadata, export and delete.</summary>
public sealed partial class HistoryViewModel(MonitoringService service, IDialogService dialogs, IShellService shell, IUiDispatcher ui, TimeProvider time) : PageViewModel
{
    private SessionData? _data;
    private SessionReport? _report;
    private IReadOnlyDictionary<string, MetricSeries>? _series;
    private int _loadVersion;

    public override string Title => "기록";
    public override string Glyph => "";

    public ObservableCollection<SessionItem> Sessions { get; } = [];
    public ObservableCollection<StatRow> Stats { get; } = [];
    public ObservableCollection<DegradationRow> Degradations { get; } = [];
    public ObservableCollection<ThrottleRow> Throttles { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];
    public ObservableCollection<ConditionRow> Conditions { get; } = [];
    public IReadOnlyList<MetricDefinition> ChartMetrics { get; } = SeriesExtractor.SummaryMetricKeys.Select(MetricCatalog.Get).ToList();

    [ObservableProperty] public partial string FilterText { get; set; } = "";
    [ObservableProperty] public partial DateTime? FromDate { get; set; }
    [ObservableProperty] public partial DateTime? ToDate { get; set; }
    [ObservableProperty] public partial SessionItem? SelectedSession { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial string EditName { get; set; } = "";
    [ObservableProperty] public partial string EditNotes { get; set; } = "";
    [ObservableProperty] public partial string EditTags { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string EnergyText { get; set; } = "";
    [ObservableProperty] public partial string HostText { get; set; } = "";
    [ObservableProperty] public partial MetricDefinition? SelectedMetric { get; set; } = MetricCatalog.Get(MetricKeys.RtAnswerTokS);
    [ObservableProperty] public partial ChartModel? Chart { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";

    public override Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var store = service.Store;
        Sessions.Clear();
        if (store is null)
        {
            StatusText = service.StorageProblem ?? "데이터베이스가 열려 있지 않습니다.";
            return;
        }

        var filter = new SessionFilter(FilterText,
            FromDate is DateTime from ? new DateTimeOffset(from.Date) : null,
            ToDate is DateTime to ? new DateTimeOffset(to.Date.AddDays(1).AddTicks(-1)) : null, 2000);
        var previous = SelectedSession?.Id;
        var sessions = await Task.Run(() => store.ListSessions(filter));
        foreach (var s in sessions)
        {
            Sessions.Add(new SessionItem(s));
        }

        StatusText = $"세션 {sessions.Count}개";
        SelectedSession = Sessions.FirstOrDefault(s => s.Id == previous) ?? Sessions.FirstOrDefault();
    }

    partial void OnSelectedSessionChanged(SessionItem? value) => _ = LoadSelectedAsync(value);

    partial void OnSelectedMetricChanged(MetricDefinition? value) => UpdateChart();

    private async Task LoadSelectedAsync(SessionItem? item)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        HasSelection = item is not null;
        Stats.Clear();
        Degradations.Clear();
        Throttles.Clear();
        Events.Clear();
        Conditions.Clear();
        Chart = null;
        _data = null;
        _report = null;
        _series = null;
        if (item is null || service.Store is not { } store)
        {
            return;
        }

        EditName = item.Name;
        EditNotes = item.Session.Notes ?? "";
        EditTags = item.Session.Tags ?? "";
        IsBusy = true;
        try
        {
            var options = DetectionFrom(service.Settings);
            var gpu = service.Settings.Ui.SelectedGpuIndex;
            var (data, report, series) = await Task.Run(() =>
            {
                var d = store.LoadSessionData(item.Id);
                if (d is null)
                {
                    return ((SessionData?)null, (SessionReport?)null, (IReadOnlyDictionary<string, MetricSeries>?)null);
                }

                return (d, SessionAnalyzer.Analyze(d, options, gpu), SeriesExtractor.Extract(d, gpu));
            });
            if (version != _loadVersion)
            {
                return; // a newer selection is loading
            }

            if (data is null || report is null || series is null)
            {
                StatusText = "세션을 찾을 수 없습니다 (삭제되었을 수 있음).";
                return;
            }

            _data = data;
            _report = report;
            _series = series;
            Fill();
        }
        catch (Exception ex)
        {
            StatusText = $"세션을 불러오지 못했습니다: {ex.Message}";
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsBusy = false;
            }
        }
    }

    private void Fill()
    {
        var data = _data!;
        var report = _report!;
        var s = data.Session;
        Summary = $"{SessionText.Kind(s)} 세션 · {Fmt.DateTime(s.StartedAt)} ~ {(s.EndedAt is null ? "기록 중" : Fmt.DateTime(s.EndedAt))} · " +
            $"{Fmt.Duration(s.Duration)} · 샘플 {s.SampleCount:N0} · 간격 {s.SampleIntervalMs} ms · 종료: {SessionText.State(s)}" +
            (s.IsMock ? " · MOCK 가상 데이터" : "") +
            $" · 요청 {report.RequestCount}건 (오류 {report.FailedRequestCount}) · 경고 {report.WarningEvents} · 오류 {report.ErrorEvents} · 수집 공백 {report.GapEvents}";
        HostText = report.Host is { } host
            ? $"{host.CpuName ?? "CPU ?"} · {string.Join(", ", host.Gpus.Select(g => g.Name))} · 드라이버 {host.GpuDriverVersion ?? Fmt.NA} · RAM {Fmt.Gib(host.RamTotalBytes, "0")}"
            : "";
        EnergyText = report.Energy.GpuEnergyJoules is double j
            ? $"GPU 에너지 {j / 3600:0.00} Wh · 출력 토큰 {Fmt.Int(report.Energy.OutputTokens)} · 토큰당 {Fmt.Num(report.Energy.JoulesPerOutputToken, "0.00", "J")} · 평균 전력 {Fmt.Num(report.Energy.AveragePowerW, "0", "W")} (계산값: {report.Energy.Basis})"
            : "GPU 에너지 카운터 값이 없어 토큰당 에너지를 계산할 수 없습니다 (N/A).";
        foreach (var m in report.Metrics.Where(m => m.Stats.Count > 0))
        {
            var f = ChartBuilder.ToFormat(m.Metric.Format);
            Stats.Add(new StatRow(m.Metric.DisplayName, m.Metric.Unit, m.Metric.OriginLabel, m.Stats.Count.ToString("N0", CultureInfo.InvariantCulture),
                Fmt.Num(m.Stats.Mean, f), Fmt.Num(m.Stats.Min, f), Fmt.Num(m.Stats.Max, f), Fmt.Num(m.Stats.P95, f),
                Fmt.Delta(m.Trend.ChangePercent), Fmt.Num(m.Trend.SlopePerHour, f), m.Metric.Source));
        }

        foreach (var d in report.Degradations)
        {
            var metric = MetricCatalog.Get(d.MetricKey);
            var f = ChartBuilder.ToFormat(metric.Format);
            Degradations.Add(new DegradationRow(metric.DisplayName, Fmt.DateTime(DateTimeOffset.FromUnixTimeMilliseconds(d.StartMs)), Fmt.Duration(d.Duration),
                $"-{d.DropPercent:0}%", $"{d.Mean.ToString(f, CultureInfo.InvariantCulture)} (기준 {d.Baseline.ToString(f, CultureInfo.InvariantCulture)}) {metric.Unit}",
                d.ConcurrentObservations.Count == 0 ? "함께 관측된 특이 사항 없음" : string.Join(" · ", d.ConcurrentObservations)));
        }

        foreach (var t in report.Throttles)
        {
            Throttles.Add(new ThrottleRow(Fmt.DateTime(DateTimeOffset.FromUnixTimeMilliseconds(t.StartMs)),
                Fmt.Duration(TimeSpan.FromMilliseconds(t.EndMs - t.StartMs)), t.Reason));
        }

        foreach (var e in data.Events.Reverse().Take(1000))
        {
            Events.Add(new EventRow(Fmt.DateTime(e.Time), e.Severity switch { EventSeverity.Error => "오류", EventSeverity.Warning => "경고", _ => "정보" },
                e.Category, e.Message, (int)e.Severity));
        }

        foreach (var c in data.RuntimeInfo)
        {
            var cond = c.Conditions;
            Conditions.Add(new ConditionRow(Fmt.DateTime(DateTimeOffset.FromUnixTimeMilliseconds(c.TimeMs)), cond.EngineVersion ?? Fmt.NA, cond.ModelId ?? Fmt.NA,
                cond.ModelFileName ?? Fmt.NA, Fmt.Int(cond.ContextTokens), cond.Slots?.ToString(CultureInfo.InvariantCulture) ?? Fmt.NA,
                cond.Settings.Count == 0 ? Fmt.NA : string.Join(", ", cond.Settings.Select(x => $"{x.Key}={x.Value}"))));
        }

        UpdateChart();
        StatusText = report.Degradations.Count > 0
            ? $"성능 저하 후보 {report.Degradations.Count}건 - 함께 관측된 항목은 원인이 아니라 같은 시간대의 기록입니다."
            : "성능 저하 후보 없음";
    }

    private void UpdateChart()
    {
        if (_series is null || _report is null || SelectedMetric is null)
        {
            Chart = null;
            return;
        }

        var series = _series.TryGetValue(SelectedMetric.Key, out var s) ? s : MetricSeries.Empty(SelectedMetric.Key);
        Chart = ChartBuilder.SessionMetric(SelectedMetric, series, _report.Session.SampleIntervalMs, _report.Degradations, _report.Throttles);
    }

    [RelayCommand]
    private async Task SaveMetadataAsync()
    {
        if (SelectedSession is null || service.Store is not { } store)
        {
            return;
        }

        var id = SelectedSession.Id;
        await Task.Run(() => store.UpdateSessionMetadata(id, EditName, string.IsNullOrWhiteSpace(EditNotes) ? null : EditNotes,
            string.IsNullOrWhiteSpace(EditTags) ? null : EditTags));
        await RefreshAsync();
        StatusText = "세션 정보를 저장했습니다.";
    }

    [RelayCommand]
    private async Task DeleteSessionAsync()
    {
        if (SelectedSession is null || service.Store is not { } store)
        {
            return;
        }

        if (SelectedSession.Session.IsActive)
        {
            dialogs.Info("삭제할 수 없음", "기록 중인 세션은 삭제할 수 없습니다. 먼저 기록을 중지하세요.");
            return;
        }

        var item = SelectedSession;
        if (!dialogs.Confirm("세션 삭제", $"세션 #{item.Id} '{item.Name}'과(와) 모든 샘플을 삭제합니다.\n되돌릴 수 없습니다. 필요하면 먼저 백업하거나 내보내세요.\n\n삭제할까요?"))
        {
            return;
        }

        var deleted = await Task.Run(() => store.DeleteSession(item.Id));
        await RefreshAsync();
        StatusText = deleted ? $"세션 #{item.Id}을(를) 삭제했습니다." : "삭제하지 못했습니다.";
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (_data is null || _report is null)
        {
            return;
        }

        var parent = dialogs.PickFolder("CSV를 저장할 폴더 선택 (세션별 하위 폴더가 만들어집니다)", service.Paths.ExportDirectory);
        if (parent is null)
        {
            return;
        }

        var folder = Path.Combine(parent, $"session-{_data.Session.Id}-{SessionExporter.SafeFileName(_data.Session.Name)}");
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()
            && !dialogs.Confirm("폴더 덮어쓰기", $"{folder}\n에 이미 파일이 있습니다. 같은 이름의 CSV 파일을 덮어쓸까요?"))
        {
            return;
        }

        var data = _data;
        var report = _report;
        var gpu = service.Settings.Ui.SelectedGpuIndex;
        await Task.Run(() => SessionExporter.ExportSessionCsv(data, report, folder, gpu));
        StatusText = $"CSV 내보내기 완료: {folder}";
        shell.OpenFolder(folder);
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        if (_data is null || _report is null)
        {
            return;
        }

        var path = dialogs.SaveFile("세션 JSON 내보내기", "JSON 파일 (*.json)|*.json",
            $"session-{_data.Session.Id}-{SessionExporter.SafeFileName(_data.Session.Name)}.json", service.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        var data = _data;
        var report = _report;
        await Task.Run(() => SessionExporter.ExportSessionJson(data, report, path));
        StatusText = $"JSON 내보내기 완료: {path}";
    }

    internal static DetectionOptions DetectionFrom(AppSettings settings)
        => new(settings.Analysis.DegradationThresholdPercent, TimeSpan.FromSeconds(settings.Analysis.MinDegradationSeconds),
            TimeSpan.FromSeconds(settings.Analysis.BaselineWindowSeconds), settings.Analysis.HighGpuTempC);

    // Used by the view to show "time since" labels; kept here so tests can control time.
    public DateTimeOffset Now => time.GetUtcNow();

    public void PostRefresh() => ui.Post(() => _ = RefreshAsync());
}
