using GpuProfit.Core;
using GpuProfit.Recording;
using GpuProfit.Storage;
using LocalAiPerf.Core.Diagnostics;
using PcControlCenter.Core;
using AppSettings = GpuProfit.Core.Settings.AppSettings;
using SettingsStore = GpuProfit.Core.Settings.SettingsStore;

namespace PcControlCenter.Modules;

/// <summary>
/// Project B in the shell: B's ProfitEngine (energy, sessions, gpc.db) on the shared hardware reader. B's recorder
/// stores every raw reading it receives with the reading's own timestamp and records its own gaps (over-long intervals,
/// GPU loss, Windows sleep); it never sees A's display buffers.
/// </summary>
public sealed class GpuEconomicsModule(ModuleContext context) : ShellModuleBase(ModuleIds.GpuEconomics, context.Log, context.Time)
{
    private DataFolderLock? _lock;
    private FileAppLogger? _fileLog;

    public AppPaths? Paths { get; private set; }
    public SettingsStore? SettingsStore { get; private set; }
    public ProfitEngine? Engine { get; private set; }
    public MemoryLogSink? LogSink { get; private set; }
    public IReadOnlyList<string> SettingsNotes { get; private set; } = [];

    /// <summary>Raised after the engine was (re)started (for example after a restore), so the UI rebuilds B's view models.</summary>
    public event Action? EngineReady;

    protected override bool Enabled => context.Settings.IsEnabled(Id);

    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        var paths = AppPaths.Resolve(context.Paths.ModuleDirectory(Id), context.ExecutableDirectory);
        paths.EnsureDirectories();
        _lock = DataFolderLock.TryAcquire(paths.DataDirectory)
                ?? throw new InvalidOperationException("이 모듈의 데이터 폴더를 다른 프로세스(gpc 명령 등)가 사용 중입니다.");
        _fileLog = new FileAppLogger(paths.LogDirectory, filePrefix: GpuProfit.Core.AppInfo.ShortName);
        LogSink = new MemoryLogSink(500, _fileLog);
        var store = new SettingsStore(paths.SettingsPath);
        var loaded = store.Load();
        if (loaded.WasCreated && !context.Unattended)
        {
            TrySave(store, loaded.Settings);
        }

        Paths = paths;
        SettingsStore = store;
        SettingsNotes = loaded.Notes;
        await StartEngineAsync(loaded.Settings).ConfigureAwait(false);
    }

    protected override async Task OnStopAsync()
    {
        await StopEngineAsync(RunEndReasons.Normal).ConfigureAwait(false);
        await context.Intervals.ReleaseAsync(Id).ConfigureAwait(false);
        _lock?.Dispose();
        _lock = null;
        _fileLog?.Dispose();
        _fileLog = null;
    }

    /// <summary>
    /// B's restore inside the shell: B's engine stops, ProfitStore.RestoreFrom replaces gpc.db (the current file is kept
    /// aside) and B starts again. The other modules keep running (the original program restarted itself instead).
    /// </summary>
    public async Task<string?> RestoreAsync(string backupPath)
    {
        if (Paths is not { } paths || SettingsStore is not { } store)
        {
            throw new InvalidOperationException("모듈이 실행 중이 아닙니다.");
        }

        SetStatus(ModuleState.Starting, "복원 중");
        await StopEngineAsync(RunEndReasons.Restore).ConfigureAwait(false);
        string? aside = null;
        Exception? failure = null;
        try
        {
            aside = await Task.Run(() => ProfitStore.RestoreFrom(backupPath, paths.DatabasePath, context.Time)).ConfigureAwait(false);
            LogSink?.Info("app", $"복원 완료, 이전 데이터베이스 보관: {(aside is null ? "없음" : Path.GetFileName(aside))}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // RestoreFrom puts the previous database back when it fails.
            LogSink?.Error("app", "복원 실패 (기존 데이터베이스를 되돌렸습니다)", ex);
            failure = ex;
        }

        await StartEngineAsync(store.Load().Settings).ConfigureAwait(false);
        RefreshStatus();
        if (failure is not null)
        {
            throw new InvalidOperationException("복원하지 못했습니다. 기존 데이터베이스는 그대로입니다: " + failure.Message, failure);
        }

        return aside;
    }

    public void NotifySuspend() => Engine?.NotifySuspend();

    public void NotifyResume() => Engine?.NotifyResume();

    protected override (bool Degraded, string Summary, string? Detail) Evaluate()
    {
        if (Engine is not { } engine)
        {
            return (true, "준비 중", null);
        }

        var active = engine.Sessions.OpenSessions().Count(s => s.State == GpuProfit.Core.Domain.SessionState.Running);
        var summary = active > 0 ? $"세션 {active}개 기록 중" : "진행 중인 세션 없음";
        if (engine.Recorder is { StoreErrors: > 0 } recorder)
        {
            return (true, $"{summary} · 센서 기록 저장 오류 {recorder.StoreErrors}회", null);
        }

        return (false, summary, null);
    }

    private async Task StartEngineAsync(AppSettings settings)
    {
        await context.Intervals.RequestAsync(Id, TimeSpan.FromSeconds(settings.Sampling.IntervalSeconds)).ConfigureAwait(false);
        var engine = await ProfitEngine.StartAsync(new EngineOptions
        {
            Paths = Paths!,
            Settings = settings,
            UseMockSensors = context.IsMock,
            Log = LogSink ?? (IAppLogger)NullLogger.Instance,
            Time = context.Time,
            Monitor = context.Hub.CreateConsumer(),
        }).ConfigureAwait(false);
        if (context.DemoData && !DemoDataSeeder.HasDemoData(engine.Store))
        {
            await Task.Run(() => DemoDataSeeder.Seed(engine.Store, engine.Sessions, context.Time.GetUtcNow())).ConfigureAwait(false);
        }

        Engine = engine;
        LogSink?.Info("app", $"PC Control Center 모듈로 시작 {GpuProfit.Core.AppInfo.Version}, 공통 센서 사용");
        EngineReady?.Invoke();
    }

    private async Task StopEngineAsync(string reason)
    {
        var engine = Engine;
        Engine = null;
        if (engine is not null)
        {
            await engine.StopAsync(reason).ConfigureAwait(false);
        }
    }

    private static void TrySave(SettingsStore store, AppSettings settings)
    {
        try
        {
            store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The defaults still apply for this run.
        }
    }
}
