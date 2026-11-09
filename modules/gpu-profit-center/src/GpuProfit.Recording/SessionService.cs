using GpuProfit.Accounting.Profitability;
using GpuProfit.Core.Domain;
using GpuProfit.Storage;
using LocalAiPerf.Core.Diagnostics;

namespace GpuProfit.Recording;

/// <summary>
/// Session lifecycle (start, pause, resume, stop, abort, manual entry) and accounting (building the engine input from
/// the database, calculating, saving the result history). One open session per GPU at a time.
/// </summary>
public sealed class SessionService(ProfitStore store, TimeProvider time, Func<long?> runId, Func<bool> isMock, Action? sampleNow = null, IAppLogger? log = null)
{
    private readonly IAppLogger _log = log ?? NullLogger.Instance;
    private readonly object _gate = new();

    /// <summary>Raised after a session was created, changed state, or was recalculated.</summary>
    public event Action<long>? SessionChanged;

    public IReadOnlyList<WorkSession> OpenSessions() =>
        [.. store.ListSessions(new SessionQuery { State = SessionState.Running }), .. store.ListSessions(new SessionQuery { State = SessionState.Paused })];

    public WorkSession Start(long workloadId, string? title, string? notes, IReadOnlyList<int>? devices = null)
    {
        lock (_gate)
        {
            if (store.GetWorkload(workloadId) is not { } workload)
            {
                throw new InvalidOperationException("작업을 찾을 수 없습니다.");
            }

            if (workload.Archived)
            {
                throw new InvalidOperationException("보관된 작업으로는 새 세션을 시작할 수 없습니다.");
            }

            var running = store.ListSessions(new SessionQuery { State = SessionState.Running });
            if (running.FirstOrDefault(s => Overlaps(s.DeviceIndices, devices)) is { } other)
            {
                throw new InvalidOperationException($"같은 GPU를 기록 중인 세션(#{other.Id})이 있습니다. 먼저 일시정지하거나 종료하세요.");
            }

            SampleEdge();
            var now = time.GetUtcNow();
            var id = store.CreateSession(new WorkSession
            {
                WorkloadId = workloadId,
                Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                State = SessionState.Running,
                Source = SessionSource.Live,
                Started = now,
                RunId = runId(),
                IsMock = isMock(),
                DeviceIndices = devices is { Count: > 0 } ? devices : null,
            });
            _log.Info("session", $"세션 #{id} 시작 (작업 {workload.Name})");
            SessionChanged?.Invoke(id);
            return store.GetSession(id)!;
        }
    }

    public void Pause(long id)
    {
        lock (_gate)
        {
            var s = Require(id);
            if (s.State != SessionState.Running)
            {
                throw new InvalidOperationException("기록 중인 세션만 일시정지할 수 있습니다.");
            }

            SampleEdge();
            store.CloseSegment(id, time.GetUtcNow(), SegmentEndReasons.Paused, SessionState.Paused, null, null);
            SessionChanged?.Invoke(id);
        }
    }

    /// <summary>Resumes a paused session, or continues a session interrupted by an unclean app end (a new segment starts now).</summary>
    public void Resume(long id)
    {
        lock (_gate)
        {
            var s = Require(id);
            if (s.State is not (SessionState.Paused or SessionState.Interrupted))
            {
                throw new InvalidOperationException("일시정지 또는 비정상 종료 후 복구된 세션만 재개할 수 있습니다.");
            }

            if (s.Source == SessionSource.Manual)
            {
                throw new InvalidOperationException("직접 입력한 세션은 재개할 수 없습니다.");
            }

            if (s.IsMock != isMock())
            {
                throw new InvalidOperationException(s.IsMock
                    ? "MOCK 센서로 기록한 세션은 MOCK 모드에서만 재개할 수 있습니다."
                    : "실제 센서로 기록한 세션은 MOCK 모드에서 재개할 수 없습니다.");
            }

            var running = store.ListSessions(new SessionQuery { State = SessionState.Running });
            if (running.FirstOrDefault(o => o.Id != id && Overlaps(o.DeviceIndices, s.DeviceIndices)) is { } other)
            {
                throw new InvalidOperationException($"같은 GPU를 기록 중인 세션(#{other.Id})이 있습니다.");
            }

            store.OpenSegment(id, time.GetUtcNow(), runId());
            SampleEdge();
            SessionChanged?.Invoke(id);
        }
    }

    /// <summary>Ends the session (<paramref name="aborted"/>: the work failed or was cancelled) and saves its calculation.</summary>
    public SessionAccountingResult Stop(long id, bool aborted = false)
    {
        lock (_gate)
        {
            var s = Require(id);
            if (s.State is SessionState.Ended or SessionState.Aborted)
            {
                throw new InvalidOperationException("이미 종료된 세션입니다.");
            }

            var now = time.GetUtcNow();
            if (s.State == SessionState.Running)
            {
                SampleEdge();
                now = time.GetUtcNow();
            }

            var segments = store.ListSegments(id);
            var end = s.State == SessionState.Running ? now : segments.Max(g => g.End) ?? now;
            store.CloseSegment(id, now, aborted ? SegmentEndReasons.Aborted : SegmentEndReasons.Stopped,
                aborted ? SessionState.Aborted : SessionState.Ended, end, aborted ? SegmentEndReasons.Aborted : SegmentEndReasons.Stopped);
            _log.Info("session", $"세션 #{id} {(aborted ? "중단" : "종료")}");
            var result = Recalculate(id, aborted ? "세션 중단" : "세션 종료").Result;
            SessionChanged?.Invoke(id);
            return result;
        }
    }

    /// <summary>The app is closing: running sessions are paused (not ended) so they can be resumed next time.</summary>
    public void PauseAllForExit(string reason = SegmentEndReasons.AppExit)
    {
        lock (_gate)
        {
            foreach (var s in store.ListSessions(new SessionQuery { State = SessionState.Running }))
            {
                try
                {
                    store.CloseSegment(s.Id, time.GetUtcNow(), reason, SessionState.Paused, null, null);
                    Recalculate(s.Id, "앱 종료로 일시정지");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log.Error("session", $"종료 시 세션 #{s.Id} 일시정지 실패 (다음 실행에서 복구됩니다)", ex);
                }
            }
        }
    }

    /// <summary>A past session entered by hand (for example with a wall meter reading). Recorded samples in its time range are used if present.</summary>
    public WorkSession CreateManual(long workloadId, string? title, DateTimeOffset start, DateTimeOffset end, double? wallMeterKwh, string? notes = null,
        bool executionConfirmed = true)
    {
        if (end < start)
        {
            throw new ArgumentException("종료 시각이 시작 시각보다 빠릅니다.");
        }

        if (end > time.GetUtcNow().AddMinutes(1))
        {
            throw new ArgumentException("직접 입력 세션은 이미 끝난 구간만 기록할 수 있습니다.");
        }

        if (wallMeterKwh is < 0 || wallMeterKwh is double k && !double.IsFinite(k))
        {
            throw new ArgumentException("전력계 값은 0 이상이어야 합니다.");
        }

        if (store.GetWorkload(workloadId) is null)
        {
            throw new InvalidOperationException("작업을 찾을 수 없습니다.");
        }

        var id = store.CreateSession(new WorkSession
        {
            WorkloadId = workloadId,
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            State = SessionState.Ended,
            Source = SessionSource.Manual,
            Started = start,
            Ended = end,
            EndReason = SegmentEndReasons.Manual,
            IsMock = false,
            ExecutionConfirmed = executionConfirmed,
            WallMeterKwh = wallMeterKwh,
            EnergyBasis = wallMeterKwh is not null ? EnergyBasis.WallMeter : null,
        }, end, SegmentEndReasons.Manual);
        Recalculate(id, "직접 입력");
        SessionChanged?.Invoke(id);
        return store.GetSession(id)!;
    }

    public void UpdateDetails(WorkSession edited)
    {
        var current = Require(edited.Id);
        store.UpdateSessionDetails(edited with { State = current.State, Started = current.Started, Ended = current.Ended, RunId = current.RunId, IsMock = current.IsMock, Source = current.Source });
        Recalculate(edited.Id, "세션 설정 변경");
        SessionChanged?.Invoke(edited.Id);
    }

    public void Delete(long id)
    {
        var s = Require(id);
        if (s.State == SessionState.Running)
        {
            throw new InvalidOperationException("기록 중인 세션은 삭제할 수 없습니다. 먼저 종료하세요.");
        }

        store.DeleteSession(id);
        _log.Info("session", $"세션 #{id} 삭제 (사용자 확인)");
        SessionChanged?.Invoke(id);
    }

    // ------------------------------------------------------------------ accounting

    public SessionAccountingInput BuildInput(long id, AccountingProfile? profile = null)
    {
        var session = Require(id);
        profile ??= store.GetProfile();
        var segments = store.ListSegments(id);
        var now = time.GetUtcNow();
        var samples = new List<GpuProfit.Accounting.Energy.PowerSample>();
        if (segments.Count > 0)
        {
            var margin = TimeSpan.FromSeconds(profile.MaxSampleGapSeconds);
            var last = segments.Any(g => g.End is null) ? now : segments.Max(g => g.End!.Value);
            samples = store.LoadPowerSamples(segments.Min(g => g.Start) - margin, last + margin, session.IsMock);
        }

        return new SessionAccountingInput
        {
            Session = session,
            Workload = store.GetWorkload(session.WorkloadId),
            Segments = segments,
            Samples = samples,
            Profile = profile,
            Revenue = store.ListRevenue(id),
            Costs = store.ListCosts(),
            Equipment = store.ListEquipment(),
            Rates = store.ListRates(),
            Now = now,
        };
    }

    public SessionAccountingResult Compute(long id, AccountingProfile? profile = null) => ProfitabilityEngine.Compute(BuildInput(id, profile));

    /// <summary>Recalculates and saves the result when its inputs changed. Returns the result and whether a new history row was saved.</summary>
    public (SessionAccountingResult Result, bool Changed) Recalculate(long id, string reason)
    {
        var result = Compute(id);
        var changed = store.SaveResult(result, reason);
        return (result, changed);
    }

    /// <summary>Recalculates every closed session (after a tariff or assumption change). Returns how many results changed.</summary>
    public int RecalculateAll(string reason, CancellationToken ct = default)
    {
        var profile = store.GetProfile();
        var changed = 0;
        foreach (var s in store.ListSessions(new SessionQuery { Limit = 100_000 }))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (store.SaveResult(Compute(s.Id, profile), reason))
                {
                    changed++;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                _log.Error("session", $"세션 #{s.Id} 재계산 실패", ex);
            }
        }

        return changed;
    }

    private WorkSession Require(long id) => store.GetSession(id) ?? throw new InvalidOperationException($"세션 #{id}을(를) 찾을 수 없습니다.");

    private void SampleEdge()
    {
        try
        {
            sampleNow?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("session", "경계 샘플 실패", ex);
        }
    }

    internal static bool Overlaps(IReadOnlyList<int>? a, IReadOnlyList<int>? b)
        => a is not { Count: > 0 } || b is not { Count: > 0 } || a.Intersect(b).Any();
}
