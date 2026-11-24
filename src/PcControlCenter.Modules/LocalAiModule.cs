using LocalAiPerf.Core;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Recording;
using PcControlCenter.Core;
using SettingsStore = LocalAiPerf.Core.Settings.SettingsStore;

namespace PcControlCenter.Modules;

/// <summary>
/// Project A in the shell: A's MonitoringService (Strata telemetry, session recorder, lapc.db) on the shared hardware
/// reader. Strata being off or unreachable is reported by A's own runtime state, not as a module failure.
/// </summary>
public sealed class LocalAiModule(ModuleContext context) : ShellModuleBase(ModuleIds.LocalAi, context.Log, context.Time)
{
    private FileAppLogger? _fileLog;

    public AppPaths? Paths { get; private set; }
    public SettingsStore? SettingsStore { get; private set; }
    public MonitoringService? Service { get; private set; }

    /// <summary>A's in-memory log (A's settings page exports it as diagnostics).</summary>
    public MemoryLogSink? LogSink { get; private set; }

    public IReadOnlyList<string> SettingsNotes { get; private set; } = [];

    /// <summary>Raised after the service was (re)started, so the UI rebuilds A's view models.</summary>
    public event Action? ServiceReady;

    protected override bool Enabled => context.Settings.IsEnabled(Id);

    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        var paths = AppPaths.Resolve(context.Paths.ModuleDirectory(Id), context.ExecutableDirectory);
        paths.EnsureDirectories();
        _fileLog = new FileAppLogger(paths.LogDirectory);
        LogSink = new MemoryLogSink(500, _fileLog);
        var store = new SettingsStore(paths.SettingsPath);
        var loaded = store.Load();
        var settings = loaded.Settings;
        settings.Normalize();
        if (loaded.WasCreated && !context.Unattended)
        {
            TrySave(store, settings);
        }

        var service = new MonitoringService(paths, settings, new MonitoringOptions
        {
            UseMockSensors = context.IsMock,
            HardwareFactory = context.Hub.CreateConsumer,
            AutoRecord = context.Unattended ? false : null,
        }, LogSink, context.Time);
        await context.Intervals.RequestAsync(Id, TimeSpan.FromMilliseconds(settings.Sampling.IntervalMs)).ConfigureAwait(false);
        await service.StartAsync().ConfigureAwait(false);
        service.StateChanged += OnServiceStateChanged;
        Paths = paths;
        SettingsStore = store;
        SettingsNotes = loaded.Notes;
        Service = service;
        LogSink.Info("app", $"PC Control Center 모듈로 시작 {LocalAiPerf.Core.AppInfo.Version}, 공통 센서 사용");
        ServiceReady?.Invoke();
    }

    protected override async Task OnStopAsync()
    {
        if (Service is { } service)
        {
            service.StateChanged -= OnServiceStateChanged;
            await service.DisposeAsync().ConfigureAwait(false);
        }

        Service = null;
        await context.Intervals.ReleaseAsync(Id).ConfigureAwait(false);
        _fileLog?.Dispose();
        _fileLog = null;
    }

    protected override (bool Degraded, string Summary, string? Detail) Evaluate()
    {
        if (Service is not { } service)
        {
            return (true, "준비 중", null);
        }

        if (service.StorageProblem is { } problem)
        {
            return (true, "기록 불가 (데이터베이스 문제, 실시간 표시는 계속)", problem);
        }

        var runtime = service.Runtime is null ? "Strata 연결 꺼짐"
            : service.LatestRuntime is { } rt ? $"Strata {RuntimeStateText.ToKorean(rt.State)}" : "Strata 연결 중";
        var recording = service.Recorder?.ActiveSession is { } s ? $"기록 중: {s.Name}" : "기록 안 함";
        return (false, $"{recording} · {runtime}", null);
    }

    private void OnServiceStateChanged()
    {
        RefreshStatus();
        if (Service is { } service)
        {
            _ = context.Intervals.RequestAsync(Id, service.Interval);
        }
    }

    private static void TrySave(SettingsStore store, LocalAiPerf.Core.Settings.AppSettings settings)
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
