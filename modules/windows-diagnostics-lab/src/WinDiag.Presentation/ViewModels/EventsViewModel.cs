using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinDiag.Core.Events;
using WinDiag.Core.Records;
using WinDiag.Core.Time;
using WinDiag.Export;
using WinDiag.Storage;

namespace WinDiag.Presentation.ViewModels;

/// <summary>Event search: time range, category, severity, channel, provider and text filters; single events or repeat groups; details.</summary>
public sealed partial class EventsViewModel : PageViewModel
{
    private const int PageSize = 300;
    private EventQuery? _lastQuery;
    private int _loaded;

    public EventsViewModel(ViewContext context) : base(context)
    {
        Ranges =
        [
            new("최근 24시간", 1), new("최근 7일", 7), new("최근 30일", 30), new("최근 90일", 90), new("최근 1년", 365), new("전체", 0), new("직접 지정", -1),
        ];
        SelectedRange = Ranges[2];
        Severities =
        [
            new("전체 (정보 포함)", null), new("경고 이상", DiagSeverity.Warning), new("오류 이상", DiagSeverity.Error), new("심각만", DiagSeverity.Critical),
        ];
        SelectedSeverity = Severities[1];
        Categories = new ObservableCollection<Selectable<string>>(EventCategories.All.Select(c => new Selectable<string>(c.Key, c.Name, true)));
        Channels = [new("전체 로그", null)];
        SelectedChannel = Channels[0];
    }

    public override string Title => "이벤트 탐색";
    public override string Glyph => "";

    public IReadOnlyList<Option<int>> Ranges { get; }
    public IReadOnlyList<Option<DiagSeverity?>> Severities { get; }
    public ObservableCollection<Selectable<string>> Categories { get; }
    public ObservableCollection<Option<string?>> Channels { get; }
    public ObservableCollection<EventRow> Events { get; } = [];
    public ObservableCollection<GroupRow> Groups { get; } = [];
    public ObservableCollection<NameValueRow> DetailFields { get; } = [];
    public ObservableCollection<NameValueRow> DetailFacts { get; } = [];
    public ObservableCollection<NameValueRow> DetailData { get; } = [];

    [ObservableProperty] public partial Option<int> SelectedRange { get; set; }
    [ObservableProperty] public partial DateTime? FromDate { get; set; }
    [ObservableProperty] public partial DateTime? ToDate { get; set; }
    [ObservableProperty] public partial Option<DiagSeverity?> SelectedSeverity { get; set; }
    [ObservableProperty] public partial Option<string?> SelectedChannel { get; set; }
    [ObservableProperty] public partial string ProviderText { get; set; } = "";
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool OnlyNew { get; set; }
    [ObservableProperty] public partial bool GroupMode { get; set; }
    [ObservableProperty] public partial string? GroupFilter { get; set; }
    [ObservableProperty] public partial bool CanLoadMore { get; set; }
    [ObservableProperty] public partial EventRow? SelectedEvent { get; set; }
    [ObservableProperty] public partial GroupRow? SelectedGroup { get; set; }
    [ObservableProperty] public partial bool HasDetail { get; set; }
    [ObservableProperty] public partial string DetailTitle { get; set; } = "";
    [ObservableProperty] public partial string DetailMeaning { get; set; } = "";
    [ObservableProperty] public partial string DetailCaution { get; set; } = "";
    [ObservableProperty] public partial string DetailMessage { get; set; } = "";
    [ObservableProperty] public partial string DetailGroup { get; set; } = "";
    [ObservableProperty] public partial string NoteText { get; set; } = "";

    public bool IsCustomRange => SelectedRange.Value < 0;

    partial void OnSelectedRangeChanged(Option<int> value) => OnPropertyChanged(nameof(IsCustomRange));

    private Task _detailLoad = Task.CompletedTask;
    private long _detailVersion;
    private long? _detailId;

    partial void OnSelectedEventChanged(EventRow? value)
    {
        if (value is not null)
        {
            _detailLoad = LoadDetailAsync(value.Id);
        }
    }

    partial void OnGroupModeChanged(bool value) => _ = SearchAsync();

    public override async Task OnActivatedAsync()
    {
        var channels = Context.Settings.Collection.Channels.Select(c => c.Name).ToList();
        if (Channels.Count != channels.Count + 1)
        {
            Replace(Channels, [new Option<string?>("전체 로그", null), .. channels.Select(c => new Option<string?>(c, c))]);
            SelectedChannel = Channels[0];
        }

        await LoadOnce(SearchAsync);
    }

    /// <summary>Opens one event (navigation from other screens).</summary>
    public async Task OpenEventAsync(long id)
    {
        // The list must be loaded before the opened event's row is added, or the first search would replace it.
        await LoadOnce(SearchAsync);
        var e = await Task.Run(() => Context.Store.GetEvent(id));
        if (e is null)
        {
            StatusText = "이벤트를 찾을 수 없습니다 (보존 기간 정리 등).";
            return;
        }

        if (GroupMode)
        {
            GroupMode = false; // starts a list search
            await SearchAsync(); // runs after it, so the row below is added to the finished list
        }

        var row = EventRow.From(e, Context.Store.LastAcknowledgedEventId);
        if (!Events.Any(r => r.Id == id))
        {
            Events.Insert(0, row);
        }

        SelectedEvent = Events.First(r => r.Id == id); // a changed selection starts the detail load
        if (_detailId != id)
        {
            _detailLoad = LoadDetailAsync(id);
        }

        await _detailLoad;
    }

    [RelayCommand]
    private Task Search() => SearchAsync();

    [RelayCommand]
    private void ClearFilters()
    {
        SelectedRange = Ranges[2];
        SelectedSeverity = Severities[1];
        SelectedChannel = Channels[0];
        ProviderText = "";
        SearchText = "";
        OnlyNew = false;
        GroupFilter = null;
        foreach (var category in Categories)
        {
            category.IsChecked = true;
        }

        _ = SearchAsync();
    }

    [RelayCommand]
    private void SelectAllCategories(string? value)
    {
        var check = value != "none";
        foreach (var category in Categories)
        {
            category.IsChecked = check;
        }
    }

    [RelayCommand]
    private async Task LoadMore()
    {
        if (_lastQuery is null || GroupMode)
        {
            return;
        }

        await Busy(async () =>
        {
            var query = _lastQuery with { Offset = _loaded, Limit = PageSize };
            var ack = Context.Store.LastAcknowledgedEventId;
            var more = await Task.Run(() => Context.Store.QueryEvents(query));
            foreach (var e in more)
            {
                Events.Add(EventRow.From(e, ack));
            }

            _loaded += more.Count;
            CanLoadMore = more.Count == PageSize;
            StatusText = $"{Events.Count}건 표시";
        });
    }

    [RelayCommand]
    private Task ShowGroupEvents(GroupRow? group)
    {
        if (group is null)
        {
            return Task.CompletedTask;
        }

        GroupFilter = group.GroupKey;
        GroupMode = false;
        return SearchAsync();
    }

    [RelayCommand]
    private Task ClearGroupFilter()
    {
        GroupFilter = null;
        return SearchAsync();
    }

    [RelayCommand]
    private async Task SaveNote()
    {
        if (SelectedEvent is not { } row)
        {
            return;
        }

        var text = NoteText;
        await Task.Run(() => Context.Store.SaveNote(NoteTargets.Group, row.GroupKey, text));
        StatusText = string.IsNullOrWhiteSpace(text) ? "메모를 지웠습니다." : "메모를 저장했습니다 (같은 유형의 이벤트에 공통으로 표시).";
    }

    [RelayCommand]
    private void ShowSensors()
    {
        if (SelectedEvent is { } row)
        {
            Context.Main?.ShowHardwareAt(row.TimeValue, row.Title);
        }
    }

    [RelayCommand]
    private void ShowTimeline()
    {
        if (SelectedEvent is { } row)
        {
            Context.Main?.ShowTimelineAround(row.TimeValue);
        }
    }

    [RelayCommand]
    private Task ExportCsv() => ExportAsync(json: false);

    [RelayCommand]
    private Task ExportJson() => ExportAsync(json: true);

    internal EventQuery BuildQuery()
    {
        var now = Context.Now;
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        if (SelectedRange.Value > 0)
        {
            from = now.AddDays(-SelectedRange.Value);
        }
        else if (SelectedRange.Value < 0)
        {
            from = FromDate is { } f ? new DateTimeOffset(f.Date) : null;
            to = ToDate is { } t ? new DateTimeOffset(t.Date.AddDays(1).AddTicks(-1)) : null;
        }

        var checkedCategories = Categories.Where(c => c.IsChecked).Select(c => c.Value).ToList();
        int? eventId = null;
        var text = SearchText.Trim();
        return new EventQuery
        {
            From = from,
            To = to,
            Categories = checkedCategories.Count == Categories.Count ? null : checkedCategories,
            MinSeverity = SelectedSeverity.Value,
            Channels = SelectedChannel.Value is { } channel ? [channel] : null,
            Providers = string.IsNullOrWhiteSpace(ProviderText) ? null : [ProviderText.Trim()],
            EventId = eventId,
            Text = text.Length == 0 ? null : text,
            GroupKey = GroupFilter,
            AfterId = OnlyNew ? Context.Store.LastAcknowledgedEventId : null,
            Limit = PageSize,
        };
    }

    private Task SearchAsync() => Busy(async () =>
    {
        var query = BuildQuery();
        _lastQuery = query;
        var store = Context.Store;
        var ack = store.LastAcknowledgedEventId;
        if (GroupMode)
        {
            var groups = await Task.Run(() => store.QueryGroups(query with { Limit = 1000 }));
            var from = query.From ?? DateTimeOffset.MinValue;
            Replace(Groups, groups.Select(g => new GroupRow(g.GroupKey, g.Title, EventCategories.Name(g.Category), Fmt.Severity(g.MaxSeverity), g.MaxSeverity,
                g.Count, Fmt.Time(g.First), Fmt.Time(g.Last), g.DistinctDays, g.FirstEver >= from && query.From is not null, g.LatestEventId)));
            StatusText = $"유형 {Groups.Count}개 (같은 공급자·ID·서명으로 묶음)";
            return;
        }

        var (events, total) = await Task.Run(() => (store.QueryEvents(query), store.CountEvents(query with { Limit = int.MaxValue })));
        Replace(Events, events.Select(e => EventRow.From(e, ack)));
        _loaded = events.Count;
        CanLoadMore = events.Count < total;
        StatusText = $"{total:N0}건 중 {Events.Count:N0}건 표시" + (GroupFilter is null ? "" : " · 한 유형만 보는 중");
    });

    private async Task LoadDetailAsync(long id)
    {
        var version = ++_detailVersion;
        _detailId = id;
        var store = Context.Store;
        var (e, groupRows, note) = await Task.Run(() =>
        {
            var ev = store.GetEvent(id);
            var groups = ev is null ? [] : store.QueryGroups(new EventQuery { GroupKey = ev.GroupKey, Limit = 1 });
            var n = ev is null ? null : store.GetNote(NoteTargets.Group, ev.GroupKey);
            return (ev, groups, n);
        });
        if (version != _detailVersion)
        {
            return; // a newer selection is loading: an older event's detail must not replace it
        }

        if (e is null)
        {
            HasDetail = false;
            return;
        }

        var classification = EventClassifier.Default.Classify(e);
        DetailTitle = e.Title;
        DetailMeaning = classification.Meaning ?? "카탈로그에 없는 이벤트입니다. 공급자 문서와 메시지를 확인하세요. 위험·오류 수준이라 수집했습니다.";
        DetailCaution = classification.Caution ?? "";
        DetailMessage = e.Message ?? (e.MessageError is { } err ? $"(메시지 없음: {err})" : "(메시지 없음)");
        Replace(DetailFields,
        [
            new("발생 시각", $"{Fmt.Time(e.TimeCreated)} ({Fmt.Display.ZoneLabel})", $"UTC {IsoTime.Utc(e.TimeCreated)}"),
            new("로그(채널)", e.Channel),
            new("공급자", e.EventSourceName is { } source && source != e.Provider ? $"{e.Provider} ({source})" : e.Provider),
            new("이벤트 ID", e.EventId.ToString(CultureInfo.InvariantCulture) + (e.Qualifiers is int q && q != 0 ? $" (Qualifiers {q})" : "")),
            new("Windows 수준", LevelText.Windows(e.Level)),
            new("진단 분류", $"{EventCategories.Name(e.Category)} · {Fmt.Severity(e.Severity)}", "이 앱의 판정 (Windows 수준과 별개)"),
            new("레코드 번호", e.RecordId.ToString(CultureInfo.InvariantCulture)),
            new("출처", e.IsVirtual ? "가상(테스트) 데이터" : e.Source == EventSources.Live ? "실행 중 실시간 수집" : "과거 로그 조회"),
            new("개인정보", e.IsMasked ? "사용자·컴퓨터 이름, 경로, SID 등 가림" : "가리지 않고 저장됨 (설정에서 마스킹 꺼짐)"),
        ]);
        Replace(DetailFacts, classification.Facts.Select(f => new NameValueRow(f.Label, f.Value, f.Note)));
        Replace(DetailData, e.Data.Select((d, i) => new NameValueRow(d.Name ?? $"[{i}]", d.Value)));
        DetailGroup = groupRows.FirstOrDefault() is { } g
            ? $"같은 유형 {g.Count}건 (처음 {Fmt.Time(g.FirstEver)}, 마지막 {Fmt.Time(g.Last)}, {g.DistinctDays}일) · 서명: {e.Signature}"
            : $"서명: {e.Signature}";
        NoteText = note?.Text ?? "";
        HasDetail = true;
    }

    private async Task ExportAsync(bool json)
    {
        var options = ExportPrompt.Ask(Context, "이벤트 내보내기");
        if (options is null)
        {
            return;
        }

        var name = $"events-{Context.Now.ToLocalTime():yyyyMMdd-HHmm}.{(json ? "json" : "csv")}";
        var path = Context.Dialogs.SaveFile("이벤트 내보내기", json ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv", name, Context.Engine.Paths.ExportDirectory);
        if (path is null)
        {
            return;
        }

        await Busy(async () =>
        {
            var query = (_lastQuery ?? BuildQuery()) with { Limit = 200_000, Offset = 0, Descending = false };
            var events = await Task.Run(() => Context.Store.QueryEvents(query));
            await Task.Run(() =>
            {
                if (json)
                {
                    EventExporter.WriteJson(events, path, options);
                }
                else
                {
                    EventExporter.WriteCsv(events, path, options);
                }
            });
            StatusText = $"{events.Count}건을 내보냈습니다: {Path.GetFileName(path)}";
        });
    }
}

/// <summary>Asks for the export privacy level; the detailed level always needs an explicit confirmation.</summary>
public static class ExportPrompt
{
    public static ExportOptions? Ask(ViewContext context, string title)
    {
        var basic = new ExportOptions { Detail = ExportDetail.Minimal, UseUtc = context.Settings.Ui.UseUtc, Masker = context.ExportMasker };
        var detailed = context.Dialogs.Confirm(title,
            "이벤트 메시지와 이벤트 데이터(상세 원본 정보)도 포함할까요?\n\n" +
            "[예] 상세 포함: 메시지·데이터를 포함합니다. 사용자·컴퓨터 이름, 사용자 폴더 경로, SID, 이메일은 가리지만 " +
            "프로그램 이름, 장치 정보 등 다른 내용이 들어 있을 수 있으니 공유 전에 확인하세요.\n" +
            "[아니요] 개인정보 최소(기본): 메시지·데이터를 빼고 시각·공급자·ID·분류·제목만 내보냅니다.");
        return detailed ? basic with { Detail = ExportDetail.Detailed } : basic;
    }
}
