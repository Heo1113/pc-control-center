using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinDiag.Analysis;
using WinDiag.Core.Records;
using WinDiag.Core.Snapshots;
using WinDiag.Export;

namespace WinDiag.Presentation.ViewModels;

/// <summary>
/// Before/after comparison around a baseline (BIOS update, driver update, EXPO on, voltage change, Windows update),
/// and a direct comparison of two configuration snapshots.
/// </summary>
public sealed partial class CompareViewModel : PageViewModel
{
    private BaselineComparison? _result;

    public CompareViewModel(ViewContext context) : base(context)
    {
        Windows = [new("3일", 3), new("7일", 7), new("14일", 14), new("30일", 30), new("90일", 90)];
        BeforeWindow = Windows[2];
        AfterWindow = Windows[2];
        NewDate = DateTime.Today;
        NewTime = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    public override string Title => "비교";
    public override string Glyph => "";

    public IReadOnlyList<Option<int>> Windows { get; }
    public ObservableCollection<BaselineRow> Baselines { get; } = [];
    public ObservableCollection<UserRecordRow> Records { get; } = [];
    public ObservableCollection<RateRow> Rates { get; } = [];
    public ObservableCollection<string> NewGroups { get; } = [];
    public ObservableCollection<string> GoneGroups { get; } = [];
    public ObservableCollection<SensorCompareRow> Sensors { get; } = [];
    public ObservableCollection<ConfigRow> ConfigChanges { get; } = [];
    public ObservableCollection<string> OtherChanges { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<SnapshotRow> Snapshots { get; } = [];
    public ObservableCollection<ConfigRow> SnapshotDiffRows { get; } = [];

    [ObservableProperty] public partial BaselineRow? SelectedBaseline { get; set; }
    [ObservableProperty] public partial UserRecordRow? SelectedRecord { get; set; }
    [ObservableProperty] public partial Option<int> BeforeWindow { get; set; }
    [ObservableProperty] public partial Option<int> AfterWindow { get; set; }
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial string ResultTitle { get; set; } = "";
    [ObservableProperty] public partial string NewName { get; set; } = "";
    [ObservableProperty] public partial DateTime? NewDate { get; set; }
    [ObservableProperty] public partial string NewTime { get; set; }
    [ObservableProperty] public partial string NewDescription { get; set; } = "";
    [ObservableProperty] public partial SnapshotRow? SnapshotA { get; set; }
    [ObservableProperty] public partial SnapshotRow? SnapshotB { get; set; }
    [ObservableProperty] public partial string SnapshotDiffText { get; set; } = "";

    public BaselineComparison? Result => _result;

    public override Task OnActivatedAsync() => LoadAsync();

    [RelayCommand]
    private Task Reload() => LoadAsync();

    [RelayCommand]
    private async Task AddBaseline()
    {
        if (string.IsNullOrWhiteSpace(NewName))
        {
            StatusText = "기준선 이름을 입력하세요.";
            return;
        }

        if (!TimelineViewModel.TryTime(NewDate, NewTime, out var time))
        {
            StatusText = "날짜와 시각(HH:mm)을 확인하세요.";
            return;
        }

        var baseline = new Baseline { Name = NewName.Trim(), Time = time, Description = string.IsNullOrWhiteSpace(NewDescription) ? null : NewDescription.Trim() };
        await Task.Run(() => Context.Store.AddBaseline(baseline));
        NewName = NewDescription = "";
        await LoadAsync();
        SelectedBaseline = Baselines.FirstOrDefault(b => b.Name == baseline.Name && b.TimeValue == time);
    }

    [RelayCommand]
    private async Task AddBaselineFromRecord()
    {
        if (SelectedRecord is not { } record)
        {
            StatusText = "기준선으로 만들 사용자 기록을 선택하세요.";
            return;
        }

        await Task.Run(() => Context.Store.AddBaseline(new Baseline { Name = record.Title, Time = record.TimeValue, UserRecordId = record.Id, Description = record.Values }));
        await LoadAsync();
        SelectedBaseline = Baselines.FirstOrDefault(b => b.UserRecordId == record.Id);
    }

    [RelayCommand]
    private async Task AddBaselineNow()
    {
        var name = Context.Dialogs.Prompt("지금을 기준선으로", "기준선 이름 (예: GPU 드라이버 업데이트 직전)", $"기준선 {Context.Now.ToLocalTime():MM-dd HH:mm}");
        if (name is null)
        {
            return;
        }

        var now = Context.Now;
        await Task.Run(() => Context.Store.AddBaseline(new Baseline { Name = name, Time = now }));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteBaseline()
    {
        if (SelectedBaseline is not { } baseline)
        {
            return;
        }

        if (!Context.Dialogs.Confirm("기준선 삭제", $"기준선 '{baseline.Name}'을(를) 삭제할까요?\n저장된 비교 결과도 함께 삭제됩니다. 이벤트·센서 기록에는 영향이 없습니다."))
        {
            return;
        }

        await Task.Run(() => Context.Store.DeleteBaseline(baseline.Id));
        await LoadAsync();
    }

    [RelayCommand]
    private Task Compare() => Busy(async () =>
    {
        if (SelectedBaseline is not { } baseline)
        {
            StatusText = "비교할 기준선을 선택하세요.";
            return;
        }

        var now = Context.Now;
        var result = await Task.Run(() => BaselineComparer.Compare(Context.Store, baseline.Name, baseline.TimeValue, BeforeWindow.Value, AfterWindow.Value, now, baseline.UserRecordId));
        _result = result;
        ResultTitle = $"'{baseline.Name}' ({Fmt.Time(baseline.TimeValue)}) 기준 · 이전 {result.BeforeDays.ToString("0.#", CultureInfo.InvariantCulture)}일 / 이후 {result.AfterDays.ToString("0.#", CultureInfo.InvariantCulture)}일";
        var rates = result.Categories.ToList();
        if (result.Crashes is { } crashes)
        {
            rates.Insert(0, crashes);
        }

        Replace(Rates, rates.Select(r => new RateRow(r.Name, r.BeforeCount, r.AfterCount, r.BeforePerDay.ToString("0.##", CultureInfo.InvariantCulture),
            r.AfterPerDay.ToString("0.##", CultureInfo.InvariantCulture), ChangeText(r),
            r.PValue is double p ? p.ToString("0.####", CultureInfo.InvariantCulture) : Fmt.NA, r.Assessment,
            r.PValue < 0.05 && r.AfterPerDay > r.BeforePerDay ? StatusLevel.Critical : r.PValue < 0.05 ? StatusLevel.Good : StatusLevel.Neutral)));
        Replace(NewGroups, result.NewGroups.Select(g => $"{g.Title} ({g.AfterCount}건)"));
        Replace(GoneGroups, result.GoneGroups.Select(g => $"{g.Title} (이전 {g.BeforeCount}건)"));
        Replace(Sensors, result.Sensors.Select(s => new SensorCompareRow($"{s.Name} ({s.Unit})", Fmt.Num(s.Before.Mean), Fmt.Num(s.After.Mean),
            Fmt.Num(s.Before.Max), Fmt.Num(s.After.Max), $"{s.Before.Count:N0} / {s.After.Count:N0}")));
        Replace(ConfigChanges, result.ConfigChanges.Select(c => new ConfigRow(c.Area, c.Item, c.Before ?? "-", c.After ?? "-", KindText(c.Kind))));
        Replace(OtherChanges, result.OtherChanges);
        Replace(Notes, result.Notes);
        HasResult = true;
        StatusText = "비교 완료. 결과는 원인을 확정하지 않습니다.";
    });

    [RelayCommand]
    private async Task SaveResult()
    {
        if (_result is not { } result || SelectedBaseline is not { } baseline)
        {
            return;
        }

        await Task.Run(() => Context.Store.SaveComparison(baseline.Id, ResultTitle, ComparisonExporter.ToStoredJson(result)));
        StatusText = "비교 결과를 데이터베이스에 저장했습니다.";
    }

    [RelayCommand]
    private async Task ExportResult(string? format)
    {
        if (_result is not { } result)
        {
            return;
        }

        format ??= "html";
        var options = new ExportOptions { UseUtc = Context.Settings.Ui.UseUtc, Masker = Context.ExportMasker };
        var filter = format switch { "json" => "JSON (*.json)|*.json", "csv" => "CSV (*.csv)|*.csv", "md" => "Markdown (*.md)|*.md", _ => "HTML (*.html)|*.html" };
        var path = Context.Dialogs.SaveFile("비교 결과 내보내기", filter, $"wdl-compare-{Context.Now.ToLocalTime():yyyyMMdd-HHmm}.{format}", Context.Engine.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        await Task.Run(() =>
        {
            switch (format)
            {
                case "json":
                    ComparisonExporter.WriteJson(result, path, options);
                    break;
                case "csv":
                    ComparisonExporter.WriteCsv(result, path);
                    break;
                case "md":
                    ComparisonExporter.WriteMarkdown(result, path, options);
                    break;
                default:
                    ComparisonExporter.WriteHtml(result, path, options);
                    break;
            }
        });
        StatusText = $"내보냈습니다: {Path.GetFileName(path)}";
    }

    [RelayCommand]
    private async Task CompareSnapshots()
    {
        if (SnapshotA is not { } a || SnapshotB is not { } b || a.Id == b.Id)
        {
            StatusText = "비교할 서로 다른 스냅샷 두 개를 고르세요.";
            return;
        }

        var (before, after) = await Task.Run(() => (Context.Store.GetSnapshot(Math.Min(a.Id, b.Id)), Context.Store.GetSnapshot(Math.Max(a.Id, b.Id))));
        if (before is null || after is null)
        {
            return;
        }

        var changes = SnapshotDiff.Compare(SystemSnapshot.FromJson(before.Json), SystemSnapshot.FromJson(after.Json));
        Replace(SnapshotDiffRows, changes.Select(c => new ConfigRow(c.Area, c.Item, c.Before ?? "-", c.After ?? "-", KindText(c.Kind))));
        SnapshotDiffText = changes.Count == 0 ? "비교 가능한 항목에서 차이가 없습니다." : $"차이 {changes.Count}건 ({Fmt.Time(before.FirstSeen)} → {Fmt.Time(after.FirstSeen)})";
    }

    [RelayCommand]
    private async Task ExportSnapshotComparison()
    {
        if (SnapshotA is not { } a || SnapshotB is not { } b || a.Id == b.Id)
        {
            return;
        }

        var path = Context.Dialogs.SaveFile("구성 비교 내보내기", "HTML (*.html)|*.html|Markdown (*.md)|*.md", $"wdl-config-{Context.Now.ToLocalTime():yyyyMMdd-HHmm}.html",
            Context.Engine.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        var options = new ExportOptions { UseUtc = Context.Settings.Ui.UseUtc, Masker = Context.ExportMasker };
        await Task.Run(() =>
        {
            var before = Context.Store.GetSnapshot(Math.Min(a.Id, b.Id))!;
            var after = Context.Store.GetSnapshot(Math.Max(a.Id, b.Id))!;
            ConfigExporter.WriteComparison(SystemSnapshot.FromJson(before.Json), SystemSnapshot.FromJson(after.Json), path, options,
                html: !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        });
        StatusText = $"구성 비교를 저장했습니다: {Path.GetFileName(path)}";
    }

    private Task LoadAsync() => Busy(async () =>
    {
        var store = Context.Store;
        var (baselines, records, snapshots) = await Task.Run(() => (store.ListBaselines(), store.ListUserRecords(), store.ListSnapshots()));
        var selected = SelectedBaseline?.Id;
        Replace(Baselines, baselines.OrderByDescending(b => b.Time).Select(b => new BaselineRow(b.Id, b.Name, Fmt.Time(b.Time), b.Description ?? "", b.Time, b.UserRecordId)));
        Replace(Records, records.OrderByDescending(r => r.Time).Select(r => new UserRecordRow(r.Id, Fmt.Time(r.Time), UserRecordKinds.Name(r.Kind), r.Title,
            string.Join("; ", r.Values.Select(v => $"{v.Name} {v.Before} → {v.After}{(v.Unit is null ? "" : " " + v.Unit)}")), r.Time)));
        Replace(Snapshots, snapshots.OrderByDescending(s => s.FirstSeen).Select(s =>
        {
            var snapshot = SystemSnapshot.FromJson(s.Json);
            var summary = $"{snapshot.Os?.DisplayName} {snapshot.Os?.BuildText} · BIOS {snapshot.Bios?.Version ?? Fmt.NA} · GPU 드라이버 {snapshot.NvidiaDriverVersion ?? snapshot.Gpus.FirstOrDefault()?.DriverVersion ?? Fmt.NA}";
            return new SnapshotRow(s.Id, Fmt.Time(s.FirstSeen), Fmt.Time(s.LastConfirmed), s.Source, summary, s.IsMock);
        }));
        SelectedBaseline = Baselines.FirstOrDefault(b => b.Id == selected) ?? Baselines.FirstOrDefault();
        if (Snapshots.Count >= 2)
        {
            SnapshotB ??= Snapshots[0];
            SnapshotA ??= Snapshots[1];
        }

        StatusText = $"기준선 {Baselines.Count}개 · 사용자 기록 {Records.Count}개 · 구성 스냅샷 {Snapshots.Count}개";
    });

    private static string ChangeText(RateComparison r)
    {
        if (r.BeforeCount + r.AfterCount == 0)
        {
            return "-";
        }

        if (r.BeforePerDay == 0)
        {
            return r.AfterPerDay > 0 ? "새로 발생" : "-";
        }

        var change = (r.AfterPerDay - r.BeforePerDay) / r.BeforePerDay * 100;
        return (change >= 0 ? "+" : "") + change.ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    private static string KindText(ChangeKind kind) => kind switch { ChangeKind.Added => "추가", ChangeKind.Removed => "제거", _ => "변경" };
}
