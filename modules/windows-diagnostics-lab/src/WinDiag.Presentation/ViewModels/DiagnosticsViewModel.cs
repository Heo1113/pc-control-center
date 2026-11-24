using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinDiag.Analysis;
using WinDiag.Core.Crash;
using WinDiag.Core.Events;
using WinDiag.Export;

namespace WinDiag.Presentation.ViewModels;

/// <summary>
/// The diagnostic report: facts, observed associations and cause candidates kept in separate lists, crash incidents
/// with their context, repeated problems and the limits of the data. Nothing here states a confirmed cause.
/// </summary>
public sealed partial class DiagnosticsViewModel : PageViewModel
{
    private DiagnosticReport? _report;

    public DiagnosticsViewModel(ViewContext context) : base(context)
    {
        Ranges = [new("최근 7일", 7), new("최근 30일", 30), new("최근 90일", 90), new("최근 1년", 365)];
        SelectedRange = Ranges[1];
    }

    public override string Title => "진단";
    public override string Glyph => "";

    public IReadOnlyList<Option<int>> Ranges { get; }
    public ObservableCollection<FindingRow> Facts { get; } = [];
    public ObservableCollection<FindingRow> Associations { get; } = [];
    public ObservableCollection<FindingRow> Hypotheses { get; } = [];
    public ObservableCollection<CrashRow> Crashes { get; } = [];
    public ObservableCollection<RepeatRow> Repeats { get; } = [];
    public ObservableCollection<string> Limitations { get; } = [];
    public ObservableCollection<NameValueRow> CrashFacts { get; } = [];
    public ObservableCollection<EventRow> CrashEventsBefore { get; } = [];
    public ObservableCollection<MetricStatsRow> CrashSensors { get; } = [];

    [ObservableProperty] public partial Option<int> SelectedRange { get; set; }
    [ObservableProperty] public partial bool HasReport { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "분석을 실행하세요.";
    [ObservableProperty] public partial bool IncludesVirtual { get; set; }
    [ObservableProperty] public partial bool IncludesMock { get; set; }
    [ObservableProperty] public partial CrashRow? SelectedCrash { get; set; }
    [ObservableProperty] public partial bool HasCrashDetail { get; set; }
    [ObservableProperty] public partial string CrashNotes { get; set; } = "";
    [ObservableProperty] public partial FindingRow? SelectedFinding { get; set; }

    public DiagnosticReport? Report => _report;

    partial void OnSelectedCrashChanged(CrashRow? value) => ShowCrash(value?.Id);

    public override Task OnActivatedAsync() => LoadOnce(RunAsync);

    public async Task SelectIncidentAsync(string id)
    {
        await LoadOnce(RunAsync); // navigation starts the first analysis; wait for it instead of running a second one
        if (_report is null || _report.Crashes.All(c => c.Id != id))
        {
            SelectedRange = Ranges[^1];
            await RunAsync();
        }

        SelectedCrash = Crashes.FirstOrDefault(c => c.Id == id);
    }

    [RelayCommand]
    private Task Run() => RunAsync();

    [RelayCommand]
    private void OpenEvidence(FindingRow? row)
    {
        if (row?.FirstEventId is long id)
        {
            Context.Main?.ShowEvent(id);
        }
    }

    [RelayCommand]
    private void ShowCrashSensors()
    {
        if (SelectedCrash is not null && _report?.Crashes.FirstOrDefault(c => c.Id == SelectedCrash.Id) is { } incident)
        {
            Context.Main?.ShowHardwareAt(incident.ReferenceTime, incident.KindText);
        }
    }

    [RelayCommand]
    private void OpenCrashEvent(EventRow? row)
    {
        if (row is not null)
        {
            Context.Main?.ShowEvent(row.Id);
        }
    }

    [RelayCommand]
    private Task ExportHtml() => ExportReportAsync("html");

    [RelayCommand]
    private Task ExportMarkdown() => ExportReportAsync("md");

    [RelayCommand]
    private async Task ExportBundle()
    {
        if (_report is not { } report)
        {
            return;
        }

        var options = ExportPrompt.Ask(Context, "진단 자료 묶음 내보내기");
        if (options is null)
        {
            return;
        }

        var folder = Context.Dialogs.PickFolder("진단 자료를 저장할 폴더", Context.Engine.Paths.ExportDirectory);
        if (folder is null)
        {
            return;
        }

        var target = Path.Combine(folder, $"wdl-diagnosis-{Context.Now.ToLocalTime():yyyyMMdd-HHmmss}");
        await Busy(async () =>
        {
            var files = await Task.Run(() => WinDiag.Export.ExportBundle.Write(Context.Store, report, target, options));
            StatusText = $"{files.Count}개 파일을 저장했습니다: {target}";
            Context.Shell.OpenFolder(target);
        });
    }

    public Task RunAsync() => Busy(async () =>
    {
        var now = Context.Now;
        var analysis = Context.Settings.Analysis;
        var options = new DiagnosticOptions
        {
            CorrelationWindowSeconds = analysis.CorrelationWindowSeconds,
            RepeatThreshold = analysis.RepeatThreshold,
            HighGpuTempC = analysis.HighGpuTempC,
            SensorWindowMinutes = analysis.SensorWindowMinutes,
            AcknowledgedEventId = Context.Store.LastAcknowledgedEventId,
        };
        StatusText = "분석 중…";
        var report = await Task.Run(() => DiagnosticAnalyzer.Analyze(Context.Store, AnalysisRange.LastDays(now, SelectedRange.Value), options, now));
        _report = report;
        Replace(Facts, report.Facts.Select(ToRow));
        Replace(Associations, report.Associations.Select(ToRow));
        Replace(Hypotheses, report.Hypotheses.Select(ToRow));
        Replace(Crashes, report.Crashes.OrderByDescending(c => c.ReferenceTime).Select(ToRow));
        Replace(Repeats, report.Repeats.Select(r => new RepeatRow(r.Group.Title, EventCategories.Name(r.Group.Category), r.Group.Count, r.Group.DistinctDays,
            r.PerDay.ToString("0.##", CultureInfo.InvariantCulture), Fmt.Time(r.Group.First), Fmt.Time(r.Group.Last), r.Trend, r.IsNewInRange, r.Group.LatestEventId,
            Fmt.Severity(r.Group.MaxSeverity))));
        Replace(Limitations, report.Limitations);
        IncludesVirtual = report.IncludesVirtual;
        IncludesMock = report.IncludesMockSensors;
        Summary = $"{Fmt.Time(report.Range.From)} ~ {Fmt.Time(report.Range.To)} · 이벤트 {report.EventCount:N0}건 · 확인된 사실 {report.Facts.Count} · 연관성 {report.Associations.Count} · 원인 후보 {report.Hypotheses.Count} · 비정상 종료 {report.Crashes.Count}";
        HasReport = true;
        StatusText = $"분석 완료 {Fmt.Clock(now)}";
        SelectedCrash = Crashes.FirstOrDefault();
    });

    internal static CrashRow ToRow(CrashIncident c) => new(
        c.Id, Fmt.Time(c.ReferenceTime), c.KindText, c.CrashTimeSource, Fmt.Time(c.BootTime),
        c.Dump is { } d ? $"{d.FileName} ({DumpStatus.Name(d.Status)})" : c.DumpPathRecorded is null ? "기록 없음" : "기록됨 (파일 확인 불가)",
        c.EventsBefore.Count == 0 ? "없음" : string.Join(", ", c.EventsBefore.GroupBy(e => e.Category).Select(g => $"{EventCategories.Name(g.Key)} {g.Count()}")),
        c.IsVirtual);

    private static FindingRow ToRow(Finding f) => new(
        f.Id, f.TierName, f.Title, f.Detail ?? "", Fmt.Severity(f.Severity), f.Severity,
        f.Evidence.Select(e => e.Text + (e.Time is { } t ? $" ({Fmt.Time(t)})" : "")).ToList(),
        f.Verification, f.Caution ?? "", f.Strength ?? "", f.Evidence.FirstOrDefault(e => e.EventRowId is not null)?.EventRowId);

    private void ShowCrash(string? id)
    {
        var incident = id is null ? null : _report?.Crashes.FirstOrDefault(c => c.Id == id);
        if (incident is null)
        {
            HasCrashDetail = false;
            return;
        }

        var facts = new List<NameValueRow>
        {
            new("종류", incident.KindText),
            new("충돌 시각", incident.EstimatedCrashTime is { } t ? Fmt.Time(t) : "알 수 없음", incident.CrashTimeSource),
            new("마지막 확인 시각", Fmt.Time(incident.LastKnownAlive), incident.LastKnownAliveSource),
            new("재부팅 기록", Fmt.Time(incident.BootTime)),
            new("같은 유형 횟수", incident.SameCodeCount.ToString(CultureInfo.InvariantCulture)),
        };
        if (incident.BugCheckCode is uint code)
        {
            facts.Add(new NameValueRow("BugCheck 코드", BugCheckCatalog.Describe(code), BugCheckCatalog.Find(code)?.Description));
            for (var i = 0; i < incident.BugCheckParameters.Count; i++)
            {
                facts.Add(new NameValueRow($"매개변수 {i + 1}", incident.BugCheckParameters[i]));
            }
        }

        facts.Add(new NameValueRow("덤프 저장 위치(기록)", incident.DumpPathRecorded ?? "기록 없음"));
        if (incident.Dump is { } dump)
        {
            facts.Add(new NameValueRow("덤프 파일", $"{dump.FileName} · {DumpStatus.Name(dump.Status)}",
                dump.StatusMessage ?? $"크기 {Fmt.Mb(dump.SizeBytes ?? 0)}, 헤더 BugCheck {(dump.BugCheckCode is uint dc ? BugCheckCatalog.Hex(dc) : Fmt.NA)}, 헤더 시각 {Fmt.Time(dump.DumpTime)}, {dump.DumpTypeName}"));
        }

        Replace(CrashFacts, facts);
        Replace(CrashEventsBefore, incident.EventsBefore.Concat(incident.Evidence).OrderBy(e => e.TimeCreated).Select(e => EventRow.From(e, 0)));
        Replace(CrashSensors, incident.Sensors?.Stats.Select(s => new MetricStatsRow($"{s.Name} ({s.Unit})", Fmt.Num(s.BeforeMean), Fmt.Num(s.BeforeMax),
            Fmt.Num(s.AfterMean), Fmt.Num(s.AfterMax), Fmt.Num(s.AtEvent))) ?? []);
        CrashNotes = string.Join("\n", (incident.Sensors is { } sensors ? [sensors.CoverageNote] : Array.Empty<string>()).Concat(incident.Notes));
        HasCrashDetail = true;
    }

    private async Task ExportReportAsync(string extension)
    {
        if (_report is not { } report)
        {
            return;
        }

        var options = new ExportOptions { UseUtc = Context.Settings.Ui.UseUtc, Masker = Context.ExportMasker };
        var path = Context.Dialogs.SaveFile("진단 보고서 저장", extension == "html" ? "HTML (*.html)|*.html" : "Markdown (*.md)|*.md",
            $"wdl-report-{Context.Now.ToLocalTime():yyyyMMdd-HHmm}.{extension}", Context.Engine.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        await Task.Run(() =>
        {
            if (extension == "html")
            {
                ReportExporter.WriteHtml(report, path, options);
            }
            else
            {
                ReportExporter.WriteMarkdown(report, path, options);
            }
        });
        StatusText = $"보고서를 저장했습니다: {Path.GetFileName(path)} (개인정보 최소 수준)";
    }
}
