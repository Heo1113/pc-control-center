using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Iron.App.Presentation;

public sealed partial class ProcessRowViewModel(ProcessReading reading) : ObservableObject
{
    public int Pid { get; } = reading.Pid;
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

    public bool IsSoftwareAdapter { get; private set; } = reading.IsSoftwareAdapter;
    public ProcessReading Reading { get; private set; } = reading;

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
    }

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

    private readonly Dictionary<int, History> _histories = [];
    private IReadOnlyList<ProcessReading> _latest = [];

    public ObservableCollection<ProcessRowViewModel> OverviewRows { get; } = [];
    public ObservableCollection<ProcessRowViewModel> Rows { get; } = [];

    [ObservableProperty] public partial string Filter { get; set; } = All;
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool ShowSoftwareAdapter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ProcessRowViewModel? Selected { get; set; }

    [ObservableProperty] public partial double?[] SelectedHistory { get; private set; } = [];
    [ObservableProperty] public partial string EmptyText { get; private set; } = "";
    [ObservableProperty] public partial string CountText { get; private set; } = "";

    public bool HasSelection => Selected is not null;

    /// <summary>How many rows the overview offers (its panel shows as many as fit).</summary>
    public int OverviewLimit { get; set; } = 14;

    public void Apply(IReadOnlyList<ProcessReading> processes, SensorPhase phase)
    {
        _latest = processes;
        var alive = new HashSet<int>();
        foreach (var p in processes)
        {
            alive.Add(p.Pid);
            if (!_histories.TryGetValue(p.Pid, out var history))
            {
                _histories[p.Pid] = history = new History();
            }

            history.Add(p.UtilizationPercent);
        }

        foreach (var gone in _histories.Keys.Where(pid => !alive.Contains(pid)).ToList())
        {
            _histories.Remove(gone);
        }

        EmptyText = phase switch
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

        SelectedHistory = newValue is not null && _histories.TryGetValue(newValue.Pid, out var h) ? h.Snapshot() : [];
    }

    [RelayCommand]
    private void SetFilter(string? filter) => Filter = filter is not null && Filters.Contains(filter) ? filter : All;

    [RelayCommand]
    private void Select(ProcessRowViewModel? row) => Selected = row;

    private void Refresh()
    {
        var ordered = _latest.OrderByDescending(p => p.DedicatedGb).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var byAdapter = ordered.Where(p => Filter == All || p.Adapter == Filter).ToList();
        var overview = byAdapter.Where(p => !p.IsSoftwareAdapter).Take(OverviewLimit).ToList();
        var search = Search.Trim();
        var full = byAdapter.Where(p => ShowSoftwareAdapter || !p.IsSoftwareAdapter)
            .Where(p => search.Length == 0 || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                                           || p.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(search, StringComparison.Ordinal))
            .ToList();

        var selectedPid = Selected?.Pid;
        Sync(OverviewRows, overview);
        Sync(Rows, full);
        var selected = Rows.FirstOrDefault(r => r.Pid == selectedPid) ?? OverviewRows.FirstOrDefault(r => r.Pid == selectedPid)
                       ?? Rows.FirstOrDefault();
        Selected = selected;
        foreach (var row in OverviewRows.Concat(Rows))
        {
            row.IsSelected = selected is not null && row.Pid == selected.Pid;
        }

        if (selected is not null && _histories.TryGetValue(selected.Pid, out var history))
        {
            SelectedHistory = history.Snapshot();
        }

        var hidden = byAdapter.Count(p => p.IsSoftwareAdapter && !ShowSoftwareAdapter);
        CountText = $"{full.Count}개 프로세스" + (hidden > 0 ? $" · 소프트웨어 어댑터 {hidden}개 숨김" : "");
    }

    /// <summary>Keeps row objects (and so selection and scroll position) when the same processes stay in the same order.</summary>
    private static void Sync(ObservableCollection<ProcessRowViewModel> rows, List<ProcessReading> wanted)
    {
        var same = rows.Count == wanted.Count && rows.Select(r => r.Pid).SequenceEqual(wanted.Select(p => p.Pid));
        if (same)
        {
            for (var i = 0; i < wanted.Count; i++)
            {
                rows[i].Update(wanted[i]);
            }

            return;
        }

        var existing = rows.ToDictionary(r => r.Pid);
        rows.Clear();
        foreach (var p in wanted)
        {
            if (existing.TryGetValue(p.Pid, out var row))
            {
                row.Update(p);
            }
            else
            {
                row = new ProcessRowViewModel(p);
            }

            rows.Add(row);
        }
    }
}
