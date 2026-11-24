using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Analytics;
using LocalAiPerf.Core.Metrics;
using LocalAiPerf.Export;
using LocalAiPerf.Presentation.Charts;
using LocalAiPerf.Presentation.Services;
using LocalAiPerf.Recording;
using LocalAiPerf.Storage;

namespace LocalAiPerf.Presentation.ViewModels;

/// <summary>
/// Compares two or more sessions: per-metric table with deltas against a baseline, condition warnings,
/// degradation candidates and an elapsed-time overlay chart. Each session keeps its color while selected.
/// </summary>
public sealed partial class CompareViewModel(MonitoringService service, IDialogService dialogs, IUiDispatcher ui) : PageViewModel
{
    public const int MaxSessions = 8;

    private readonly Dictionary<long, (SessionData Data, SessionReport Report, IReadOnlyDictionary<string, MetricSeries> Series)> _cache = [];
    private readonly Dictionary<long, int> _slots = [];
    private ComparisonReport? _comparison;

    public override string Title => "비교";
    public override string Glyph => "";

    public ObservableCollection<SessionItem> Sessions { get; } = [];
    public ObservableCollection<SessionItem> Selected { get; } = [];
    public ObservableCollection<CompareRow> Rows { get; } = [];
    public ObservableCollection<WarningRow> Warnings { get; } = [];
    public ObservableCollection<DegradationRow> Degradations { get; } = [];
    public ObservableCollection<string> Headers { get; } = [];
    public IReadOnlyList<MetricDefinition> ChartMetrics { get; } = SeriesExtractor.SummaryMetricKeys.Select(MetricCatalog.Get).ToList();

    [ObservableProperty] public partial string FilterText { get; set; } = "";
    [ObservableProperty] public partial SessionItem? Baseline { get; set; }
    [ObservableProperty] public partial MetricDefinition? SelectedMetric { get; set; } = MetricCatalog.Get(MetricKeys.RtAnswerTokS);
    [ObservableProperty] public partial ChartModel? Chart { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "비교할 세션을 2개 이상 선택하세요.";

    public override Task OnActivatedAsync() => RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (service.Store is not { } store)
        {
            StatusText = service.StorageProblem ?? "데이터베이스가 열려 있지 않습니다.";
            return;
        }

        var selectedIds = Selected.Select(s => s.Id).ToHashSet();
        var sessions = await Task.Run(() => store.ListSessions(new SessionFilter(FilterText, Limit: 2000)));
        foreach (var item in Sessions)
        {
            item.PropertyChanged -= OnItemChanged;
        }

        Sessions.Clear();
        foreach (var s in sessions)
        {
            var item = new SessionItem(s) { IsSelected = selectedIds.Contains(s.Id) };
            if (_slots.TryGetValue(s.Id, out var slot) && item.IsSelected)
            {
                item.ColorHex = ChartPalette.Slot(slot);
            }

            item.PropertyChanged += OnItemChanged;
            Sessions.Add(item);
        }

        SyncSelected();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionItem.IsSelected) || sender is not SessionItem item)
        {
            return;
        }

        if (item.IsSelected)
        {
            if (_slots.Count >= MaxSessions && !_slots.ContainsKey(item.Id))
            {
                item.IsSelected = false;
                dialogs.Info("비교 세션 수 제한", $"한 번에 최대 {MaxSessions}개 세션까지 비교합니다 (색 구분 한계). 다른 세션의 선택을 먼저 해제하세요.");
                return;
            }

            if (!_slots.ContainsKey(item.Id))
            {
                var used = _slots.Values.ToHashSet();
                _slots[item.Id] = Enumerable.Range(0, MaxSessions).First(i => !used.Contains(i));
            }

            item.ColorHex = ChartPalette.Slot(_slots[item.Id]);
        }
        else
        {
            _slots.Remove(item.Id); // the remaining sessions keep their colors
            item.ColorHex = null;
        }

        SyncSelected();
    }

    private void SyncSelected()
    {
        Selected.Clear();
        foreach (var item in Sessions.Where(s => s.IsSelected).OrderBy(s => _slots.GetValueOrDefault(s.Id)))
        {
            Selected.Add(item);
        }

        if (Baseline is null || !Selected.Contains(Baseline))
        {
            Baseline = Selected.FirstOrDefault();
        }

        CompareCommand.NotifyCanExecuteChanged();
        StatusText = Selected.Count < 2 ? "비교할 세션을 2개 이상 선택하세요." : $"{Selected.Count}개 세션 선택됨 - [비교]를 누르세요.";
    }

    private bool CanCompare() => Selected.Count >= 2 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareAsync()
    {
        if (service.Store is not { } store || Selected.Count < 2)
        {
            return;
        }

        IsBusy = true;
        CompareCommand.NotifyCanExecuteChanged();
        try
        {
            var options = HistoryViewModel.DetectionFrom(service.Settings);
            var gpu = service.Settings.Ui.SelectedGpuIndex;
            var items = Selected.ToList();
            var baselineIndex = Math.Max(0, items.IndexOf(Baseline ?? items[0]));
            // The cache is only touched on the UI thread; the background task gets a snapshot of it.
            var cached = items.Select(i => !i.Session.IsActive && _cache.TryGetValue(i.Id, out var c) ? c : default).ToList();
            var loaded = await Task.Run(() => items.Select((item, index) =>
            {
                if (cached[index].Data is not null)
                {
                    return cached[index];
                }

                var data = store.LoadSessionData(item.Id) ?? throw new InvalidOperationException($"세션 #{item.Id}을(를) 찾을 수 없습니다.");
                return (Data: data, Report: SessionAnalyzer.Analyze(data, options, gpu), Series: SeriesExtractor.Extract(data, gpu));
            }).ToList());
            for (var i = 0; i < items.Count; i++)
            {
                _cache[items[i].Id] = loaded[i];
            }

            _comparison = SessionComparer.Compare(loaded.Select(l => l.Report).ToList(), baselineIndex);
            Headers.Clear();
            foreach (var item in items)
            {
                Headers.Add(item.Label);
            }

            Rows.Clear();
            foreach (var row in _comparison.Rows)
            {
                var format = ChartBuilder.ToFormat(row.Metric.Format);
                var cells = row.PerSession.Select((stats, i) => new CompareCell(
                    stats.Count == 0 ? Fmt.NA : $"{Fmt.Num(stats.Mean, format)} ({Fmt.Num(stats.Min, format)}~{Fmt.Num(stats.Max, format)}, n={stats.Count})",
                    i == baselineIndex ? "기준" : Fmt.Delta(row.DeltaPercent[i]), i == baselineIndex ? 0 : row.Direction(i), i == baselineIndex)).ToList();
                Rows.Add(new CompareRow(row.Metric.DisplayName, row.Metric.Unit, row.Metric.OriginLabel, cells, row.Note ?? ""));
            }

            Warnings.Clear();
            foreach (var w in _comparison.Warnings)
            {
                Warnings.Add(new WarningRow(w.Kind, w.Message));
            }

            Degradations.Clear();
            foreach (var report in _comparison.Sessions)
            {
                foreach (var d in report.Degradations)
                {
                    var metric = MetricCatalog.Get(d.MetricKey);
                    Degradations.Add(new DegradationRow($"#{report.Session.Id} {metric.DisplayName}", Fmt.DateTime(DateTimeOffset.FromUnixTimeMilliseconds(d.StartMs)),
                        Fmt.Duration(d.Duration), $"-{d.DropPercent:0}%", "", string.Join(" · ", d.ConcurrentObservations)));
                }
            }

            HasResult = true;
            UpdateChart();
            StatusText = $"기준: {items[baselineIndex].Label}. 변화율 = (세션 평균 − 기준 평균) ÷ 기준 평균 (계산값).";
        }
        catch (Exception ex)
        {
            StatusText = $"비교하지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            CompareCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedMetricChanged(MetricDefinition? value) => UpdateChart();

    private void UpdateChart()
    {
        if (!HasResult || SelectedMetric is null)
        {
            Chart = null;
            return;
        }

        var sessions = Selected.Where(s => _cache.ContainsKey(s.Id)).Select(s =>
        {
            var (data, _, series) = _cache[s.Id];
            var metricSeries = series.TryGetValue(SelectedMetric.Key, out var ms) ? ms : MetricSeries.Empty(SelectedMetric.Key);
            return (s.Label, s.ColorHex ?? ChartPalette.Slot(0), data.Session.StartedAt.ToUnixTimeMilliseconds(), metricSeries, data.Session.SampleIntervalMs);
        }).ToList();
        Chart = ChartBuilder.Overlay(SelectedMetric, sessions);
    }

    [RelayCommand]
    private async Task ExportAsync(string? format)
    {
        if (_comparison is null)
        {
            return;
        }

        var csv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        var path = dialogs.SaveFile("비교 결과 내보내기", csv ? "CSV 파일 (*.csv)|*.csv" : "JSON 파일 (*.json)|*.json",
            $"compare-{string.Join("-", _comparison.Sessions.Select(s => s.Session.Id))}.{(csv ? "csv" : "json")}", service.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        var comparison = _comparison;
        await Task.Run(() =>
        {
            if (csv)
            {
                SessionExporter.ExportComparisonCsv(comparison, path);
            }
            else
            {
                SessionExporter.ExportComparisonJson(comparison, path);
            }
        });
        StatusText = $"내보내기 완료: {path}";
    }

    public void InvalidateCache() => ui.Post(_cache.Clear);
}
