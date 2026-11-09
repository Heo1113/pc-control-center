using System.Diagnostics;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Settings;
using LocalAiPerf.Telemetry;

namespace PcControlCenter.Hardware;

public enum HubState
{
    NotStarted,
    Running,
    Stopped,
}

/// <summary>State of the shared reader for the status bar, the dashboard and the hardware page.</summary>
public sealed record HubHealth
{
    public required HubState State { get; init; }
    public required TimeSpan Interval { get; init; }
    public required bool IsMock { get; init; }
    public DateTimeOffset? LastReadingAt { get; init; }
    public long Readings { get; init; }

    /// <summary>Readings that took longer than the interval, so the next tick was skipped (a gap, never filled in).</summary>
    public long SkippedTicks { get; init; }

    /// <summary>Unexpected exceptions around a reading (the A monitor itself never throws; this guards the loop).</summary>
    public long LoopErrors { get; init; }

    public string? LastError { get; init; }
    public int Consumers { get; init; }

    /// <summary>Issues reported with the newest reading (sensor unavailable, NVML error ...), as measured.</summary>
    public IReadOnlyList<SensorIssue> LatestIssues { get; init; } = [];
}

/// <summary>
/// The single hardware reader of PC Control Center. It owns project A's <see cref="HardwareMonitor"/> (NVML GPU, Windows
/// CPU/RAM, or the mock providers) and reads it on one loop. Modules get a <see cref="HardwareMonitor"/> view from
/// <see cref="CreateConsumer"/>: their own recorders keep their own interval, storage and gap rules, but every value
/// they store is one of these readings with its original timestamp and issues. No module opens NVML itself.
/// </summary>
/// <remarks>
/// The hub keeps no averaged or downsampled data. <see cref="Live"/> is a short raw buffer for display; modules that
/// account energy (project B) get each raw reading through their own view, not through a display buffer.
/// </remarks>
public sealed class SharedHardwareHub : IHardwareSnapshotSource, IAsyncDisposable
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(SamplingSettings.MinIntervalMs);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMilliseconds(SamplingSettings.MaxIntervalMs);

    private readonly HardwareMonitor _reader;
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    // A plain monitor object (not System.Threading.Lock): consumers wait on it with Monitor.Wait.
    private readonly object _gate = new();
    private readonly LinkedList<HardwareSnapshot> _live = new();
    private readonly TimeSpan _liveWindow;
    private readonly TimeSpan? _consumerWaitOverride;
    private SharedHardwareSample? _latest;
    private long _sequence;
    private long _skippedTicks;
    private long _loopErrors;
    private string? _lastError;
    private int _consumers;
    private TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private HubState _state = HubState.NotStarted;

    /// <param name="reader">A real (not shared) monitor; the hub disposes it.</param>
    /// <param name="consumerWait">How long a consumer waits for a new reading before it gets a stale placeholder.
    /// Null = two intervals plus half a second.</param>
    public SharedHardwareHub(HardwareMonitor reader, TimeSpan interval, TimeProvider? time = null, IAppLogger? log = null,
        TimeSpan? liveWindow = null, TimeSpan? consumerWait = null)
    {
        if (reader.IsSharedView)
        {
            throw new ArgumentException("The hub needs a monitor that reads the sensors itself.", nameof(reader));
        }

        _reader = reader;
        _interval = Clamp(interval);
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _liveWindow = liveWindow ?? TimeSpan.FromMinutes(60);
        _consumerWaitOverride = consumerWait;
    }

    /// <summary>Creates the reader from the switches of project A (NVML GPU, Windows/Linux system, or mock providers).</summary>
    public static SharedHardwareHub Create(bool gpuEnabled, bool systemEnabled, bool useMock, TimeSpan interval, TimeProvider? time = null,
        IAppLogger? log = null)
    {
        var monitor = HardwareMonitor.Create(new SamplingSettings { GpuEnabled = gpuEnabled, SystemEnabled = systemEnabled }, useMock, time, log);
        return new SharedHardwareHub(monitor, interval, time, log);
    }

    /// <summary>The A monitor that really reads the sensors.</summary>
    public HardwareMonitor Reader => _reader;

    public bool IsMock => _reader.IsMock;
    public HardwareInfo Info => _reader.Info;
    public IReadOnlyList<SensorCapability> Capabilities => _reader.Capabilities;

    public TimeSpan Interval
    {
        get
        {
            lock (_gate)
            {
                return _interval;
            }
        }
    }

    public TimeSpan ConsumerWait => _consumerWaitOverride ?? Interval * 2 + TimeSpan.FromMilliseconds(500);

    public string GpuProviderName => _reader.Gpu.Name;
    public bool GpuAvailable => _reader.Gpu.IsAvailable;
    public string? GpuUnavailableReason => _reader.Gpu.UnavailableReason;
    public string? GpuDriverVersion => _reader.Gpu.DriverVersion;
    public string? GpuLibraryVersion => _reader.Gpu.LibraryVersion;
    public IReadOnlyList<GpuInfo> GpuDevices => _reader.Gpu.Devices;
    public IReadOnlyList<SensorCapability> GpuCapabilities => _reader.Gpu.Capabilities;
    public string SystemProviderName => _reader.System.Name;
    public string? CpuName => _reader.System.CpuName;
    public int LogicalProcessors => _reader.System.LogicalProcessors;
    public IReadOnlyList<SensorCapability> SystemCapabilities => _reader.System.Capabilities;

    public SharedHardwareSample? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    public HubHealth Health
    {
        get
        {
            lock (_gate)
            {
                return new HubHealth
                {
                    State = _state,
                    Interval = _interval,
                    IsMock = IsMock,
                    LastReadingAt = _latest?.Snapshot.Timestamp,
                    Readings = _sequence,
                    SkippedTicks = _skippedTicks,
                    LoopErrors = _loopErrors,
                    LastError = _lastError,
                    Consumers = _consumers,
                    LatestIssues = _latest?.Snapshot.Issues ?? [],
                };
            }
        }
    }

    /// <summary>Raised on the reader thread after every reading. Handlers must be quick; exceptions are logged.</summary>
    public event Action<HardwareSnapshot>? Sampled;

    /// <summary>A view for one module. It shares this reader and never opens a sensor itself.</summary>
    public HardwareMonitor CreateConsumer()
    {
        Interlocked.Increment(ref _consumers);
        return HardwareMonitor.Shared(this, _time, _log);
    }

    /// <summary>Raw readings of the last <paramref name="window"/> (display only; nothing is averaged or interpolated).</summary>
    public IReadOnlyList<HardwareSnapshot> Live(TimeSpan window)
    {
        lock (_gate)
        {
            if (_live.Last is null)
            {
                return [];
            }

            var cutoff = _live.Last.Value.Timestamp - window;
            return [.. _live.Where(s => s.Timestamp >= cutoff)];
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _state = HubState.Running;
            var token = _cts.Token;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // A thread of its own, not the thread pool: the module recorders park pool threads while they wait for the
            // next reading, and pool timers fire late when the pool is busy (startup, many modules). The readings must
            // stay on time regardless, otherwise consumers would record "not measured" placeholders for nothing.
            var thread = new Thread(() =>
            {
                try
                {
                    Loop(token);
                }
                finally
                {
                    finished.TrySetResult();
                }
            })
            {
                IsBackground = true,
                Name = "PcControlCenter shared sensor reader",
            };
            _loop = finished.Task;
            thread.Start();
        }
    }

    /// <summary>
    /// Changes the reading interval (the fastest interval any module asks for). The loop restarts with the new timer;
    /// readings already taken are kept.
    /// </summary>
    public async Task SetIntervalAsync(TimeSpan interval)
    {
        var clamped = Clamp(interval);
        bool restart;
        lock (_gate)
        {
            if (clamped == _interval)
            {
                return;
            }

            _interval = clamped;
            restart = _cts is not null;
        }

        _log.Info("hardware", $"공통 센서 간격 변경: {clamped.TotalMilliseconds:0} ms");
        if (restart)
        {
            await StopLoopAsync(HubState.Running).ConfigureAwait(false);
            Start();
        }
    }

    public Task StopAsync() => StopLoopAsync(HubState.Stopped);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _reader.Dispose();
    }

    /// <summary>Takes one reading now (the loop's step; also used by tests and one-shot tools).</summary>
    public SharedHardwareSample ReadNow()
    {
        HardwareSnapshot snapshot;
        try
        {
            snapshot = _reader.Sample();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // HardwareMonitor.Sample never throws by contract; this keeps the loop alive if a provider breaks it.
            _log.Error("hardware", "공통 센서 읽기 중 예외", ex);
            lock (_gate)
            {
                _loopErrors++;
                _lastError = ex.Message;
            }

            snapshot = new HardwareSnapshot
            {
                Timestamp = _time.GetUtcNow(),
                IsMock = IsMock,
                Issues = [new SensorIssue("공통 센서", "hub", SensorStatus.Error, $"센서 읽기 중 예외: {ex.GetType().Name}")],
            };
        }

        SharedHardwareSample sample;
        lock (_gate)
        {
            sample = new SharedHardwareSample(++_sequence, snapshot, IsStale: false);
            _latest = sample;
            _live.AddLast(snapshot);
            var cutoff = snapshot.Timestamp - _liveWindow;
            while (_live.First is { } first && first.Value.Timestamp < cutoff)
            {
                _live.RemoveFirst();
            }

            Monitor.PulseAll(_gate);
        }

        try
        {
            Sampled?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _log.Error("hardware", "공통 센서 처리기 예외", ex);
        }

        return sample;
    }

    public SharedHardwareSample NextAfter(long afterSequence)
    {
        // Waiting is real time even when timestamps come from a test clock.
        var started = Stopwatch.GetTimestamp();
        var wait = ConsumerWait;
        lock (_gate)
        {
            while (true)
            {
                if (_latest is { } latest && latest.Sequence > afterSequence)
                {
                    return latest;
                }

                var remaining = wait - Stopwatch.GetElapsedTime(started);
                if (_state != HubState.Running || remaining <= TimeSpan.Zero)
                {
                    break;
                }

                Monitor.Wait(_gate, remaining);
            }

            var reason = _state switch
            {
                HubState.NotStarted => "공통 센서 수집이 아직 시작되지 않았습니다.",
                HubState.Stopped => "공통 센서 수집이 중지되었습니다.",
                _ => $"공통 센서에서 {wait.TotalSeconds:0.#}초 동안 새 측정값이 오지 않았습니다.",
            };
            return new SharedHardwareSample(afterSequence, new HardwareSnapshot
            {
                Timestamp = _time.GetUtcNow(),
                IsMock = IsMock,
                Issues = [new SensorIssue("공통 센서", "hub", SensorStatus.Unavailable, reason + " 이 시점의 값은 측정되지 않았습니다 (N/A).")],
            }, IsStale: true);
        }
    }

    private void Loop(CancellationToken ct)
    {
        TimeSpan interval;
        lock (_gate)
        {
            interval = _interval;
        }

        // The schedule runs on real time (an OS wait on this thread, no timer callback on the pool). Skipped ticks are
        // counted on the hub's clock, which is what the timestamps of the readings use.
        var next = Stopwatch.GetTimestamp();
        var last = _time.GetTimestamp();
        var first = true;
        while (true)
        {
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), next);
            if (remaining > TimeSpan.Zero ? ct.WaitHandle.WaitOne(remaining) : ct.IsCancellationRequested)
            {
                return;
            }

            if (first)
            {
                first = false;
            }
            else
            {
                var now = _time.GetTimestamp();
                var skipped = Math.Max(0, (long)Math.Round(_time.GetElapsedTime(last, now) / interval) - 1);
                last = now;
                if (skipped > 0)
                {
                    lock (_gate)
                    {
                        _skippedTicks += skipped;
                    }
                }
            }

            try
            {
                ReadNow();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // ReadNow already guards the reader and the handlers; this keeps the thread alive whatever happens.
                lock (_gate)
                {
                    _loopErrors++;
                    _lastError = ex.Message;
                }
            }

            // Like PeriodicTimer: fixed ticks; ticks missed while a reading took too long are dropped, never queued.
            var ticks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
            next += ticks;
            var behind = Stopwatch.GetTimestamp() - next;
            if (behind > 0)
            {
                next += (behind / ticks + 1) * ticks;
            }
        }
    }

    private async Task StopLoopAsync(HubState finalState)
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
            _state = finalState;
            Monitor.PulseAll(_gate);
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
    }

    private static TimeSpan Clamp(TimeSpan interval)
        => interval < MinInterval ? MinInterval : interval > MaxInterval ? MaxInterval : interval;
}
