using LocalAiPerf.Core;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Telemetry;

namespace LocalAiPerf.Recording;

/// <summary>
/// Samples hardware on a fixed interval and polls the AI runtime on its own loop, so that a slow or absent
/// runtime never delays hardware sampling. Each runtime snapshot is attached to exactly one frame.
/// </summary>
public sealed class TelemetryCollector : IAsyncDisposable
{
    private readonly HardwareMonitor _hardware;
    private readonly IAiRuntimeAdapter? _runtime;
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _hardwareLoop;
    private Task? _runtimeLoop;
    private RuntimeSnapshot? _pendingRuntime;
    private RuntimeSnapshot? _latestRuntime;
    private long _sequence;

    public TelemetryCollector(HardwareMonitor hardware, IAiRuntimeAdapter? runtime, TimeSpan interval, TimeProvider? time = null, IAppLogger? log = null)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _hardware = hardware;
        _runtime = runtime;
        Interval = interval;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    public TimeSpan Interval { get; }
    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public RuntimeSnapshot? LatestRuntime
    {
        get
        {
            lock (_gate)
            {
                return _latestRuntime;
            }
        }
    }

    /// <summary>Raised on a background thread for every tick.</summary>
    public event Action<TelemetryFrame>? FrameCollected;

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _hardwareLoop = Task.Run(() => HardwareLoopAsync(token), CancellationToken.None);
        if (_runtime is not null)
        {
            _runtimeLoop = Task.Run(() => RuntimeLoopAsync(token), CancellationToken.None);
        }
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (var loop in new[] { _hardwareLoop, _runtimeLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Takes one sample immediately (used by tests and the CLI's one-shot mode).</summary>
    public TelemetryFrame CollectOnce(int skippedTicks = 0)
    {
        var started = _time.GetTimestamp();
        var hardware = _hardware.Sample();
        RuntimeSnapshot? runtime;
        lock (_gate)
        {
            runtime = _pendingRuntime;
            _pendingRuntime = null;
        }

        return new TelemetryFrame(Interlocked.Increment(ref _sequence), hardware.Timestamp, hardware, runtime,
            _time.GetElapsedTime(started), skippedTicks);
    }

    private async Task HardwareLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        var last = _time.GetUtcNow();
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

            var now = _time.GetUtcNow();
            var skipped = Math.Max(0, (int)Math.Round((now - last) / Interval) - 1);
            last = now;
            TelemetryFrame frame;
            try
            {
                frame = CollectOnce(skipped);
            }
            catch (Exception ex)
            {
                _log.Error("collector", "샘플 수집 중 예외", ex);
                continue;
            }

            try
            {
                FrameCollected?.Invoke(frame);
            }
            catch (Exception ex)
            {
                _log.Error("collector", "프레임 처리기 예외", ex);
            }
        }
    }

    private async Task RuntimeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var started = _time.GetUtcNow();
            RuntimeSnapshot snapshot;
            try
            {
                snapshot = await _runtime!.PollAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("collector", "런타임 조회 중 예외", ex);
                snapshot = new RuntimeSnapshot
                {
                    RuntimeKind = _runtime!.Kind,
                    Timestamp = _time.GetUtcNow(),
                    State = RuntimeConnectionState.Unreachable,
                    Endpoint = _runtime.EndpointDisplay,
                    Error = new RuntimeError(RuntimeErrorCategory.Connection, $"예상하지 못한 오류: {ex.GetType().Name}"),
                };
            }

            lock (_gate)
            {
                _pendingRuntime = snapshot;
                _latestRuntime = snapshot;
            }

            var wait = Interval - (_time.GetUtcNow() - started);
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
