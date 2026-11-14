using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Core.Diagnostics;
using PcControlCenter.Core;
using PcControlCenter.GpuMemory;
using PcControlCenter.Hardware;
using PcControlCenter.Modules;

namespace PcControlCenter.Presentation;

/// <summary>A page in the menu plan: which section, which module (null = shell page).</summary>
public sealed record PagePlan(string Section, string Key, string Title, string? ModuleId);

/// <summary>
/// The integrated main window: one menu for all four programs, one status bar, the integrated dashboard. Module pages
/// are the modules' own view models (bound by the WPF layer once the module runs); until then a placeholder says why.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>
    /// The menu. Duplicate screens of A, B and C (three dashboards, three settings pages, sensor pages) are merged into
    /// one dashboard, one hardware section and one settings section; each module page keeps its original function.
    /// </summary>
    public static readonly IReadOnlyList<PagePlan> Plan =
    [
        new(SectionKeys.Dashboard, "dashboard", "통합 대시보드", null),
        new(SectionKeys.Hardware, "hardware/shared", "공통 센서", null),
        new(SectionKeys.Hardware, "hardware/gpu-memory", "GPU 메모리", null),
        new(SectionKeys.Hardware, "hardware/live", "실시간 센서·그래프", ModuleIds.LocalAi),
        new(SectionKeys.Hardware, "hardware/gpu-power", "GPU 전력·장치", ModuleIds.GpuEconomics),
        new(SectionKeys.Hardware, "hardware/around-events", "오류 시점 센서 기록", ModuleIds.Diagnostics),
        new(SectionKeys.LocalAi, "local-ai/overview", "추론 개요", ModuleIds.LocalAi),
        new(SectionKeys.LocalAi, "local-ai/runtime", "Strata 런타임", ModuleIds.LocalAi),
        new(SectionKeys.LocalAi, "local-ai/history", "세션 기록", ModuleIds.LocalAi),
        new(SectionKeys.LocalAi, "local-ai/compare", "조건별 비교", ModuleIds.LocalAi),
        new(SectionKeys.Economics, "economics/overview", "현황·전력비", ModuleIds.GpuEconomics),
        new(SectionKeys.Economics, "economics/sessions", "세션·에너지", ModuleIds.GpuEconomics),
        new(SectionKeys.Economics, "economics/profitability", "수익성 (실현·예상)", ModuleIds.GpuEconomics),
        new(SectionKeys.Economics, "economics/scenarios", "시나리오·손익", ModuleIds.GpuEconomics),
        new(SectionKeys.Economics, "economics/history", "기간별 기록", ModuleIds.GpuEconomics),
        new(SectionKeys.Economics, "economics/compare", "세션 비교", ModuleIds.GpuEconomics),
        new(SectionKeys.Diagnostics, "diagnostics/overview", "현황", ModuleIds.Diagnostics),
        new(SectionKeys.Diagnostics, "diagnostics/events", "이벤트 탐색", ModuleIds.Diagnostics),
        new(SectionKeys.Diagnostics, "diagnostics/timeline", "타임라인", ModuleIds.Diagnostics),
        new(SectionKeys.Diagnostics, "diagnostics/analysis", "진단 (WHEA·GPU·BSOD)", ModuleIds.Diagnostics),
        new(SectionKeys.Diagnostics, "diagnostics/compare", "기준선 비교", ModuleIds.Diagnostics),
        new(SectionKeys.Operator, "operator/overview", "현황", ModuleIds.Operator),
        new(SectionKeys.Operator, "operator/run", "작업 실행", ModuleIds.Operator),
        new(SectionKeys.Operator, "operator/inspector", "창 검사기", ModuleIds.Operator),
        new(SectionKeys.Operator, "operator/workflows", "워크플로", ModuleIds.Operator),
        new(SectionKeys.Operator, "operator/history", "작업 기록", ModuleIds.Operator),
        new(SectionKeys.Operator, "operator/models", "모델 연결 (Strata)", ModuleIds.Operator),
        new(SectionKeys.History, "history/timeline", "통합 기록", null),
        new(SectionKeys.Settings, "settings/common", "공통", null),
        new(SectionKeys.Settings, "settings/data", "데이터·이전", null),
        new(SectionKeys.Settings, "settings/local-ai", "로컬 AI", ModuleIds.LocalAi),
        new(SectionKeys.Settings, "settings/economics", "GPU 경제성", ModuleIds.GpuEconomics),
        new(SectionKeys.Settings, "settings/diagnostics", "시스템 진단", ModuleIds.Diagnostics),
        new(SectionKeys.Settings, "settings/operator-safety", "Operator 안전 권한", ModuleIds.Operator),
        new(SectionKeys.Settings, "settings/operator", "Operator 설정", ModuleIds.Operator),
    ];

    private static readonly (string Key, string Title, string Glyph, string Description)[] SectionInfo =
    [
        (SectionKeys.Dashboard, "대시보드", "", "GPU·CPU·RAM, Strata, 전력·전력비, 최근 오류, Operator 상태를 한 화면에서 봅니다."),
        (SectionKeys.Hardware, "하드웨어", "", "공통 센서 한 곳에서 읽은 GPU·CPU·RAM 값과 그래프, 기록."),
        (SectionKeys.LocalAi, "로컬 AI", "", "Strata 텔레메트리, 추론 성능, 세션 기록, 조건별 비교 (A)."),
        (SectionKeys.Economics, "GPU 경제성", "", "세션·에너지·비용, 실현/예상 수익, 손익·시나리오 (B)."),
        (SectionKeys.Diagnostics, "시스템 진단", "", "이벤트 탐색, WHEA·GPU 오류·BSOD, 타임라인, 기준선 비교 (C)."),
        (SectionKeys.Operator, "AI Operator", "", "자연어 작업, 계획 확인, 단계별 승인, 일시정지·긴급 중단, 창 검사기 (E)."),
        (SectionKeys.History, "통합 기록", "", "같은 시간대의 센서·추론·전력·오류·작업 기록을 출처와 함께 봅니다."),
        (SectionKeys.Settings, "설정", "", "공통 설정, 데이터 위치·이전, 모듈별 설정, Operator 안전 권한."),
    ];

    private readonly ShellRuntime _runtime;
    private readonly IShellDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, ModuleStatusRow> _moduleRows = [];
    private readonly Dictionary<string, Func<Task>> _restarts = [];
    private readonly List<Action> _tickHandlers = [];

    public ShellViewModel(ShellRuntime runtime, IShellDispatcher ui, IShellDialogs dialogs, Func<OperatorSummary?>? operatorSummary = null,
        IReadOnlyList<string>? moduleIdsWithRestart = null)
    {
        _runtime = runtime;
        _ui = ui;
        _time = runtime.Options.Time;
        Dialogs = dialogs;
        Hub = runtime.Hub;
        IsMock = runtime.Hub.IsMock;
        foreach (var (key, title, glyph, description) in SectionInfo)
        {
            Sections.Add(new ShellSection(key, title, glyph, description));
        }

        foreach (var module in runtime.Supervisor.Modules)
        {
            if (module is ShellModuleBase restartable)
            {
                _restarts[module.Id] = () => restartable.RestartAsync();
            }
        }

        operatorSummary ??= runtime.Operator is { } operatorModule ? operatorModule.Summary : () => null;
        Dashboard = new DashboardViewModel(runtime.Hub, runtime.LocalAi, runtime.Economics, runtime.Diagnostics, operatorSummary, ui,
            key => Navigate(key), _time, runtime.GpuMemory);
        Operator = runtime.Operator is { } operatorForControls ? new OperatorControlViewModel(operatorForControls, ui) : null;
        SharedSensors = new SharedSensorsViewModel(runtime.Hub, runtime.Intervals, _time);
        // The page's layout (sort, columns, panel size) is remembered in the shell settings, which the settings page saves too.
        // Local runtimes are asked about the selected process only (loopback GETs); MOCK answers with --mock-sensors.
        GpuMemory = new GpuMemoryViewModel(runtime.GpuMemory, runtime.Hub, () => StrataOf(runtime), _time,
            runtime.Settings.GpuMemoryPage, () => runtime.SettingsStore.Save(runtime.Settings),
            runtime.Options.MockSensors ? new MockRuntimeHttp() : new HttpRuntimeClient());
        History = new HistoryViewModel(new PcControlCenter.History.TimelineReader(runtime.Paths), ui, _time);
        Data = new DataMigrationViewModel(new PcControlCenter.Migration.LegacyMigrator(runtime.Paths, time: _time), new SupervisorModuleRestarter(runtime.Supervisor),
            runtime.Paths, runtime.Options.LegacyLocations ?? LegacyDataLocations.Defaults(), dialogs, ui);
        Settings = new ShellSettingsViewModel(runtime.SettingsStore, runtime.Settings, runtime.Paths, dialogs);
        foreach (var id in ModuleIds.All)
        {
            var row = new ModuleStatusRow(id);
            row.Update(StatusOf(id));
            _moduleRows[id] = row;
            ModuleRows.Add(row);
            Dashboard.Modules.Add(row);
        }

        foreach (var plan in Plan)
        {
            var section = Section(plan.Section);
            object content = plan.Key switch
            {
                "dashboard" => Dashboard,
                "hardware/shared" => SharedSensors,
                "hardware/gpu-memory" => GpuMemory,
                "settings/common" => Settings,
                "history/timeline" => History,
                "settings/data" => Data,
                _ when plan.ModuleId is { } id => new ModuleUnavailableViewModel(id, plan.Title, StatusOf(id), _restarts.GetValueOrDefault(id)),
                _ => new PendingPageViewModel(plan.Title),
            };
            section.Tabs.Add(new ShellTab(plan.Key, plan.Title, plan.ModuleId, content)
            {
                // The GPU memory reader reads faster (and right away) while its page is on screen.
                Activated = ReferenceEquals(content, GpuMemory) ? () => GpuMemory.IsVisible = true : null,
            });
        }

        foreach (var section in Sections)
        {
            section.SelectedTab = section.Tabs.FirstOrDefault();
        }

        foreach (var note in runtime.SettingsNotes)
        {
            Notices.Add("공통 설정: " + note);
        }

        if (runtime.Paths.Note is { } pathNote)
        {
            Notices.Add(pathNote);
        }

        runtime.Supervisor.StatusChanged += status => _ui.Post(() => OnModuleStatus(status));
        CurrentSection = Sections[0];
        Tick();
    }

    public SharedHardwareHub Hub { get; }
    public IShellDialogs Dialogs { get; }
    public bool IsMock { get; }
    public string AppTitle => $"{AppInfo.ProductName} {AppInfo.Version}";
    public string DataRootText => _runtime.Paths.IsPortable ? "데이터: 포터블 (data 폴더)" : $"데이터: {_runtime.Paths.Root}";
    public ObservableCollection<ShellSection> Sections { get; } = [];
    public ObservableCollection<ModuleStatusRow> ModuleRows { get; } = [];
    public ObservableCollection<string> Notices { get; } = [];
    public DashboardViewModel Dashboard { get; }
    public SharedSensorsViewModel SharedSensors { get; }

    /// <summary>Hardware › GPU 메모리 (per-process and per-adapter GPU memory from the Windows counters).</summary>
    public GpuMemoryViewModel GpuMemory { get; }
    public HistoryViewModel History { get; }
    public DataMigrationViewModel Data { get; }
    public ShellSettingsViewModel Settings { get; }

    /// <summary>Emergency stop, resume and host restart of the Operator, or null when the Operator is not part of this run.</summary>
    public OperatorControlViewModel? Operator { get; }

    public bool HasOperator => Operator is not null;

    [ObservableProperty]
    public partial ShellSection? CurrentSection { get; set; }

    [ObservableProperty] public partial string SensorStatusText { get; set; } = "";
    [ObservableProperty] public partial Level SensorStatusLevel { get; set; }
    [ObservableProperty] public partial string ClockText { get; set; } = "";

    /// <summary>Raised when a module stopped or failed: the UI layer drops that module's view models.</summary>
    public event Action<string>? ModuleUnbound;

    public ShellSection Section(string key) => Sections.First(s => s.Key == key);

    public ShellTab? FindTab(string key) => Sections.SelectMany(s => s.Tabs).FirstOrDefault(t => t.Key == key);

    public IEnumerable<ShellTab> TabsOf(string moduleId) => Sections.SelectMany(s => s.Tabs).Where(t => t.ModuleId == moduleId);

    /// <summary>Shows a module page: <paramref name="activate"/> makes it the module's current page when its tab is visible.</summary>
    public void BindModulePage(string tabKey, object page, object? header, Action? activate)
    {
        var tab = FindTab(tabKey) ?? throw new ArgumentException($"알 수 없는 화면 {tabKey}", nameof(tabKey));
        tab.Content = page;
        tab.Header = header;
        tab.Activated = activate;
        if (CurrentSection?.SelectedTab == tab)
        {
            activate?.Invoke();
        }
    }

    /// <summary>Puts the placeholder back on every page of a module (it stopped, failed or restarts).</summary>
    public void UnbindModule(string moduleId)
    {
        var status = StatusOf(moduleId);
        foreach (var tab in TabsOf(moduleId))
        {
            tab.Content = new ModuleUnavailableViewModel(moduleId, tab.Title, status, _restarts.GetValueOrDefault(moduleId));
            tab.Header = null;
            tab.Activated = null;
        }

        ModuleUnbound?.Invoke(moduleId);
    }

    /// <summary>Called every second by the UI timer (also ticks module view models registered by the UI layer).</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        ClockText = Text.Clock(now);
        var health = Hub.Health;
        var age = health.LastReadingAt is { } at ? now - at : (TimeSpan?)null;
        SensorStatusLevel = health.State != HubState.Running ? Level.Error
            : age is null ? Level.Muted
            : age > health.Interval * 2 + TimeSpan.FromSeconds(1) ? Level.Error
            : IsMock || health.LatestIssues.Count > 0 ? Level.Warning : Level.Good;
        SensorStatusText = health.State != HubState.Running ? "공통 센서: 중지됨"
            : age is null ? "공통 센서: 첫 측정 기다리는 중"
            : $"공통 센서{(IsMock ? " (MOCK)" : "")}: 측정 {Text.Clock(health.LastReadingAt)} · {Text.Age(age.Value)}"
              + (health.LatestIssues.Count > 0 ? $" · 문제 {health.LatestIssues.Count}건" : "");
        Dashboard.IsActive = CurrentSection?.Key == SectionKeys.Dashboard;
        Dashboard.Tick();
        if (CurrentSection?.SelectedTab?.Content == SharedSensors)
        {
            SharedSensors.Refresh();
        }

        GpuMemory.IsVisible = CurrentSection?.SelectedTab?.Content == GpuMemory;
        if (GpuMemory.IsVisible)
        {
            GpuMemory.Refresh();
        }

        foreach (var handler in _tickHandlers.ToArray())
        {
            try
            {
                handler();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _runtime.Log.Error("ui", "화면 갱신 오류", ex);
            }
        }
    }

    public void AddTickHandler(Action handler) => _tickHandlers.Add(handler);

    public void RemoveTickHandler(Action handler) => _tickHandlers.Remove(handler);

    /// <summary>Opens a section (and optionally one of its tabs).</summary>
    public void Navigate(string sectionKey, string? tabKey = null)
    {
        var section = Sections.FirstOrDefault(s => s.Key == sectionKey) ?? Sections.FirstOrDefault(s => s.Tabs.Any(t => t.Key == sectionKey));
        if (section is null)
        {
            return;
        }

        tabKey ??= section.Key == sectionKey ? null : sectionKey;
        if (tabKey is not null && section.Find(tabKey) is { } tab)
        {
            section.SelectedTab = tab;
        }

        CurrentSection = section;
    }

    /// <summary>Follows a module's own navigation (for example C's "보기" on a notification opens its events page).</summary>
    public void FollowModulePage(object page)
    {
        foreach (var section in Sections)
        {
            if (section.Tabs.FirstOrDefault(t => ReferenceEquals(t.Content, page)) is { } tab)
            {
                section.SelectedTab = tab;
                CurrentSection = section;
                return;
            }
        }
    }

    [RelayCommand]
    private void Open(ShellSection? section)
    {
        if (section is not null)
        {
            CurrentSection = section;
        }
    }

    [RelayCommand]
    private void DismissNotice(string? notice)
    {
        if (notice is not null)
        {
            Notices.Remove(notice);
        }
    }

    partial void OnCurrentSectionChanged(ShellSection? oldValue, ShellSection? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
            if (newValue.Key == SectionKeys.Dashboard)
            {
                Dashboard.Invalidate();
            }
        }
    }

    /// <summary>The local AI module's latest Strata poll for the GPU memory page, or why there is none.</summary>
    private static StrataContext StrataOf(ShellRuntime runtime)
    {
        var status = runtime.LocalAi.Status;
        if (!status.IsUsable)
        {
            return new StrataContext(null, $"로컬 AI 모듈: {ModuleStateText.ToKorean(status.State)} — Strata 정보를 쓸 수 없습니다.");
        }

        var service = runtime.LocalAi.Service;
        return service?.Runtime is null
            ? new StrataContext(null, "로컬 AI 모듈의 Strata 연결이 꺼져 있습니다 (로컬 AI 설정).")
            : new StrataContext(service.LatestRuntime, service.LatestRuntime is null ? "로컬 AI 모듈이 Strata에 연결하는 중입니다." : null,
                service.Settings.Strata.ApiKeyEnvironmentVariable);
    }

    private ModuleStatus StatusOf(string id) => _runtime.Supervisor.Find(id)?.Status
                                                ?? new ModuleStatus(id, ModuleState.Disabled, "이번 실행에서는 사용하지 않습니다.", _time.GetUtcNow());

    private void OnModuleStatus(ModuleStatus status)
    {
        if (_moduleRows.TryGetValue(status.ModuleId, out var row))
        {
            row.Update(status);
        }

        foreach (var tab in TabsOf(status.ModuleId))
        {
            if (tab.Content is ModuleUnavailableViewModel placeholder)
            {
                placeholder.Update(status);
            }
        }

        if (status.State is ModuleState.Failed or ModuleState.Stopped or ModuleState.Stopping
            && TabsOf(status.ModuleId).Any(t => t.Content is not ModuleUnavailableViewModel))
        {
            UnbindModule(status.ModuleId);
        }
    }

    /// <summary>Lets the Operator integration (added later) report its row like the other modules.</summary>
    public void ReportModuleStatus(ModuleStatus status) => _ui.Post(() => OnModuleStatus(status));
}

/// <summary>A shell page that is not available in this build; it says so instead of showing an empty or fake screen.</summary>
public sealed class PendingPageViewModel(string title)
{
    public string Title { get; } = title;
    public string Message => $"'{Title}' 화면은 아직 이 빌드에 연결되지 않았습니다.";
}
