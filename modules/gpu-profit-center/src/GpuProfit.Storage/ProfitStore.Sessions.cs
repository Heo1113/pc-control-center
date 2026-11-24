using System.Globalization;
using GpuProfit.Core.Domain;
using Microsoft.Data.Sqlite;
using static LocalAiPerf.Storage.SqliteHelpers;

namespace GpuProfit.Storage;

public sealed partial class ProfitStore
{
    // ------------------------------------------------------------------ workloads

    public long AddWorkload(Workload w)
    {
        Guard(w.Name, w.Notes);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        return Write(c => ScalarLong(c, """
            INSERT INTO workloads (name, type, notes, archived, created_ms, updated_ms) VALUES (@n, @t, @x, @a, @c, @c) RETURNING id;
            """, null, ("@n", w.Name.Trim()), ("@t", (int)w.Type), ("@x", w.Notes), ("@a", w.Archived ? 1 : 0), ("@c", now)));
    }

    public void UpdateWorkload(Workload w)
    {
        Guard(w.Name, w.Notes);
        Write(c => Execute(c, "UPDATE workloads SET name = @n, type = @t, notes = @x, archived = @a, updated_ms = @u WHERE id = @id;", null,
            ("@n", w.Name.Trim()), ("@t", (int)w.Type), ("@x", w.Notes), ("@a", w.Archived ? 1 : 0), ("@u", _time.GetUtcNow().ToUnixTimeMilliseconds()), ("@id", w.Id)));
    }

    public IReadOnlyList<Workload> ListWorkloads(bool includeArchived = true)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = $"SELECT id, name, type, notes, archived, created_ms, updated_ms FROM workloads {(includeArchived ? "" : "WHERE archived = 0")} ORDER BY archived, name;";
        var list = new List<Workload>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Workload
            {
                Id = r.GetInt64(0), Name = r.GetString(1), Type = (WorkloadType)r.GetInt32(2), Notes = S(r, 3), Archived = r.GetInt64(4) != 0,
                Created = At(r.GetInt64(5)), Updated = At(r.GetInt64(6)),
            });
        }

        return list;
    }

    public Workload? GetWorkload(long id) => ListWorkloads().FirstOrDefault(w => w.Id == id);

    // ------------------------------------------------------------------ sessions

    /// <summary>Creates a session with its first segment (open when <paramref name="firstSegmentEnd"/> is null) in one transaction.</summary>
    public long CreateSession(WorkSession s, DateTimeOffset? firstSegmentEnd = null, string? firstSegmentEndReason = null)
    {
        Guard(s.Title, s.Notes);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        return WriteTx((c, tx) =>
        {
            var id = ScalarLong(c, """
                INSERT INTO sessions (workload_id, title, notes, state, source, started_ms, ended_ms, end_reason, run_id, is_mock, devices,
                    execution_confirmed, no_revenue, energy_basis, extra_system_w, idle_gpu_w, unobserved_policy, rate_krw_kwh, wall_meter_kwh,
                    work_hours, created_ms, updated_ms)
                VALUES (@w, @ti, @no, @st, @src, @s, @e, @er, @run, @m, @dev, @ec, @nr, @eb, @xs, @ig, @up, @rate, @wm, @wh, @c, @c) RETURNING id;
                """, tx, SessionParameters(s, now));
            Execute(c, "INSERT INTO session_segments (session_id, start_ms, end_ms, end_reason, run_id) VALUES (@id, @s, @e, @r, @run);", tx,
                ("@id", id), ("@s", s.Started.ToUnixTimeMilliseconds()), ("@e", ToMs(firstSegmentEnd)), ("@r", firstSegmentEndReason), ("@run", s.RunId));
            return id;
        });
    }

    private static (string, object?)[] SessionParameters(WorkSession s, long now) =>
    [
        ("@w", s.WorkloadId), ("@ti", s.Title), ("@no", s.Notes), ("@st", (int)s.State), ("@src", (int)s.Source), ("@s", s.Started.ToUnixTimeMilliseconds()),
        ("@e", ToMs(s.Ended)), ("@er", s.EndReason), ("@run", s.RunId), ("@m", s.IsMock ? 1 : 0),
        ("@dev", s.DeviceIndices is { Count: > 0 } d ? string.Join(",", d.Select(i => i.ToString(CultureInfo.InvariantCulture))) : null),
        ("@ec", s.ExecutionConfirmed ? 1 : 0), ("@nr", s.NoRevenueConfirmed ? 1 : 0), ("@eb", (int?)s.EnergyBasis), ("@xs", s.ExtraSystemPowerW),
        ("@ig", s.IdleGpuPowerW), ("@up", (int?)s.UnobservedPolicy), ("@rate", Dec(s.ElectricityRateKrwPerKwh)), ("@wm", s.WallMeterKwh),
        ("@wh", s.WorkHoursOverride), ("@c", now),
    ];

    /// <summary>Saves the user-editable fields of a session (not its state or segments).</summary>
    public void UpdateSessionDetails(WorkSession s)
    {
        Guard(s.Title, s.Notes);
        Write(c => Execute(c, """
            UPDATE sessions SET workload_id = @w, title = @ti, notes = @no, devices = @dev, execution_confirmed = @ec, no_revenue = @nr,
                energy_basis = @eb, extra_system_w = @xs, idle_gpu_w = @ig, unobserved_policy = @up, rate_krw_kwh = @rate, wall_meter_kwh = @wm,
                work_hours = @wh, updated_ms = @c
            WHERE id = @id;
            """, null, [.. SessionParameters(s, _time.GetUtcNow().ToUnixTimeMilliseconds()), ("@id", s.Id)]));
    }

    public void SetSessionState(long id, SessionState state, DateTimeOffset? ended, string? endReason) => Write(c => Execute(c,
        "UPDATE sessions SET state = @st, ended_ms = @e, end_reason = @r, updated_ms = @u WHERE id = @id;", null,
        ("@st", (int)state), ("@e", ToMs(ended)), ("@r", endReason), ("@u", _time.GetUtcNow().ToUnixTimeMilliseconds()), ("@id", id)));

    /// <summary>Closes the open segment of a session (if any) and sets the new state, in one transaction.</summary>
    public void CloseSegment(long sessionId, DateTimeOffset at, string reason, SessionState newState, DateTimeOffset? sessionEnded, string? endReason)
        => WriteTx((c, tx) =>
        {
            Execute(c, "UPDATE session_segments SET end_ms = MAX(start_ms, @t), end_reason = @r WHERE session_id = @id AND end_ms IS NULL;", tx,
                ("@t", at.ToUnixTimeMilliseconds()), ("@r", reason), ("@id", sessionId));
            Execute(c, "UPDATE sessions SET state = @st, ended_ms = @e, end_reason = @er, updated_ms = @u WHERE id = @id;", tx,
                ("@st", (int)newState), ("@e", ToMs(sessionEnded)), ("@er", endReason), ("@u", _time.GetUtcNow().ToUnixTimeMilliseconds()), ("@id", sessionId));
            return 0;
        });

    /// <summary>Opens a new segment (resume) and marks the session running, in one transaction. Fails if a segment is already open.</summary>
    public void OpenSegment(long sessionId, DateTimeOffset at, long? runId) => WriteTx((c, tx) =>
    {
        if (ScalarLong(c, "SELECT COUNT(*) FROM session_segments WHERE session_id = @id AND end_ms IS NULL;", tx, ("@id", sessionId)) > 0)
        {
            throw new InvalidOperationException("이미 기록 중인 구간이 있습니다.");
        }

        Execute(c, "INSERT INTO session_segments (session_id, start_ms, run_id) VALUES (@id, @s, @run);", tx,
            ("@id", sessionId), ("@s", at.ToUnixTimeMilliseconds()), ("@run", runId));
        Execute(c, "UPDATE sessions SET state = @st, ended_ms = NULL, end_reason = NULL, run_id = @run, updated_ms = @u WHERE id = @id;", tx,
            ("@st", (int)SessionState.Running), ("@run", runId), ("@u", _time.GetUtcNow().ToUnixTimeMilliseconds()), ("@id", sessionId));
        return 0;
    });

    /// <summary>Replaces the segments of a manual (user-entered) session.</summary>
    public void ReplaceManualSegments(long sessionId, IReadOnlyList<SessionSegment> segments) => WriteTx((c, tx) =>
    {
        if (ScalarLong(c, "SELECT source FROM sessions WHERE id = @id;", tx, ("@id", sessionId)) != (long)SessionSource.Manual)
        {
            throw new InvalidOperationException("직접 입력한 세션의 구간만 고칠 수 있습니다. 기록된 세션의 구간은 측정 근거이므로 바꾸지 않습니다.");
        }

        Execute(c, "DELETE FROM session_segments WHERE session_id = @id;", tx, ("@id", sessionId));
        foreach (var g in segments)
        {
            Execute(c, "INSERT INTO session_segments (session_id, start_ms, end_ms, end_reason) VALUES (@id, @s, @e, @r);", tx,
                ("@id", sessionId), ("@s", g.Start.ToUnixTimeMilliseconds()), ("@e", ToMs(g.End)), ("@r", g.EndReason ?? SegmentEndReasons.Manual));
        }

        var start = segments.Count > 0 ? segments.Min(g => g.Start) : (DateTimeOffset?)null;
        var end = segments.Count > 0 ? segments.Max(g => g.End) : null;
        Execute(c, "UPDATE sessions SET started_ms = COALESCE(@s, started_ms), ended_ms = @e, updated_ms = @u WHERE id = @id;", tx,
            ("@s", ToMs(start)), ("@e", ToMs(end)), ("@u", _time.GetUtcNow().ToUnixTimeMilliseconds()), ("@id", sessionId));
        return 0;
    });

    public WorkSession? GetSession(long id) => ListSessions(new SessionQuery { Limit = 1 }, id).FirstOrDefault();

    public IReadOnlyList<WorkSession> ListSessions(SessionQuery query) => ListSessions(query, null);

    private List<WorkSession> ListSessions(SessionQuery query, long? id)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.workload_id, s.title, s.notes, s.state, s.source, s.started_ms, s.ended_ms, s.end_reason, s.run_id, s.is_mock, s.devices,
                   s.execution_confirmed, s.no_revenue, s.energy_basis, s.extra_system_w, s.idle_gpu_w, s.unobserved_policy, s.rate_krw_kwh,
                   s.wall_meter_kwh, s.work_hours, s.created_ms, s.updated_ms
            FROM sessions s JOIN workloads w ON w.id = s.workload_id
            WHERE (@id IS NULL OR s.id = @id)
              AND (@w IS NULL OR s.workload_id = @w)
              AND (@type IS NULL OR w.type = @type)
              AND (@st IS NULL OR s.state = @st)
              AND (@mock IS NULL OR s.is_mock = @mock)
              AND (@to IS NULL OR s.started_ms <= @to)
              AND (@from IS NULL OR COALESCE(s.ended_ms, @now) >= @from)
              AND (@q IS NULL OR s.title LIKE @q ESCAPE '\' OR s.notes LIKE @q ESCAPE '\' OR w.name LIKE @q ESCAPE '\' OR CAST(s.id AS TEXT) = @qraw)
            ORDER BY s.started_ms DESC LIMIT @limit;
            """;
        var text = string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim();
        command.Parameters.AddWithValue("@id", (object?)id ?? DBNull.Value);
        command.Parameters.AddWithValue("@w", (object?)query.WorkloadId ?? DBNull.Value);
        command.Parameters.AddWithValue("@type", (object?)(int?)query.Type ?? DBNull.Value);
        command.Parameters.AddWithValue("@st", (object?)(int?)query.State ?? DBNull.Value);
        command.Parameters.AddWithValue("@mock", query.IsMock is bool m ? m ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("@from", (object?)ToMs(query.From) ?? DBNull.Value);
        command.Parameters.AddWithValue("@to", (object?)ToMs(query.To) ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", _time.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@q", text is null ? DBNull.Value : "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%");
        command.Parameters.AddWithValue("@qraw", (object?)text?.TrimStart('#') ?? DBNull.Value);
        command.Parameters.AddWithValue("@limit", Math.Clamp(query.Limit, 1, 100_000));
        var list = new List<WorkSession>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new WorkSession
            {
                Id = r.GetInt64(0),
                WorkloadId = r.GetInt64(1),
                Title = S(r, 2),
                Notes = S(r, 3),
                State = (SessionState)r.GetInt32(4),
                Source = (SessionSource)r.GetInt32(5),
                Started = At(r.GetInt64(6)),
                Ended = Ms(L(r, 7)),
                EndReason = S(r, 8),
                RunId = L(r, 9),
                IsMock = r.GetInt64(10) != 0,
                DeviceIndices = S(r, 11) is { Length: > 0 } dev ? dev.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList() : null,
                ExecutionConfirmed = r.GetInt64(12) != 0,
                NoRevenueConfirmed = r.GetInt64(13) != 0,
                EnergyBasis = I(r, 14) is int eb ? (EnergyBasis)eb : null,
                ExtraSystemPowerW = D(r, 15),
                IdleGpuPowerW = D(r, 16),
                UnobservedPolicy = I(r, 17) is int up ? (UnobservedPolicy)up : null,
                ElectricityRateKrwPerKwh = Dec(S(r, 18)),
                WallMeterKwh = D(r, 19),
                WorkHoursOverride = D(r, 20),
                Created = At(r.GetInt64(21)),
                Updated = At(r.GetInt64(22)),
            });
        }

        return list;
    }

    public IReadOnlyList<SessionSegment> ListSegments(long sessionId)
    {
        using var reader = OpenReader();
        return ReadSegments(reader, null, "WHERE session_id = @id", ("@id", sessionId));
    }

    /// <summary>Segments of every session (for period totals); keyed by session.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<SessionSegment>> ListAllSegments()
    {
        using var reader = OpenReader();
        return ReadSegments(reader, null, "").GroupBy(g => g.SessionId).ToDictionary(g => g.Key, g => (IReadOnlyList<SessionSegment>)g.ToList());
    }

    private static List<SessionSegment> ReadSegments(SqliteConnection c, SqliteTransaction? tx, string where, params (string, object?)[] parameters)
    {
        using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT id, session_id, start_ms, end_ms, end_reason FROM session_segments {where} ORDER BY start_ms;";
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var list = new List<SessionSegment>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SessionSegment { Id = r.GetInt64(0), SessionId = r.GetInt64(1), Start = At(r.GetInt64(2)), End = Ms(L(r, 3)), EndReason = S(r, 4) });
        }

        return list;
    }

    /// <summary>
    /// After an unclean end: every session still recording in a run other than <paramref name="currentRunId"/> has its
    /// open segment closed at the last moment that run was alive, and becomes <see cref="SessionState.Interrupted"/>.
    /// Nothing is deleted; the user can resume the session.
    /// </summary>
    public IReadOnlyList<WorkSession> RecoverInterruptedSessions(long? currentRunId) => WriteTx((c, tx) =>
    {
        using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT g.id, g.session_id, g.start_ms, g.run_id FROM session_segments g
            WHERE g.end_ms IS NULL AND (@run IS NULL OR g.run_id IS NULL OR g.run_id <> @run);
            """;
        command.Parameters.AddWithValue("@run", (object?)currentRunId ?? DBNull.Value);
        var open = new List<(long Segment, long Session, long Start, long? Run)>();
        using (var r = command.ExecuteReader())
        {
            while (r.Read())
            {
                open.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), L(r, 3)));
            }
        }

        var sessions = new HashSet<long>();
        foreach (var (segment, session, start, run) in open)
        {
            var end = run is long id ? LastAlive(c, tx, id, start) : start;
            Execute(c, "UPDATE session_segments SET end_ms = @e, end_reason = @r WHERE id = @id;", tx,
                ("@e", Math.Max(start, end)), ("@r", SegmentEndReasons.RecoveredAfterCrash), ("@id", segment));
            Execute(c, "UPDATE sessions SET state = @st, ended_ms = @e, end_reason = @r WHERE id = @id;", tx,
                ("@st", (int)SessionState.Interrupted), ("@e", Math.Max(start, end)), ("@r", SegmentEndReasons.RecoveredAfterCrash), ("@id", session));
            sessions.Add(session);
        }

        return (IReadOnlyList<long>)[.. sessions];
    }).Select(id => GetSession(id)!).ToList();

    /// <summary>Deletes a session with its segments, entries and results (the user confirmed). Sensor samples are kept.</summary>
    public void DeleteSession(long id) => WriteTx((c, tx) => Execute(c, "DELETE FROM sessions WHERE id = @id;", tx, ("@id", id)));

    private static void Guard(params string?[] texts)
    {
        foreach (var t in texts)
        {
            if (SensitiveTextGuard.Check(t) is { } reason)
            {
                throw new ArgumentException(reason);
            }
        }
    }
}
