using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Iron.App.Presentation;

public sealed partial class ProcessRowViewModel(ProcessReading reading) : ObservableObject
{
    public int Pid { get; } = reading.Pid;

    /// <summary>The process instance this row stands for (PID + creation time): a reused PID gets a new row.</summary>
    public (int Pid, long StartTicks) Instance { get; } = reading.Instance;
    public string PidText => Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty] public partial string Name { get; private set; } = reading.Name;
    [ObservableProperty] public partial string Adapter { get; private set; } = reading.Adapter;
    [ObservableProperty] public partial string AdapterShort { get; private set; } = Short(reading.Adapter);
    [ObservableProperty] public partial string DedicatedText { get; private set; } = Fmt.Gb(reading.DedicatedGb);
    [ObservableProperty] public partial string SharedText { get; private set; } = Fmt.Gb(reading.SharedGb);
    [ObservableProperty] public partial string UtilizationText { get; private set; } = Fmt.Int(reading.UtilizationPercent) + "%";
    [ObservableProperty] public partial string Engine { get; private set; } = reading.Engine;

    /// <summary>More than 3 GB dedicated: the number is drawn in orange (prototype rule).</summary>
    [ObservableProperty] public partial bool IsHeavy { get; private set; } = reading.DedicatedGb > 3;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>Shown in the existing name tooltip (identity text and icon). Stage 1: from mock data only.</summary>
    [ObservableProperty] public partial ProcessIdentityView? Identity { get; set; }

    /// <summary>
    /// Null for a process on one GPU. For a process on several: says that the row is one GPU's use (not a total) and lists the
    /// other GPUs' dedicated/shared memory and engine load (shown as a tooltip on the GPU and memory cells; no new element).
    /// </summary>
    [ObservableProperty] public partial string? UsageNote { get; private set; } = Note(reading);

    /// <summary>The GPU cell's tooltip: the adapter name, plus <see cref="UsageNote"/> for a process on several GPUs.</summary>
    [ObservableProperty] public partial string AdapterToolTip { get; private set; } = AdapterTip(reading);

    public bool IsSoftwareAdapter { get; private set; } = reading.IsSoftwareAdapter;
    public ProcessReading Reading { get; private set; } = reading;

    /// <summary>Key of the load history this row shows: the process instance on the GPU whose values the row shows.</summary>
    public ProcessListViewModel.UsageKey HistoryKey => new(Reading.Pid, Reading.StartTicks, Reading.Shown.Id);

    public void Update(ProcessReading r)
    {
        Reading = r;
        Name = r.Name;
        Adapter = r.Adapter;
        AdapterShort = Short(r.Adapter);
        DedicatedText = Fmt.Gb(r.DedicatedGb);
        SharedText = Fmt.Gb(r.SharedGb);
        UtilizationText = Fmt.Int(r.UtilizationPercent) + "%";
        Engine = r.Engine;
        IsHeavy = r.DedicatedGb > 3;
        IsSoftwareAdapter = r.IsSoftwareAdapter;
        UsageNote = Note(r);
        AdapterToolTip = AdapterTip(r);
    }

    internal static string? Note(ProcessReading r)
    {
        if (r.OtherAdapters is not { Count: > 0 } others)
        {
            return null;
        }

        var lines = others.Select(o => $"· {o.Adapter}: 전용 {Fmt.Gb(o.DedicatedGb)} · 공유 {Fmt.Gb(o.SharedGb)}"
                                       + (o.Engine.Length > 0 ? $" · {o.Engine} {Fmt.Int(o.UtilizationPercent)}%" : ""));
        return $"표시 값은 {r.Adapter} 한 GPU의 사용량입니다 (모든 GPU의 합계가 아님).\n다른 GPU 사용:\n" + string.Join("\n", lines);
    }

    private static string AdapterTip(ProcessReading r) => Note(r) is { } note ? r.Adapter + "\n" + note : r.Adapter;

    public static string Short(string adapter) => adapter.StartsWith("RTX ", StringComparison.Ordinal) ? adapter[4..] : adapter.Contains("Basic Render", StringComparison.Ordinal) ? "SW" : adapter;
}

/// <summary>
/// GPU memory per process from the Windows WDDM counters (PDH). The overview shows the top rows; the 프로세스 tab the whole
/// list with search, the software adapter (Basic Render Driver) hidden by default, and an inspector with the last 60 s.
/// </summary>
public sealed partial class ProcessListViewModel : ObservableObject
{
    public const string All = "전체";
    public static readonly IReadOnlyList<string> Filters = [All, "RTX 5080", "RTX 5060 Ti"];

    /// <summary>One load history per process instance and GPU: a reused PID, or the same process on another GPU, never shares one.</summary>
    public readonly record struct UsageKey(int Pid, long StartTicks, string AdapterId);

    private readonly Dictionary<UsageKey, History> _histories = [];
    private readonly Dictionary<(int Pid, long StartTicks), ProcessRowViewModel> _rows = [];
    private IReadOnlyList<ProcessReading> _latest = [];

    /// <summary>Identity for the name tooltip. Stage 1 uses mock data only (no real process lookup).</summary>
    public IProcessIdentityProvider IdentityProvider { get; init; } = new MockProcessIdentityProvider();

    public ObservableCollection<ProcessRowViewModel> OverviewRows { get; } = [];
    public ObservableCollection<ProcessRowViewModel> Rows { get; } = [];

    [ObservableProperty] public partial string Filter { get; set; } = All;
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool ShowSoftwareAdapter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ListSelection))]
    public partial ProcessRowViewModel? Selected { get; set; }

    /// <summary>
    /// What the two lists (overview top rows, full table) bind their selection to. A list that does not contain the
    /// selected process reports "nothing selected"; that is ignored, so one list never clears the other's selection.
    /// </summary>
    public ProcessRowViewModel? ListSelection
    {
        get => Selected;
        set
        {
            if (value is not null)
            {
                Selected = value;
            }
        }
    }

    [ObservableProperty] public partial double?[] SelectedHistory { get; private set; } = [];
    [ObservableProperty] public partial string EmptyText { get; private set; } = "";
    [ObservableProperty] public partial string CountText { get; private set; } = "";

    public bool HasSelection => Selected is not null;

    /// <summary>How many rows the overview shows: as many as its panel can hold (set by the view), at least one.</summary>
    public int OverviewLimit { get; private set; } = 4;

    public void SetOverviewLimit(int rows)
    {
        rows = Math.Clamp(rows, 1, 50);
        if (rows != OverviewLimit)
        {
            OverviewLimit = rows;
            Refresh();
        }
    }

    /// <param name="unavailable">Stage 2: why there is no current list (WDDM failed or stale); shown instead of "no process".</param>
    public void Apply(IReadOnlyList<ProcessReading> processes, SensorPhase phase, string? unavailable = null)
    {
        _latest = processes;
        var alive = new HashSet<UsageKey>();
        var instances = new HashSet<(int, long)>();
        foreach (var p in processes)
        {
            instances.Add(p.Instance);
            foreach (var u in p.Usages)
            {
                // every GPU of the process keeps its own history, whichever GPU the row shows now
                var key = new UsageKey(p.Pid, p.StartTicks, u.Id);
                if (alive.Add(key))
                {
                    if (!_histories.TryGetValue(key, out var history))
                    {
                        _histories[key] = history = new History();
                    }

                    history.Add(u.UtilizationPercent);
                }
            }
        }

        // a process (instance) gone, a GPU it stopped using, or no current reading at all: that history ends
        foreach (var gone in _histories.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _histories.Remove(gone);
        }

        foreach (var gone in _rows.Keys.Where(k => !instances.Contains(k)).ToList())
        {
            _rows.Remove(gone);
        }

        EmptyText = processes.Count == 0 && unavailable is not null ? unavailable : phase switch
        {
            SensorPhase.Initializing => "WDDM 카운터를 여는 중...",
            SensorPhase.NoDevice => "GPU 어댑터 카운터를 찾지 못했습니다 (미연결).",
            _ => processes.Count == 0 ? "GPU 메모리를 쓰는 프로세스가 없습니다." : "",
        };
        Refresh();
    }

    partial void OnFilterChanged(string value) => Refresh();
    partial void OnSearchChanged(string value) => Refresh();
    partial void OnShowSoftwareAdapterChanged(bool value) => Refresh();

    partial void OnSelectedChanged(ProcessRowViewModel? oldValue, ProcessRowViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        SelectedHistory = newValue is not null && _histories.TryGetValue(newValue.HistoryKey, out var h) ? h.Snapshot() : [];
    }

    [RelayCommand]
    private void SetFilter(string? filter) => Filter = filter is not null && Filters.Contains(filter) ? filter : All;

    [RelayCommand]
    private void Select(ProcessRowViewModel? row) => Selected = row;

    private void Refresh()
    {
        var ordered = OnFilter(_latest, Filter).OrderByDescending(p => p.DedicatedGb).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var byAdapter = ordered;
        var overview = byAdapter.Where(p => !p.IsSoftwareAdapter).Take(OverviewLimit).ToList();
        var search = Search.Trim();
        var full = byAdapter.Where(p => ShowSoftwareAdapter || !p.IsSoftwareAdapter)
            .Where(p => search.Length == 0 || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                                           || p.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(search, StringComparison.Ordinal)
                                           || IdentityProvider.Identify(p).Matches(search))
            .ToList();

        var selectedInstance = Selected?.Instance;
        Sync(OverviewRows, overview, _rows, IdentityProvider);
        Sync(Rows, full, _rows, IdentityProvider);
        var selected = Rows.FirstOrDefault(r => r.Instance == selectedInstance) ?? OverviewRows.FirstOrDefault(r => r.Instance == selectedInstance)
                       ?? Rows.FirstOrDefault();
        Selected = selected;
        foreach (var row in OverviewRows.Concat(Rows))
        {
            row.IsSelected = selected is not null && row.Instance == selected.Instance;
        }

        if (selected is not null && _histories.TryGetValue(selected.HistoryKey, out var history))
        {
            SelectedHistory = history.Snapshot();
        }

        var hidden = byAdapter.Count(p => p.IsSoftwareAdapter && !ShowSoftwareAdapter);
        CountText = $"{full.Count}개 프로세스" + (hidden > 0 ? $" · 소프트웨어 어댑터 {hidden}개 숨김" : "");
    }

    /// <summary>
    /// The rows of a view. "전체": one row per process with its representative GPU's values (as before). A GPU filter: every
    /// process that holds memory on that GPU, with that GPU's dedicated/shared memory and engine load. The filter label is a
    /// model name (the buttons); it is resolved to the GPUs' identities (LUIDs) seen in this reading, and rows are matched by
    /// identity. With two cards of one model (separate labels deferred) a process shows the larger of its uses on them.
    /// </summary>
    internal static IEnumerable<ProcessReading> OnFilter(IReadOnlyList<ProcessReading> processes, string filter)
    {
        if (filter == All)
        {
            return processes;
        }

        var ids = processes.SelectMany(p => p.Usages).Where(u => u.Adapter == filter).Select(u => u.Id).ToHashSet();
        return processes
            .Select(p => (p, use: p.Usages.Where(u => ids.Contains(u.Id)).OrderByDescending(u => u.DedicatedGb).FirstOrDefault()))
            .Where(x => x.use is not null)
            .Select(x => x.use!.Id == x.p.Shown.Id ? x.p : x.p.On(x.use!));
    }

    /// <summary>Keeps row objects (and so selection and scroll position) when the same processes stay in the same order.</summary>
    private static void Sync(ObservableCollection<ProcessRowViewModel> rows, List<ProcessReading> wanted, Dictionary<(int, long), ProcessRowViewModel> shared,
        IProcessIdentityProvider identities)
    {
        var same = rows.Count == wanted.Count && rows.Select(r => r.Instance).SequenceEqual(wanted.Select(p => p.Instance));
        if (same)
        {
            for (var i = 0; i < wanted.Count; i++)
            {
                rows[i].Update(wanted[i]);
                rows[i].Identity = identities.Identify(wanted[i]);
            }

            return;
        }

        // One row object per process, shared by both lists, so a selection made in one is found in the other.
        rows.Clear();
        foreach (var p in wanted)
        {
            if (shared.TryGetValue(p.Instance, out var row))
            {
                row.Update(p);
            }
            else
            {
                shared[p.Instance] = row = new ProcessRowViewModel(p);
            }

            row.Identity = identities.Identify(p);
            rows.Add(row);
        }
    }
}
