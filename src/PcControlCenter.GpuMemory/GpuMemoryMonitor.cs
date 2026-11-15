using System.Diagnostics;
using LocalAiPerf.Core.Diagnostics;

namespace PcControlCenter.GpuMemory;

public enum GpuMemoryMonitorState
{
    NotStarted,

    /// <summary>Reading on its own thread.</summary>
    Running,

    /// <summary>The provider cannot read on this PC (reason in <see cref="GpuMemoryHealth.UnavailableReason"/>); the app works without it.</summary>
    Unavailable,

    /// <summary>Switched off in the shell settings.</summary>
    Disabled,

    Stopped,
}

/// <summary>State of the GPU memory reader for its page and the smoke report.</summary>
public sealed record GpuMemoryHealth
{
    public required GpuMemoryMonitorState State { get; init; }
    public required string ProviderName { get; init; }
    public required bool IsMock { get; init; }
    public string? UnavailableReason { get; init; }

    /// <summary>The interval in effect now (shorter while the page is visible).</summary>
    public required TimeSpan Interval { get; init; }

    public bool IsActive { get; init; }
    public DateTimeOffset? LastReadingAt { get; init; }
    public long Readings { get; init; }

    /// <summary>Readings that threw (the provider broke its contract); the reader backs off and starts the provider again.</summary>
    public long Failures { get; init; }

    public string? LastError { get; init; }
    public TimeSpan? LastCollectionTime { get; init; }
}

/// <summary>One point of the in-memory history (no database: see docs/GPU_MEMORY.md, "기록").</summary>
public sealed record GpuMemoryHistoryPoint(
    DateTimeOffset Timestamp,
    IReadOnlyList<GpuAdapterHistoryValue> Adapters,
    IReadOnlyList<GpuProcessHistoryValue> Processes);

/// <summary><paramref name="Busiest"/>: the busiest engine in percent; null where engines were not read (the page was not open).</summary>
public readonly record struct GpuAdapterHistoryValue(long Luid, long? DedicatedUsageBytes, long? SharedUsageBytes, double? Busiest = null);

/// <summary><paramref name="Busiest"/>: the process's busiest engine in percent; null where engines were not read.</summary>
public readonly record struct GpuProcessHistoryValue(ProcessKey Key, string Name, long Luid, long? DedicatedBytes, long? SharedBytes, double? Busiest = null);

/// <summary>Thresholds shared by the page, the history and the tests.</summary>
public static class GpuMemoryRules
{
    public const long MiB = 1L << 20;

    /// <summary>A row worth listing: at least 1 MiB of local memory, or 16 MiB in system RAM or committed (as gputaskman lists rows).</summary>
    public static bool IsListed(GpuProcessMemory p)
        => p.DedicatedBytes >= MiB || p.SharedBytes >= 16 * MiB || p.CommittedBytes >= 16 * MiB;

    /// <summary>Most processes kept per history point (largest local memory first).</summary>
    public const int HistoryProcessesPerPoint = 64;
}

/// <summary>
/// The GPU memory reader. It owns one <see cref="IGpuMemoryProvider"/> and reads it on a thread of its own, separate from
/// the shared hardware hub (which keeps NVML to itself) and from the UI thread. A failure here only empties the GPU
/// memory page: the hub, the modules and the dashboard do not depend on it.
/// </summary>
/// <remarks>
/// Two intervals keep the cost low: <see cref="ActiveInterval"/> while the GPU memory page is visible (<see cref="SetActive"/>),
/// <see cref="BackgroundInterval"/> otherwise, so the history chart has something to show when the page opens.
/// Readings stay in memory only (a bounded window); nothing is written to the modules' databases.
/// </remarks>
public sealed class GpuMemoryMonitor : IAsyncDisposable
{
    public static readonly TimeSpan DefaultActiveInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultBackgroundInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultHistoryWindow = TimeSpan.FromMinutes(30);
    private const int MaxHistoryPoints = 2000;
    private const int FailuresBeforeRestart = 3;
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnavailableRetry = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<IGpuMemoryProvider>? _factory;
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private readonly TimeSpan _historyWindow;
    private readonly Lock _gate = new();
    private readonly LinkedList<GpuMemoryHistoryPoint> _history = new();
    private readonly AutoResetEvent _wake = new(false);
    private IGpuMemoryProvider? _provider;
    private GpuMemorySnapshot? _latest;
    private GpuMemoryMonitorState _state;
    private string _providerName = "-";
    private bool _isMock;
    private string? _unavailableReason;
    private bool _active;
    private GpuMemoryReadRequest _request = GpuMemoryReadRequest.None;
    private long _readings;
    private long _failures;
    private int _consecutiveFailures;
    private string? _lastError;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _stuck;

    /// <param name="providerFactory">Creates the provider on the reader thread; null = switched off (<paramref name="disabledReason"/>).</param>
    public GpuMemoryMonitor(Func<IGpuMemoryProvider>? providerFactory, TimeProvider? time = null, IAppLogger? log = null,
        TimeSpan? activeInterval = null, TimeSpan? backgroundInterval = null, TimeSpan? historyWindow = null,
        string disabledReason = "설정에서 꺼져 있습니다.")
    {
        _factory = providerFactory;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        ActiveInterval = activeInterval ?? DefaultActiveInterval;
        BackgroundInterval = backgroundInterval ?? DefaultBackgroundInterval;
        _historyWindow = historyWindow ?? DefaultHistoryWindow;
        _state = providerFactory is null ? GpuMemoryMonitorState.Disabled : GpuMemoryMonitorState.NotStarted;
        if (providerFactory is null)
        {
            _unavailableReason = disabledReason;
        }
    }

    /// <summary>
    /// The reader of the app: Windows counters on Windows, the MOCK provider with --mock-sensors, nothing elsewhere or when
    /// switched off (the page then says why).
    /// </summary>
    public static GpuMemoryMonitor Create(bool enabled, bool useMock, TimeProvider? time = null, IAppLogger? log = null)
    {
        if (!enabled)
        {
            return new GpuMemoryMonitor(null, time, log, disabledReason: "공통 설정에서 '프로세스별 GPU 메모리 읽기'가 꺼져 있습니다.");
        }

        if (useMock)
        {
            return new GpuMemoryMonitor(() => new Mock.MockGpuMemoryProvider(time), time, log);
        }

        if (OperatingSystem.IsWindows())
        {
            return new GpuMemoryMonitor(WindowsProvider(time, log), time, log);
        }

        return new GpuMemoryMonitor(() => new UnavailableGpuMemoryProvider(
            "프로세스별 GPU 메모리는 Windows 성능 카운터(WDDM)로만 읽습니다. 이 운영체제에서는 N/A입니다.", time), time, log);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Func<IGpuMemoryProvider> WindowsProvider(TimeProvider? time, IAppLogger? log)
        => () => new Windows.WindowsGpuMemoryProvider(time, log);

    public TimeSpan ActiveInterval { get; }
    public TimeSpan BackgroundInterval { get; }

    public TimeSpan Interval
    {
        get
        {
            lock (_gate)
            {
                return _active ? ActiveInterval : BackgroundInterval;
            }
        }
    }

    public GpuMemorySnapshot? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    public GpuMemoryHealth Health
    {
        get
        {
            lock (_gate)
            {
                return new GpuMemoryHealth
                {
                    State = _state,
                    ProviderName = _providerName,
                    IsMock = _isMock,
                    UnavailableReason = _unavailableReason,
                    Interval = _active ? ActiveInterval : BackgroundInterval,
                    IsActive = _active,
                    LastReadingAt = _latest?.Timestamp,
                    Readings = _readings,
                    Failures = _failures,
                    LastError = _lastError,
                    LastCollectionTime = _latest?.CollectionTime,
                };
            }
        }
    }

    /// <summary>Raised on the reader thread after each reading. Handlers must be quick; exceptions are logged.</summary>
    public event Action<GpuMemorySnapshot>? Sampled;

    /// <summary>History points of the last <paramref name="window"/>, oldest first (raw readings, nothing averaged).</summary>
    public IReadOnlyList<GpuMemoryHistoryPoint> History(TimeSpan window)
    {
        lock (_gate)
        {
            if (_history.Last is null)
            {
                return [];
            }

            var cutoff = _history.Last.Value.Timestamp - window;
            return [.. _history.Where(p => p.Timestamp >= cutoff)];
        }
    }

    /// <summary>The GPU memory page became visible (or hidden): read every <see cref="ActiveInterval"/> while it is, and right away.</summary>
    public void SetActive(bool active)
    {
        bool wake;
        lock (_gate)
        {
            wake = active && !_active;
            _active = active;
        }

        if (wake)
        {
            Wake();
        }
    }

    /// <summary>
    /// Local TCP ports whose listening process the next readings identify (the Strata server's port). Empty = none:
    /// no other listener is looked up.
    /// </summary>
    public void SetWatchedPorts(IEnumerable<int> ports)
    {
        var set = ports.Where(p => p is > 0 and <= 65535).ToHashSet();
        lock (_gate)
        {
            if (!set.SetEquals(_request.WatchedPorts))
            {
                _request = new GpuMemoryReadRequest(set);
            }
        }
    }

    public IReadOnlySet<int> WatchedPorts
    {
        get
        {
            lock (_gate)
            {
                return _request.WatchedPorts;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null || _factory is null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _state = GpuMemoryMonitorState.Running;
            var token = _cts.Token;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    Loop(token);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log.Error("gpu-memory", "GPU 메모리 리더 중단", ex);
                    lock (_gate)
                    {
                        _lastError = ex.Message;
                        _state = GpuMemoryMonitorState.Unavailable;
                        _unavailableReason = "GPU 메모리 리더가 예기치 않게 멈췄습니다: " + ex.Message;
                    }
                }
                finally
                {
                    DisposeProvider();
                    finished.TrySetResult();
                }
            })
            {
                IsBackground = true,
                Name = "PcControlCenter GPU memory reader",
            };
            _loop = finished.Task;
            thread.Start();
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
            if (_state is GpuMemoryMonitorState.Running or GpuMemoryMonitorState.NotStarted)
            {
                _state = GpuMemoryMonitorState.Stopped;
            }
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        Wake();
        if (loop is not null)
        {
            try
            {
                // A Windows call that never returns must not hold up the app's shutdown; the thread is a background thread.
                await loop.WaitAsync(StopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _stuck = true;
                _log.Warn("gpu-memory", $"GPU 메모리 리더가 {StopTimeout.TotalSeconds:0}초 안에 끝나지 않았습니다. 리더를 해제하지 않고 둡니다.");
                return;
            }
        }

        cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (!_stuck)
        {
            // Never dispose the provider while a stuck reader thread may still be inside it.
            DisposeProvider();
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Takes one reading on the caller's thread (tests and one-shot tools; never while <see cref="Start"/> runs the loop).
    /// Creates the provider when needed. Never throws.
    /// </summary>
    public GpuMemorySnapshot? ReadNow()
    {
        if (_factory is null)
        {
            return null;
        }

        if (_provider is null && !TryCreateProvider())
        {
            return null;
        }

        return ReadOnce();
    }

    private void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_provider is null && !TryCreateProvider())
            {
                if (WaitOrStop(ct, UnavailableRetry))
                {
                    return;
                }

                continue;
            }

            if (_provider!.UnavailableReason is not null)
            {
                // Nothing to read on this PC (not Windows, or the GPU counters are missing). Try again now and then: a
                // driver installed or a GPU enabled while the app runs can bring the counters. The reason stays visible.
                DisposeProvider(keepState: true);
                if (WaitOrStop(ct, UnavailableRetry))
                {
                    return;
                }

                continue;
            }

            var started = Stopwatch.GetTimestamp();
            var reading = ReadOnce();
            if (reading is null)
            {
                int consecutive;
                lock (_gate)
                {
                    consecutive = _consecutiveFailures;
                }

                if (consecutive >= FailuresBeforeRestart)
                {
                    _log.Warn("gpu-memory", $"GPU 메모리 읽기가 {consecutive}회 연속 실패해 {RestartBackoff.TotalSeconds:0}초 뒤 다시 시작합니다.");
                    DisposeProvider(keepState: true);
                    if (WaitOrStop(ct, RestartBackoff))
                    {
                        return;
                    }

                    continue;
                }
            }

            var wait = Interval - Stopwatch.GetElapsedTime(started);
            if (WaitOrStop(ct, wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(50)))
            {
                return;
            }
        }
    }

    /// <summary>Waits for the interval, an immediate-read request or the stop. True = stop.</summary>
    private bool WaitOrStop(CancellationToken ct, TimeSpan wait)
    {
        if (ct.IsCancellationRequested)
        {
            return true;
        }

        WaitHandle.WaitAny([ct.WaitHandle, _wake], wait);
        return ct.IsCancellationRequested;
    }

    private bool TryCreateProvider()
    {
        try
        {
            var provider = _factory!();
            lock (_gate)
            {
                _provider = provider;
                _providerName = provider.Name;
                _isMock = provider.IsMock;
                _unavailableReason = provider.UnavailableReason;
                if (_cts is not null)
                {
                    _state = provider.UnavailableReason is null ? GpuMemoryMonitorState.Running : GpuMemoryMonitorState.Unavailable;
                }
                else if (provider.UnavailableReason is not null)
                {
                    _state = GpuMemoryMonitorState.Unavailable;
                }
            }

            if (provider.UnavailableReason is { } reason)
            {
                _log.Info("gpu-memory", "프로세스별 GPU 메모리 사용 불가: " + reason);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("gpu-memory", "GPU 메모리 리더를 준비하지 못했습니다", ex);
            lock (_gate)
            {
                _failures++;
                _lastError = ex.Message;
                _state = GpuMemoryMonitorState.Unavailable;
                _unavailableReason = $"GPU 메모리 리더를 준비하지 못했습니다 ({ex.GetType().Name}: {ex.Message}). {UnavailableRetry.TotalSeconds:0}초 뒤 다시 시도합니다.";
            }

            return false;
        }
    }

    private GpuMemorySnapshot? ReadOnce()
    {
        GpuMemorySnapshot reading;
        GpuMemoryReadRequest request;
        lock (_gate)
        {
            // Engine activity and process details only while the page is visible (they cost more than the memory counters).
            request = _request with { IncludeDetails = _active };
        }

        try
        {
            var started = Stopwatch.GetTimestamp();
            reading = _provider!.Read(request);
            if (reading.CollectionTime == TimeSpan.Zero)
            {
                reading = reading with { CollectionTime = Stopwatch.GetElapsedTime(started) };
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Providers never throw by contract; a broken one costs only this page's values.
            _log.Error("gpu-memory", "GPU 메모리 읽기 중 예외", ex);
            lock (_gate)
            {
                _failures++;
                _consecutiveFailures++;
                _lastError = ex.Message;
            }

            return null;
        }

        reading = reading with { ExpectedInterval = Interval };
        var point = ToHistoryPoint(reading);
        lock (_gate)
        {
            _latest = reading;
            _readings++;
            _consecutiveFailures = 0;
            _unavailableReason = _provider?.UnavailableReason;
            _history.AddLast(point);
            var cutoff = point.Timestamp - _historyWindow;
            while (_history.First is { } first && (first.Value.Timestamp < cutoff || _history.Count > MaxHistoryPoints))
            {
                _history.RemoveFirst();
            }
        }

        try
        {
            Sampled?.Invoke(reading);
        }
        catch (Exception ex)
        {
            _log.Error("gpu-memory", "GPU 메모리 처리기 예외", ex);
        }

        return reading;
    }

    private static GpuMemoryHistoryPoint ToHistoryPoint(GpuMemorySnapshot s)
    {
        var adapters = s.Adapters.Select(a => new GpuAdapterHistoryValue(a.Luid, a.DedicatedUsageBytes, a.SharedUsageBytes,
            s.EnginesRead ? a.Engines?.Busiest : null)).ToList();
        var processes = s.Processes
            .Where(GpuMemoryRules.IsListed)
            .OrderByDescending(p => p.DedicatedBytes ?? 0)
            .Take(GpuMemoryRules.HistoryProcessesPerPoint)
            .Select(p =>
            {
                var entry = s.Process(p.ProcessId);
                var key = entry?.Key ?? new ProcessKey(p.ProcessId, 0, $"PID {p.ProcessId}");
                // A process the engine counters did not list was idle on that adapter: 0 %, but only when the engines were read.
                var busiest = s.EnginesRead ? p.Engines?.Busiest ?? 0 : (double?)null;
                return new GpuProcessHistoryValue(key, entry?.Name ?? $"PID {p.ProcessId}", p.Luid, p.DedicatedBytes, p.SharedBytes, busiest);
            })
            .ToList();
        return new GpuMemoryHistoryPoint(s.Timestamp, adapters, processes);
    }

    private void DisposeProvider(bool keepState = false)
    {
        IGpuMemoryProvider? provider;
        lock (_gate)
        {
            provider = _provider;
            _provider = null;
            _consecutiveFailures = 0;
            if (!keepState && _state == GpuMemoryMonitorState.Running)
            {
                _state = GpuMemoryMonitorState.Stopped;
            }
        }

        try
        {
            provider?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("gpu-memory", "GPU 메모리 리더 정리 중 예외", ex);
        }
    }
}
