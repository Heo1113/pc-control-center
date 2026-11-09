using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;
using WinDiag.Collection;
using WinDiag.Collection.Notifications;
using WinDiag.Core;
using WinDiag.Core.Events;
using WinDiag.Core.Privacy;
using WinDiag.Core.Settings;
using WinDiag.Core.Time;
using WinDiag.Storage;

namespace WinDiag.Presentation.ViewModels;

/// <summary>Everything a page needs: the engine, settings, dialogs and navigation.</summary>
public sealed class ViewContext
{
    public required DiagnosticsEngine Engine { get; init; }
    public required SettingsStore SettingsStore { get; init; }
    public required IUiDispatcher Ui { get; init; }
    public required IDialogService Dialogs { get; init; }
    public required IShellService Shell { get; init; }
    public IAppHost? Host { get; init; }
    public IAppLogger Log { get; init; } = NullLogger.Instance;
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Masks exported text again for this machine's user and computer names.</summary>
    public PrivacyMasker ExportMasker { get; init; } = PrivacyMasker.ForCurrentMachine();

    public MainViewModel? Main { get; set; }

    public DiagnosticsStore Store => Engine.Store;
    public AppSettings Settings => Engine.Settings;
    public DateTimeOffset Now => Time.GetUtcNow();
}

/// <summary>Base of the seven screens. Only the visible page refreshes on the timer.</summary>
public abstract partial class PageViewModel(ViewContext context) : ObservableObject
{
    protected ViewContext Context { get; } = context;

    public abstract string Title { get; }

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public abstract string Glyph { get; }

    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";

    public virtual Task OnActivatedAsync() => Task.CompletedTask;

    private Task? _firstLoad;

    /// <summary>True once the page's first load has been started (it may still be running).</summary>
    protected bool HasStartedFirstLoad => _firstLoad is not null;

    /// <summary>
    /// Starts the page's first load once; every later call returns the same task. An activation or navigation that
    /// arrives while the first load is still running therefore waits for the data instead of returning early, and work
    /// that follows (opening one event, selecting one crash) cannot be overwritten by the first load finishing later.
    /// The load must not throw (page work goes through <see cref="Busy"/>, which reports failures in the status line).
    /// </summary>
    protected Task LoadOnce(Func<Task> load) => _firstLoad ??= load();

    public virtual void OnSensor(HardwareSnapshot snapshot)
    {
    }

    public virtual void Tick(DateTimeOffset now)
    {
    }

    private static readonly AsyncLocal<bool> InsideBusy = new();
    private readonly SemaphoreSlim _busyGate = new(1, 1);

    /// <summary>
    /// Runs work with the busy flag; failures are shown in the status line and logged, never thrown into the UI.
    /// Work items of one page run one after another (a refresh started by activation and one started by a button never
    /// interleave); a nested call from inside running work runs directly.
    /// </summary>
    protected async Task Busy(Func<Task> work)
    {
        if (InsideBusy.Value)
        {
            await work();
            return;
        }

        await _busyGate.WaitAsync();
        InsideBusy.Value = true;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "오류: " + ex.Message;
            Context.Log.Error("ui", $"{Title}: 작업 실패", ex);
        }
        finally
        {
            IsBusy = false;
            InsideBusy.Value = false;
            _busyGate.Release();
        }
    }

    protected static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ViewContext _context;
    private DateTimeOffset _lastStats;

    public MainViewModel(ViewContext context)
    {
        _context = context;
        context.Main = this;
        Fmt.Display = context.Settings.Ui.UseUtc ? TimeDisplay.Utc : TimeDisplay.Local;
        Dashboard = new DashboardViewModel(context);
        Events = new EventsViewModel(context);
        Hardware = new HardwareViewModel(context);
        Timeline = new TimelineViewModel(context);
        Diagnostics = new DiagnosticsViewModel(context);
        Compare = new CompareViewModel(context);
        Settings = new SettingsViewModel(context);
        Pages = [Dashboard, Events, Hardware, Timeline, Diagnostics, Compare, Settings];
        AppTitle = $"{AppInfo.ProductName} {AppInfo.Version}" + (context.Engine.IsMock ? "  [MOCK 센서]" : "");
        IsMock = context.Engine.IsMock;

        if (context.Store.OpenNotice is { } notice)
        {
            Notices.Add(notice);
        }

        foreach (var run in context.Engine.RecoveredRuns)
        {
            Notices.Add($"이전 실행({Fmt.Time(run.Started)} 시작)이 정상 종료되지 않았습니다. 마지막 기록({Fmt.Time(run.Ended)})까지의 데이터는 보존되었습니다.");
        }

        if (context.Engine.EventCollectionUnavailableReason is { } reason)
        {
            Notices.Add(reason);
        }

        context.Engine.Notified += OnNotified;
        context.Engine.SensorSampled += OnSensor;
        CurrentPage = Dashboard;
        UpdateStatus(context.Now, force: true);
    }

    public DashboardViewModel Dashboard { get; }
    public EventsViewModel Events { get; }
    public HardwareViewModel Hardware { get; }
    public TimelineViewModel Timeline { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public CompareViewModel Compare { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    public ObservableCollection<string> Notices { get; } = [];
    public ObservableCollection<NotificationRow> Notifications { get; } = [];
    public string AppTitle { get; }
    public bool IsMock { get; }

    /// <summary>Raised on the UI thread for every notification (the WPF app shows a tray balloon).</summary>
    public event Action<DiagNotification>? NotificationRaised;

    [ObservableProperty] public partial PageViewModel CurrentPage { get; set; }
    [ObservableProperty] public partial string CollectionText { get; set; } = "";
    [ObservableProperty] public partial StatusLevel CollectionLevel { get; set; }
    [ObservableProperty] public partial string SensorText { get; set; } = "";
    [ObservableProperty] public partial StatusLevel SensorLevel { get; set; }
    [ObservableProperty] public partial string DatabaseText { get; set; } = "";
    [ObservableProperty] public partial bool HasVirtualData { get; set; }

    partial void OnCurrentPageChanged(PageViewModel oldValue, PageViewModel newValue)
    {
        // oldValue is null when the first page is set in the constructor.
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }

        newValue.IsActive = true;
        _ = newValue.OnActivatedAsync();
    }

    public void Tick()
    {
        var now = _context.Now;
        UpdateStatus(now, force: false);
        CurrentPage.Tick(now);
    }

    public void ShowEvent(long eventId)
    {
        CurrentPage = Events;
        _ = Events.OpenEventAsync(eventId);
    }

    public void ShowHardwareAt(DateTimeOffset time, string title)
    {
        Hardware.Focus(time, title);
        CurrentPage = Hardware;
    }

    public void ShowTimelineAround(DateTimeOffset time)
    {
        Timeline.Around(time);
        CurrentPage = Timeline;
    }

    [RelayCommand]
    private void DismissNotice(string notice) => Notices.Remove(notice);

    [RelayCommand]
    private void DismissNotification(NotificationRow row) => Notifications.Remove(row);

    [RelayCommand]
    private void OpenNotification(NotificationRow row)
    {
        Notifications.Remove(row);
        ShowEvent(row.EventId);
    }

    public void Dispose()
    {
        _context.Engine.Notified -= OnNotified;
        _context.Engine.SensorSampled -= OnSensor;
    }

    private void OnNotified(DiagNotification notification) => _context.Ui.Post(() =>
    {
        Notifications.Insert(0, new NotificationRow(Fmt.Clock(notification.Time), notification.Title, notification.Message, notification.Severity, notification.EventId));
        while (Notifications.Count > 5)
        {
            Notifications.RemoveAt(Notifications.Count - 1);
        }

        NotificationRaised?.Invoke(notification);
    });

    private void OnSensor(HardwareSnapshot snapshot) => _context.Ui.Post(() =>
    {
        Dashboard.OnSensor(snapshot);
        if (CurrentPage == Hardware)
        {
            Hardware.OnSensor(snapshot);
        }
    });

    private void UpdateStatus(DateTimeOffset now, bool force)
    {
        var engine = _context.Engine;
        if (engine.Collector is null)
        {
            CollectionText = "이벤트 수집 불가 (Windows 아님)";
            CollectionLevel = StatusLevel.Critical;
        }
        else if (engine.IsCollecting)
        {
            CollectionText = "이벤트 확인 중…";
            CollectionLevel = StatusLevel.Good;
        }
        else if (engine.LastPollError is { } error)
        {
            CollectionText = "이벤트 수집 오류: " + error;
            CollectionLevel = StatusLevel.Critical;
        }
        else
        {
            var denied = engine.LastReport?.Channels.Count(c => c.Status is ChannelStatus.AccessDenied or ChannelStatus.Error) ?? 0;
            CollectionText = engine.LastPoll is { } last
                ? $"이벤트 확인 {Fmt.Ago(last, now)}" + (denied > 0 ? $" · 읽지 못한 로그 {denied}개" : "")
                : "이벤트 확인 대기";
            CollectionLevel = denied > 0 ? StatusLevel.Warning : engine.LastPoll is null ? StatusLevel.Neutral : StatusLevel.Good;
        }

        var latest = engine.Recorder?.Live.Latest;
        if (engine.Recorder is null)
        {
            SensorText = "센서 기록 꺼짐";
            SensorLevel = StatusLevel.Neutral;
        }
        else if (latest is null)
        {
            SensorText = "센서 대기";
            SensorLevel = StatusLevel.Neutral;
        }
        else
        {
            var age = now - latest.Timestamp;
            var gpu = engine.Hardware.Gpu.IsAvailable ? "GPU·CPU·RAM" : "CPU·RAM (GPU 센서 없음)";
            SensorText = $"센서 {gpu} · {Fmt.Ago(latest.Timestamp, now)}" + (engine.IsMock ? " · MOCK" : "");
            SensorLevel = age > TimeSpan.FromSeconds(engine.Settings.Sensors.IntervalSeconds * 3 + 5) ? StatusLevel.Warning : StatusLevel.Good;
        }

        if (force || now - _lastStats > TimeSpan.FromSeconds(30))
        {
            _lastStats = now;
            try
            {
                var stats = _context.Store.GetStats();
                DatabaseText = $"DB {Fmt.Mb(stats.FileBytes + stats.WalBytes)} · 이벤트 {stats.Events:N0}건";
                HasVirtualData = stats.VirtualEvents > 0;
                if (stats.FileBytes > _context.Settings.Retention.WarnDatabaseMb * 1048576L && !Notices.Any(n => n.StartsWith("데이터베이스 크기", StringComparison.Ordinal)))
                {
                    Notices.Add($"데이터베이스 크기가 경고 기준({_context.Settings.Retention.WarnDatabaseMb} MB)을 넘었습니다. 설정에서 보존 기간을 확인하세요. (자동으로 삭제하지 않습니다)");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DatabaseText = "DB 상태 확인 실패";
                _context.Log.Warn("ui", "DB 상태 확인 실패", ex);
            }
        }
    }
}
