using LocalAiPerf.Core.Diagnostics;
using PcControlCenter.Core;
using PcControlCenter.Hardware;

namespace PcControlCenter.Modules;

public sealed record ShellOptions
{
    public string? DataDirectory { get; init; }
    public string ExecutableDirectory { get; init; } = AppContext.BaseDirectory;
    public bool MockSensors { get; init; }
    public bool DemoData { get; init; }
    public bool Unattended { get; init; }
    public IReadOnlyList<LegacyLocation>? LegacyLocations { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The Operator (E) module; null leaves the Operator out of this run (it then shows as not available).</summary>
    public OperatorModuleOptions? Operator { get; init; }
}

/// <summary>
/// Everything the integrated app runs: the data root, the one sensor reader and the modules. The WPF shell and the
/// tests both use it. Modules start independently; the sensor reader runs even when every module failed.
/// </summary>
public sealed class ShellRuntime : IAsyncDisposable
{
    private DataRootLock? _lock;

    private ShellRuntime(ShellPaths paths, ShellSettingsStore store, LoadedShellSettings loaded, DataRootLock rootLock, FileAppLogger fileLog,
        MemoryLogSink log, SharedHardwareHub hub, ShellOptions options)
    {
        Paths = paths;
        SettingsStore = store;
        Settings = loaded.Settings;
        SettingsNotes = loaded.Notes;
        _lock = rootLock;
        FileLog = fileLog;
        Log = log;
        Hub = hub;
        Options = options;
        Intervals = new SensorIntervalCoordinator(hub, TimeSpan.FromMilliseconds(Settings.Hardware.MaxIntervalMs));
        Supervisor = new ModuleSupervisor(log);
        var context = new ModuleContext
        {
            Paths = paths,
            Settings = Settings,
            Hub = hub,
            Intervals = Intervals,
            Log = log,
            Time = options.Time,
            ExecutableDirectory = options.ExecutableDirectory,
            DemoData = options.DemoData,
            Unattended = options.Unattended,
        };
        Context = context;
        LocalAi = Supervisor.Add(new LocalAiModule(context));
        Economics = Supervisor.Add(new GpuEconomicsModule(context));
        Diagnostics = Supervisor.Add(new DiagnosticsModule(context));
        if (options.Operator is { } operatorOptions)
        {
            Operator = Supervisor.Add(new OperatorModule(context, operatorOptions));
        }
    }

    public ShellPaths Paths { get; }
    public ShellSettingsStore SettingsStore { get; }
    public ShellSettings Settings { get; }
    public IReadOnlyList<string> SettingsNotes { get; }
    public FileAppLogger FileLog { get; }
    public MemoryLogSink Log { get; }
    public SharedHardwareHub Hub { get; }
    public SensorIntervalCoordinator Intervals { get; }
    public ModuleSupervisor Supervisor { get; }
    public ModuleContext Context { get; }
    public ShellOptions Options { get; }
    public LocalAiModule LocalAi { get; }
    public GpuEconomicsModule Economics { get; }
    public DiagnosticsModule Diagnostics { get; }
    public OperatorModule? Operator { get; }

    /// <summary>
    /// Prepares the data root and the sensor reader. Throws <see cref="ShellStartException"/> for problems the user must
    /// fix (data folder overlaps an original program, another instance runs); module problems never throw here.
    /// </summary>
    public static ShellRuntime Create(ShellOptions options)
    {
        ShellPaths paths;
        try
        {
            paths = ShellPaths.Resolve(options.DataDirectory, options.ExecutableDirectory, options.LegacyLocations);
            paths.EnsureDirectories();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ShellStartException(ShellStartProblem.DataFolder, "데이터 폴더를 준비할 수 없습니다: " + ex.Message, ex);
        }

        var rootLock = DataRootLock.TryAcquire(paths.Root)
                       ?? throw new ShellStartException(ShellStartProblem.AlreadyRunning, "같은 데이터 폴더를 쓰는 PC Control Center가 이미 실행 중입니다.");
        var fileLog = new FileAppLogger(paths.LogDirectory, filePrefix: AppInfo.ShortName);
        var log = new MemoryLogSink(500, fileLog);
        var store = new ShellSettingsStore(paths.SettingsPath);
        var loaded = store.Load();
        if (loaded.WasCreated && !options.Unattended)
        {
            try
            {
                store.Save(loaded.Settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn("settings", "기본 설정 저장 실패", ex);
            }
        }

        var hardware = loaded.Settings.Hardware;
        var hub = SharedHardwareHub.Create(hardware.GpuEnabled, hardware.SystemEnabled, options.MockSensors,
            TimeSpan.FromMilliseconds(hardware.MaxIntervalMs), options.Time, log);
        log.Info("app", $"시작 {AppInfo.Version}, 데이터 폴더 {(paths.IsPortable ? "(포터블)" : paths.Root)}, 관리자 권한 {Environment.IsPrivilegedProcess}, "
                        + $"센서 {(hub.IsMock ? "MOCK" : hub.GpuProviderName)}");
        return new ShellRuntime(paths, store, loaded, rootLock, fileLog, log, hub, options);
    }

    /// <summary>Starts the sensor reader, then all modules in parallel.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Hub.Start();
        await Supervisor.StartAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var module in Supervisor.Modules)
        {
            Log.Info("app", $"모듈 {module.Id}: {ModuleStateText.ToKorean(module.Status.State)} — {module.Status.Summary}");
        }
    }

    public async Task StopAsync()
    {
        await Supervisor.StopAllAsync().ConfigureAwait(false);
        await Hub.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await Hub.DisposeAsync().ConfigureAwait(false);
        Log.Info("app", "종료");
        FileLog.Dispose();
        _lock?.Dispose();
        _lock = null;
    }
}

public enum ShellStartProblem
{
    DataFolder,
    AlreadyRunning,
}

public sealed class ShellStartException(ShellStartProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public ShellStartProblem Problem { get; } = problem;
}
