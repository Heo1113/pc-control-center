using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinDiag.Analysis;
using WinDiag.Core.Events;
using WinDiag.Core.Records;
using WinDiag.Export;

namespace WinDiag.Presentation.ViewModels;

/// <summary>
/// One timeline of system changes, errors, program crashes, restarts, recording periods and user records, plus the form
/// for recording changes the app cannot read itself (BIOS settings, EXPO, voltages). User input is always labelled.
/// </summary>
public sealed partial class TimelineViewModel : PageViewModel
{
    private IReadOnlyList<TimelineItem> _items = [];
    private DateTimeOffset? _around;

    public TimelineViewModel(ViewContext context) : base(context)
    {
        Ranges = [new("최근 24시간", 1), new("최근 7일", 7), new("최근 30일", 30), new("최근 90일", 90), new("최근 1년", 365)];
        SelectedRange = Ranges[2];
        Kinds = new ObservableCollection<Selectable<TimelineKind>>(Enum.GetValues<TimelineKind>()
            .Select(k => new Selectable<TimelineKind>(k, EventCategories.KindName(k), k != TimelineKind.Info)));
        RecordKinds = UserRecordKinds.All.Select(k => new Option<string>(k.Name, k.Key)).ToList();
        NewKind = RecordKinds[1];
        NewDate = DateTime.Today;
        NewTime = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    public override string Title => "타임라인";
    public override string Glyph => "";

    public IReadOnlyList<Option<int>> Ranges { get; }
    public ObservableCollection<Selectable<TimelineKind>> Kinds { get; }
    public ObservableCollection<TimelineRow> Items { get; } = [];
    public IReadOnlyList<Option<string>> RecordKinds { get; }
    public ObservableCollection<UserValueChange> NewValues { get; } = [];

    [ObservableProperty] public partial Option<int> SelectedRange { get; set; }
    [ObservableProperty] public partial bool HideRoutine { get; set; } = true;
    [ObservableProperty] public partial TimelineRow? SelectedItem { get; set; }
    [ObservableProperty] public partial string SelectedDetail { get; set; } = "";
    [ObservableProperty] public partial Option<string> NewKind { get; set; }
    [ObservableProperty] public partial DateTime? NewDate { get; set; }
    [ObservableProperty] public partial string NewTime { get; set; }
    [ObservableProperty] public partial string NewTitle { get; set; } = "";
    [ObservableProperty] public partial string NewDetail { get; set; } = "";
    [ObservableProperty] public partial string NewValueName { get; set; } = "";
    [ObservableProperty] public partial string NewValueBefore { get; set; } = "";
    [ObservableProperty] public partial string NewValueAfter { get; set; } = "";
    [ObservableProperty] public partial string NewValueUnit { get; set; } = "";

    partial void OnSelectedItemChanged(TimelineRow? value)
    {
        SelectedDetail = value is null ? "" : $"{value.Time} · {value.Kind} · 출처: {value.Source}\n{value.Title}\n{value.Detail}";
    }

    public override async Task OnActivatedAsync()
    {
        // The first load shows the day around a navigation target if one was set before it started.
        var loadedBefore = HasStartedFirstLoad;
        await LoadOnce(RefreshAsync);
        if (loadedBefore && _around is not null)
        {
            await RefreshAsync();
        }
    }

    /// <summary>Shows the day around a time (navigation from other screens).</summary>
    public void Around(DateTimeOffset time)
    {
        _around = time;
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    [RelayCommand]
    private void AddValue()
    {
        if (string.IsNullOrWhiteSpace(NewValueName))
        {
            StatusText = "값 이름을 입력하세요 (예: VDD, 메모리 속도).";
            return;
        }

        NewValues.Add(new UserValueChange(NewValueName.Trim(), Blank(NewValueBefore), Blank(NewValueAfter), Blank(NewValueUnit)));
        NewValueName = NewValueBefore = NewValueAfter = NewValueUnit = "";
    }

    [RelayCommand]
    private void RemoveValue(UserValueChange? value)
    {
        if (value is not null)
        {
            NewValues.Remove(value);
        }
    }

    [RelayCommand]
    private async Task SaveRecord()
    {
        if (string.IsNullOrWhiteSpace(NewTitle))
        {
            StatusText = "기록 제목을 입력하세요.";
            return;
        }

        if (!TryTime(NewDate, NewTime, out var time))
        {
            StatusText = "날짜와 시각(HH:mm)을 확인하세요.";
            return;
        }

        var record = new UserRecord
        {
            Time = time, Kind = NewKind.Value, Title = NewTitle.Trim(), Detail = Blank(NewDetail), Values = NewValues.ToList(),
        };
        await Task.Run(() => Context.Store.AddUserRecord(record));
        NewTitle = NewDetail = "";
        NewValues.Clear();
        StatusText = "사용자 기록을 저장했습니다. 이 기록은 '사용자 입력'으로 표시되며 측정값과 구분됩니다.";
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteSelectedRecord()
    {
        if (SelectedItem?.UserRecordId is not long id)
        {
            StatusText = "삭제할 사용자 기록을 선택하세요 (Windows 이벤트와 자동 수집 기록은 삭제할 수 없습니다).";
            return;
        }

        if (!Context.Dialogs.Confirm("사용자 기록 삭제", $"이 사용자 기록을 삭제할까요?\n\n{SelectedItem.Title}\n\n수집된 이벤트와 센서 기록에는 영향이 없습니다."))
        {
            return;
        }

        await Task.Run(() => Context.Store.DeleteUserRecord(id));
        StatusText = "사용자 기록을 삭제했습니다.";
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task CreateBaseline()
    {
        if (SelectedItem is not { } item)
        {
            StatusText = "기준선으로 쓸 항목을 선택하세요.";
            return;
        }

        var name = Context.Dialogs.Prompt("기준선 만들기", "기준선 이름 (예: BIOS 3.25 업데이트)", item.Title.Length > 60 ? item.Title[..60] : item.Title);
        if (name is null)
        {
            return;
        }

        await Task.Run(() => Context.Store.AddBaseline(new Baseline { Name = name, Time = item.TimeValue, UserRecordId = item.UserRecordId, Description = item.Title }));
        StatusText = $"기준선 '{name}'을(를) 만들었습니다. 비교 화면에서 전후를 비교할 수 있습니다.";
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedItem is null || Context.Main is not { } main)
        {
            return;
        }

        if (SelectedItem.EventRowId is long id)
        {
            main.ShowEvent(id);
        }
        else if (SelectedItem.IncidentId is { } incident)
        {
            main.CurrentPage = main.Diagnostics;
            _ = main.Diagnostics.SelectIncidentAsync(incident);
        }
        else
        {
            main.ShowHardwareAt(SelectedItem.TimeValue, SelectedItem.Title);
        }
    }

    [RelayCommand]
    private void ShowSensors()
    {
        if (SelectedItem is { } item)
        {
            Context.Main?.ShowHardwareAt(item.TimeValue, item.Title);
        }
    }

    [RelayCommand]
    private Task ExportCsv() => ExportAsync(json: false);

    [RelayCommand]
    private Task ExportJson() => ExportAsync(json: true);

    public Task RefreshAsync() => Busy(async () =>
    {
        var now = Context.Now;
        AnalysisRange range;
        if (_around is { } around)
        {
            range = new AnalysisRange(around.AddHours(-12), Min(around.AddHours(12), now));
            _around = null;
        }
        else
        {
            range = AnalysisRange.LastDays(now, SelectedRange.Value);
        }

        var kinds = Kinds.Where(k => k.IsChecked).Select(k => k.Value).ToHashSet();
        var options = new TimelineOptions { Kinds = kinds, IncludeRoutineBootEvents = !HideRoutine };
        var records = new Dictionary<DateTimeOffset, long>();
        var store = Context.Store;
        _items = await Task.Run(() =>
        {
            foreach (var r in store.ListUserRecords(range.From, range.To))
            {
                records[r.Time] = r.Id;
            }

            return TimelineBuilder.Build(store, range, options);
        });

        var rows = new List<TimelineRow>();
        string? lastDay = null;
        foreach (var item in _items.OrderByDescending(i => i.Time))
        {
            var day = Fmt.Display.Date(item.Time);
            rows.Add(new TimelineRow(day, day != lastDay, Fmt.Clock(item.Time) + (item.End is { } end && end - item.Time > TimeSpan.FromMinutes(1) ? " ~ " + Fmt.Short(end) : ""),
                EventCategories.KindName(item.Kind), item.SourceLabel, item.Title, item.Detail ?? "", Fmt.Severity(item.Severity), item.Severity, item.Count,
                item.IsUserInput, item.IsVirtual, item.TimeIsApproximate, item.EventRowId, item.IncidentId,
                item.Source == TimelineSource.UserRecord && records.TryGetValue(item.Time, out var id) ? id : null, item.Time));
            lastDay = day;
        }

        Replace(Items, rows);
        StatusText = $"{Fmt.Time(range.From)} ~ {Fmt.Time(range.To)} · 항목 {rows.Count}개 (같은 문제가 30분 안에 반복되면 하나로 묶음)";
    });

    private async Task ExportAsync(bool json)
    {
        var options = new ExportOptions { UseUtc = Context.Settings.Ui.UseUtc, Masker = Context.ExportMasker };
        var path = Context.Dialogs.SaveFile("타임라인 내보내기", json ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv",
            $"timeline-{Context.Now.ToLocalTime():yyyyMMdd-HHmm}.{(json ? "json" : "csv")}", Context.Engine.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        var items = _items;
        await Task.Run(() =>
        {
            if (json)
            {
                TimelineExporter.WriteJson(items, path, options);
            }
            else
            {
                TimelineExporter.WriteCsv(items, path, options);
            }
        });
        StatusText = $"타임라인 {items.Count}개 항목을 내보냈습니다.";
    }

    internal static bool TryTime(DateTime? date, string? time, out DateTimeOffset result)
    {
        result = default;
        if (date is not { } d || !TimeSpan.TryParseExact((time ?? "").Trim(), [@"hh\:mm", @"h\:mm", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var t))
        {
            return false;
        }

        var local = DateTime.SpecifyKind(d.Date + t, Fmt.Display.IsUtc ? DateTimeKind.Utc : DateTimeKind.Local);
        result = new DateTimeOffset(local).ToUniversalTime();
        return true;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
