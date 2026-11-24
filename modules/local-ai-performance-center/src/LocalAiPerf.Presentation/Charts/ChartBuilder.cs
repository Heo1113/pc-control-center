using LocalAiPerf.Analytics;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Metrics;
using LocalAiPerf.Core.Runtime;

namespace LocalAiPerf.Presentation.Charts;

/// <summary>Builds chart models from live frames and from stored series.</summary>
public static class ChartBuilder
{
    private const double GiB = 1073741824.0;

    public static double ToX(DateTimeOffset time) => time.LocalDateTime.ToOADate();

    public static double ToX(long unixMs) => ToX(DateTimeOffset.FromUnixTimeMilliseconds(unixMs));

    /// <summary>Gap threshold for time charts: three sample intervals, at least five seconds (in OADate days).</summary>
    public static double TimeGap(TimeSpan interval) => Math.Max(interval.TotalSeconds * 3, 5) / 86400.0;

    public static ChartSeries Live(IReadOnlyList<TelemetryFrame> frames, string label, int slot, Func<TelemetryFrame, double?> value, bool derived = false)
    {
        var xs = new List<double>(frames.Count);
        var ys = new List<double>(frames.Count);
        foreach (var frame in frames)
        {
            if (value(frame) is double v && double.IsFinite(v))
            {
                xs.Add(ToX(frame.Timestamp));
                ys.Add(v);
            }
        }

        return new ChartSeries(label, [.. xs], [.. ys], ChartPalette.Slot(slot), derived);
    }

    /// <summary>Runtime values come from runtime snapshots, which carry their own timestamps.</summary>
    public static ChartSeries LiveRuntime(IReadOnlyList<TelemetryFrame> frames, string label, int slot, Func<RuntimeSnapshot, double?> value, bool derived = false)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var frame in frames)
        {
            if (frame.Runtime is { IsReachable: true } rt && value(rt) is double v && double.IsFinite(v))
            {
                xs.Add(ToX(rt.Timestamp));
                ys.Add(v);
            }
        }

        return new ChartSeries(label, [.. xs], [.. ys], ChartPalette.Slot(slot), derived);
    }

    public static ChartModel Utilization(IReadOnlyList<TelemetryFrame> frames, int gpu, TimeSpan interval) => new()
    {
        Title = "사용률",
        Unit = "%",
        Series =
        [
            Live(frames, "GPU", 0, f => f.Hardware.Gpu(gpu)?.UtilizationPercent),
            Live(frames, "CPU", 1, f => f.Hardware.System.CpuUtilizationPercent),
        ],
        YMin = 0,
        YMax = 100,
        GapThreshold = TimeGap(interval),
    };

    public static ChartModel GpuPower(IReadOnlyList<TelemetryFrame> frames, int gpu, TimeSpan interval)
    {
        var limit = frames.LastOrDefault(f => f.Hardware.Gpu(gpu)?.PowerLimitW is not null)?.Hardware.Gpu(gpu)?.PowerLimitW;
        return new ChartModel
        {
            Title = "GPU 전력",
            Unit = "W",
            Series = [Live(frames, "GPU 전력", 0, f => f.Hardware.Gpu(gpu)?.PowerW)],
            ReferenceLines = limit is double l ? [new ChartReferenceLine(l, $"전력 제한 {l:0} W", ChartPalette.MutedInk)] : [],
            YMin = 0,
            GapThreshold = TimeGap(interval),
        };
    }

    public static ChartModel GpuTemperature(IReadOnlyList<TelemetryFrame> frames, int gpu, TimeSpan interval, double highTemp) => new()
    {
        Title = "GPU 온도",
        Unit = "°C",
        Series = [Live(frames, "GPU 온도", 0, f => f.Hardware.Gpu(gpu)?.TemperatureC)],
        ReferenceLines = [new ChartReferenceLine(highTemp, $"고온 기준 {highTemp:0} °C", ChartPalette.MutedInk)],
        YMin = 20,
        ValueFormat = "0",
        GapThreshold = TimeGap(interval),
    };

    public static ChartModel Vram(IReadOnlyList<TelemetryFrame> frames, int gpu, TimeSpan interval)
    {
        var total = frames.LastOrDefault(f => f.Hardware.Gpu(gpu)?.MemoryTotalBytes is not null)?.Hardware.Gpu(gpu)?.MemoryTotalBytes;
        return new ChartModel
        {
            Title = "VRAM 사용량",
            Unit = "GiB",
            Series = [Live(frames, "VRAM", 0, f => f.Hardware.Gpu(gpu)?.MemoryUsedBytes / GiB)],
            ReferenceLines = total is long t ? [new ChartReferenceLine(t / GiB, $"전체 {t / GiB:0.0} GiB", ChartPalette.MutedInk)] : [],
            YMin = 0,
            ValueFormat = "0.00",
            GapThreshold = TimeGap(interval),
        };
    }

    public static ChartModel GpuClock(IReadOnlyList<TelemetryFrame> frames, int gpu, TimeSpan interval) => new()
    {
        Title = "GPU 그래픽 클럭",
        Unit = "MHz",
        Series = [Live(frames, "그래픽 클럭", 0, f => f.Hardware.Gpu(gpu)?.GraphicsClockMhz)],
        YMin = 0,
        ValueFormat = "0",
        GapThreshold = TimeGap(interval),
    };

    public static ChartModel Pcie(IReadOnlyList<TelemetryFrame> frames, int gpu, TimeSpan interval) => new()
    {
        Title = "PCIe 전송량",
        Unit = "MB/s",
        Series =
        [
            Live(frames, "수신 (호스트→GPU)", 0, f => f.Hardware.Gpu(gpu)?.PcieRxKBps / 1024.0),
            Live(frames, "송신 (GPU→호스트)", 1, f => f.Hardware.Gpu(gpu)?.PcieTxKBps / 1024.0),
        ],
        YMin = 0,
        GapThreshold = TimeGap(interval),
    };

    public static ChartModel Cpu(IReadOnlyList<TelemetryFrame> frames, TimeSpan interval) => new()
    {
        Title = "CPU 사용률",
        Unit = "%",
        Series = [Live(frames, "CPU", 0, f => f.Hardware.System.CpuUtilizationPercent)],
        YMin = 0,
        YMax = 100,
        GapThreshold = TimeGap(interval),
    };

    public static ChartModel Ram(IReadOnlyList<TelemetryFrame> frames, TimeSpan interval)
    {
        var total = frames.LastOrDefault(f => f.Hardware.System.MemoryTotalBytes is not null)?.Hardware.System.MemoryTotalBytes;
        return new ChartModel
        {
            Title = "시스템 RAM 사용량",
            Unit = "GiB",
            Series = [Live(frames, "RAM", 0, f => f.Hardware.System.MemoryUsedBytes / GiB)],
            ReferenceLines = total is long t ? [new ChartReferenceLine(t / GiB, $"전체 {t / GiB:0.0} GiB", ChartPalette.MutedInk)] : [],
            YMin = 0,
            ValueFormat = "0.00",
            GapThreshold = TimeGap(interval),
        };
    }

    public static ChartModel Throughput(IReadOnlyList<TelemetryFrame> frames, TimeSpan interval) => new()
    {
        Title = "추론 처리량",
        Unit = "tok/s",
        Series =
        [
            LiveRuntime(frames, "답변 생성 속도 (엔진 보고)", 0, r => r.Rates?.AnswerTokPerSec),
            LiveRuntime(frames, "전체 출력 처리량 (계산값)", 1, r => r.Rates?.AggregateOutputTokPerSec, derived: true),
        ],
        YMin = 0,
        GapThreshold = TimeGap(interval),
        EmptyText = "AI 엔진에서 받은 처리량 값이 없습니다 (생성 중이 아니거나 연결 안 됨)",
    };

    /// <summary>A stored metric over a session, with degradation candidates and throttle intervals highlighted.</summary>
    public static ChartModel SessionMetric(MetricDefinition metric, MetricSeries series, int intervalMs,
        IEnumerable<DegradationCandidate> degradations, IEnumerable<ThrottleInterval> throttles)
    {
        var spans = new List<ChartSpan>();
        foreach (var d in degradations.Where(d => d.MetricKey == metric.Key))
        {
            spans.Add(new ChartSpan(ToX(d.StartMs), ToX(Math.Max(d.EndMs, d.StartMs + intervalMs)), ChartPalette.Serious, "성능 저하 후보"));
        }

        if (metric.Group is MetricGroup.Gpu or MetricGroup.Runtime or MetricGroup.Efficiency)
        {
            foreach (var t in throttles)
            {
                spans.Add(new ChartSpan(ToX(t.StartMs), ToX(Math.Max(t.EndMs, t.StartMs + intervalMs)), ChartPalette.Warning, "클럭 제한/고온"));
            }
        }

        return new ChartModel
        {
            Title = $"{metric.DisplayName} ({metric.OriginLabel})",
            Unit = metric.Unit,
            Series = [new ChartSeries(metric.DisplayName, series.Points.Select(p => ToX(p.TimeMs)).ToArray(), series.Points.Select(p => p.Value).ToArray(), ChartPalette.Slot(0), metric.Origin == MetricOrigin.Derived)],
            Spans = spans,
            YMin = metric.Unit == "%" ? 0 : null,
            ValueFormat = ToFormat(metric.Format),
            GapThreshold = metric.Group == MetricGroup.Request ? double.PositiveInfinity : TimeGap(TimeSpan.FromMilliseconds(intervalMs)),
            EmptyText = $"이 세션에는 '{metric.DisplayName}' 값이 없습니다 (N/A)",
        };
    }

    /// <summary>Sessions overlaid on elapsed minutes since each session's start. Colors come from the caller (stable per session).</summary>
    public static ChartModel Overlay(MetricDefinition metric, IReadOnlyList<(string Label, string ColorHex, long StartMs, MetricSeries Series, int IntervalMs)> sessions) => new()
    {
        Title = $"{metric.DisplayName} - 세션 시작 기준 경과 시간 ({metric.OriginLabel})",
        Unit = metric.Unit,
        XIsTime = false,
        XLabel = "경과 시간 (분)",
        Series = sessions.Select(s => new ChartSeries(s.Label,
            s.Series.Points.Select(p => (p.TimeMs - s.StartMs) / 60000.0).ToArray(),
            s.Series.Points.Select(p => p.Value).ToArray(), s.ColorHex, metric.Origin == MetricOrigin.Derived)).ToList(),
        YMin = metric.Unit == "%" ? 0 : null,
        ValueFormat = ToFormat(metric.Format),
        GapThreshold = metric.Group == MetricGroup.Request ? double.PositiveInfinity
            : Math.Max(3 * sessions.Select(s => s.IntervalMs).DefaultIfEmpty(1000).Max(), 5000) / 60000.0,
    };

    /// <summary>"F1" -> "0.0", "F0" -> "0".</summary>
    public static string ToFormat(string metricFormat)
        => metricFormat.Length == 2 && metricFormat[0] == 'F' && char.IsDigit(metricFormat[1])
            ? metricFormat[1] == '0' ? "0" : "0." + new string('0', metricFormat[1] - '0')
            : "0.0";
}
