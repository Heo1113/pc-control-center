using System.Globalization;
using System.Text.Json;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Sessions;
using Microsoft.Data.Sqlite;
using static LocalAiPerf.Storage.SqliteHelpers;

namespace LocalAiPerf.Storage;

/// <summary>
/// The app's own SQLite database. WAL journal with synchronous=FULL: every committed tick survives an app crash
/// and a crash never corrupts earlier data. Only this app's data folder is written; runtime files are never touched.
/// </summary>
public sealed class TelemetryStore : IDisposable
{
    private readonly SqliteConnection _writer;
    private readonly object _writeGate = new();
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private bool _disposed;

    private TelemetryStore(string path, SqliteConnection writer, string? notice, TimeProvider time, IAppLogger log)
    {
        DatabasePath = path;
        _writer = writer;
        OpenNotice = notice;
        _time = time;
        _log = log;
    }

    public string DatabasePath { get; }

    /// <summary>Set when the store had to recover from a problem while opening (for example a quarantined corrupt file).</summary>
    public string? OpenNotice { get; }

    /// <summary>
    /// Opens or creates the database. A corrupt file is moved aside (never deleted) and a new database is created.
    /// </summary>
    public static TelemetryStore Open(string path, TimeProvider? time = null, IAppLogger? log = null)
    {
        time ??= TimeProvider.System;
        log ??= NullLogger.Instance;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            return OpenCore(path, null, time, log);
        }
        catch (Exception ex) when (IsCorruption(ex))
        {
            var aside = Quarantine(path, "corrupt", time);
            var notice = $"데이터베이스 파일이 손상되어 '{Path.GetFileName(aside)}'(으)로 옮겨 보관하고 새 데이터베이스를 만들었습니다. ({ex.Message})";
            log.Error("storage", notice, ex);
            return OpenCore(path, notice, time, log);
        }
    }

    private static TelemetryStore OpenCore(string path, string? notice, TimeProvider time, IAppLogger log)
    {
        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        if (!HasValidHeader(path))
        {
            throw new StorageCorruptException("SQLite 파일 헤더가 올바르지 않습니다.");
        }

        var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadWriteCreate, pooling: false));
        try
        {
            connection.Open();
            Exec(connection, "PRAGMA busy_timeout=10000;");

            // Read-only checks first: nothing may be written into a file this version does not own.
            _ = ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master;"); // reads the schema: detects most corruption cheaply
            var version = ScalarLong(connection, "PRAGMA user_version;");
            if (version > Schema.CurrentVersion)
            {
                throw new StorageVersionException(
                    $"이 데이터베이스는 더 새로운 버전의 앱에서 만들어졌습니다 (스키마 {version}, 지원 {Schema.CurrentVersion}). 데이터를 보호하기 위해 열지 않습니다.");
            }

            if (isNew)
            {
                Exec(connection, "PRAGMA auto_vacuum=INCREMENTAL;");
            }

            Exec(connection, "PRAGMA journal_mode=WAL;");
            Exec(connection, "PRAGMA synchronous=FULL;");
            if (version < Schema.CurrentVersion)
            {
                // Additive migrations only: existing rows are never rewritten.
                using var tx = connection.BeginTransaction();
                if (version < 1)
                {
                    Exec(connection, Schema.V1, tx);
                }

                Exec(connection, $"PRAGMA user_version={Schema.CurrentVersion};", tx);
                tx.Commit();
            }

            if (!isNew && ScalarLong(connection, "SELECT COUNT(*) FROM sessions WHERE ended_ms IS NULL;") > 0)
            {
                // The previous run did not end cleanly: verify the file before writing more data into it.
                var check = Scalar(connection, "PRAGMA quick_check;") as string;
                if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new StorageCorruptException($"무결성 검사 실패: {check}");
                }
            }
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new TelemetryStore(path, connection, notice, time, log);
    }

    /// <summary>Moves the database and its WAL/SHM side files aside. Returns the new main file path.</summary>
    internal static string Quarantine(string path, string label, TimeProvider time)
    {
        SqliteConnection.ClearAllPools();
        var stamp = time.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var aside = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}.{label}-{stamp}{Path.GetExtension(path)}");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(path + suffix))
            {
                File.Move(path + suffix, aside + suffix, overwrite: false);
            }
        }

        return aside;
    }

    // ------------------------------------------------------------------ sessions

    public SessionInfo StartSession(SessionStartRequest request)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            var uid = Guid.NewGuid().ToString("N");
            var id = ScalarLong(_writer, """
                INSERT INTO sessions (uid, name, notes, tags, kind, started_ms, sample_interval_ms, app_version, host_json,
                                      runtime_kind, runtime_endpoint, is_mock)
                VALUES (@uid, @name, @notes, @tags, @kind, @started, @interval, @app, @host, @rk, @re, @mock)
                RETURNING id;
                """, null,
                ("@uid", uid), ("@name", request.Name), ("@notes", request.Notes), ("@tags", request.Tags), ("@kind", (int)request.Kind),
                ("@started", request.StartedAt.ToUnixTimeMilliseconds()), ("@interval", request.SampleIntervalMs),
                ("@app", request.AppVersion), ("@host", request.HostJson), ("@rk", request.RuntimeKind), ("@re", request.RuntimeEndpoint),
                ("@mock", request.IsMock ? 1 : 0));
            InsertEvent(new EventRecord(id, request.StartedAt, EventSeverity.Info, EventCategories.Session,
                $"세션 시작: {request.Name} ({(request.Kind == SessionKind.Manual ? "수동" : "자동")}, 간격 {request.SampleIntervalMs} ms)"), null);
            return GetSession(id)!;
        }
    }

    public void EndSession(long sessionId, DateTimeOffset endedAt, string reason)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            using var tx = _writer.BeginTransaction();
            Execute(_writer, "UPDATE sessions SET ended_ms = @ended, end_reason = @reason WHERE id = @id AND ended_ms IS NULL;", tx,
                ("@ended", endedAt.ToUnixTimeMilliseconds()), ("@reason", reason), ("@id", sessionId));
            InsertEvent(new EventRecord(sessionId, endedAt, EventSeverity.Info, EventCategories.Session,
                $"세션 종료: {SessionEndReasons.ToKorean(reason)}"), tx);
            tx.Commit();
        }
    }

    /// <summary>Closes sessions left open by a crash, using the last recorded sample as the end time.</summary>
    public int RecoverUnfinishedSessions(string reason = SessionEndReasons.RecoveredAfterCrash)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            using var tx = _writer.BeginTransaction();
            var ids = new List<long>();
            using (var command = _writer.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "SELECT id FROM sessions WHERE ended_ms IS NULL;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ids.Add(reader.GetInt64(0));
                }
            }

            foreach (var id in ids)
            {
                Execute(_writer, """
                    UPDATE sessions SET ended_ms = COALESCE(last_sample_ms, started_ms), end_reason = @reason WHERE id = @id;
                    """, tx, ("@reason", reason), ("@id", id));
                InsertEvent(new EventRecord(id, _time.GetUtcNow(), EventSeverity.Warning, EventCategories.Session,
                    "이전 실행이 정상 종료되지 않아 마지막 샘플 시각으로 세션을 닫았습니다. 그 전까지 기록된 데이터는 보존됩니다."), tx);
            }

            tx.Commit();
            return ids.Count;
        }
    }

    public void UpdateSessionMetadata(long sessionId, string name, string? notes, string? tags)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            Execute(_writer, "UPDATE sessions SET name = @name, notes = @notes, tags = @tags WHERE id = @id;", null,
                ("@name", string.IsNullOrWhiteSpace(name) ? "이름 없는 세션" : name.Trim()), ("@notes", notes), ("@tags", tags), ("@id", sessionId));
        }
    }

    /// <summary>Deletes one finished session and all its samples. The active session cannot be deleted.</summary>
    public bool DeleteSession(long sessionId)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            return Execute(_writer, "DELETE FROM sessions WHERE id = @id AND ended_ms IS NOT NULL;", null, ("@id", sessionId)) > 0;
        }
    }

    public SessionInfo? GetSession(long sessionId)
        => QuerySessions("WHERE s.id = @id", 1, ("@id", sessionId)).FirstOrDefault();

    public IReadOnlyList<SessionInfo> ListSessions(SessionFilter? filter = null)
    {
        filter ??= new SessionFilter();
        var text = string.IsNullOrWhiteSpace(filter.Text) ? null : $"%{filter.Text.Trim()}%";
        return QuerySessions("""
            WHERE (@text IS NULL OR s.name LIKE @text OR s.notes LIKE @text OR s.tags LIKE @text
                   OR EXISTS (SELECT 1 FROM runtime_info ri WHERE ri.session_id = s.id AND (ri.model_id LIKE @text OR ri.engine_version LIKE @text)))
              AND (@from IS NULL OR COALESCE(s.ended_ms, s.last_sample_ms, s.started_ms) >= @from)
              AND (@to IS NULL OR s.started_ms <= @to)
            """, Math.Clamp(filter.Limit, 1, 10_000),
            ("@text", text), ("@from", filter.From?.ToUnixTimeMilliseconds()), ("@to", filter.To?.ToUnixTimeMilliseconds()));
    }

    private List<SessionInfo> QuerySessions(string where, int limit, params (string Name, object? Value)[] parameters)
    {
        using var connection = OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.uid, s.name, s.notes, s.tags, s.kind, s.started_ms, s.ended_ms, s.end_reason, s.last_sample_ms,
                   s.sample_interval_ms, s.app_version, s.host_json, s.runtime_kind, s.runtime_endpoint, s.sample_count, s.is_mock,
                   (SELECT ri.model_id FROM runtime_info ri WHERE ri.session_id = s.id AND ri.model_id IS NOT NULL ORDER BY ri.t_ms DESC LIMIT 1),
                   (SELECT COUNT(DISTINCT ri.model_id) FROM runtime_info ri WHERE ri.session_id = s.id),
                   (SELECT ri.engine_version FROM runtime_info ri WHERE ri.session_id = s.id AND ri.engine_version IS NOT NULL ORDER BY ri.t_ms DESC LIMIT 1),
                   (SELECT COUNT(DISTINCT ri.engine_version) FROM runtime_info ri WHERE ri.session_id = s.id)
            FROM sessions s
            {where}
            ORDER BY s.started_ms DESC
            LIMIT {limit};
            """;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var list = new List<SessionInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var model = S(reader, 17);
            var modelCount = reader.GetInt64(18);
            var engine = S(reader, 19);
            var engineCount = reader.GetInt64(20);
            list.Add(new SessionInfo
            {
                Id = reader.GetInt64(0),
                Uid = reader.GetString(1),
                Name = reader.GetString(2),
                Notes = S(reader, 3),
                Tags = S(reader, 4),
                Kind = (SessionKind)reader.GetInt32(5),
                StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                EndedAt = L(reader, 7) is long ended ? DateTimeOffset.FromUnixTimeMilliseconds(ended) : null,
                EndReason = S(reader, 8),
                LastSampleAt = L(reader, 9) is long last ? DateTimeOffset.FromUnixTimeMilliseconds(last) : null,
                SampleIntervalMs = reader.GetInt32(10),
                AppVersion = reader.GetString(11),
                HostJson = S(reader, 12),
                RuntimeKind = S(reader, 13),
                RuntimeEndpoint = S(reader, 14),
                SampleCount = reader.GetInt64(15),
                IsMock = reader.GetInt64(16) != 0,
                ModelSummary = model is null ? null : modelCount > 1 ? $"{model} 외 {modelCount - 1}개" : model,
                EngineVersionSummary = engine is null ? null : engineCount > 1 ? $"{engine} 외 {engineCount - 1}개" : engine,
            });
        }

        return list;
    }

    // ------------------------------------------------------------------ writes

    /// <summary>Writes one tick (system, GPU, runtime rows, new requests, condition changes, events) in one transaction.</summary>
    public void WriteFrame(FrameWrite write)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            using var tx = _writer.BeginTransaction();
            var frame = write.Frame;
            var t = frame.Timestamp.ToUnixTimeMilliseconds();
            var system = frame.Hardware.System;
            Execute(_writer, """
                INSERT OR REPLACE INTO system_samples (session_id, t_ms, cpu_util, ram_used, ram_total, flags)
                VALUES (@s, @t, @cpu, @used, @total, @flags);
                """, tx, ("@s", write.SessionId), ("@t", t), ("@cpu", system.CpuUtilizationPercent), ("@used", system.MemoryUsedBytes),
                ("@total", system.MemoryTotalBytes), ("@flags", (int)write.Flags));

            foreach (var gpu in frame.Hardware.Gpus)
            {
                Execute(_writer, """
                    INSERT OR REPLACE INTO gpu_samples (session_id, t_ms, gpu_index, util, mem_util, temp_c, power_w, power_limit_w,
                        vram_used, vram_total, clock_gfx_mhz, clock_mem_mhz, fan_pct, pcie_tx_kbps, pcie_rx_kbps, energy_mj, throttle)
                    VALUES (@s, @t, @i, @util, @mu, @temp, @pw, @pl, @vu, @vt, @cg, @cm, @fan, @tx, @rx, @e, @th);
                    """, tx, ("@s", write.SessionId), ("@t", t), ("@i", gpu.Index), ("@util", gpu.UtilizationPercent),
                    ("@mu", gpu.MemoryControllerUtilizationPercent), ("@temp", gpu.TemperatureC), ("@pw", gpu.PowerW), ("@pl", gpu.PowerLimitW),
                    ("@vu", gpu.MemoryUsedBytes), ("@vt", gpu.MemoryTotalBytes), ("@cg", gpu.GraphicsClockMhz), ("@cm", gpu.MemoryClockMhz),
                    ("@fan", gpu.FanSpeedPercent), ("@tx", gpu.PcieTxKBps), ("@rx", gpu.PcieRxKBps), ("@e", gpu.TotalEnergyMilliJoules),
                    ("@th", gpu.ThrottleReasons is ulong th ? unchecked((long)th) : null));
            }

            if (frame.Runtime is RuntimeSnapshot rt)
            {
                var reachable = rt.IsReachable;
                Execute(_writer, """
                    INSERT OR REPLACE INTO runtime_samples (session_id, t_ms, state, error_category, model_loaded, answer_tok_s, prefill_tok_s,
                        aggregate_tok_s, active, queued, waiting, occupied_slots, slots_effective, context_max, context_used, output_counter,
                        admission_rejected, queue_timeouts, disconnect_cancels)
                    VALUES (@s, @t, @state, @err, @loaded, @ans, @pre, @agg, @act, @q, @w, @occ, @slots, @cmax, @cused, @out, @rej, @qto, @dc);
                    """, tx, ("@s", write.SessionId), ("@t", rt.Timestamp.ToUnixTimeMilliseconds()), ("@state", (int)rt.State),
                    ("@err", rt.Error is { } e ? (int)e.Category : null),
                    ("@loaded", reachable && rt.Model?.Loaded is bool loaded ? (loaded ? 1 : 0) : null),
                    ("@ans", reachable ? rt.Rates?.AnswerTokPerSec : null), ("@pre", reachable ? rt.Rates?.PrefillTokPerSec : null),
                    ("@agg", reachable ? rt.Rates?.AggregateOutputTokPerSec : null),
                    ("@act", reachable ? rt.Work?.Active : null), ("@q", reachable ? rt.Work?.Queued : null), ("@w", reachable ? rt.Work?.Waiting : null),
                    ("@occ", reachable ? rt.Work?.OccupiedSlots : null), ("@slots", reachable ? rt.Work?.SlotsEffective : null),
                    ("@cmax", reachable ? rt.Model?.ContextTokens : null), ("@cused", reachable ? rt.MaxSlotContextUsed : null),
                    ("@out", reachable ? rt.Rates?.OutputTokensCounter : null), ("@rej", reachable ? rt.Work?.AdmissionRejected : null),
                    ("@qto", reachable ? rt.Work?.QueueTimeouts : null), ("@dc", reachable ? rt.Work?.ClientDisconnectCancels : null));
            }

            foreach (var request in write.NewRequests)
            {
                Execute(_writer, """
                    INSERT OR IGNORE INTO runtime_requests (session_id, instance_id, request_id, observed_ms, started_ms, duration_s, finish,
                        prompt_tokens, reused_tokens, output_tokens, prompt_ms, decode_ms, first_token_s)
                    VALUES (@s, @inst, @id, @obs, @start, @dur, @fin, @pt, @rt, @ot, @pms, @dms, @ft);
                    """, tx, ("@s", write.SessionId), ("@inst", write.InstanceId ?? ""), ("@id", request.Id), ("@obs", t),
                    ("@start", request.StartedUnix is double started ? (long)(started * 1000) : null), ("@dur", request.DurationSeconds),
                    ("@fin", request.Finish), ("@pt", request.PromptTokens), ("@rt", request.ReusedTokens), ("@ot", request.OutputTokens),
                    ("@pms", request.PromptMs), ("@dms", request.DecodeMs), ("@ft", request.FirstEngineTokenSeconds));
            }

            if (write.Conditions is RuntimeConditions c)
            {
                Execute(_writer, """
                    INSERT INTO runtime_info (session_id, t_ms, runtime_kind, service, engine_version, instance_id, model_id, model_file,
                        context_max, slots, settings_json, fingerprint)
                    VALUES (@s, @t, @rk, @svc, @ev, @inst, @model, @file, @ctx, @slots, @settings, @fp);
                    """, tx, ("@s", write.SessionId), ("@t", t), ("@rk", c.RuntimeKind), ("@svc", c.Service), ("@ev", c.EngineVersion),
                    ("@inst", c.InstanceId), ("@model", c.ModelId), ("@file", c.ModelFileName), ("@ctx", c.ContextTokens), ("@slots", c.Slots),
                    ("@settings", c.Settings.Count == 0 ? null : JsonSerializer.Serialize(c.Settings.ToDictionary(s => s.Key, s => s.Value))),
                    ("@fp", c.Fingerprint));
            }

            foreach (var e in write.Events)
            {
                InsertEvent(e, tx);
            }

            Execute(_writer, """
                UPDATE sessions SET last_sample_ms = MAX(COALESCE(last_sample_ms, 0), @t), sample_count = sample_count + 1 WHERE id = @s;
                """, tx, ("@t", t), ("@s", write.SessionId));
            tx.Commit();
        }
    }

    public void WriteEvent(EventRecord record)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            InsertEvent(record, null);
        }
    }

    private void InsertEvent(EventRecord e, SqliteTransaction? tx)
        => Execute(_writer, "INSERT INTO events (session_id, t_ms, severity, category, message) VALUES (@s, @t, @sev, @cat, @msg);", tx,
            ("@s", e.SessionId), ("@t", e.Time.ToUnixTimeMilliseconds()), ("@sev", (int)e.Severity), ("@cat", e.Category),
            ("@msg", SecretRedactor.Redact(e.Message.Length > 1000 ? e.Message[..1000] : e.Message)));

    // ------------------------------------------------------------------ reads

    public SqliteConnection OpenReader()
    {
        ThrowIfDisposed();
        var connection = new SqliteConnection(ConnectionString(DatabasePath, SqliteOpenMode.ReadOnly));
        connection.Open();
        return connection;
    }

    public SessionData? LoadSessionData(long sessionId)
    {
        var session = GetSession(sessionId);
        if (session is null)
        {
            return null;
        }

        using var connection = OpenReader();
        var system = new List<SystemPoint>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT t_ms, cpu_util, ram_used, ram_total, flags FROM system_samples WHERE session_id = @s ORDER BY t_ms;";
            command.Parameters.AddWithValue("@s", sessionId);
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                system.Add(new SystemPoint(r.GetInt64(0), D(r, 1), L(r, 2), L(r, 3), (SampleFlags)r.GetInt32(4)));
            }
        }

        var gpu = new List<GpuPoint>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t_ms, gpu_index, util, mem_util, temp_c, power_w, power_limit_w, vram_used, vram_total, clock_gfx_mhz, clock_mem_mhz,
                       fan_pct, pcie_tx_kbps, pcie_rx_kbps, energy_mj, throttle
                FROM gpu_samples WHERE session_id = @s ORDER BY t_ms, gpu_index;
                """;
            command.Parameters.AddWithValue("@s", sessionId);
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                gpu.Add(new GpuPoint(r.GetInt64(0), r.GetInt32(1), D(r, 2), D(r, 3), D(r, 4), D(r, 5), D(r, 6), L(r, 7), L(r, 8),
                    D(r, 9), D(r, 10), D(r, 11), D(r, 12), D(r, 13), L(r, 14), L(r, 15)));
            }
        }

        var runtime = new List<RuntimePoint>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t_ms, state, error_category, model_loaded, answer_tok_s, prefill_tok_s, aggregate_tok_s, active, queued, waiting,
                       occupied_slots, slots_effective, context_max, context_used, output_counter, admission_rejected, queue_timeouts, disconnect_cancels
                FROM runtime_samples WHERE session_id = @s ORDER BY t_ms;
                """;
            command.Parameters.AddWithValue("@s", sessionId);
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                runtime.Add(new RuntimePoint(r.GetInt64(0), (RuntimeConnectionState)r.GetInt32(1),
                    I(r, 2) is int err ? (RuntimeErrorCategory)err : null, L(r, 3) is long loaded ? loaded != 0 : null,
                    D(r, 4), D(r, 5), D(r, 6), I(r, 7), I(r, 8), I(r, 9), I(r, 10), I(r, 11), L(r, 12), L(r, 13), L(r, 14), L(r, 15), L(r, 16), L(r, 17)));
            }
        }

        var requests = new List<RequestRow>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT instance_id, request_id, observed_ms, started_ms, duration_s, finish, prompt_tokens, reused_tokens, output_tokens,
                       prompt_ms, decode_ms, first_token_s
                FROM runtime_requests WHERE session_id = @s ORDER BY COALESCE(started_ms, observed_ms);
                """;
            command.Parameters.AddWithValue("@s", sessionId);
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                requests.Add(new RequestRow(r.GetString(0), r.GetString(1), r.GetInt64(2), L(r, 3), D(r, 4), S(r, 5), L(r, 6), L(r, 7), L(r, 8),
                    D(r, 9), D(r, 10), D(r, 11)));
            }
        }

        var info = new List<RuntimeInfoRow>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t_ms, runtime_kind, service, engine_version, instance_id, model_id, model_file, context_max, slots, settings_json, fingerprint
                FROM runtime_info WHERE session_id = @s ORDER BY t_ms;
                """;
            command.Parameters.AddWithValue("@s", sessionId);
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                var settings = new List<RuntimeSetting>();
                if (S(r, 9) is string json)
                {
                    try
                    {
                        foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [])
                        {
                            settings.Add(new RuntimeSetting(key, value));
                        }
                    }
                    catch (JsonException)
                    {
                        // Unreadable settings are shown as unknown rather than failing the whole session.
                    }
                }

                info.Add(new RuntimeInfoRow(r.GetInt64(0), new RuntimeConditions
                {
                    RuntimeKind = S(r, 1),
                    Service = S(r, 2),
                    EngineVersion = S(r, 3),
                    InstanceId = S(r, 4),
                    ModelId = S(r, 5),
                    ModelFileName = S(r, 6),
                    ContextTokens = L(r, 7),
                    Slots = I(r, 8),
                    Settings = settings,
                }, r.GetString(10)));
            }
        }

        return new SessionData(session, system, gpu, runtime, requests, info, ListEvents(sessionId, 10_000, connection));
    }

    public IReadOnlyList<EventRecord> ListEvents(long? sessionId, int limit = 500)
    {
        using var connection = OpenReader();
        return ListEvents(sessionId, limit, connection);
    }

    private static List<EventRecord> ListEvents(long? sessionId, int limit, SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sessionId is null
            ? "SELECT session_id, t_ms, severity, category, message FROM events ORDER BY t_ms DESC, id DESC LIMIT @limit;"
            : "SELECT session_id, t_ms, severity, category, message FROM events WHERE session_id = @s ORDER BY t_ms, id LIMIT @limit;";
        command.Parameters.AddWithValue("@s", (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@limit", limit);
        var list = new List<EventRecord>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EventRecord(L(r, 0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), (EventSeverity)r.GetInt32(2), r.GetString(3), r.GetString(4)));
        }

        return list;
    }

    // ------------------------------------------------------------------ maintenance

    public long UsedBytes()
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            return UsedBytes(_writer);
        }
    }

    private static long UsedBytes(SqliteConnection connection)
        => (ScalarLong(connection, "PRAGMA page_count;") - ScalarLong(connection, "PRAGMA freelist_count;")) * ScalarLong(connection, "PRAGMA page_size;");

    public DatabaseStats GetStats()
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            var pageSize = ScalarLong(_writer, "PRAGMA page_size;");
            var pages = ScalarLong(_writer, "PRAGMA page_count;");
            var free = ScalarLong(_writer, "PRAGMA freelist_count;");
            var oldest = Scalar(_writer, "SELECT MIN(started_ms) FROM sessions;");
            var newest = Scalar(_writer, "SELECT MAX(COALESCE(last_sample_ms, started_ms)) FROM sessions;");
            return new DatabaseStats(
                DatabasePath,
                File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0,
                File.Exists(DatabasePath + "-wal") ? new FileInfo(DatabasePath + "-wal").Length : 0,
                (pages - free) * pageSize,
                free * pageSize,
                ScalarLong(_writer, "SELECT COUNT(*) FROM sessions;"),
                ScalarLong(_writer, "SELECT COUNT(*) FROM system_samples;"),
                ScalarLong(_writer, "SELECT COUNT(*) FROM gpu_samples;"),
                ScalarLong(_writer, "SELECT COUNT(*) FROM runtime_samples;"),
                ScalarLong(_writer, "SELECT COUNT(*) FROM runtime_requests;"),
                ScalarLong(_writer, "SELECT COUNT(*) FROM events;"),
                oldest is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(oldest, CultureInfo.InvariantCulture)),
                newest is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(newest, CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>Full SQLite integrity check. Returns "ok" or the first problems found.</summary>
    public string IntegrityCheck()
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            using var command = _writer.CreateCommand();
            command.CommandText = "PRAGMA integrity_check(20);";
            using var reader = command.ExecuteReader();
            var lines = new List<string>();
            while (reader.Read())
            {
                lines.Add(reader.GetString(0));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Deletes data older than the retention period, then removes the oldest sessions while the database is above
    /// the size limit. The active session is only trimmed (oldest samples first), never deleted.
    /// </summary>
    public RetentionResult ApplyRetention(int retentionDays, long maxBytes, long? activeSessionId)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            var notes = new List<string>();
            var before = UsedBytes(_writer);
            var now = _time.GetUtcNow();
            var cutoff = (now - TimeSpan.FromDays(retentionDays)).ToUnixTimeMilliseconds();
            var active = activeSessionId ?? -1;
            var sessionsDeleted = 0;
            long rows = 0;

            using (var tx = _writer.BeginTransaction())
            {
                sessionsDeleted += Execute(_writer,
                    "DELETE FROM sessions WHERE id <> @a AND ended_ms IS NOT NULL AND COALESCE(last_sample_ms, ended_ms, started_ms) < @cutoff;",
                    tx, ("@a", active), ("@cutoff", cutoff));
                rows += TrimBefore(cutoff, "started_ms < @cutoff", tx, ("@cutoff", cutoff));
                rows += Execute(_writer, "DELETE FROM events WHERE session_id IS NULL AND t_ms < @cutoff;", tx, ("@cutoff", cutoff));
                tx.Commit();
            }

            if (sessionsDeleted > 0 || rows > 0)
            {
                notes.Add($"보존 기간({retentionDays}일)이 지난 데이터 삭제: 세션 {sessionsDeleted}개, 샘플 {rows}행");
            }

            var used = UsedBytes(_writer);
            if (used > maxBytes)
            {
                var target = (long)(maxBytes * 0.9);
                var removedForSize = 0;
                while (used > target)
                {
                    var oldest = Scalar(_writer, "SELECT id FROM sessions WHERE id <> @a ORDER BY started_ms LIMIT 1;", null, ("@a", active));
                    if (oldest is null)
                    {
                        break;
                    }

                    Execute(_writer, "DELETE FROM sessions WHERE id = @id;", null, ("@id", oldest));
                    sessionsDeleted++;
                    removedForSize++;
                    used = UsedBytes(_writer);
                }

                for (var round = 0; round < 10 && used > target && activeSessionId is long a; round++)
                {
                    var min = Scalar(_writer, "SELECT MIN(t_ms) FROM system_samples WHERE session_id = @a;", null, ("@a", a));
                    var max = Scalar(_writer, "SELECT MAX(t_ms) FROM system_samples WHERE session_id = @a;", null, ("@a", a));
                    if (min is null || max is null)
                    {
                        break;
                    }

                    var lo = Convert.ToInt64(min, CultureInfo.InvariantCulture);
                    var hi = Convert.ToInt64(max, CultureInfo.InvariantCulture);
                    var cut = lo + Math.Max(1, (hi - lo) / 10);
                    using var tx = _writer.BeginTransaction();
                    rows += TrimBefore(cut, "id = @a", tx, ("@cutoff", cut), ("@a", a));
                    tx.Commit();
                    used = UsedBytes(_writer);
                    notes.Add("저장 공간 상한 때문에 현재 기록 중인 세션의 가장 오래된 샘플 일부를 삭제했습니다.");
                }

                if (removedForSize > 0)
                {
                    notes.Add($"저장 공간 상한({maxBytes / (1024 * 1024)} MB) 초과로 가장 오래된 세션 {removedForSize}개를 삭제했습니다.");
                }
            }

            Exec(_writer, "PRAGMA incremental_vacuum;");
            Exec(_writer, "PRAGMA wal_checkpoint(TRUNCATE);");
            var after = UsedBytes(_writer);
            foreach (var note in notes)
            {
                InsertEvent(new EventRecord(null, now, EventSeverity.Info, EventCategories.Retention, note), null);
                _log.Info("storage", note);
            }

            return new RetentionResult(sessionsDeleted, rows, before, after, notes);
        }
    }

    private long TrimBefore(long cutoffMs, string sessionWhere, SqliteTransaction tx, params (string Name, object? Value)[] parameters)
    {
        long rows = 0;
        var ids = new List<long>();
        using (var command = _writer.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = $"SELECT id FROM sessions WHERE {sessionWhere};";
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        foreach (var id in ids)
        {
            foreach (var table in new[] { "system_samples", "gpu_samples", "runtime_samples", "events" })
            {
                rows += Execute(_writer, $"DELETE FROM {table} WHERE session_id = @id AND t_ms < @cut;", tx, ("@id", id), ("@cut", cutoffMs));
            }

            rows += Execute(_writer, "DELETE FROM runtime_requests WHERE session_id = @id AND observed_ms < @cut;", tx, ("@id", id), ("@cut", cutoffMs));
        }

        return rows;
    }

    /// <summary>Writes a consistent copy of the live database (SQLite online backup) to a new file.</summary>
    public void BackupTo(string destinationPath, bool overwrite = false)
    {
        var full = Path.GetFullPath(destinationPath);
        if (string.Equals(full, Path.GetFullPath(DatabasePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("백업 파일은 사용 중인 데이터베이스와 다른 파일이어야 합니다.");
        }

        if (File.Exists(full) && !overwrite)
        {
            throw new IOException($"백업 파일이 이미 있습니다: {full}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".partial";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        using (var destination = new SqliteConnection(ConnectionString(temp, SqliteOpenMode.ReadWriteCreate, pooling: false)))
        {
            destination.Open();
            lock (_writeGate)
            {
                ThrowIfDisposed();
                _writer.BackupDatabase(destination);
            }

            Exec(destination, "PRAGMA journal_mode=DELETE;");
        }

        File.Move(temp, full, overwrite);
    }

    /// <summary>Checks that a file is a readable backup of this app's database (read-only; nothing is changed).</summary>
    public static BackupValidation ValidateBackup(string path)
    {
        if (!File.Exists(path))
        {
            return new BackupValidation(false, "파일이 없습니다.", 0, 0, null, null);
        }

        try
        {
            if (!HasValidHeader(path) || new FileInfo(path).Length == 0)
            {
                return new BackupValidation(false, "SQLite 데이터베이스 파일이 아닙니다.", 0, 0, null, null);
            }

            using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly, pooling: false));
            connection.Open();
            var check = Scalar(connection, "PRAGMA quick_check;") as string;
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new BackupValidation(false, $"무결성 검사 실패: {check}", 0, 0, null, null);
            }

            var version = (int)ScalarLong(connection, "PRAGMA user_version;");
            if (version < 1 || version > Schema.CurrentVersion)
            {
                return new BackupValidation(false, $"지원하지 않는 스키마 버전입니다 ({version}).", version, 0, null, null);
            }

            foreach (var table in Schema.RequiredTables)
            {
                if (ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @n;", null, ("@n", table)) == 0)
                {
                    return new BackupValidation(false, $"필수 테이블이 없습니다: {table}", version, 0, null, null);
                }
            }

            var sessions = ScalarLong(connection, "SELECT COUNT(*) FROM sessions;");
            var oldest = Scalar(connection, "SELECT MIN(started_ms) FROM sessions;");
            var newest = Scalar(connection, "SELECT MAX(COALESCE(last_sample_ms, started_ms)) FROM sessions;");
            return new BackupValidation(true, "정상", version, sessions,
                oldest is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(oldest, CultureInfo.InvariantCulture)),
                newest is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(newest, CultureInfo.InvariantCulture)));
        }
        catch (SqliteException ex)
        {
            return new BackupValidation(false, $"백업 파일을 열 수 없습니다: {ex.Message}", 0, 0, null, null);
        }
    }

    /// <summary>
    /// Replaces the database with a validated backup. Must be called while no store is open on <paramref name="databasePath"/>.
    /// The current database is kept next to it (".before-restore-...") so the restore can be undone by hand.
    /// </summary>
    public static string? RestoreFrom(string backupPath, string databasePath, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var validation = ValidateBackup(backupPath);
        if (!validation.IsValid)
        {
            throw new InvalidDataException($"백업 파일이 올바르지 않습니다: {validation.Message}");
        }

        SqliteConnection.ClearAllPools();
        string? aside = File.Exists(databasePath) ? Quarantine(databasePath, "before-restore", time) : null;
        try
        {
            using var source = new SqliteConnection(ConnectionString(backupPath, SqliteOpenMode.ReadOnly, pooling: false));
            using var destination = new SqliteConnection(ConnectionString(databasePath, SqliteOpenMode.ReadWriteCreate, pooling: false));
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
            Exec(destination, "PRAGMA journal_mode=WAL;");
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(databasePath + suffix))
                {
                    File.Delete(databasePath + suffix);
                }

                if (aside is not null && File.Exists(aside + suffix))
                {
                    File.Move(aside + suffix, databasePath + suffix);
                }
            }

            throw;
        }

        return aside;
    }

    public void Checkpoint()
    {
        lock (_writeGate)
        {
            if (!_disposed)
            {
                Exec(_writer, "PRAGMA wal_checkpoint(PASSIVE);");
            }
        }
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                Exec(_writer, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (SqliteException ex)
            {
                _log.Warn("storage", "종료 시 WAL 체크포인트 실패", ex);
            }

            _writer.Dispose();
        }

        SqliteConnection.ClearPool(new SqliteConnection(ConnectionString(DatabasePath, SqliteOpenMode.ReadOnly)));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
