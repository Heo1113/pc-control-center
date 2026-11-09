using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcControlCenter.Modules;
using PcControlCenter.Operator;

namespace PcControlCenter.Presentation;

/// <summary>
/// The Operator controls that are always reachable in the shell: emergency stop (status bar button and Ctrl+Alt+F12),
/// resume after an emergency stop, and restarting the separate host process. The emergency stop first stops E's
/// running task and denies open approvals (E's own MainViewModel.EmergencyStop), then latches and, if needed, kills the
/// host so no further input can happen.
/// </summary>
public sealed partial class OperatorControlViewModel : ObservableObject
{
    private readonly OperatorModule _module;
    private readonly IShellDispatcher _ui;

    public OperatorControlViewModel(OperatorModule module, IShellDispatcher ui)
    {
        _module = module;
        _ui = ui;
        module.HostChanged += () => _ui.Post(Refresh);
        module.StatusChanged += _ => _ui.Post(Refresh);
        Refresh();
    }

    /// <summary>Set by the UI layer once E's view models exist: stops E's running task and closes approvals as denied.</summary>
    public Action? StopRunningTask { get; set; }

    [ObservableProperty] public partial string HostText { get; set; } = "";
    [ObservableProperty] public partial Level HostLevel { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    public partial bool IsLatched { get; set; }

    [ObservableProperty] public partial bool IsMock { get; set; }
    [ObservableProperty] public partial bool IsAvailable { get; set; }

    public string IsolationText =>
        "화면 조작(UI Automation·키보드·마우스 입력·화면 캡처·프로그램 실행)은 별도 Operator 실행 프로세스에서만 일어납니다. "
        + "계획 검증·안전 정책·승인·기록은 프로젝트 E의 코드 그대로 이 창에서 처리하고, 실행 프로세스는 차단 목록·관리자 권한 창·이 프로그램 자신의 창을 스스로 한 번 더 거부합니다.";

    public void Refresh()
    {
        var host = _module.Host;
        IsAvailable = host is not null;
        IsLatched = host?.EmergencyLatched == true;
        IsMock = host?.ExpectMock == true;
        HostText = host is null ? $"Operator: {_module.Status.Summary}" : "Operator: " + OperatorModule.HostText(host);
        HostLevel = host is null ? Level.Muted
            : host.State == HostState.Connected && !host.EmergencyLatched ? (host.ExpectMock ? Level.Warning : Level.Good)
            : host.State is HostState.Failed ? Level.Error : Level.Warning;
    }

    [RelayCommand]
    private async Task EmergencyStopAsync()
    {
        try
        {
            StopRunningTask?.Invoke();
        }
        finally
        {
            await _module.EmergencyStopAsync();
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(IsLatched))]
    private async Task ResumeAsync()
    {
        await _module.ResumeAsync();
        Refresh();
    }

    [RelayCommand]
    private async Task RestartHostAsync()
    {
        await _module.RestartHostAsync();
        Refresh();
    }
}
