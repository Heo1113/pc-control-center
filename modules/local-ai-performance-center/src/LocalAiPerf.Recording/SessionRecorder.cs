using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Sessions;
using LocalAiPerf.Storage;

namespace LocalAiPerf.Recording;

public enum RecorderStatus
{
    Idle,
    Recording,
    StorageError,
}

public sealed record RecorderOptions(
    int SampleIntervalMs,
    bool AutoSplitOnRuntimeChange,
    string? RuntimeKind,
    string? RuntimeEndpoint,
    bool IsMock);

/// <summary>
/// Owns the active session and writes frames to the store on a single background writer.
/// Start, stop and frames are processed in order, so a session never receives frames after it ended.
/// Prompt and answer text never reach this class: runtime snapshots carry only counts and timings.
/// </summary>
public sealed class SessionRecorder : IAsyncDisposable
{
    private const int MaxPendingFrames = 600;
    private const int MaxRetryFrames = 300;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly TelemetryStore _store;
    private readonly Func<HardwareInfo> _hostInfo;
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private readonly Queue<FrameWrite> _retry = new();
    private readonly Queue<string> _seenOrder = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeIssues = new(StringComparer.Ordinal);

    private RecorderOptions _options;
    private SessionInfo? _active;
    private int _pendingFrames;
    private long _dropped;
    private DateTimeOffset _nextRetry;
    private RuntimeConnectionState? _lastState;
    private RuntimeConditions? _lastConditions;
    private string? _lastFingerprint;
    private (string? Instance, long? Rejected, long? Timeouts, long? Disconnects)? _lastCounters;
    private DateTimeOffset? _lastFrameTime;
    private bool _baselineTaken;

    public SessionRecorder(TelemetryStore store, RecorderOptions options, Func<HardwareInfo> hostInfo, TimeProvider? time = null, IAppLogger? log = null)
    {
        _store = store;
        _options = options;
        _hostInfo = hostInfo;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _writer = Task.Run(WriterLoopAsync);
    }

    public SessionInfo? ActiveSession => Volatile.Read(ref _active);
    public RecorderStatus Status { get; private set; } = RecorderStatus.Idle;
    public string? LastError { get; private set; }
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>Raised (on the writer thread) when the session or the recorder status changes.</summary>
    public event Action? StatusChanged;

    public void UpdateOptions(RecorderOptions options) => _options = options;

    public Task<SessionInfo> StartAsync(string? name, SessionKind kind, string? notes = null)
    {
        var done = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(new StartCommand(name, kind, notes, done)))
        {
            done.SetException(new ObjectDisposedException(nameof(SessionRecorder)));
        }

        return done.Task;
    }

    public Task StopAsync(string reason = SessionEndReasons.UserStopped)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(new StopCommand(reason, done)))
        {
            done.SetResult();
        }

        return done.Task;
    }

    /// <summary>Waits until every queued command has been processed.</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(new FlushCommand(done)))
        {
            done.SetResult();
        }

        return done.Task;
    }

    /// <summary>Queues a frame. Never blocks; frames are dropped (and counted) if the writer falls far behind.</summary>
    public void Enqueue(TelemetryFrame frame)
    {
        if (Volatile.Read(ref _active) is null)
        {
            return;
        }

        if (Interlocked.Increment(ref _pendingFrames) > MaxPendingFrames)
        {
            Interlocked.Decrement(ref _pendingFrames);
            Interlocked.Increment(ref _dropped);
            return;
        }

        if (!_commands.Writer.TryWrite(new FrameCommand(frame)))
        {
            Interlocked.Decrement(ref _pendingFrames);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _commands.Writer.TryComplete();
        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("recorder", "기록기 종료 중 예외", ex);
        }
    }

    private async Task WriterLoopAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (command)
                {
                    case FrameCommand frame:
                        Interlocked.Decrement(ref _pendingFrames);
                        ProcessFrame(frame.Frame);
                        break;
                    case StartCommand start:
                        start.Done.TrySetResult(StartSession(start.Name, start.Kind, start.Notes, _time.GetUtcNow(), SessionEndReasons.Replaced));
                        break;
                    case StopCommand stop:
                        EndSession(stop.Reason, _time.GetUtcNow());
                        stop.Done.TrySetResult();
                        break;
                    case FlushCommand flush:
                        flush.Done.TrySetResult();
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.Error("recorder", "기록 명령 처리 실패", ex);
                switch (command)
                {
                    case StartCommand start:
                        start.Done.TrySetException(ex);
                        break;
                    case StopCommand stop:
                        stop.Done.TrySetException(ex);
                        break;
                    case FlushCommand flush:
                        flush.Done.TrySetResult();
                        break;
                }

                SetStatus(RecorderStatus.StorageError, ex.Message);
            }
        }

        if (_active is not null)
        {
            try
            {
                EndSession(SessionEndReasons.AppExit, _time.GetUtcNow());
            }
            catch (Exception ex)
            {
                _log.Error("recorder", "종료 시 세션 마감 실패 (다음 실행에서 복구됩니다)", ex);
            }
        }
    }

    private SessionInfo StartSession(string? name, SessionKind kind, string? notes, DateTimeOffset at, string replaceReason)
    {
        if (_active is not null)
        {
            EndSession(replaceReason, at);
        }

        var host = _hostInfo();
        var hostJson = JsonSerializer.Serialize(host);
        var local = at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var finalName = string.IsNullOrWhiteSpace(name)
            ? (kind == SessionKind.Auto ? $"자동 기록 {local}" : $"수동 기록 {local}")
            : name.Trim();
        var session = _store.StartSession(new SessionStartRequest(finalName, kind, at, _options.SampleIntervalMs, AppInfo.Version, hostJson,
            _options.RuntimeKind, _options.RuntimeEndpoint, _options.IsMock || host.IsMock, notes));
        ResetSessionState();
        Volatile.Write(ref _active, session);
        _log.Info("recorder", $"세션 시작 #{session.Id} '{session.Name}'");
        SetStatus(RecorderStatus.Recording, null);
        return session;
    }

    private void EndSession(string reason, DateTimeOffset at)
    {
        var active = _active;
        if (active is null)
        {
            return;
        }

        FlushRetry(force: true);
        _store.EndSession(active.Id, at, reason);
        Volatile.Write(ref _active, null);
        _log.Info("recorder", $"세션 종료 #{active.Id} ({reason})");
        SetStatus(RecorderStatus.Idle, null);
    }

    private void ResetSessionState()
    {
        _seen.Clear();
        _seenOrder.Clear();
        _activeIssues.Clear();
        _lastState = null;
        _lastConditions = null;
        _lastFingerprint = null;
        _lastCounters = null;
        _lastFrameTime = null;
        _baselineTaken = false;
    }

    private void ProcessFrame(TelemetryFrame frame)
    {
        if (_active is null)
        {
            return;
        }

        var rt = frame.Runtime;
        RuntimeConditions? conditions = null;
        if (rt is not null && rt.IsReachable)
        {
            conditions = RuntimeConditions.FromSnapshot(rt);
            if (_options.AutoSplitOnRuntimeChange && _active.Kind == SessionKind.Auto
                && RuntimeConditions.IsMaterialChange(_lastConditions, conditions))
            {
                var change = string.Join(", ", RuntimeConditions.Describe(_lastConditions, conditions));
                var previousId = _active.Id;
                _store.WriteEvent(new EventRecord(previousId, frame.Timestamp, EventSeverity.Info, EventCategories.RuntimeChange,
                    $"엔진/모델 조건이 바뀌어 이 세션을 닫고 새 세션을 시작합니다: {change}"));
                var next = StartSession(null, SessionKind.Auto, $"세션 #{previousId}에서 조건 변경으로 분할: {change}", frame.Timestamp,
                    SessionEndReasons.ConditionChanged);
                _log.Info("recorder", $"조건 변경으로 세션 분할 #{previousId} -> #{next.Id}: {change}");
            }
        }

        var session = _active!;
        var events = new List<EventRecord>();
        var flags = frame.Hardware.IsMock ? SampleFlags.Mock : SampleFlags.None;
        if (frame.SkippedTicks > 0)
        {
            flags |= SampleFlags.TickOverrun;
        }

        if (_lastFrameTime is DateTimeOffset previous)
        {
            var gap = frame.Timestamp - previous;
            if (gap > TimeSpan.FromMilliseconds(Math.Max(3 * _options.SampleIntervalMs, 5000)))
            {
                events.Add(new EventRecord(session.Id, frame.Timestamp, EventSeverity.Warning, EventCategories.Gap,
                    $"수집 공백 {gap.TotalSeconds:0}초 (절전, 일시 중지 또는 지연). 이 구간의 값은 기록되지 않았습니다."));
            }
        }

        _lastFrameTime = frame.Timestamp;
        foreach (var issue in frame.Hardware.Issues)
        {
            if (issue.Scope.StartsWith("cpu", StringComparison.Ordinal))
            {
                flags |= SampleFlags.CpuError;
            }
            else if (issue.Scope.StartsWith("ram", StringComparison.Ordinal))
            {
                flags |= SampleFlags.MemoryError;
            }
            else if (issue.Scope.StartsWith("gpu", StringComparison.Ordinal))
            {
                flags |= SampleFlags.GpuUnavailable;
            }
        }

        TrackIssues(frame, session.Id, events);
        string? instance = null;
        var newRequests = new List<CompletedRequest>();
        RuntimeConditions? conditionsToWrite = null;
        if (rt is not null)
        {
            if (_lastState != rt.State)
            {
                events.Add(StateEvent(session.Id, rt));
                _lastState = rt.State;
            }

            if (rt.IsReachable && conditions is not null)
            {
                if (conditions.Fingerprint != _lastFingerprint)
                {
                    conditionsToWrite = conditions;
                    if (_lastConditions is not null && RuntimeConditions.IsMaterialChange(_lastConditions, conditions))
                    {
                        events.Add(new EventRecord(session.Id, rt.Timestamp, EventSeverity.Info, EventCategories.RuntimeChange,
                            "런타임 조건 변경: " + string.Join(", ", RuntimeConditions.Describe(_lastConditions, conditions))));
                    }

                    _lastFingerprint = conditions.Fingerprint;
                }

                _lastConditions = conditions;
                instance = rt.Server?.InstanceId ?? "";
                CollectRequests(rt, session, instance, newRequests, events);
                CollectCounters(rt, session.Id, instance, events);
            }
        }

        var write = new FrameWrite
        {
            SessionId = session.Id,
            Frame = frame,
            Flags = flags,
            NewRequests = newRequests,
            InstanceId = instance,
            Conditions = conditionsToWrite,
            Events = events,
        };
        Write(write);
    }

    private void CollectRequests(RuntimeSnapshot rt, SessionInfo session, string instance, List<CompletedRequest> newRequests, List<EventRecord> events)
    {
        var sessionStartUnix = session.StartedAt.ToUnixTimeMilliseconds() / 1000.0;
        var errors = 0;
        foreach (var request in rt.RecentRequests)
        {
            var key = instance + "|" + request.Id;
            if (_seen.Contains(key))
            {
                continue;
            }

            Remember(key);
            var finishedBeforeSession = request.StartedUnix is double started && started + (request.DurationSeconds ?? 0) < sessionStartUnix;
            if (finishedBeforeSession || (!_baselineTaken && request.StartedUnix is null))
            {
                continue; // completed before this session: belongs to an earlier period
            }

            newRequests.Add(request);
            if (request.IsError)
            {
                errors++;
            }
        }

        _baselineTaken = true;
        if (errors > 0)
        {
            events.Add(new EventRecord(session.Id, rt.Timestamp, EventSeverity.Warning, EventCategories.RuntimeRequestError,
                $"Strata가 오류/시간 초과로 끝난 요청 {errors}건을 보고했습니다."));
        }
    }

    private void CollectCounters(RuntimeSnapshot rt, long sessionId, string instance, List<EventRecord> events)
    {
        var work = rt.Work;
        var current = (Instance: (string?)instance, Rejected: work?.AdmissionRejected, Timeouts: work?.QueueTimeouts, Disconnects: work?.ClientDisconnectCancels);
        if (_lastCounters is { } last && last.Instance == instance)
        {
            static long Delta(long? now, long? before) => now is long n && before is long b && n > b ? n - b : 0;
            var rejected = Delta(current.Rejected, last.Rejected);
            var timeouts = Delta(current.Timeouts, last.Timeouts);
            var disconnects = Delta(current.Disconnects, last.Disconnects);
            if (rejected + timeouts + disconnects > 0)
            {
                events.Add(new EventRecord(sessionId, rt.Timestamp, EventSeverity.Warning, EventCategories.RuntimeRequestError,
                    $"Strata 보고: 입장 거부 {rejected}건, 대기 시간 초과 {timeouts}건, 클라이언트 연결 끊김 {disconnects}건"));
            }
        }

        _lastCounters = current;
    }

    private void TrackIssues(TelemetryFrame frame, long sessionId, List<EventRecord> events)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issue in frame.Hardware.Issues)
        {
            var key = $"{issue.Source}|{issue.Scope}|{issue.Message}";
            current.Add(key);
            if (_activeIssues.Add(key))
            {
                events.Add(new EventRecord(sessionId, frame.Timestamp, EventSeverity.Warning, EventCategories.Sensor,
                    $"센서 문제 ({issue.Source} {issue.Scope}): {issue.Message}"));
            }
        }

        foreach (var resolved in _activeIssues.Where(k => !current.Contains(k)).ToList())
        {
            _activeIssues.Remove(resolved);
            var parts = resolved.Split('|');
            events.Add(new EventRecord(sessionId, frame.Timestamp, EventSeverity.Info, EventCategories.Sensor,
                $"센서 문제 해소 ({parts[0]} {parts[1]})"));
        }
    }

    private static EventRecord StateEvent(long sessionId, RuntimeSnapshot rt)
    {
        var text = $"{rt.RuntimeKind} 연결 상태: {RuntimeStateText.ToKorean(rt.State)}";
        if (rt.Error is { } error)
        {
            text += $" - [{RuntimeStateText.ToKorean(error.Category)}] {error.Message}";
        }

        var severity = rt.State switch
        {
            RuntimeConnectionState.Connected => EventSeverity.Info,
            RuntimeConnectionState.Incompatible => EventSeverity.Error,
            _ => EventSeverity.Warning,
        };
        var category = rt.Error?.Category == RuntimeErrorCategory.Compatibility ? EventCategories.RuntimeCompatibility : EventCategories.RuntimeConnection;
        return new EventRecord(sessionId, rt.Timestamp, severity, category, text);
    }

    private void Remember(string key)
    {
        if (_seen.Add(key))
        {
            _seenOrder.Enqueue(key);
            while (_seenOrder.Count > 10_000)
            {
                _seen.Remove(_seenOrder.Dequeue());
            }
        }
    }

    private void Write(FrameWrite write)
    {
        var now = _time.GetUtcNow();
        if (Status == RecorderStatus.StorageError)
        {
            Buffer(write);
            if (now >= _nextRetry)
            {
                FlushRetry(force: false);
            }

            return;
        }

        try
        {
            _store.WriteFrame(write);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            _log.Error("recorder", "샘플 저장 실패 - 메모리에 보관하고 다시 시도합니다", ex);
            Buffer(write);
            _nextRetry = now + RetryInterval;
            SetStatus(RecorderStatus.StorageError, ex.Message);
        }
    }

    private void Buffer(FrameWrite write)
    {
        _retry.Enqueue(write);
        while (_retry.Count > MaxRetryFrames)
        {
            _retry.Dequeue();
            Interlocked.Increment(ref _dropped);
        }
    }

    private void FlushRetry(bool force)
    {
        if (_retry.Count == 0)
        {
            if (Status == RecorderStatus.StorageError && force)
            {
                SetStatus(_active is null ? RecorderStatus.Idle : RecorderStatus.Recording, null);
            }

            return;
        }

        try
        {
            while (_retry.TryPeek(out var pending))
            {
                _store.WriteFrame(pending);
                _retry.Dequeue();
            }

            var dropped = Interlocked.Exchange(ref _dropped, 0);
            if (_active is not null)
            {
                _store.WriteEvent(new EventRecord(_active.Id, _time.GetUtcNow(), dropped > 0 ? EventSeverity.Warning : EventSeverity.Info,
                    EventCategories.Storage, dropped > 0
                        ? $"저장 오류에서 복구했습니다. 보관하지 못한 샘플 {dropped}개는 기록에서 빠졌습니다."
                        : "저장 오류에서 복구했습니다. 보관해 둔 샘플을 모두 기록했습니다."));
            }

            SetStatus(_active is null ? RecorderStatus.Idle : RecorderStatus.Recording, null);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            _nextRetry = _time.GetUtcNow() + RetryInterval;
            SetStatus(RecorderStatus.StorageError, ex.Message);
            if (force)
            {
                throw;
            }
        }
    }

    private void SetStatus(RecorderStatus status, string? error)
    {
        var changed = Status != status || LastError != error;
        Status = status;
        LastError = error;
        if (changed || status == RecorderStatus.Recording)
        {
            try
            {
                StatusChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error("recorder", "상태 변경 처리기 예외", ex);
            }
        }
    }

    private abstract record Command;

    private sealed record FrameCommand(TelemetryFrame Frame) : Command;

    private sealed record StartCommand(string? Name, SessionKind Kind, string? Notes, TaskCompletionSource<SessionInfo> Done) : Command;

    private sealed record StopCommand(string Reason, TaskCompletionSource Done) : Command;

    private sealed record FlushCommand(TaskCompletionSource Done) : Command;
}
