using GpuProfit.Storage;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Telemetry;

namespace GpuProfit.Recording;

/// <summary>Recent snapshots kept in memory for the live charts (independent of the database).</summary>
public sealed class LiveSnapshotBuffer(TimeSpan window)
{
    private readonly object _gate = new();
    private readonly LinkedList<HardwareSnapshot> _items = new();

    public TimeSpan Window { get; } = window;

    public HardwareSnapshot? Latest
    {
        get
        {
            lock (_gate)
            {
                return _items.Last?.Value;
            }
        }
    }

    public void Add(HardwareSnapshot snapshot)
    {
        lock (_gate)
        {
            _items.AddLast(snapshot);
            var cutoff = snapshot.Timestamp - Window;
            while (_items.First is { } first && first.Value.Timestamp < cutoff)
            {
                _items.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<HardwareSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return [.. _items];
        }
    }
}

/// <summary>
/// Samples project A's <see cref="HardwareMonitor"/> on a fixed interval and stores every sample with its real
/// timestamp. Adapted from project C's SensorRecorder (commit 06cc9e5), which follows A's TelemetryCollector: a
/// PeriodicTimer loop that never throws, plus wall-clock vs. monotonic clock comparison. Added for this app: sample
/// gaps longer than the accounting limit, GPU sensor loss/return and Windows sleep are recorded as gaps.
/// </summary>
public sealed class TelemetryRecorder : IAsyncDisposable
{
    private readonly HardwareMonitor _monitor;
    private readonly ProfitStore? _store;
    private readonly long _runId;
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private readonly object _sampleGate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset? _lastWall;
    private long _lastMono;
    private int _storeErrors;
    private int? _lastGpuCount;
    private DateTimeOffset? _suspendedAt;

    public TelemetryRecorder(HardwareMonitor monitor, ProfitStore? store, long runId, TimeSpan interval, Func<double> maxGapSeconds,
        TimeProvider? time = null, IAppLogger? log = null)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _monitor = monitor;
        _store = store;
        _runId = runId;
        Interval = interval;
        MaxGapSeconds = maxGapSeconds;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        Live = new LiveSnapshotBuffer(TimeSpan.FromMinutes(60));
    }

    public TimeSpan Interval { get; }
    public Func<double> MaxGapSeconds { get; }
    public HardwareMonitor Monitor => _monitor;
    public LiveSnapshotBuffer Live { get; }
    public long SamplesStored { get; private set; }
    public int StoreErrors => _storeErrors;
    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>Raised on a background thread after each sample.</summary>
    public event Action<HardwareSnapshot>? Sampled;

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        if (_store is not null && !_monitor.Gpu.IsAvailable && _monitor.Gpu.UnavailableReason is { } reason)
        {
            var now = _time.GetUtcNow();
            TryAddGap(new CollectionGap
            {
                Scope = "gpu",
                Kind = GapKinds.SensorUnavailable,
                From = now,
                Detected = now,
                Detail = $"GPU 센서를 사용할 수 없어 GPU 전력은 기록하지 않습니다 (N/A, 0 W로 간주하지 않음): {reason}",
            });
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Windows is going to sleep: the time until <see cref="NotifyResume"/> is recorded as a suspend gap.</summary>
    public void NotifySuspend() => _suspendedAt = _time.GetUtcNow();

    public void NotifyResume()
    {
        var now = _time.GetUtcNow();
        var from = _suspendedAt ?? _lastWall;
        _suspendedAt = null;
        if (from is { } f && now > f)
        {
            TryAddGap(new CollectionGap
            {
                Scope = "system",
                Kind = GapKinds.Suspend,
                From = f,
                To = now,
                Detected = now,
                Detail = "Windows 절전·최대 절전 구간입니다. 이 구간의 전력은 측정되지 않았으며 에너지에 포함하지 않습니다.",
            });
        }
    }

    /// <summary>Takes and stores one sample now (also used at session start and stop so segment edges have a sample).</summary>
    public HardwareSnapshot SampleOnce()
    {
        lock (_sampleGate)
        {
            var snapshot = _monitor.Sample();
            CheckClockAndGap(snapshot.Timestamp);
            CheckGpuPresence(snapshot);
            Live.Add(snapshot);
            if (_store is not null)
            {
                try
                {
                    if (_store.WriteSnapshot(_runId, snapshot))
                    {
                        SamplesStored++;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Storage problems are logged (rate-limited); live values keep flowing.
                    if (_storeErrors++ % 100 == 0)
                    {
                        _log.Error("telemetry", "센서 기록 저장 실패", ex);
                    }
                }
            }

            try
            {
                Sampled?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                _log.Error("telemetry", "센서 처리기 예외", ex);
            }

            return snapshot;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            SampleOnce();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("telemetry", "첫 센서 샘플 실패", ex);
        }

        using var timer = new PeriodicTimer(Interval, _time);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                SampleOnce();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Error("telemetry", "센서 샘플 실패", ex);
            }
        }
    }

    private void CheckClockAndGap(DateTimeOffset wall)
    {
        var mono = _time.GetTimestamp();
        if (_lastWall is { } lastWall)
        {
            var wallDelta = wall - lastWall;
            var monoDelta = _time.GetElapsedTime(_lastMono, mono);
            var drift = wallDelta - monoDelta;
            var threshold = TimeSpan.FromSeconds(Math.Max(10, Interval.TotalSeconds * 2));
            if (drift.Duration() > threshold)
            {
                TryAddGap(new CollectionGap
                {
                    Scope = "telemetry",
                    Kind = GapKinds.ClockJump,
                    From = lastWall,
                    To = wall,
                    Detected = wall,
                    Detail = drift < TimeSpan.Zero
                        ? $"시스템 시각이 약 {drift.Duration().TotalSeconds:0}초 뒤로 이동했습니다. 이 시점 전후의 에너지 적산은 시각 순서대로만 계산됩니다."
                        : $"시스템 시각이 약 {drift.TotalSeconds:0}초 앞으로 이동했거나 절전에서 복귀했습니다. 이 구간은 측정되지 않았습니다.",
                });
            }
            else if (wallDelta.TotalSeconds > MaxGapSeconds() && _suspendedAt is null)
            {
                TryAddGap(new CollectionGap
                {
                    Scope = "telemetry",
                    Kind = GapKinds.SampleGap,
                    From = lastWall,
                    To = wall,
                    Detected = wall,
                    Detail = $"샘플 간격 {wallDelta.TotalSeconds:0.#}초가 최대 간격 {MaxGapSeconds():0.#}초를 넘었습니다 (앱 응답 없음·과부하 등). 이 구간의 에너지는 보간하지 않습니다.",
                });
            }
        }

        _lastWall = wall;
        _lastMono = mono;
    }

    private void CheckGpuPresence(HardwareSnapshot snapshot)
    {
        var count = snapshot.Gpus.Count(g => g.PowerW is not null);
        if (_lastGpuCount is int last && last > 0 && count < last)
        {
            var reason = snapshot.Issues.FirstOrDefault(i => i.Scope.StartsWith("gpu", StringComparison.Ordinal))?.Message ?? "GPU 전력 값을 읽지 못했습니다.";
            TryAddGap(new CollectionGap
            {
                Scope = "gpu",
                Kind = GapKinds.SensorUnavailable,
                From = snapshot.Timestamp,
                Detected = snapshot.Timestamp,
                Detail = $"GPU 전력 측정이 끊겼습니다 (전력 값이 있는 GPU {last}개 → {count}개): {reason}",
            });
        }

        _lastGpuCount = count;
    }

    private void TryAddGap(CollectionGap gap)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            _store.AddGap(gap);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("telemetry", "공백 기록 실패", ex);
        }
    }
}
