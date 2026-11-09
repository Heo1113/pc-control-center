using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Recording;
using LocalAiPerf.Presentation.Services;

namespace LocalAiPerf.Presentation.ViewModels;

public enum Freshness
{
    Waiting,
    Fresh,
    Stale,
    Stopped,
}

/// <summary>Shell: navigation, status bar, recording controls and notices.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly MonitoringService _service;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private TelemetryFrame? _latest;
    private DateTimeOffset _lastDatabaseCheck = DateTimeOffset.MinValue;
    private int _framePending;

    public MainViewModel(MonitoringService service, SettingsStore settingsStore, IUiDispatcher ui, IDialogService dialogs, IShellService shell,
        MemoryLogSink? logSink = null, TimeProvider? time = null)
    {
        _service = service;
        _ui = ui;
        _dialogs = dialogs;
        _time = time ?? TimeProvider.System;
        Dashboard = new DashboardViewModel(service);
        Hardware = new HardwareViewModel(service);
        Runtime = new RuntimeViewModel(service);
        History = new HistoryViewModel(service, dialogs, shell, ui, _time);
        Compare = new CompareViewModel(service, dialogs, ui);
        Settings = new SettingsViewModel(service, settingsStore, dialogs, shell, ui, logSink);
        Pages = [Dashboard, Hardware, Runtime, History, Compare, Settings];
        IsMock = service.IsMock;
        Notices = new ObservableCollection<string>(service.Notices);
        CurrentPage = Dashboard;
        service.FrameCollected += OnFrameFromCollector;
        service.StateChanged += OnStateFromService;
        UpdateRecordingState();
        Tick();
    }

    public DashboardViewModel Dashboard { get; }
    public HardwareViewModel Hardware { get; }
    public RuntimeViewModel Runtime { get; }
    public HistoryViewModel History { get; }
    public CompareViewModel Compare { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    public ObservableCollection<string> Notices { get; }
    public bool IsMock { get; }
    public string AppTitle => $"{AppInfo.ProductName} {AppInfo.Version}";

    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; set; }

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial string RecordingText { get; set; } = "기록 안 함";

    [ObservableProperty]
    public partial bool HasStorageProblem { get; set; }

    [ObservableProperty]
    public partial string FreshnessText { get; set; } = "데이터 기다리는 중";

    [ObservableProperty]
    public partial Freshness Freshness { get; set; } = Freshness.Waiting;

    [ObservableProperty]
    public partial string RuntimeStatusText { get; set; } = "AI 엔진: -";

    [ObservableProperty]
    public partial RuntimeConnectionState RuntimeState { get; set; } = RuntimeConnectionState.Disabled;

    [ObservableProperty]
    public partial string DatabaseText { get; set; } = "DB: -";

    /// <summary>Number of frames received (used by the smoke test).</summary>
    public long FramesReceived { get; private set; }

    partial void OnCurrentPageChanged(PageViewModel oldValue, PageViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }

        newValue.IsActive = true;
        _ = ActivateAsync(newValue);
    }

    private async Task ActivateAsync(PageViewModel page)
    {
        try
        {
            await page.OnActivatedAsync();
            if (_latest is not null && page.IsActive)
            {
                page.OnFrame(_latest);
            }
        }
        catch (Exception ex)
        {
            _dialogs.Error("화면 오류", $"{page.Title} 화면을 여는 중 오류가 났습니다: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        if (_service.Recorder is null)
        {
            _dialogs.Error("기록할 수 없음", _service.StorageProblem ?? "데이터베이스가 열려 있지 않습니다.");
            return;
        }

        var name = _dialogs.Prompt("수동 기록 시작", "세션 이름 (예: Strata 0.2.9 업데이트 전, Q4_K_XL ctx 32k):",
            $"수동 기록 {DateTime.Now:yyyy-MM-dd HH:mm}");
        if (name is null)
        {
            return;
        }

        await _service.StartRecordingAsync(name);
        UpdateRecordingState();
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        await _service.StopRecordingAsync();
        UpdateRecordingState();
    }

    [RelayCommand]
    private void DismissNotice(string? notice)
    {
        if (notice is not null)
        {
            Notices.Remove(notice);
        }
    }

    [RelayCommand]
    private void Navigate(PageViewModel? page)
    {
        if (page is not null)
        {
            CurrentPage = page;
        }
    }

    /// <summary>Called every second by the view's timer: refreshes the age of the newest data.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        var interval = _service.Interval;
        if (_latest is null)
        {
            Freshness = Freshness.Waiting;
            FreshnessText = "데이터 기다리는 중";
        }
        else
        {
            var age = now - _latest.Timestamp;
            Freshness = age <= interval * 2 + TimeSpan.FromSeconds(1) ? Freshness.Fresh
                : age <= interval * 10 + TimeSpan.FromSeconds(2) ? Freshness.Stale : Freshness.Stopped;
            var state = Freshness switch { Freshness.Fresh => "정상 갱신", Freshness.Stale => "갱신 지연", _ => "갱신 중단" };
            FreshnessText = $"측정 {Fmt.Time(_latest.Timestamp)} · {Math.Max(0, age.TotalSeconds):0.0}초 전 · {state}";
        }

        var rt = _service.LatestRuntime;
        if (_service.Runtime is null)
        {
            RuntimeState = RuntimeConnectionState.Disabled;
            RuntimeStatusText = "AI 엔진: 꺼짐";
        }
        else if (rt is null)
        {
            RuntimeState = RuntimeConnectionState.Connecting;
            RuntimeStatusText = $"{_service.Runtime.DisplayName}: 연결 중";
        }
        else
        {
            RuntimeState = rt.State;
            RuntimeStatusText = $"{_service.Runtime.DisplayName}: {RuntimeStateText.ToKorean(rt.State)}";
        }

        if (IsRecording && _service.Recorder?.ActiveSession is { } session)
        {
            RecordingText = $"기록 중: {session.Name} ({Fmt.Duration(now - session.StartedAt)})";
        }

        if (now - _lastDatabaseCheck > TimeSpan.FromSeconds(30))
        {
            _lastDatabaseCheck = now;
            try
            {
                DatabaseText = _service.Store is { } store ? $"DB {Fmt.Bytes(store.UsedBytes())}" : "DB 사용 안 함";
            }
            catch (Exception ex) when (ex is ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException)
            {
                DatabaseText = "DB 확인 불가";
            }
        }
    }

    public void Dispose()
    {
        _service.FrameCollected -= OnFrameFromCollector;
        _service.StateChanged -= OnStateFromService;
    }

    private void OnFrameFromCollector(TelemetryFrame frame)
    {
        // Coalesce: if the UI is still busy with the previous frame, only the newest one is shown.
        _latest = frame;
        if (Interlocked.Exchange(ref _framePending, 1) == 1)
        {
            return;
        }

        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _framePending, 0);
            var newest = _latest;
            if (newest is null)
            {
                return;
            }

            FramesReceived++;
            try
            {
                CurrentPage.OnFrame(newest);
            }
            catch (Exception ex)
            {
                RecordingText = $"화면 갱신 오류: {ex.Message}";
            }

            Tick();
        });
    }

    private void OnStateFromService() => _ui.Post(() =>
    {
        UpdateRecordingState();
        foreach (var notice in _service.Notices)
        {
            if (!Notices.Contains(notice))
            {
                Notices.Add(notice);
            }
        }
    });

    private void UpdateRecordingState()
    {
        var recorder = _service.Recorder;
        HasStorageProblem = (recorder is null && _service.StorageProblem is not null) || recorder?.Status == RecorderStatus.StorageError;
        IsRecording = recorder?.ActiveSession is not null;
        if (recorder is null)
        {
            RecordingText = _service.StorageProblem is null ? "기록 안 함" : "기록 불가 (데이터베이스 문제)";
        }
        else if (recorder.Status == RecorderStatus.StorageError)
        {
            RecordingText = $"저장 오류 - 다시 시도 중: {recorder.LastError}";
        }
        else if (recorder.ActiveSession is { } session)
        {
            RecordingText = $"기록 중: {session.Name} ({Fmt.Duration(_time.GetUtcNow() - session.StartedAt)})";
        }
        else
        {
            RecordingText = "기록 안 함 (실시간 표시만)";
        }

        StopRecordingCommand.NotifyCanExecuteChanged();
    }
}
