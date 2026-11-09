using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsAiOperator.Core.Policy;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.Presentation.ViewModels;

public sealed partial class AllowedAppRow : ObservableObject
{
    public AllowedAppRow(AllowedApp app)
    {
        Enabled = app.Enabled;
        Id = app.Id;
        DisplayName = app.DisplayName;
        ProcessNames = string.Join(", ", app.ProcessNames);
        LaunchPath = app.LaunchPath ?? string.Empty;
        Interact = app.Permission == AppPermission.Interact;
        RequireApprovalForLaunch = app.RequireApprovalForLaunch;
        RequireApprovalForAllActions = app.RequireApprovalForAllActions;
        IsSystemSettings = app.IsSystemSettings;
    }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial string Id { get; set; }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    public partial string ProcessNames { get; set; }

    [ObservableProperty]
    public partial string LaunchPath { get; set; }

    [ObservableProperty]
    public partial bool Interact { get; set; }

    [ObservableProperty]
    public partial bool RequireApprovalForLaunch { get; set; }

    [ObservableProperty]
    public partial bool RequireApprovalForAllActions { get; set; }

    [ObservableProperty]
    public partial bool IsSystemSettings { get; set; }

    public AllowedApp ToModel() => new()
    {
        Enabled = Enabled,
        Id = Id.Trim(),
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Id.Trim() : DisplayName.Trim(),
        ProcessNames = ProcessNames.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        LaunchPath = string.IsNullOrWhiteSpace(LaunchPath) ? null : LaunchPath.Trim(),
        Permission = Interact ? AppPermission.Interact : AppPermission.ReadOnly,
        RequireApprovalForLaunch = RequireApprovalForLaunch,
        RequireApprovalForAllActions = RequireApprovalForAllActions,
        IsSystemSettings = IsSystemSettings,
    };
}

/// <summary>Allow list, block list, granted folders, approval rules (mandatory ones shown locked) and execution limits.</summary>
public sealed partial class SafetySettingsViewModel : PageViewModel
{
    private readonly OperatorContext _context;
    private readonly IDialogService _dialogs;

    public SafetySettingsViewModel(OperatorContext context, IDialogService dialogs)
        : base("안전 설정", "", "safety")
    {
        _context = context;
        _dialogs = dialogs;
        MandatoryRules = Enum.GetValues<RiskCategory>().Select(RiskCatalog.Get).Where(i => i.Mandatory)
            .Select(i => i.Denied ? $"{i.Label}: 항상 거부 — {i.Recovery}" : $"{i.Label}: 항상 승인 필요 — {i.Impact}").ToList();
        BlockedProcesses = string.Join(", ", BuiltInBlockList.Processes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        Load();
    }

    public IReadOnlyList<string> MandatoryRules { get; }
    public string BlockedProcesses { get; }
    public ObservableCollection<AllowedAppRow> Apps { get; } = [];
    public ObservableCollection<string> Folders { get; } = [];
    public string EmergencyHotkey => "Ctrl+Alt+F12 (전역 긴급 중단)";

    [ObservableProperty]
    public partial AllowedAppRow? SelectedApp { get; set; }

    [ObservableProperty]
    public partial string ExtraBlocked { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RequirePlanReview { get; set; }

    [ObservableProperty]
    public partial bool RequireApprovalForAll { get; set; }

    [ObservableProperty]
    public partial bool ConfirmButtons { get; set; }

    [ObservableProperty]
    public partial bool CloseButtons { get; set; }

    [ObservableProperty]
    public partial bool CoordinateClicks { get; set; }

    [ObservableProperty]
    public partial bool InjectionGuard { get; set; }

    [ObservableProperty]
    public partial bool AllowVision { get; set; }

    [ObservableProperty]
    public partial int MaxSteps { get; set; }

    [ObservableProperty]
    public partial int TaskTimeout { get; set; }

    [ObservableProperty]
    public partial int StepTimeout { get; set; }

    [ObservableProperty]
    public partial int MaxFailures { get; set; }

    [ObservableProperty]
    public partial int MaxReplans { get; set; }

    [ObservableProperty]
    public partial int MaxModelCalls { get; set; }

    [ObservableProperty]
    public partial int ApprovalTimeout { get; set; }

    [ObservableProperty]
    public partial int MaxTextLength { get; set; }

    [ObservableProperty]
    public partial int MinInterval { get; set; }

    [ObservableProperty]
    public partial string? SaveResult { get; set; }

    public override void OnNavigatedTo() => Load();

    [RelayCommand]
    private void Load()
    {
        var s = _context.Settings.Safety;
        Apps.Clear();
        foreach (var app in s.AllowedApps)
        {
            Apps.Add(new AllowedAppRow(app));
        }

        Folders.Clear();
        foreach (var folder in s.GrantedFolders)
        {
            Folders.Add(folder);
        }

        ExtraBlocked = string.Join(", ", s.ExtraBlockedProcesses);
        RequirePlanReview = s.RequirePlanReview;
        RequireApprovalForAll = s.RequireApprovalForAllInteractions;
        ConfirmButtons = s.RequireApprovalForConfirmButtons;
        CloseButtons = s.RequireApprovalForCloseButtons;
        CoordinateClicks = s.RequireApprovalForCoordinateClicks;
        InjectionGuard = s.InjectionGuard;
        AllowVision = s.AllowVision;
        MaxSteps = s.MaxStepsPerTask;
        TaskTimeout = s.TaskTimeoutSeconds;
        StepTimeout = s.StepTimeoutSeconds;
        MaxFailures = s.MaxConsecutiveFailures;
        MaxReplans = s.MaxReplans;
        MaxModelCalls = s.MaxModelCallsPerTask;
        ApprovalTimeout = s.ApprovalTimeoutSeconds;
        MaxTextLength = s.MaxTextLength;
        MinInterval = s.MinActionIntervalMs;
        SaveResult = null;
    }

    [RelayCommand]
    private void AddApp()
    {
        var row = new AllowedAppRow(new AllowedApp { Id = "new-app", DisplayName = "새 앱", Permission = AppPermission.ReadOnly, Enabled = false });
        Apps.Add(row);
        SelectedApp = row;
    }

    [RelayCommand]
    private void RemoveApp()
    {
        if (SelectedApp is not null && _dialogs.Confirm("허용 앱 제거", $"'{SelectedApp.DisplayName}'을(를) 허용 목록에서 제거합니다. 저장 전까지는 적용되지 않습니다."))
        {
            Apps.Remove(SelectedApp);
        }
    }

    [RelayCommand]
    private void AddFolder()
    {
        var folder = _dialogs.PickFolder("목록 읽기를 허용할 폴더(파일 내용은 읽지 않음)");
        if (folder is not null && !Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            Folders.Add(folder);
        }
    }

    [RelayCommand]
    private void RemoveFolder(string folder) => Folders.Remove(folder);

    [RelayCommand]
    private void Save()
    {
        var apps = Apps.Select(a => a.ToModel()).ToList();
        var problems = new List<string>();
        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.Id) || !System.Text.RegularExpressions.Regex.IsMatch(app.Id, "^[A-Za-z0-9._-]{1,64}$"))
            {
                problems.Add($"앱 ID '{app.Id}'는 영문·숫자·.-_ 1~64자여야 합니다.");
            }

            var blocked = app.ProcessNames.Where(p => BuiltInBlockList.IsBlocked(p)).ToList();
            if (blocked.Count > 0 && app.Enabled)
            {
                problems.Add($"'{app.DisplayName}'의 프로세스 {string.Join(", ", blocked)}는 차단 목록에 있어 허용할 수 없습니다.");
            }

            if (app.ProcessNames.Count == 0 && app.Enabled)
            {
                problems.Add($"'{app.DisplayName}'에 프로세스 이름이 없습니다.");
            }
        }

        if (apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            problems.Add("앱 ID가 중복됩니다.");
        }

        if (problems.Count > 0)
        {
            SaveResult = "저장하지 않았습니다: " + string.Join(" ", problems);
            return;
        }

        if (!RequirePlanReview && _context.Settings.Safety.RequirePlanReview
            && !_dialogs.Confirm("계획 확인 끄기", "AI 계획을 실행 전에 확인하는 단계를 끕니다. 위험한 단계는 계속 승인을 받지만, 계획 전체를 미리 보지 않게 됩니다. 계속할까요?"))
        {
            return;
        }

        var s = _context.Settings.Safety;
        s.AllowedApps = apps;
        s.GrantedFolders = Folders.ToList();
        s.ExtraBlockedProcesses = ExtraBlocked.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        s.RequirePlanReview = RequirePlanReview;
        s.RequireApprovalForAllInteractions = RequireApprovalForAll;
        s.RequireApprovalForConfirmButtons = ConfirmButtons;
        s.RequireApprovalForCloseButtons = CloseButtons;
        s.RequireApprovalForCoordinateClicks = CoordinateClicks;
        s.InjectionGuard = InjectionGuard;
        s.AllowVision = AllowVision;
        s.MaxStepsPerTask = MaxSteps;
        s.TaskTimeoutSeconds = TaskTimeout;
        s.StepTimeoutSeconds = StepTimeout;
        s.MaxConsecutiveFailures = MaxFailures;
        s.MaxReplans = MaxReplans;
        s.MaxModelCallsPerTask = MaxModelCalls;
        s.ApprovalTimeoutSeconds = ApprovalTimeout;
        s.MaxTextLength = MaxTextLength;
        s.MinActionIntervalMs = MinInterval;
        var notes = _context.SaveSettings();
        Load();
        SaveResult = notes.Count == 0 ? "저장했습니다. 다음 작업부터 적용됩니다(실행 중인 작업은 시작할 때의 설정을 유지)." : "저장했습니다(일부 값 조정): " + string.Join(" ", notes);
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        if (!_dialogs.Confirm("기본값으로 되돌리기", "안전 설정(허용 앱·폴더·승인 규칙·한도)을 기본값으로 되돌립니다. 지금 설정은 사라집니다. 계속할까요?"))
        {
            return;
        }

        var fresh = new SafetySettings();
        fresh.AllowedApps.AddRange(AllowedApp.Defaults());
        _context.Settings.Safety = fresh;
        _context.SaveSettings();
        Load();
        SaveResult = "기본값으로 되돌렸습니다.";
    }
}
