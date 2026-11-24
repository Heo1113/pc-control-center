using System.Globalization;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;
using SamplingSettings = LocalAiPerf.Core.Settings.SamplingSettings; // PC Control Center: A's real type (the shim was removed)
using LocalAiPerf.Telemetry;
using WinDiag.Collection.Crash;
using WinDiag.Collection.Notifications;
using WinDiag.Collection.Sensors;
using WinDiag.Collection.Snapshots;
using WinDiag.Collection.Sources;
using WinDiag.Core;
using WinDiag.Core.Crash;
using WinDiag.Core.Events;
using WinDiag.Core.Privacy;
using WinDiag.Core.Settings;
using WinDiag.Core.Snapshots;
using WinDiag.Storage;

namespace WinDiag.Collection;

public sealed record EngineOptions
{
    public required AppPaths Paths { get; init; }
    public required AppSettings Settings { get; init; }

    /// <summary>Synthetic sensors from project A's mock providers (tests, demos, machines without sensors). Flagged in the UI.</summary>
    public bool UseMockSensors { get; init; }

    /// <summary>Null: the Windows event log on Windows, nothing elsewhere.</summary>
    public IEventLogSource? EventSource { get; init; }

    public ISnapshotProvider? SnapshotProvider { get; init; }

    /// <summary>Run the polling, snapshot and retention loops (off in tests that drive the engine step by step).</summary>
    public bool StartBackgroundLoops { get; init; } = true;

    public bool ScanDumps { get; init; } = true;

    /// <summary>
    /// Record sensor samples while the engine runs. Off for command-line work that only reads the database (status,
    /// report, export...), which must not add samples or a "sensor unavailable" gap each time it is used.
    /// </summary>
    public bool RecordSensors { get; init; } = true;

    public PrivacyMasker? Masker { get; init; }

    /// <summary>
    /// PC Control Center: the hardware monitor to use (for example a view over the shared sensor reader, so that this
    /// engine does not open NVML a second time). Null = HardwareMonitor.Create as in project C.
    /// </summary>
    public HardwareMonitor? Monitor { get; init; }
    public TimeProvider? Time { get; init; }
    public IAppLogger? Log { get; init; }
}

/// <summary>
/// Runs collection for the app and the CLI: the event collector, project A's sensors, configuration snapshots, dump
/// metadata, notifications and retention, all writing to this app's own database. Nothing here changes system settings.
/// </summary>
public sealed class DiagnosticsEngine : IAsyncDisposable
{
    private readonly EngineOptions _options;
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly NotificationService _notifications;
    private volatile AppSettings _settings;
    private SensorRecorder? _recorder;
    private bool _stopped;

    private DiagnosticsEngine(EngineOptions options, DiagnosticsStore store, HardwareMonitor hardware, long runId,
        IReadOnlyList<AppRun> recoveredRuns, TimeProvider time, IAppLogger log)
    {
        _options = options;
        _settings = options.Settings;
        Store = store;
        Hardware = hardware;
        RunId = runId;
        RecoveredRuns = recoveredRuns;
        _time = time;
        _log = log;
        _notifications = new NotificationService(() => _settings.Notifications, time);
        var source = options.EventSource ?? (OperatingSystem.IsWindows() ? new WindowsEventLogSource() : null);
        if (source is not null)
        {
            Collector = new EventCollector(source, store, EventClassifier.Default, options.Masker ?? PrivacyMasker.ForCurrentMachine(), () => _settings, time, log);
            Collector.EventsStored += OnEventsStored;
        }

        SnapshotProvider = options.SnapshotProvider
                           ?? (OperatingSystem.IsWindows() && !options.UseMockSensors
                               ? new WindowsSnapshotProvider(() => Hardware.Info)
                               : new MockSnapshotProvider(() => Hardware.Info));
    }

    public DiagnosticsStore Store { get; }
    public HardwareMonitor Hardware { get; }
    public long RunId { get; }
    public IReadOnlyList<AppRun> RecoveredRuns { get; }
    public EventCollector? Collector { get; }
    public ISnapshotProvider SnapshotProvider { get; }
    public SensorRecorder? Recorder => _recorder;
    public AppSettings Settings => _settings;
    public bool IsMock => _options.UseMockSensors;
    public AppPaths Paths => _options.Paths;

    /// <summary>Why events cannot be collected on this machine, or null.</summary>
    public string? EventCollectionUnavailableReason => Collector is null ? "Windows가 아니어서 Windows 이벤트 로그를 읽을 수 없습니다." : null;

    public CollectionReport? LastReport { get; private set; }
    public DateTimeOffset? LastPoll { get; private set; }
    public string? LastPollError { get; private set; }
    public DumpScanResult? LastDumpScan { get; private set; }
    public bool IsCollecting { get; private set; }

    public event Action<StoredBatch>? EventsStored;
    public event Action<DiagNotification>? Notified;
    public event Action<HardwareSnapshot>? SensorSampled;
    public event Action? StatusChanged;

    public static Task<DiagnosticsEngine> StartAsync(EngineOptions options)
    {
        var time = options.Time ?? TimeProvider.System;
        var log = options.Log ?? NullLogger.Instance;
        options.Paths.EnsureDirectories();
        var store = DiagnosticsStore.Open(options.Paths.DatabasePath, time, log);
        try
        {
            var recovered = store.RecoverUnfinishedRuns();
            if (EventReclassifier.RunIfOutdated(store, EventClassifier.Default, options.Masker ?? PrivacyMasker.ForCurrentMachine()) is { } reclassified)
            {
                log.Info("engine", $"분류 규칙 버전 {EventCatalog.Version}: 저장된 이벤트 {reclassified.Checked}건 중 {reclassified.Changed}건을 새 규칙으로 다시 분류");
            }

            var settings = options.Settings;
            var hardware = options.Monitor ?? HardwareMonitor.Create(
                new SamplingSettings { GpuEnabled = settings.Sensors.GpuEnabled, SystemEnabled = settings.Sensors.SystemEnabled },
                options.UseMockSensors, time, log);
            var runId = store.StartRun(time.GetUtcNow(), AppInfo.Version, options.UseMockSensors, settings.Sensors.IntervalSeconds * 1000,
                Environment.IsPrivilegedProcess);
            var engine = new DiagnosticsEngine(options, store, hardware, runId, recovered, time, log);
            engine.StartSensors();
            if (options.StartBackgroundLoops)
            {
                engine.StartLoops();
            }

            return Task.FromResult(engine);
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    /// <summary>Applies new settings. Sensor changes restart the sensor loop; other values apply at the next cycle.</summary>
    public async Task ApplySettingsAsync(AppSettings settings)
    {
        settings.Normalize();
        var old = _settings;
        _settings = settings;
        if (old.Sensors.Enabled != settings.Sensors.Enabled || old.Sensors.IntervalSeconds != settings.Sensors.IntervalSeconds)
        {
            if (_recorder is not null)
            {
                await _recorder.StopAsync().ConfigureAwait(false);
                _recorder.Sampled -= OnSampled;
                _recorder = null;
            }

            StartSensors();
        }

        StatusChanged?.Invoke();
    }

    public async Task<CollectionReport?> PollNowAsync(CancellationToken cancellationToken = default)
    {
        if (Collector is null)
        {
            return null;
        }

        IsCollecting = true;
        StatusChanged?.Invoke();
        try
        {
            var report = await Collector.PollAsync(cancellationToken).ConfigureAwait(false);
            LastReport = report;
            LastPoll = _time.GetUtcNow();
            LastPollError = null;
            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastPollError = ex.Message;
            _log.Error("engine", "이벤트 수집 실패", ex);
            return null;
        }
        finally
        {
            IsCollecting = false;
            StatusChanged?.Invoke();
        }
    }

    public async Task<CollectionReport?> ImportHistoryAsync(DateTimeOffset from, DateTimeOffset to, IProgress<CollectionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Collector is null)
        {
            return null;
        }

        IsCollecting = true;
        StatusChanged?.Invoke();
        try
        {
            var report = await Collector.ImportHistoryAsync(from, to, progress, cancellationToken).ConfigureAwait(false);
            LastReport = report;
            return report;
        }
        finally
        {
            IsCollecting = false;
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Reads the configuration now and stores it when it changed. Returns the save result.</summary>
    public async Task<SnapshotSaveResult> TakeSnapshotAsync(string source, CancellationToken cancellationToken = default)
    {
        await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var includeDrivers = _settings.Snapshots.IncludeDrivers;
            var snapshot = await Task.Run(() => SnapshotProvider.Collect(includeDrivers, cancellationToken), cancellationToken).ConfigureAwait(false);
            snapshot = snapshot with { CollectedAt = _time.GetUtcNow() };
            var result = Store.SaveSnapshot(snapshot, source);
            StatusChanged?.Invoke();
            return result;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    public Task<DumpScanResult> ScanDumpsAsync(IReadOnlyList<string>? locations = null)
        => Task.Run(() =>
        {
            var result = new DumpScanner(_time).Scan(locations);
            Store.SaveDumpFiles(result.Files);
            LastDumpScan = result;
            StatusChanged?.Invoke();
            return result;
        });

    public long Acknowledge()
    {
        var id = Store.Acknowledge();
        StatusChanged?.Invoke();
        return id;
    }

    /// <summary>
    /// Imports Windows event XML files (for example "wevtutil qe System /f:xml" output from another PC). The events are
    /// stored with source "import"; the positions of this PC's live logs are not changed.
    /// </summary>
    public async Task<CollectionReport> ImportXmlFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var source = MemoryEventSource.FromXmlFiles(paths);
        var settings = _settings.Clone();
        var channels = paths.Count == 0 ? [] : source.Channels;
        settings.Collection.Channels = channels.Select(c => new ChannelSetting { Name = c, Enabled = true }).ToList();
        var collector = new EventCollector(source, Store, EventClassifier.Default, _options.Masker ?? PrivacyMasker.ForCurrentMachine(), () => settings, _time, _log,
            trackProgress: false, sourceOverride: EventSources.Import);
        var report = await collector.ImportHistoryAsync(DateTimeOffset.MinValue.AddYears(1), _time.GetUtcNow().AddDays(1), null, cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke();
        return report;
    }

    /// <summary>Loads the virtual demo scenario into this database (events flagged virtual; sensors in a mock run).</summary>
    public async Task<CollectionReport> SeedVirtualScenarioAsync()
    {
        var now = _time.GetUtcNow();
        var source = VirtualScenario.BuildSource(now);
        var collector = new EventCollector(source, Store, EventClassifier.Default, PrivacyMasker.Generic, () => _settings, _time, _log, trackProgress: false);
        var report = await collector.ImportHistoryAsync(now.AddDays(-31), now).ConfigureAwait(false);
        VirtualScenario.SeedSideData(Store, now);
        StatusChanged?.Invoke();
        return report;
    }

    public async Task StopAsync(string reason = RunEndReasons.Normal)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var loop in _loops)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Error("engine", "종료 중 작업 오류", ex);
            }
        }

        if (_recorder is not null)
        {
            await _recorder.StopAsync().ConfigureAwait(false);
        }

        try
        {
            Store.EndRun(RunId, _time.GetUtcNow(), reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("engine", "실행 종료 기록 실패 (다음 실행에서 복구됨)", ex);
        }

        Hardware.Dispose();
        Store.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private void StartSensors()
    {
        if (!_options.RecordSensors || !_settings.Sensors.Enabled)
        {
            return;
        }

        _recorder = new SensorRecorder(Hardware, Store, RunId, TimeSpan.FromSeconds(_settings.Sensors.IntervalSeconds), _time, _log);
        _recorder.Sampled += OnSampled;
        _recorder.Start();
    }

    private void OnSampled(HardwareSnapshot snapshot) => SensorSampled?.Invoke(snapshot);

    private void StartLoops()
    {
        var token = _stop.Token;
        _loops.Add(Task.Run(() => EventLoopAsync(token), CancellationToken.None));
        _loops.Add(Task.Run(() => SnapshotLoopAsync(token), CancellationToken.None));
        _loops.Add(Task.Run(() => MaintenanceLoopAsync(token), CancellationToken.None));
    }

    private async Task EventLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollNowAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.Collection.PollSeconds), _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SnapshotLoopAsync(CancellationToken ct)
    {
        if (_options.ScanDumps)
        {
            try
            {
                await ScanDumpsAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("engine", "덤프 확인 실패", ex);
            }
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await TakeSnapshotAsync("auto", ct).ConfigureAwait(false);
                _log.Info("engine", result.IsNew ? "시스템 구성 스냅샷 저장 (변경 감지 또는 첫 스냅샷)" : "시스템 구성 변경 없음 (확인 시각만 갱신)");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("engine", "시스템 구성 스냅샷 실패", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(_settings.Snapshots.IntervalHours), _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var last = Store.GetMetaLong(DiagnosticsStore.MetaLastRetention);
                if (_time.GetUtcNow().ToUnixTimeMilliseconds() - last > TimeSpan.FromHours(24).TotalMilliseconds)
                {
                    var result = Store.ApplyRetention(_settings.Retention.EventDays, _settings.Retention.SensorDays);
                    foreach (var note in result.Notes)
                    {
                        _log.Info("retention", note);
                    }
                }

                if (_recorder is null)
                {
                    Store.Heartbeat(RunId, _time.GetUtcNow());
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("engine", "유지 관리 작업 실패", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnEventsStored(StoredBatch batch)
    {
        try
        {
            EventsStored?.Invoke(batch);
            if (!batch.IsLive)
            {
                return;
            }

            var events = Store.GetEvents(batch.EventIds);
            foreach (var notification in _notifications.Evaluate(events))
            {
                Notified?.Invoke(notification);
            }

            if (_options.ScanDumps && events.Any(e => e.Category is EventCategories.BugCheck or EventCategories.CrashDump))
            {
                _ = ScanDumpsAsync().ContinueWith(t => _log.Warn("engine", "덤프 확인 실패", t.Exception),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("engine", "새 이벤트 알림 처리 실패", ex);
        }
    }

    /// <summary>One-line status for logs and the CLI.</summary>
    public string Describe()
    {
        var stats = Store.GetStats();
        return string.Create(CultureInfo.InvariantCulture,
            $"이벤트 {stats.Events}건 (가상 {stats.VirtualEvents}), 센서 샘플 {stats.SensorSamples}, 스냅샷 {stats.Snapshots}, 누락 구간 {stats.Gaps}");
    }
}
