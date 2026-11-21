using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcControlCenter.Basalt;
using PcControlCenter.Core;

namespace PcControlCenter.Presentation;

/// <summary>
/// 로컬 AI › Basalt: the server's state, quality/fast mode, start/stop, the last stop's result, and Basalt's own dashboard
/// (shown by the view in WebView2 only while Basalt answers). Works whether the local AI module runs or not.
/// </summary>
public sealed partial class BasaltViewModel : ObservableObject
{
    private readonly BasaltController _controller;
    private readonly ShellSettingsStore _store;
    private readonly ShellSettings _settings;
    private readonly IShellDialogs _dialogs;
    private readonly IShellDispatcher _ui;
    private readonly Action<string>? _navigate;
    private bool _visible;
    private string _shownKey = "";

    public BasaltViewModel(BasaltController controller, ShellSettingsStore store, ShellSettings settings, IShellDialogs dialogs, IShellDispatcher ui,
        string webViewDataFolder, Action<string>? navigate = null)
    {
        _controller = controller;
        _store = store;
        _settings = settings;
        _dialogs = dialogs;
        _ui = ui;
        _navigate = navigate;
        WebViewDataFolder = webViewDataFolder;
        IsMock = controller.IsMock;
        IsFast = settings.Basalt.Mode == BasaltSettings.FastMode;
        Apply(controller.Status);
        controller.Changed += status => _ui.Post(() => Apply(status));
    }

    public bool IsMock { get; }

    /// <summary>WebView2's profile folder (under the app's data folder: cookies and storage of the dashboard stay there).</summary>
    public string WebViewDataFolder { get; }

    [ObservableProperty] public partial BasaltState State { get; private set; }
    [ObservableProperty] public partial string StateText { get; private set; } = "";
    [ObservableProperty] public partial Level StateLevel { get; private set; }
    [ObservableProperty] public partial string Detail { get; private set; } = "";
    [ObservableProperty] public partial string ModeLine { get; private set; } = "";
    [ObservableProperty] public partial string? Notice { get; private set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool ShowDashboard { get; private set; }
    [ObservableProperty] public partial string DashboardUrl { get; private set; } = "";
    [ObservableProperty] public partial string GuidanceTitle { get; private set; } = "";
    [ObservableProperty] public partial string GuidanceText { get; private set; } = "";
    [ObservableProperty] public partial string? LastStopSummary { get; private set; }
    [ObservableProperty] public partial Level LastStopLevel { get; private set; }

    /// <summary>Why the dashboard cannot be shown inside the app (no WebView2 runtime, ...); set by the view.</summary>
    [ObservableProperty] public partial string? WebViewProblem { get; set; }

    [ObservableProperty] public partial bool IsFast { get; set; }

    /// <summary>The processes of the server and how each was recognised (tooltip and details).</summary>
    public ObservableCollection<string> Processes { get; } = [];

    public ObservableCollection<string> LastStopLines { get; } = [];

    public bool IsQuality
    {
        get => !IsFast;
        set => IsFast = !value;
    }

    public string PathsLine => $"{_settings.Basalt.Resolve(_settings.Basalt.StartScript)} · 품질용 {_settings.Basalt.Resolve(_settings.Basalt.QualityConfig)} · "
                               + $"빠른 모드 {_settings.Basalt.Resolve(_settings.Basalt.FastConfig)}";

    /// <summary>The page is on screen: the controller reads the process tree at every check.</summary>
    public bool IsVisible
    {
        get => _visible;
        set
        {
            if (_visible == value)
            {
                return;
            }

            _visible = value;
            _controller.Detailed = value;
            if (value)
            {
                _ = _controller.RefreshAsync();
            }
        }
    }

    partial void OnIsFastChanged(bool value)
    {
        OnPropertyChanged(nameof(IsQuality));
        var mode = value ? BasaltSettings.FastMode : BasaltSettings.QualityMode;
        if (_settings.Basalt.Mode != mode)
        {
            _settings.Basalt.Mode = mode;
            try
            {
                _store.Save(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Notice = "모드 선택을 저장하지 못했습니다: " + ex.Message;
            }
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => !IsBusy && State is BasaltState.Off or BasaltState.Unknown or BasaltState.Failed;

    private bool CanStop() => !IsBusy && State is BasaltState.Running or BasaltState.Starting or BasaltState.Leftover or BasaltState.Failed;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsBusy = true;
        try
        {
            var refusal = await _controller.StartServerAsync(IsFast ? BasaltSettings.FastMode : BasaltSettings.QualityMode);
            Notice = refusal?.Message;
        }
        finally
        {
            IsBusy = false;
            _ui.Post(() => Apply(_controller.Status));
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        IsBusy = true;
        try
        {
            await _controller.StopServerAsync();
        }
        finally
        {
            IsBusy = false;
            _ui.Post(() => Apply(_controller.Status));
        }
    }

    [RelayCommand]
    private void OpenInBrowser() => _dialogs.OpenUrl(LocalUrl);

    [RelayCommand]
    private void OpenSettings() => _navigate?.Invoke("settings/common");

    [RelayCommand]
    private Task Refresh() => _controller.RefreshAsync();

    /// <summary>The view asks before WebView2 follows a link: only Basalt's own address stays in the app.</summary>
    public bool IsDashboardAddress(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && uri.Port == _controller.Port;

    /// <summary>A link outside Basalt's address: http(s) goes to the default browser, anything else is ignored.</summary>
    public void OpenOutside(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        {
            _dialogs.OpenUrl(uri.AbsoluteUri);
        }
    }

    private string LocalUrl => $"http://127.0.0.1:{_controller.Port.ToString(CultureInfo.InvariantCulture)}/";

    private void Apply(BasaltStatus status)
    {
        State = status.State;
        (StateText, StateLevel) = status.State switch
        {
            BasaltState.Running => ("실행 중", Level.Good),
            BasaltState.Starting => ("켜는 중", Level.Warning),
            BasaltState.Stopping => ("끄는 중", Level.Warning),
            BasaltState.PortBusy => ($"포트 {status.Port} 사용 중 (다른 프로그램)", Level.Error),
            BasaltState.Leftover => ("Basalt 프로세스가 남아 있음", Level.Error),
            BasaltState.Failed => ("문제 있음", Level.Error),
            BasaltState.Unavailable => ("사용할 수 없음", Level.Muted),
            BasaltState.Unknown => ("확인 중", Level.Muted),
            _ => ("꺼짐", Level.Muted),
        };
        if (status.IsMock)
        {
            StateText = "MOCK · " + StateText;
        }

        Detail = status.Detail;
        var mode = status.StartedHere ? BasaltController.ModeText(status.Mode)
            : status.State is BasaltState.Running or BasaltState.Starting ? "알 수 없음 (이 프로그램이 켜지 않음)"
            : null;
        ModeLine = $"127.0.0.1:{status.Port}" + (mode is null ? "" : $" · 모드 {mode}") + (status.IsMock ? " · MOCK 서버 (실제 Basalt 아님)" : "");
        Notice = status.Notice;
        DashboardUrl = LocalUrl;
        ShowDashboard = status.State == BasaltState.Running;
        (GuidanceTitle, GuidanceText) = status.State switch
        {
            BasaltState.Running => ("", ""),
            BasaltState.Starting => ("Basalt를 켜는 중입니다", "모델을 올리는 동안 몇 분 걸릴 수 있습니다. 응답하면 여기에 Basalt 대시보드가 나타납니다. 진행 상황은 Basalt 창에서 볼 수 있습니다."),
            BasaltState.Stopping => ("Basalt를 끄는 중입니다", "Ctrl+C로 정상 종료를 기다린 뒤, 남은 프로세스가 있으면 정리하고 결과를 확인합니다."),
            BasaltState.PortBusy => ($"포트 {status.Port}를 다른 프로그램이 쓰고 있습니다",
                "그 프로그램은 Basalt로 확인되지 않아 켜지도 끄지도 않습니다. 그 프로그램을 끄거나 설정 › 공통 › Basalt에서 포트를 바꾸세요."),
            BasaltState.Leftover => ("Basalt 프로세스가 남아 있습니다", "포트를 쓰지 않는 Basalt 프로세스가 있습니다. [끄기]를 누르면 정리하고 남은 것이 없는지 확인합니다."),
            BasaltState.Unavailable => ("Basalt 상태를 읽을 수 없습니다", status.Detail),
            _ => ("Basalt가 꺼져 있습니다",
                "품질용이나 빠른 모드를 고르고 [켜기]를 누르면 Basalt의 시작 스크립트를 새 창에서 실행합니다. 두 모드는 같은 포트를 써서 동시에 켤 수 없습니다. "
                + "PC Control Center를 닫아도 Basalt는 계속 실행됩니다."),
        };
        var lines = status.Tree?.Members.Select(m => $"{m.Process.Label} · {BasaltController.RoleText(m.Role)}").ToList() ?? [];
        if (status.Tree?.Unverified is { Count: > 0 } unverified)
        {
            lines.AddRange(unverified.Select(u => "확인 못 함: " + u));
        }

        var key = string.Join("|", lines);
        if (key != _shownKey)
        {
            _shownKey = key;
            Processes.Clear();
            foreach (var line in lines)
            {
                Processes.Add(line);
            }
        }

        if (status.LastStop is { } stop)
        {
            LastStopSummary = $"마지막 끄기 {stop.At.ToLocalTime():HH:mm:ss} · {stop.Summary} · {BasaltProcessTree.Seconds(stop.Took)}초";
            LastStopLevel = stop.Clean ? Level.Good : Level.Error;
            var stopLines = new List<string> { stop.Signal };
            stopLines.AddRange(stop.Ended.Select(e => "종료: " + e));
            stopLines.AddRange(stop.Left.Select(l => "남음: " + l));
            stopLines.Add(stop.PortFree ? $"포트 {status.Port}: 비어 있음" : $"포트 {status.Port}: 사용 중 ({stop.PortOwner})");
            if (!LastStopLines.SequenceEqual(stopLines))
            {
                LastStopLines.Clear();
                foreach (var line in stopLines)
                {
                    LastStopLines.Add(line);
                }
            }
        }

        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
}
