using LocalAiPerf.Core.Diagnostics;
using PcControlCenter.Core;
using WinDiag.Collection;
using WinDiag.Core;
using WinDiag.Storage;
using AppSettings = WinDiag.Core.Settings.AppSettings;
using SettingsStore = WinDiag.Core.Settings.SettingsStore;

namespace PcControlCenter.Modules;

/// <summary>
/// Project C in the shell: C's DiagnosticsEngine (Windows event log, WHEA, snapshots, dumps, wdl.db) with its sensor
/// recorder on the shared hardware reader. Event collection is C's own source; a denied or missing event log degrades
/// only this module.
/// </summary>
public sealed class DiagnosticsModule(ModuleContext context) : ShellModuleBase(ModuleIds.Diagnostics, context.Log, context.Time)
{
    private DataFolderLock? _lock;
    private FileAppLogger? _fileLog;

    public AppPaths? Paths { get; private set; }
    public SettingsStore? SettingsStore { get; private set; }
    public DiagnosticsEngine? Engine { get; private set; }
    public MemoryLogSink? LogSink { get; private set; }
    public IReadOnlyList<string> SettingsNotes { get; private set; } = [];

    public event Action? EngineReady;

    protected override bool Enabled => context.Settings.IsEnabled(Id);

    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        var paths = AppPaths.Resolve(context.Paths.ModuleDirectory(Id), context.ExecutableDirectory);
        paths.EnsureDirectories();
        _lock = DataFolderLock.TryAcquire(paths.DataDirectory)
                ?? throw new InvalidOperationException("이 모듈의 데이터 폴더를 다른 프로세스(wdl 명령 등)가 사용 중입니다.");
        _fileLog = new FileAppLogger(paths.LogDirectory, filePrefix: WinDiag.Core.AppInfo.ShortName);
        LogSink = new MemoryLogSink(500, _fileLog);
        var store = new SettingsStore(paths.SettingsPath);
        var loaded = store.Load();
        loaded.Settings.Normalize();
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
        await StopEngineAsync().ConfigureAwait(false);
        await context.Intervals.ReleaseAsync(Id).ConfigureAwait(false);
        _lock?.Dispose();
        _lock = null;
        _fileLog?.Dispose();
        _fileLog = null;
    }

    /// <summary>C's restore inside the shell (C's engine only; the other modules keep running).</summary>
    public async Task<string?> RestoreAsync(string backupPath)
    {
        if (Paths is not { } paths || SettingsStore is not { } store)
        {
            throw new InvalidOperationException("모듈이 실행 중이 아닙니다.");
        }

        SetStatus(ModuleState.Starting, "복원 중");
        await StopEngineAsync().ConfigureAwait(false);
        string? aside = null;
        Exception? failure = null;
        try
        {
            aside = await Task.Run(() => DiagnosticsStore.RestoreFrom(backupPath, paths.DatabasePath, context.Time)).ConfigureAwait(false);
            LogSink?.Info("app", $"복원 완료, 이전 데이터베이스 보관: {(aside is null ? "없음" : Path.GetFileName(aside))}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogSink?.Error("app", "복원 실패 (기존 데이터베이스를 되돌렸습니다)", ex);
            failure = ex;
        }

        var settings = store.Load().Settings;
        settings.Normalize();
        await StartEngineAsync(settings).ConfigureAwait(false);
        RefreshStatus();
        if (failure is not null)
        {
            throw new InvalidOperationException("복원하지 못했습니다. 기존 데이터베이스는 그대로입니다: " + failure.Message, failure);
        }

        return aside;
    }

    protected override (bool Degraded, string Summary, string? Detail) Evaluate()
    {
        if (Engine is not { } engine)
        {
            return (true, "준비 중", null);
        }

        if (engine.EventCollectionUnavailableReason is { } unavailable)
        {
            return (true, "이벤트 로그를 읽을 수 없음", unavailable);
        }

        if (engine.LastPollError is { } error)
        {
            return (true, "이벤트 로그 수집 오류 (센서 기록은 계속)", SecretRedactor.Redact(error));
        }

        return (false, engine.LastPoll is { } at ? $"이벤트 수집 정상 · 마지막 {at.ToLocalTime():HH:mm:ss}" : "이벤트 수집 대기", null);
    }

    private async Task StartEngineAsync(AppSettings settings)
    {
        await context.Intervals.RequestAsync(Id, TimeSpan.FromSeconds(settings.Sensors.IntervalSeconds)).ConfigureAwait(false);
        var engine = await DiagnosticsEngine.StartAsync(new EngineOptions
        {
            Paths = Paths!,
            Settings = settings,
            UseMockSensors = context.IsMock,
            Log = LogSink,
            Time = context.Time,
            Monitor = context.Hub.CreateConsumer(),
        }).ConfigureAwait(false);
        if (context.DemoData && engine.Store.GetStats().VirtualEvents == 0)
        {
            await engine.SeedVirtualScenarioAsync().ConfigureAwait(false);
        }

        engine.StatusChanged += RefreshStatus;
        Engine = engine;
        LogSink?.Info("app", $"PC Control Center 모듈로 시작 {WinDiag.Core.AppInfo.Version}, 공통 센서 사용");
        EngineReady?.Invoke();
    }

    private async Task StopEngineAsync()
    {
        var engine = Engine;
        Engine = null;
        if (engine is not null)
        {
            engine.StatusChanged -= RefreshStatus;
            await engine.StopAsync().ConfigureAwait(false);
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
