using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Presentation.Charts;
using PcControlCenter.History;

namespace PcControlCenter.Presentation;

public sealed record HistoryRange(string Label, TimeSpan Length)
{
    public override string ToString() => Label;
}

public sealed record CoverageRow(string Source, string State, string Points, string Range, string Gaps, string Note, Level Level);

public sealed record TimelineRow(string Time, string End, string Source, string Kind, string Title, string Detail, Level Level, bool IsTestData);

/// <summary>
/// The integrated history: one time range, the sensors, inference speed, power, Windows errors and Operator tasks of
/// that range side by side, each with its source and the time ranges it did not observe. Things that happened at the same
/// time are listed together; no cause or effect is inferred.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    /// <summary>Most points drawn per line; more are averaged into buckets for display only (the databases keep every value).</summary>
    public const int MaxDisplayPoints = 2000;

    private readonly TimelineReader _reader;
    private readonly IShellDispatcher _ui;
    private readonly TimeProvider _time;

    public HistoryViewModel(TimelineReader reader, IShellDispatcher ui, TimeProvider? time = null)
    {
        _reader = reader;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        SelectedRange = Ranges[0];
    }

    public IReadOnlyList<HistoryRange> Ranges { get; } =
    [
        new("최근 1시간", TimeSpan.FromHours(1)),
        new("최근 6시간", TimeSpan.FromHours(6)),
        new("최근 24시간", TimeSpan.FromHours(24)),
        new("최근 3일", TimeSpan.FromDays(3)),
        new("최근 7일", TimeSpan.FromDays(7)),
    ];

    public string Disclaimer =>
        "서로 다른 기록을 같은 시간축에 나란히 놓았을 뿐, 한 사건이 다른 사건의 원인이라고 판단하지 않습니다. 각 값은 기록한 모듈의 데이터베이스에서 그대로 읽었고, "
        + "측정하지 않은 구간(모듈 미실행, 절전, 센서 끊김 등)은 선을 잇지 않고 공백으로 표시합니다.";

    [ObservableProperty] public partial HistoryRange SelectedRange { get; set; }

    /// <summary>End of the range in local time; empty = now.</summary>
    [ObservableProperty] public partial DateTime? EndLocal { get; set; }

    [ObservableProperty] public partial ChartModel? GpuUtilChart { get; set; }
    [ObservableProperty] public partial ChartModel? GpuTempChart { get; set; }
    [ObservableProperty] public partial ChartModel? PowerChart { get; set; }
    [ObservableProperty] public partial ChartModel? InferenceChart { get; set; }
    [ObservableProperty] public partial ChartModel? CpuChart { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "기간을 고르고 [불러오기]를 누르세요.";
    [ObservableProperty] public partial string RangeText { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public ObservableCollection<TimelineRow> Events { get; } = [];
    public ObservableCollection<CoverageRow> Coverage { get; } = [];

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "불러오는 중…";
        try
        {
            var to = EndLocal is { } end ? new DateTimeOffset(end) : _time.GetUtcNow();
            var from = to - SelectedRange.Length;
            var result = await Task.Run(() => _reader.Read(from, to));
            _ui.Post(() => Apply(result));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ui.Post(() => StatusText = "불러오지 못했습니다: " + ex.Message);
        }
        finally
        {
            _ui.Post(() => IsBusy = false);
        }
    }

    internal void Apply(TimelineResult result)
    {
        RangeText = $"{Text.DateTime(result.From)} ~ {Text.DateTime(result.To)}";
        var b = result.Series.Where(s => s.Source == TimelineReader.SourceB).ToList();
        var c = result.Series.Where(s => s.Source == TimelineReader.SourceC).ToList();
        var sensors = b.Count > 0 ? b : c;
        var sensorPrefix = b.Count > 0 ? "b." : "c.";
        var sensorGaps = result.Gaps.Where(g => g.Source == (b.Count > 0 ? TimelineReader.SourceB : TimelineReader.SourceC)).ToList();
        GpuUtilChart = Chart("GPU 사용률", "%", sensors.Where(s => s.Key.StartsWith(sensorPrefix + "gpu", StringComparison.Ordinal) && s.Key.EndsWith(".util", StringComparison.Ordinal)), sensorGaps, 0, 100);
        GpuTempChart = Chart("GPU 온도", "°C", sensors.Where(s => s.Key.StartsWith(sensorPrefix + "gpu", StringComparison.Ordinal) && s.Key.EndsWith(".temp", StringComparison.Ordinal)), sensorGaps);
        PowerChart = Chart("GPU 보드 전력 (실측, NVML)", "W", sensors.Where(s => s.Key.StartsWith(sensorPrefix + "gpu", StringComparison.Ordinal) && s.Key.EndsWith(".power", StringComparison.Ordinal)), sensorGaps);
        CpuChart = Chart("CPU 사용률", "%", sensors.Where(s => s.Key == sensorPrefix + "cpu"), sensorGaps, 0, 100);
        InferenceChart = Chart("추론 속도 (Strata 보고)", "tok/s", result.Series.Where(s => s.Source == TimelineReader.SourceA),
            result.Gaps.Where(g => g.Source == TimelineReader.SourceA).ToList());

        Events.Clear();
        foreach (var e in result.Events.OrderByDescending(e => e.Time).Take(2000))
        {
            Events.Add(new TimelineRow(Text.DateTime(e.Time), e.End is { } end ? Text.DateTime(end) : "", e.Source, e.Kind, e.Title, e.Detail ?? "", e.Severity switch
            {
                TimelineSeverity.Critical or TimelineSeverity.Error => Level.Error,
                TimelineSeverity.Warning => Level.Warning,
                _ => Level.Muted,
            }, e.IsTestData));
        }

        Coverage.Clear();
        foreach (var s in result.Coverage)
        {
            var used = s.Source == TimelineReader.SourceC && b.Count > 0 && c.Count > 0 ? " (센서 그래프는 B 기록 사용, 같은 측정값)" : "";
            Coverage.Add(new CoverageRow(s.Source, s.Available ? "읽음" : "없음",
                s.Available ? $"값 {Text.Count(s.Points)} · 사건 {Text.Count(s.Events)}" : "-",
                s.First is null ? "-" : $"{Text.Clock(s.First)} ~ {Text.Clock(s.Last)}",
                s.RecordedGaps > TimeSpan.Zero ? $"{FormatSpan(s.RecordedGaps)} 미관측" : "기록된 공백 없음",
                (s.Problem ?? "") + used,
                !s.Available ? Level.Muted : s.RecordedGaps > TimeSpan.Zero || s.Problem is not null ? Level.Warning : Level.Good));
        }

        var mock = result.Series.Any(s => s.IsMock) || result.Events.Any(e => e.IsTestData);
        StatusText = $"값 {Text.Count(result.Series.Sum(s => s.Points.Count))}개, 사건 {Text.Count(result.Events.Count)}건, 미관측 구간 {result.Gaps.Count}개"
                     + (mock ? " · MOCK/가상/데모로 표시된 기록이 포함되어 있습니다 (실제 측정 아님)." : "");
    }

    private static ChartModel Chart(string title, string unit, IEnumerable<TimelineSeries> series, IReadOnlyList<TimelineGap> gaps, double? yMin = null, double? yMax = null)
    {
        var list = series.ToList();
        var interval = list.Count == 0 ? TimeSpan.FromSeconds(1) : list.Max(s => s.ExpectedInterval);
        var chartSeries = list.Select((s, i) =>
        {
            var points = Bucket(s.Points);
            return new ChartSeries(s.IsMock ? s.Label + " [MOCK]" : s.Label, [.. points.Select(p => p.Time.LocalDateTime.ToOADate())], [.. points.Select(p => p.Value)],
                ChartPalette.Slot(i));
        }).ToList();
        var spans = gaps.Where(g => g.To > g.From)
            .Select(g => new ChartSpan(g.From.LocalDateTime.ToOADate(), g.To.LocalDateTime.ToOADate(), ChartPalette.Warning, "미관측 구간"))
            .ToList();
        return new ChartModel
        {
            Title = title,
            Unit = unit,
            Series = chartSeries,
            Spans = spans,
            XIsTime = true,

            // Points further apart than three recording intervals are not connected (a gap stays a gap).
            GapThreshold = Math.Max(interval.TotalDays * 3, TimeSpan.FromSeconds(10).TotalDays),
            YMin = yMin,
            YMax = yMax,
            EmptyText = "이 기간에 기록된 값이 없습니다 (N/A)",
        };
    }

    /// <summary>Averages into at most <see cref="MaxDisplayPoints"/> buckets for drawing; never invents points in empty buckets.</summary>
    internal static IReadOnlyList<TimelinePoint> Bucket(IReadOnlyList<TimelinePoint> points)
    {
        if (points.Count <= MaxDisplayPoints)
        {
            return points;
        }

        var first = points[0].Time;
        var span = (points[^1].Time - first).Ticks;
        var width = Math.Max(1, span / MaxDisplayPoints);
        return points.GroupBy(p => (p.Time - first).Ticks / width)
            .Select(g => new TimelinePoint(new DateTimeOffset(g.Average(p => p.Time.UtcTicks) is var ticks ? (long)ticks : 0, TimeSpan.Zero), g.Average(p => p.Value)))
            .ToList();
    }

    private static string FormatSpan(TimeSpan span) => span.TotalMinutes < 1 ? $"{span.TotalSeconds:0}초" : span.TotalHours < 1 ? $"{span.TotalMinutes:0}분" : $"{span.TotalHours:0.#}시간";
}
