using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAiPerf.Export;
using WinDiag.Analysis;
using WinDiag.Core;
using WinDiag.Core.Crash;
using WinDiag.Core.Events;
using WinDiag.Core.Privacy;
using WinDiag.Core.Snapshots;
using WinDiag.Core.Time;
using WinDiag.Storage;

namespace WinDiag.Export;

/// <summary>
/// Minimal (default): no event messages or event data, device instance IDs shortened. Detailed: messages and data
/// included after the user confirmed; still masked for user and computer names.
/// </summary>
public enum ExportDetail
{
    Minimal,
    Detailed,
}

public sealed record ExportOptions
{
    public ExportDetail Detail { get; init; } = ExportDetail.Minimal;
    public bool UseUtc { get; init; }

    /// <summary>Applied to every exported text (again), in case masking at collection was switched off.</summary>
    public PrivacyMasker Masker { get; init; } = PrivacyMasker.Generic;

    public TimeDisplay Display => UseUtc ? TimeDisplay.Utc : TimeDisplay.Local;

    public string Clean(string? text)
    {
        var masked = Masker.Mask(text);
        return Detail == ExportDetail.Minimal ? PrivacyMasker.MaskDeviceInstances(masked) : masked;
    }

    public string PrivacyLabel => Detail == ExportDetail.Minimal
        ? "개인정보 최소 (이벤트 메시지·데이터 제외, 사용자·컴퓨터 이름과 장치 인스턴스 ID 가림)"
        : "상세 (사용자가 확인함: 이벤트 메시지·데이터 포함, 사용자·컴퓨터 이름은 가림)";
}

internal static class ExportJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
    }
}

public static class EventExporter
{
    public const int FormatVersion = 1;

    public static void WriteCsv(IReadOnlyList<StoredEvent> events, string path, ExportOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var csv = new CsvWriter(path);
        var detailed = options.Detail == ExportDetail.Detailed;
        var header = new List<object?>
        {
            "time_utc", "time_display", "time_zone", "channel", "provider", "event_id", "record_id", "windows_level", "severity", "category",
            "category_name", "title", "group_key", "source", "is_virtual",
        };
        if (detailed)
        {
            header.AddRange(["signature", "message", "data"]);
        }

        csv.Row([.. header]);
        foreach (var e in events)
        {
            var row = new List<object?>
            {
                IsoTime.Utc(e.TimeCreated), options.Display.Full(e.TimeCreated), options.Display.ZoneLabel, e.Channel, e.Provider, e.EventId, e.RecordId,
                e.Level, e.Severity.ToString(), e.Category, EventCategories.Name(e.Category), options.Clean(e.Title), e.GroupKey, e.Source, e.IsVirtual,
            };
            if (detailed)
            {
                row.AddRange([options.Clean(e.Signature), options.Clean(e.Message),
                    string.Join("; ", e.Data.Select(d => $"{d.Name ?? "-"}={options.Clean(d.Value)}"))]);
            }

            csv.Row([.. row]);
        }
    }

    public static void WriteJson(IReadOnlyList<StoredEvent> events, string path, ExportOptions options)
    {
        var detailed = options.Detail == ExportDetail.Detailed;
        ExportJson.Write(path, new
        {
            formatVersion = FormatVersion,
            product = AppInfo.ProductName,
            appVersion = AppInfo.Version,
            privacy = options.PrivacyLabel,
            timeZone = options.Display.ZoneLabel,
            includesVirtualData = events.Any(e => e.IsVirtual),
            events = events.Select(e => new
            {
                timeUtc = IsoTime.Utc(e.TimeCreated),
                timeDisplay = options.Display.Full(e.TimeCreated),
                e.Channel,
                e.Provider,
                e.EventId,
                e.RecordId,
                windowsLevel = e.Level,
                severity = e.Severity,
                e.Category,
                categoryName = EventCategories.Name(e.Category),
                title = options.Clean(e.Title),
                e.GroupKey,
                e.Source,
                e.IsVirtual,
                signature = detailed ? options.Clean(e.Signature) : null,
                message = detailed ? options.Clean(e.Message) : null,
                data = detailed ? e.Data.Select(d => new { name = d.Name, value = options.Clean(d.Value) }).ToList() : null,
            }).ToList(),
        });
    }
}

public static class TimelineExporter
{
    public static void WriteCsv(IReadOnlyList<TimelineItem> items, string path, ExportOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var csv = new CsvWriter(path);
        csv.Row("time_utc", "time_display", "end_display", "time_is_approximate", "kind", "source", "title", "detail", "severity", "count", "is_user_input", "is_virtual");
        foreach (var i in items)
        {
            csv.Row(IsoTime.Utc(i.Time), options.Display.Full(i.Time), i.End is { } end ? options.Display.Full(end) : null, i.TimeIsApproximate,
                EventCategories.KindName(i.Kind), i.SourceLabel, options.Clean(i.Title), options.Clean(i.Detail), i.Severity.ToString(), i.Count, i.IsUserInput, i.IsVirtual);
        }
    }

    public static void WriteJson(IReadOnlyList<TimelineItem> items, string path, ExportOptions options)
        => ExportJson.Write(path, new
        {
            product = AppInfo.ProductName,
            privacy = options.PrivacyLabel,
            timeZone = options.Display.ZoneLabel,
            items = items.Select(i => new
            {
                timeUtc = IsoTime.Utc(i.Time),
                endUtc = i.End is { } end ? IsoTime.Utc(end) : null,
                i.TimeIsApproximate,
                kind = EventCategories.KindName(i.Kind),
                source = i.SourceLabel,
                title = options.Clean(i.Title),
                detail = options.Clean(i.Detail),
                i.Severity,
                i.Count,
                i.IsUserInput,
                i.IsVirtual,
            }).ToList(),
        });
}

public static class SensorExporter
{
    public static void WriteCsv(SensorSeries series, string path, ExportOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var csv = new CsvWriter(path);
        csv.Row("time_utc", "time_display", "cpu_util_pct", "ram_used_bytes", "ram_total_bytes", "gpu_index", "gpu_util_pct", "gpu_temp_c", "gpu_power_w",
            "gpu_power_limit_w", "vram_used_bytes", "vram_total_bytes", "gpu_clock_mhz", "gpu_mem_clock_mhz", "gpu_fan_pct", "bucket_ms");
        var gpu = series.Gpu.Where(g => g.GpuIndex == 0).ToDictionary(g => g.TimeMs);
        foreach (var s in series.System)
        {
            gpu.TryGetValue(s.TimeMs, out var g);
            var t = DateTimeOffset.FromUnixTimeMilliseconds(s.TimeMs);
            csv.Row(IsoTime.Utc(t), options.Display.Full(t), s.CpuUtil, s.RamUsed, s.RamTotal, g?.GpuIndex, g?.Util, g?.TempC, g?.PowerW, g?.PowerLimitW,
                g?.VramUsed, g?.VramTotal, g?.ClockGfxMhz, g?.ClockMemMhz, g?.FanPct, series.BucketMs);
        }
    }
}

public static class ReportExporter
{
    public static ReportDocument Build(DiagnosticReport report, ExportOptions options)
    {
        var d = options.Display;
        var doc = new ReportDocument($"{AppInfo.ProductName} 진단 보고서");
        doc.P($"생성 {d.Full(report.GeneratedAt)} ({d.ZoneLabel}) · 분석 기간 {d.Full(report.Range.From)} ~ {d.Full(report.Range.To)} · 앱 버전 {AppInfo.Version}");
        doc.P(string.Create(CultureInfo.InvariantCulture,
            $"이벤트 {report.EventCount}건 · 센서 기록 약 {report.Coverage.SensorCoverageHours:0.#}시간 · 수집 누락·제한 {report.Coverage.GapCount}건 · 내보내기 수준: {options.PrivacyLabel}"));
        doc.Info("이 보고서는 진단을 돕는 자료입니다. '확인된 사실', '관측된 연관성', '원인 후보(추가 검증 필요)'를 구분하며, 어떤 항목도 원인을 확정하지 않습니다.");
        if (report.IncludesVirtual)
        {
            doc.Warn("가상(테스트) 이벤트가 포함되어 있습니다. 실제 PC의 진단 결과가 아닙니다.");
        }

        if (report.IncludesMockSensors)
        {
            doc.Warn("MOCK(가상) 센서 값이 포함되어 있습니다. 실제 하드웨어 측정이 아닙니다.");
        }

        void Findings(string heading, IReadOnlyList<Finding> findings, string empty)
        {
            doc.H(2, heading);
            if (findings.Count == 0)
            {
                doc.P(empty);
                return;
            }

            foreach (var f in findings)
            {
                doc.H(3, $"[{LevelText.Severity(f.Severity)}] {options.Clean(f.Title)}");
                doc.P(options.Clean(f.Detail));
                doc.List(f.Evidence.Select(e => "근거: " + options.Clean(e.Text) + (e.Time is { } t ? $" ({d.Full(t)})" : "")));
                if (f.Verification.Count > 0)
                {
                    doc.P("추가 검증 방법:");
                    doc.List(f.Verification.Select(options.Clean), numbered: true);
                }

                if (f.Caution is not null)
                {
                    doc.Info(options.Clean(f.Caution));
                }
            }
        }

        Findings("1. 확인된 사실", report.Facts, "분석 기간에 경고 이상 문제 기록이 없습니다.");
        Findings("2. 관측된 연관성", report.Associations, "통계적으로 우연보다 뚜렷한 연관성은 발견되지 않았습니다.");
        Findings("3. 원인 후보 (추가 검증 필요)", report.Hypotheses, "근거가 충분한 원인 후보가 없습니다.");

        doc.H(2, "4. 시스템 충돌·비정상 종료");
        doc.Grid(["시각(기준)", "종류", "충돌 시각 근거", "재부팅 기록", "덤프", "직전 30분 기록"],
            report.Crashes.Select(c => (IReadOnlyList<string>)
            [
                d.Full(c.ReferenceTime), c.KindText, c.CrashTimeSource, c.BootTime is { } b ? d.Full(b) : "N/A",
                c.Dump is { } dump ? $"{dump.FileName} ({DumpStatus.Name(dump.Status)})" : c.DumpPathRecorded is null ? "기록 없음" : "기록됨, 파일 확인 불가",
                string.Join(", ", c.EventsBefore.GroupBy(e => e.Category).Select(g => $"{EventCategories.Name(g.Key)} {g.Count()}건")),
            ]));
        if (report.Crashes.Count == 0)
        {
            doc.P("분석 기간에 비정상 종료 기록이 없습니다.");
        }

        doc.H(2, "5. 반복되는 문제");
        doc.Grid(["문제", "분류", "횟수", "일수", "처음", "마지막", "추세"],
            report.Repeats.Take(30).Select(r => (IReadOnlyList<string>)
            [
                options.Clean(r.Group.Title), EventCategories.Name(r.Group.Category), r.Group.Count.ToString(CultureInfo.InvariantCulture),
                r.Group.DistinctDays.ToString(CultureInfo.InvariantCulture), d.Full(r.Group.First), d.Full(r.Group.Last), r.Trend,
            ]));

        doc.H(2, "6. 구성 변경 (스냅샷 비교)");
        foreach (var change in report.Changes)
        {
            doc.P($"{d.Full(change.WindowFrom)} ~ {d.Full(change.WindowTo)} 사이{(change.IsMock ? " (MOCK 스냅샷)" : "")}");
            doc.List(change.Changes.Select(c => options.Clean(c.Describe())));
        }

        if (report.Changes.Count == 0)
        {
            doc.P("분석 기간에 감지된 구성 변경이 없습니다 (스냅샷은 앱 실행 중에만 저장).");
        }

        doc.H(2, "7. 수집 범위와 한계");
        doc.List(report.Limitations.Select(options.Clean));
        doc.Grid(["로그", "상태", "가장 오래된 기록", "마지막 확인"],
            report.Coverage.Channels.Select(c => (IReadOnlyList<string>)
            [
                c.Channel, ChannelStatusText.Name(c.Status), c.OldestEventTime is { } o ? d.Full(o) : "N/A", c.LastScan is { } s ? d.Full(s) : "N/A",
            ]));
        return doc;
    }

    public static void WriteMarkdown(DiagnosticReport report, string path, ExportOptions options) => Write(path, Build(report, options).ToMarkdown());

    public static void WriteHtml(DiagnosticReport report, string path, ExportOptions options) => Write(path, Build(report, options).ToHtml());

    internal static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}

public static class ComparisonExporter
{
    public static ReportDocument Build(BaselineComparison c, ExportOptions options)
    {
        var d = options.Display;
        var doc = new ReportDocument($"기준선 전후 비교: {options.Clean(c.BaselineName)}");
        doc.P($"기준 시각 {d.Full(c.BaselineTime)} ({d.ZoneLabel}) · 이전 {d.Full(c.Before.From)} ~ {d.Full(c.Before.To)} · 이후 {d.Full(c.After.From)} ~ {d.Full(c.After.To)}");
        foreach (var note in c.Notes)
        {
            doc.Info(options.Clean(note));
        }

        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        static string P(double? p) => p is double v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "N/A";
        doc.H(2, "오류 빈도 (하루당)");
        doc.Grid(["분류", "이전 건수", "이후 건수", "이전/일", "이후/일", "p", "판단"],
            c.Categories.Concat(c.Crashes is null ? [] : [c.Crashes]).Select(r => (IReadOnlyList<string>)
            [
                r.Name, r.BeforeCount.ToString(CultureInfo.InvariantCulture), r.AfterCount.ToString(CultureInfo.InvariantCulture), F(r.BeforePerDay), F(r.AfterPerDay), P(r.PValue), r.Assessment,
            ]));
        doc.H(2, "새로 나타난 문제");
        doc.List(c.NewGroups.Select(g => $"{options.Clean(g.Title)} ({g.AfterCount}건)"));
        doc.H(2, "사라진 문제");
        doc.List(c.GoneGroups.Select(g => $"{options.Clean(g.Title)} (이전 {g.BeforeCount}건)"));
        doc.H(2, "센서 통계 (실측, 앱 실행 중 기록분)");
        doc.Grid(["지표", "이전 평균", "이후 평균", "이전 최대", "이후 최대", "이전 샘플", "이후 샘플"],
            c.Sensors.Select(s => (IReadOnlyList<string>)
            [
                $"{s.Name} ({s.Unit})", SensorMetrics.Format(s.Before.Mean), SensorMetrics.Format(s.After.Mean), SensorMetrics.Format(s.Before.Max),
                SensorMetrics.Format(s.After.Max), s.Before.Count.ToString(CultureInfo.InvariantCulture), s.After.Count.ToString(CultureInfo.InvariantCulture),
            ]));
        doc.H(2, "구성 차이 (기준선 시점 vs 이후 구간 끝)");
        doc.List(c.ConfigChanges.Select(x => options.Clean(x.Describe())));
        if (c.ConfigChanges.Count == 0)
        {
            doc.P("구성 차이가 없거나 비교할 스냅샷이 없습니다.");
        }

        doc.H(2, "같은 기간의 다른 변경");
        doc.List(c.OtherChanges.Select(options.Clean));
        return doc;
    }

    public static void WriteJson(BaselineComparison c, string path, ExportOptions options)
        => ExportJson.Write(path, new { product = AppInfo.ProductName, privacy = options.PrivacyLabel, comparison = Sanitize(c, options) });

    public static void WriteCsv(BaselineComparison c, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var csv = new CsvWriter(path);
        csv.Row("key", "name", "before_count", "after_count", "before_per_day", "after_per_day", "ratio", "p_value", "assessment", "before_days", "after_days");
        foreach (var r in c.Categories.Concat(c.Severities).Concat(c.Crashes is null ? [] : [c.Crashes]))
        {
            csv.Row(r.Key, r.Name, r.BeforeCount, r.AfterCount, r.BeforePerDay, r.AfterPerDay, r.Ratio, r.PValue, r.Assessment, c.BeforeDays, c.AfterDays);
        }
    }

    public static void WriteMarkdown(BaselineComparison c, string path, ExportOptions options) => ReportExporter.Write(path, Build(c, options).ToMarkdown());

    public static void WriteHtml(BaselineComparison c, string path, ExportOptions options) => ReportExporter.Write(path, Build(c, options).ToHtml());

    public static string ToStoredJson(BaselineComparison c) => JsonSerializer.Serialize(c, ExportJson.Options);

    private static BaselineComparison Sanitize(BaselineComparison c, ExportOptions o) => c with
    {
        BaselineName = o.Clean(c.BaselineName),
        NewGroups = c.NewGroups.Select(g => g with { Title = o.Clean(g.Title) }).ToList(),
        GoneGroups = c.GoneGroups.Select(g => g with { Title = o.Clean(g.Title) }).ToList(),
        ChangedGroups = c.ChangedGroups.Select(g => g with { Title = o.Clean(g.Title) }).ToList(),
        OtherChanges = c.OtherChanges.Select(o.Clean).ToList(),
        ConfigChanges = c.ConfigChanges.Select(x => x with { Before = o.Clean(x.Before), After = o.Clean(x.After), Item = o.Clean(x.Item) }).ToList(),
    };
}

public static class ConfigExporter
{
    /// <summary>The configuration snapshot as JSON (it never contains user names, computer names or serial numbers).</summary>
    public static void WriteJson(SystemSnapshot snapshot, string path) => ExportJson.Write(path, new { product = AppInfo.ProductName, snapshot });

    public static ReportDocument BuildComparison(SystemSnapshot before, SystemSnapshot after, ExportOptions options)
    {
        var d = options.Display;
        var changes = SnapshotDiff.Compare(before, after);
        var doc = new ReportDocument("시스템 구성 비교");
        doc.P($"이전 {d.Full(before.CollectedAt)} · 이후 {d.Full(after.CollectedAt)} ({d.ZoneLabel})");
        if (before.IsMock || after.IsMock)
        {
            doc.Warn("MOCK 스냅샷이 포함되어 있습니다.");
        }

        doc.Grid(["영역", "항목", "이전", "이후", "변경"],
            changes.Select(c => (IReadOnlyList<string>)
                [c.Area, options.Clean(c.Item), options.Clean(c.Before) is { Length: > 0 } b ? b : "-", options.Clean(c.After) is { Length: > 0 } a ? a : "-", c.Kind.ToString()]));
        if (changes.Count == 0)
        {
            doc.P("두 스냅샷 사이에 비교 가능한 구성 차이가 없습니다.");
        }

        doc.List(before.Issues.Concat(after.Issues).Select(i => $"읽지 못한 항목: {i.Part} — {i.Message}"));
        return doc;
    }

    public static void WriteComparison(SystemSnapshot before, SystemSnapshot after, string path, ExportOptions options, bool html)
    {
        var doc = BuildComparison(before, after, options);
        ReportExporter.Write(path, html ? doc.ToHtml() : doc.ToMarkdown());
    }
}

/// <summary>A folder with everything for sharing a diagnosis: report, events, timeline, sensors, configuration.</summary>
public static class ExportBundle
{
    public static IReadOnlyList<string> Write(DiagnosticsStore store, DiagnosticReport report, string directory, ExportOptions options)
    {
        Directory.CreateDirectory(directory);
        var files = new List<string>();
        string Add(string name)
        {
            var path = Path.Combine(directory, name);
            files.Add(path);
            return path;
        }

        ReportExporter.WriteHtml(report, Add("report.html"), options);
        ReportExporter.WriteMarkdown(report, Add("report.md"), options);
        var events = store.QueryEvents(new EventQuery { From = report.Range.From, To = report.Range.To, Limit = 100_000, Descending = false });
        EventExporter.WriteCsv(events, Add("events.csv"), options);
        EventExporter.WriteJson(events, Add("events.json"), options);
        var timeline = TimelineBuilder.Build(store, report.Range, incidents: report.Crashes);
        TimelineExporter.WriteCsv(timeline, Add("timeline.csv"), options);
        SensorExporter.WriteCsv(store.QuerySensors(report.Range.From, report.Range.To, maxPoints: 50_000), Add("sensors.csv"), options);
        if (store.ListSnapshots().LastOrDefault() is { } latest)
        {
            ConfigExporter.WriteJson(SystemSnapshot.FromJson(latest.Json), Add("configuration.json"));
        }

        var readme = new StringBuilder()
            .AppendLine($"{AppInfo.ProductName} {AppInfo.Version} 진단 자료")
            .AppendLine($"내보낸 시각: {options.Display.WithZone(DateTimeOffset.UtcNow)}")
            .AppendLine($"분석 기간: {options.Display.Full(report.Range.From)} ~ {options.Display.Full(report.Range.To)} ({options.Display.ZoneLabel})")
            .AppendLine($"개인정보 수준: {options.PrivacyLabel}")
            .AppendLine(report.IncludesVirtual ? "주의: 가상(테스트) 이벤트가 포함되어 있습니다." : "")
            .AppendLine(report.IncludesMockSensors ? "주의: MOCK(가상) 센서 값이 포함되어 있습니다." : "")
            .AppendLine("report.html/report.md: 진단 보고서 (사실·연관성·원인 후보 구분, 원인 확정 아님)")
            .AppendLine("events.csv/json: 이벤트 목록 · timeline.csv: 타임라인 · sensors.csv: 센서 기록 · configuration.json: 최신 구성 스냅샷")
            .AppendLine("시각은 UTC(time_utc)와 표시 시간대(time_display)를 함께 기록했습니다.");
        ReportExporter.Write(Add("README.txt"), readme.ToString());
        return files;
    }
}
