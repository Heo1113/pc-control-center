using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Telemetry;
using WinDiag.Core.Events;
using WinDiag.Core.Time;
using WinDiag.Storage;

namespace WinDiag.Collection.Sensors;

/// <summary>Recent sensor snapshots kept in memory for the live charts (independent of the database).</summary>
public sealed class LiveSensorBuffer(TimeSpan window)
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
/// Samples project A's <see cref="HardwareMonitor"/> on a fixed interval and stores every sample. The sampling loop
/// follows A's TelemetryCollector (PeriodicTimer, never throws). It also compares the wall clock with the monotonic
/// clock between samples, so that a system time change is recorded instead of silently distorting the data.
/// </summary>
public sealed class SensorRecorder : IAsyncDisposable
{
    private readonly HardwareMonitor _monitor;
    private readonly DiagnosticsStore? _store;
    private readonly long _runId;
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset? _lastWall;
    private long _lastMono;
    private int _storeErrors;

    public SensorRecorder(HardwareMonitor monitor, DiagnosticsStore? store, long runId, TimeSpan interval, TimeProvider? time = null, IAppLogger? log = null)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _monitor = monitor;
        _store = store;
        _runId = runId;
        Interval = interval;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        Live = new LiveSensorBuffer(TimeSpan.FromMinutes(60));
    }

    public TimeSpan Interval { get; }
    public HardwareMonitor Monitor => _monitor;
    public LiveSensorBuffer Live { get; }
    public long SamplesStored { get; private set; }
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
            _store.AddGap(new CollectionGap
            {
                Scope = "sensors",
                Kind = GapKinds.SensorUnavailable,
                From = now,
                Detected = now,
                Detail = $"GPU 센서를 사용할 수 없어 GPU 값은 기록하지 않습니다 (N/A): {reason}",
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

    /// <summary>Takes and stores one sample now (tests and the first sample after start).</summary>
    public HardwareSnapshot SampleOnce()
    {
        var snapshot = _monitor.Sample();
        CheckClock(snapshot.Timestamp);
        Live.Add(snapshot);
        if (_store is not null)
        {
            try
            {
                if (_store.WriteSensorSample(_runId, snapshot))
                {
                    SamplesStored++;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Storage problems are logged (rate-limited); live values keep flowing.
                if (_storeErrors++ % 100 == 0)
                {
                    _log.Error("sensors", "센서 기록 저장 실패", ex);
                }
            }
        }

        try
        {
            Sampled?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _log.Error("sensors", "센서 처리기 예외", ex);
        }

        return snapshot;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            SampleOnce();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("sensors", "첫 센서 샘플 실패", ex);
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
                _log.Error("sensors", "센서 샘플 실패", ex);
            }
        }
    }

    private void CheckClock(DateTimeOffset wall)
    {
        var mono = _time.GetTimestamp();
        if (_lastWall is { } lastWall)
        {
            var wallDelta = wall - lastWall;
            var monoDelta = _time.GetElapsedTime(_lastMono, mono);
            var drift = wallDelta - monoDelta;
            var threshold = TimeSpan.FromSeconds(Math.Max(10, Interval.TotalSeconds * 2));
            if (drift.Duration() > threshold && _store is not null)
            {
                var detail = drift < TimeSpan.Zero
                    ? $"시스템 시각이 약 {TimeDisplay.Duration(drift)} 뒤로 이동했습니다. 이 시점 전후의 시간 순서 비교에 주의하세요."
                    : $"시스템 시각이 약 {TimeDisplay.Duration(drift)} 앞으로 이동했거나 절전·최대 절전에서 복귀했습니다.";
                try
                {
                    _store.AddGap(new CollectionGap { Scope = "sensors", Kind = GapKinds.ClockJump, From = lastWall, To = wall, Detected = wall, Detail = detail });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log.Warn("sensors", "시각 변경 기록 실패", ex);
                }
            }
        }

        _lastWall = wall;
        _lastMono = mono;
    }
}
