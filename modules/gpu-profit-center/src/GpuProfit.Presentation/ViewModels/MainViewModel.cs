using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuProfit.Accounting.Analysis;
using GpuProfit.Core;
using GpuProfit.Core.Domain;
using GpuProfit.Core.Settings;
using GpuProfit.Recording;
using GpuProfit.Storage;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;

namespace GpuProfit.Presentation.ViewModels;

/// <summary>Everything a page needs: the engine, settings, dialogs and navigation. (Structure from project C's ViewContext.)</summary>
public sealed class ViewContext
{
    public required ProfitEngine Engine { get; init; }
    public required SettingsStore SettingsStore { get; init; }
    public required IUiDispatcher Ui { get; init; }
    public required IDialogService Dialogs { get; init; }
    public required IShellService Shell { get; init; }
    public IAppHost? Host { get; init; }
    public IAppLogger Log { get; init; } = NullLogger.Instance;
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public MainViewModel? Main { get; set; }

    public ProfitStore Store => Engine.Store;
    public SessionService Sessions => Engine.Sessions;
    public AppSettings Settings => Engine.Settings;
    public AppPaths Paths => Engine.Options.Paths;
    public DateTimeOffset Now => Time.GetUtcNow();

    /// <summary>Raised (on the UI thread) after sessions, entries or accounting conditions changed, so pages can reload.</summary>
    public event Action? DataChanged;

    public void NotifyDataChanged() => Ui.Post(() => DataChanged?.Invoke());

    /// <summary>Calculates sessions with the shared engine on a background thread.</summary>
    public Task<List<AccountedSession>> AccountAsync(IEnumerable<WorkSession> sessions)
    {
        var list = sessions.ToList();
        return Task.Run(() =>
        {
            var workloads = Store.ListWorkloads().ToDictionary(w => w.Id);
            var profile = Store.GetProfile();
            return list.Select(s => new AccountedSession(s, workloads.GetValueOrDefault(s.WorkloadId), Sessions.Compute(s.Id, profile))).ToList();
        });
    }
}

/// <summary>Base of the eight screens. Busy adapted from project C's PageViewModel (commit 06cc9e5).</summary>
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

    public virtual void OnSensor(HardwareSnapshot snapshot)
    {
    }

    public virtual void Tick(DateTimeOffset now)
    {
    }

    // The page whose work is running in this async flow. Per page: work of one page never skips another page's gate
    // (project C kept a single flag for all pages, which let a refresh triggered from another page run unserialized).
    private static readonly AsyncLocal<PageViewModel?> BusyOwner = new();
    private readonly SemaphoreSlim _busyGate = new(1, 1);

    /// <summary>
    /// Runs work with the busy flag; failures are shown in the status line and logged, never thrown into the UI.
    /// Work items of one page run one after another; a nested call from inside running work runs directly.
    /// </summary>
    protected async Task Busy(Func<Task> work)
    {
        if (ReferenceEquals(BusyOwner.Value, this))
        {
            await work();
            return;
        }

        await _busyGate.WaitAsync();
        var previous = BusyOwner.Value;
        BusyOwner.Value = this;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "오류: " + SecretRedactor.Redact(ex.Message);
            Context.Log.Error("ui", $"{Title}: 작업 실패", ex);
        }
        finally
        {
            IsBusy = false;
            BusyOwner.Value = previous;
            _busyGate.Release();
        }
    }

    /// <summary>Runs a user action; input and rule errors (ArgumentException, InvalidOperationException) go to the status line.</summary>
    protected Task Act(Func<Task> work, string? done = null) => Busy(async () =>
    {
        try
        {
            await work();
            if (done is not null)
            {
                StatusText = done;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            StatusText = "입력 확인: " + ex.Message;
        }
    });

    protected static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    protected static decimal ParseMoney(string? text, string field)
        => DecimalText.TryParseUser(text, out var v) ? v : throw new ArgumentException($"{field}: 숫자를 입력하세요.");

    protected static decimal? ParseOptionalMoney(string? text, string field)
        => string.IsNullOrWhiteSpace(text) ? null : ParseMoney(text, field);

    protected static double ParseNumber(string? text, string field)
        => DecimalText.TryParseUserDouble(text, out var v) ? v : throw new ArgumentException($"{field}: 숫자를 입력하세요.");

    protected static double? ParseOptionalNumber(string? text, string field)
        => string.IsNullOrWhiteSpace(text) ? null : ParseNumber(text, field);

    protected static DateTimeOffset ParseTime(string? text, string field)
        => Fmt.TryParseInputTime(text, out var t) ? t : throw new ArgumentException($"{field}: 'yyyy-MM-dd HH:mm' 형식으로 입력하세요.");
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ViewContext _context;
    private DateTimeOffset _lastStatus;

    public MainViewModel(ViewContext context)
    {
        _context = context;
        context.Main = this;
        Fmt.UseUtc = context.Settings.Ui.UseUtc;
        Dashboard = new DashboardViewModel(context);
        Hardware = new HardwareViewModel(context);
        Sessions = new SessionsViewModel(context);
        Profitability = new ProfitabilityViewModel(context);
        Compare = new CompareViewModel(context);
        Scenarios = new ScenariosViewModel(context);
        History = new HistoryViewModel(context);
        Settings = new SettingsViewModel(context);
        Pages = [Dashboard, Hardware, Sessions, Profitability, Compare, Scenarios, History, Settings];
        IsMock = context.Engine.IsMock;
        AppTitle = $"{AppInfo.ProductName} {AppInfo.Version}" + (IsMock ? "  [MOCK 센서]" : "");

        if (context.Store.OpenNotice is { } notice)
        {
            Notices.Add(notice);
        }

        if (context.Paths.Note is { } pathNote)
        {
            Notices.Add(pathNote);
        }

        foreach (var run in context.Engine.RecoveredRuns)
        {
            Notices.Add($"이전 실행({Fmt.Time(run.Started)} 시작)이 정상 종료되지 않았습니다. 마지막 기록({Fmt.Time(run.Ended)})까지의 데이터는 보존했습니다.");
        }

        foreach (var s in context.Engine.RecoveredSessions)
        {
            Notices.Add($"세션 #{s.Id}은(는) 기록 중에 앱이 끝나 마지막 기록 시점({Fmt.Time(s.Ended)})에서 닫았습니다. 앱이 꺼져 있던 시간은 세션 시간·에너지에 넣지 않았습니다. 세션 화면에서 '재개'할 수 있습니다.");
        }

        if (context.Engine.Hardware is { Gpu.IsAvailable: false } hw)
        {
            Notices.Add($"GPU 센서를 사용할 수 없습니다: {hw.Gpu.UnavailableReason} GPU 값은 N/A로 표시하며, 외부 전력계 값으로 세션을 계산할 수 있습니다.");
        }

        context.Engine.SensorSampled += OnSensor;
        context.Sessions.SessionChanged += _ => context.NotifyDataChanged();
        CurrentPage = Dashboard;
        UpdateStatus(context.Now);
    }

    public DashboardViewModel Dashboard { get; }
    public HardwareViewModel Hardware { get; }
    public SessionsViewModel Sessions { get; }
    public ProfitabilityViewModel Profitability { get; }
    public CompareViewModel Compare { get; }
    public ScenariosViewModel Scenarios { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    public ObservableCollection<string> Notices { get; } = [];
    public string AppTitle { get; }
    public bool IsMock { get; }

    [ObservableProperty] public partial PageViewModel CurrentPage { get; set; }
    [ObservableProperty] public partial string SensorText { get; set; } = "";
    [ObservableProperty] public partial bool SensorOk { get; set; }
    [ObservableProperty] public partial string RecordingText { get; set; } = "";
    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial string DatabaseText { get; set; } = "";
    [ObservableProperty] public partial bool HasDemoData { get; set; }

    partial void OnCurrentPageChanged(PageViewModel oldValue, PageViewModel newValue)
    {
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
        CurrentPage.Tick(now);
        if (now - _lastStatus >= TimeSpan.FromSeconds(5))
        {
            UpdateStatus(now);
        }
    }

    /// <summary>Opens the accounting of one session.</summary>
    public void ShowProfitability(long sessionId)
    {
        Profitability.Select(sessionId);
        CurrentPage = Profitability;
    }

    public void ShowSession(long sessionId)
    {
        Sessions.Select(sessionId);
        CurrentPage = Sessions;
    }

    [RelayCommand]
    private void DismissNotice(string notice) => Notices.Remove(notice);

    private void OnSensor(HardwareSnapshot snapshot) => _context.Ui.Post(() =>
    {
        foreach (var page in Pages)
        {
            page.OnSensor(snapshot);
        }
    });

    private void UpdateStatus(DateTimeOffset now)
    {
        _lastStatus = now;
        try
        {
            var latest = _context.Engine.Recorder?.Live.Latest;
            var withPower = latest?.Gpus.Count(g => g.PowerW is not null) ?? 0;
            SensorOk = withPower > 0;
            SensorText = latest is null ? "센서: 측정 대기"
                : withPower > 0 ? $"센서: GPU {withPower}개 측정 중{(IsMock ? " (MOCK)" : "")} · {Fmt.ShortTime(latest.Timestamp)}"
                : "센서: GPU 전력 N/A (미지원·사용 불가)";
            var open = _context.Store.ListSessions(new SessionQuery { State = SessionState.Running });
            IsRecording = open.Count > 0;
            RecordingText = open.Count == 0 ? "기록 중인 세션 없음" : $"기록 중: {string.Join(", ", open.Select(s => $"#{s.Id} {s.Title}".Trim()))}";
            var stats = _context.Store.GetStats();
            DatabaseText = $"DB {(stats.FileBytes + stats.WalBytes) / (1024.0 * 1024):0.0} MB · 세션 {stats.Sessions} · 샘플 {stats.GpuSamples:N0}{(_context.Paths.IsPortable ? " · 포터블" : "")}";
            HasDemoData = DemoDataSeeder.HasDemoData(_context.Store);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Warn("ui", "상태 표시줄 갱신 실패", ex);
        }
    }

    public void Dispose()
    {
        _context.Engine.SensorSampled -= OnSensor;
    }
}
