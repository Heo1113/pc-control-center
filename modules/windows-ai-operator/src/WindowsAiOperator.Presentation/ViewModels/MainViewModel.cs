using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsAiOperator.Core;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Security;
using WindowsAiOperator.Storage;

namespace WindowsAiOperator.Presentation.ViewModels;

/// <summary>Overview: desktop, foreground window, model, current task, recent tasks and warnings.</summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly OperatorContext _context;
    private readonly TaskRunnerViewModel _runner;
    private readonly Action<string> _navigate;

    public DashboardViewModel(OperatorContext context, TaskRunnerViewModel runner, Action<string> navigate)
        : base("대시보드", "", "dashboard")
    {
        _context = context;
        _runner = runner;
        _navigate = navigate;
        Warnings = new ObservableCollection<string>(context.StartupNotes);
        Refresh();
    }

    public ObservableCollection<string> Warnings { get; }
    public ObservableCollection<HistoryRow> RecentTasks { get; } = [];

    [ObservableProperty]
    public partial string DesktopText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Tone DesktopTone { get; set; }

    [ObservableProperty]
    public partial string ForegroundText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AllowedWindowsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModelText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModelDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Tone ModelTone { get; set; }

    [ObservableProperty]
    public partial string TaskText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Tone TaskTone { get; set; }

    [ObservableProperty]
    public partial string SafetyText { get; set; } = string.Empty;

    public override void OnNavigatedTo() => Refresh();

    [RelayCommand]
    public void Refresh()
    {
        var desktop = _context.Desktop;
        DesktopText = desktop.IsMock ? "MOCK 데스크톱 — 실제 Windows 화면을 조작하지 않습니다(테스트·데모 모드)" : desktop.Status.Description;
        DesktopTone = desktop.IsMock ? Tone.Warning : desktop.Status.Available ? Tone.Good : Tone.Bad;
        try
        {
            var foreground = desktop.GetForegroundWindow();
            if (foreground is null)
            {
                ForegroundText = "활성 창 없음";
            }
            else
            {
                var access = _context.Policy.CheckWindow(foreground, _context.Settings.Safety, desktop.OwnProcessId);
                ForegroundText = $"{Redactor.OneLine(foreground.Title, 60)} ({foreground.ProcessName}.exe) — {(access.CanInteract ? "조작 가능" : access.CanRead ? "읽기 전용" : "자동화 안 함")}";
            }

            var windows = desktop.ListWindows();
            var allowed = windows.Count(w => _context.Policy.CheckWindow(w, _context.Settings.Safety, desktop.OwnProcessId).CanRead);
            AllowedWindowsText = $"열린 창 {windows.Count}개 중 허용 앱 창 {allowed}개";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            ForegroundText = "창 정보를 읽지 못했습니다: " + ex.Message;
        }

        var profile = _context.Settings.ActiveProfile;
        if (profile is null)
        {
            ModelText = "모델 연결 없음";
            ModelTone = Tone.Warning;
        }
        else
        {
            var probe = _context.Models.LastProbe(profile);
            var (client, problem) = _context.Models.CreateClient(profile);
            (client as IDisposable)?.Dispose();
            var (vision, reason) = _context.Models.Vision(profile);
            ModelText = $"{profile.Name} · {profile.Model ?? "모델 미선택"}";
            ModelDetail = (problem ?? (probe is null ? "연결 확인 전" : probe.Reachable ? $"연결됨({probe.ServerKind})" : $"연결 안 됨: {probe.Message}"))
                          + $" · {(vision && _context.Settings.Safety.AllowVision ? "이미지 입력 사용 가능" : "텍스트 전용(UI 구조 사용)")}";
            ModelTone = problem is not null || probe is { Reachable: false } ? Tone.Bad : probe is null ? Tone.Warning : Tone.Good;
            _ = reason;
        }

        TaskText = _runner.IsRunning ? $"실행 중: {_runner.StatusText}" : _runner.LastRun is { } last ? $"마지막 작업: {TaskStatusText.Korean(last.Status)} — {Redactor.OneLine(last.Request, 60)}" : "실행한 작업 없음";
        TaskTone = _runner.IsRunning ? Tone.Info : _runner.StatusTone;
        var safety = _context.Settings.Safety;
        SafetyText = $"허용 앱 {safety.AllowedApps.Count(a => a.Enabled)}개 · 계획 확인 {(safety.RequirePlanReview ? "켜짐" : "꺼짐")} · " +
                     $"모든 조작 승인 {(safety.RequireApprovalForAllInteractions ? "켜짐" : "꺼짐")} · 최대 {safety.MaxStepsPerTask}단계 · 작업 제한 {safety.TaskTimeoutSeconds}초 · 긴급 중단 Ctrl+Alt+F12";
        RecentTasks.Clear();
        foreach (var task in _context.Store.QueryTasks(new HistoryQuery(Limit: 6)))
        {
            RecentTasks.Add(new HistoryRow(task));
        }
    }

    [RelayCommand]
    private void Go(string key) => _navigate(key);
}

/// <summary>Navigation, status bar and the emergency stop that is always available.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly OperatorContext _context;

    public MainViewModel(OperatorContext context, IUiDispatcher ui, IDialogService dialogs, IApprovalPresenter presenter, IHighlighter highlighter, IAppHost host)
    {
        _context = context;
        Runner = new TaskRunnerViewModel(context, ui, dialogs, presenter);
        Dashboard = new DashboardViewModel(context, Runner, Navigate);
        Inspector = new InspectorViewModel(context, ui, dialogs, highlighter, Runner, handle =>
        {
            Runner.SelectWindow(handle);
            Navigate(Runner.Key);
        });
        Workflows = new WorkflowLibraryViewModel(context, dialogs, Runner, () => Navigate(Runner.Key));
        History = new HistoryViewModel(context, dialogs, request =>
        {
            Runner.RequestText = request.StartsWith('[') ? string.Empty : request;
            Navigate(Runner.Key);
        });
        Models = new ModelsViewModel(context, dialogs, ui);
        Safety = new SafetySettingsViewModel(context, dialogs);
        Settings = new SettingsViewModel(context, dialogs, host, () => Runner.IsRunning);
        Pages = [Dashboard, Runner, Inspector, Workflows, History, Models, Safety, Settings];
        CurrentPage = Dashboard;
        Runner.TaskFinished += _ => ui.Post(() =>
        {
            History.Refresh();
            Dashboard.Refresh();
        });
        Runner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TaskRunnerViewModel.IsRunning) or nameof(TaskRunnerViewModel.StatusText) or nameof(TaskRunnerViewModel.IsPaused))
            {
                OnPropertyChanged(nameof(StatusLine));
                OnPropertyChanged(nameof(IsRunning));
                EmergencyStopCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public TaskRunnerViewModel Runner { get; }
    public DashboardViewModel Dashboard { get; }
    public InspectorViewModel Inspector { get; }
    public WorkflowLibraryViewModel Workflows { get; }
    public HistoryViewModel History { get; }
    public ModelsViewModel Models { get; }
    public SafetySettingsViewModel Safety { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    public string Title => $"{AppInfo.ProductName} {AppInfo.Version}" + (_context.Desktop.IsMock ? " — MOCK 데스크톱" : string.Empty);
    public bool IsMock => _context.Desktop.IsMock;
    public bool IsRunning => Runner.IsRunning;
    public string StatusLine => Runner.IsRunning ? $"{(Runner.IsPaused ? "일시정지" : "실행 중")} · {Runner.StatusText}" : $"대기 중 · 데이터: {_context.Paths.DataDirectory}";

    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; set; }

    partial void OnCurrentPageChanged(PageViewModel oldValue, PageViewModel newValue)
    {
        oldValue?.OnNavigatedFrom();
        newValue.OnNavigatedTo();
    }

    public void Navigate(string key)
    {
        var page = Pages.FirstOrDefault(p => p.Key == key);
        if (page is not null)
        {
            CurrentPage = page;
        }
    }

    [RelayCommand]
    private void NavigateTo(PageViewModel page) => CurrentPage = page;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    public void EmergencyStop()
    {
        _context.Log.Warn("ui", "emergency stop");
        Runner.StopAll("긴급 중단");
    }
}
