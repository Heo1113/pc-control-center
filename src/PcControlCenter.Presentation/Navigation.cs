using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcControlCenter.Core;

namespace PcControlCenter.Presentation;

public static class SectionKeys
{
    public const string Dashboard = "dashboard";
    public const string Hardware = "hardware";
    public const string LocalAi = "local-ai";
    public const string Economics = "economics";
    public const string Diagnostics = "diagnostics";
    public const string Operator = "operator";
    public const string History = "history";
    public const string Settings = "settings";
}

/// <summary>
/// One tab inside a menu section: a shell page, or a page of one module (its original view model, shown with its
/// original view). A module page that cannot be shown (module off, failed, starting) shows a placeholder instead.
/// </summary>
public sealed partial class ShellTab(string key, string title, string? moduleId, object content) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;

    /// <summary>The module this page belongs to, or null for the shell's own pages.</summary>
    public string? ModuleId { get; } = moduleId;

    /// <summary>"A · Local AI Performance Center" etc., shown as the page's origin.</summary>
    public string? Origin => ModuleId is null ? null : ModuleIds.Origin(ModuleId);

    [ObservableProperty]
    public partial object Content { get; set; } = content;

    /// <summary>The module's status/controls strip shown above its pages (its original status bar), or null.</summary>
    [ObservableProperty]
    public partial object? Header { get; set; }

    /// <summary>Called when the tab becomes visible (module pages: make it the module's current page so it gets live data).</summary>
    public Action? Activated { get; set; }
}

public sealed partial class ShellSection(string key, string title, string glyph, string description) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; } = glyph;

    public string Description { get; } = description;

    public ObservableCollection<ShellTab> Tabs { get; } = [];

    [ObservableProperty]
    public partial ShellTab? SelectedTab { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public ShellTab? Find(string key) => Tabs.FirstOrDefault(t => t.Key == key);

    partial void OnSelectedTabChanged(ShellTab? value)
    {
        if (IsActive)
        {
            value?.Activated?.Invoke();
        }
    }

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
        {
            SelectedTab?.Activated?.Invoke();
        }
    }
}

/// <summary>Shown in place of a module's pages while the module is off, starting or failed. Never shows stale or fake data.</summary>
public sealed partial class ModuleUnavailableViewModel : ObservableObject
{
    private readonly Func<Task>? _restart;

    public ModuleUnavailableViewModel(string moduleId, string pageTitle, ModuleStatus status, Func<Task>? restart)
    {
        ModuleId = moduleId;
        PageTitle = pageTitle;
        _restart = restart;
        Update(status);
    }

    public string ModuleId { get; }
    public string ModuleName => ModuleIds.DisplayName(ModuleId);
    public string Origin => ModuleIds.Origin(ModuleId);
    public string PageTitle { get; }

    [ObservableProperty]
    public partial string StateText { get; set; } = "";

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial string? Detail { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestartCommand))]
    public partial bool CanRestart { get; set; }

    public void Update(ModuleStatus status)
    {
        StateText = ModuleStateText.ToKorean(status.State);
        Message = status.State switch
        {
            ModuleState.Disabled => status.Summary == "이번 실행에서는 사용하지 않습니다."
                ? $"{ModuleName} 모듈은 이번 실행에서 사용하지 않습니다."
                : $"{ModuleName} 모듈이 설정에서 꺼져 있습니다. 설정 › 공통에서 켤 수 있습니다 (다시 시작 필요).",
            ModuleState.Starting or ModuleState.NotStarted => $"{ModuleName} 모듈을 시작하는 중입니다.",
            ModuleState.Failed => $"{ModuleName} 모듈이 동작하지 않습니다: {status.Summary}. 다른 모듈은 계속 동작합니다.",
            _ => $"{ModuleName} 모듈: {status.Summary}",
        };
        Detail = status.Detail;
        CanRestart = _restart is not null && status.State is ModuleState.Failed or ModuleState.Stopped;
    }

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RestartAsync()
    {
        if (_restart is not null)
        {
            CanRestart = false;
            await _restart();
        }
    }
}

/// <summary>One module in the common status bar and on the dashboard.</summary>
public sealed partial class ModuleStatusRow(string moduleId) : ObservableObject
{
    public string ModuleId { get; } = moduleId;
    public string Name => ModuleIds.DisplayName(ModuleId);
    public string Origin => ModuleIds.Origin(ModuleId);

    [ObservableProperty]
    public partial string StateText { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial Level Level { get; set; }

    public void Update(ModuleStatus status)
    {
        StateText = ModuleStateText.ToKorean(status.State);
        Summary = status.Detail is { Length: > 0 } detail ? $"{status.Summary} ({detail})" : status.Summary;
        Level = status.State switch
        {
            ModuleState.Running => Level.Good,
            ModuleState.Degraded or ModuleState.Starting or ModuleState.NotStarted or ModuleState.Stopping => Level.Warning,
            ModuleState.Failed => Level.Error,
            _ => Level.Muted,
        };
    }
}
