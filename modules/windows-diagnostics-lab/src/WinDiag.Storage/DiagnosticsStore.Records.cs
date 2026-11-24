using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinDiag.Core.Crash;
using WinDiag.Core.Records;
using WinDiag.Core.Snapshots;
using static LocalAiPerf.Storage.SqliteHelpers;

namespace WinDiag.Storage;

public sealed partial class DiagnosticsStore
{
    // ------------------------------------------------------------------ snapshots

    /// <summary>
    /// Saves a configuration snapshot. When the configuration equals the latest stored one, only that snapshot's
    /// "last confirmed" time moves forward; otherwise a new snapshot row is added.
    /// </summary>
    public SnapshotSaveResult SaveSnapshot(SystemSnapshot snapshot, string source)
    {
        var hash = snapshot.ContentHash();
        var at = snapshot.CollectedAt.ToUnixTimeMilliseconds();
        lock (_writeGate)
        {
            ThrowIfDisposed();
            using var tx = _writer.BeginTransaction();
            var previous = ReadSnapshots(_writer, tx, "WHERE is_mock = @m ORDER BY first_seen_ms DESC, id DESC LIMIT 1",
                ("@m", snapshot.IsMock ? 1 : 0)).FirstOrDefault();
            if (previous is not null && previous.ContentHash == hash)
            {
                Execute(_writer, "UPDATE snapshots SET last_confirmed_ms = MAX(last_confirmed_ms, @t) WHERE id = @id;", tx, ("@t", at), ("@id", previous.Id));
                tx.Commit();
                return new SnapshotSaveResult(previous.Id, false, previous);
            }

            var id = ScalarLong(_writer, """
                INSERT INTO snapshots (first_seen_ms, last_confirmed_ms, content_hash, source, is_mock, json)
                VALUES (@t, @t, @h, @s, @m, @j) RETURNING id;
                """, tx, ("@t", at), ("@h", hash), ("@s", source), ("@m", snapshot.IsMock ? 1 : 0), ("@j", snapshot.ToJson()));
            tx.Commit();
            return new SnapshotSaveResult(id, true, previous);
        }
    }

    public IReadOnlyList<StoredSnapshot> ListSnapshots(bool includeMock = true)
    {
        using var reader = OpenReader();
        return ReadSnapshots(reader, null, includeMock ? "ORDER BY first_seen_ms, id" : "WHERE is_mock = 0 ORDER BY first_seen_ms, id");
    }

    public StoredSnapshot? GetSnapshot(long id)
    {
        using var reader = OpenReader();
        return ReadSnapshots(reader, null, "WHERE id = @id", ("@id", id)).FirstOrDefault();
    }

    /// <summary>The snapshot that describes the system at a time (the latest one first seen at or before it).</summary>
    public StoredSnapshot? SnapshotAt(DateTimeOffset time, bool includeMock = true)
    {
        using var reader = OpenReader();
        return ReadSnapshots(reader, null,
            $"WHERE first_seen_ms <= @t {(includeMock ? "" : "AND is_mock = 0")} ORDER BY first_seen_ms DESC, id DESC LIMIT 1",
            ("@t", time.ToUnixTimeMilliseconds())).FirstOrDefault();
    }

    private static List<StoredSnapshot> ReadSnapshots(SqliteConnection connection, SqliteTransaction? tx, string tail, params (string, object?)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT id, first_seen_ms, last_confirmed_ms, content_hash, source, is_mock, json FROM snapshots {tail};";
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var list = new List<StoredSnapshot>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new StoredSnapshot(r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)), r.GetString(3), r.GetString(4), r.GetInt64(5) != 0, r.GetString(6)));
        }

        return list;
    }

    // ------------------------------------------------------------------ user records

    public long AddUserRecord(UserRecord record)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            return ScalarLong(_writer, """
                INSERT INTO user_records (time_ms, created_ms, kind, title, detail, values_json) VALUES (@t, @c, @k, @ti, @d, @v) RETURNING id;
                """, null,
                ("@t", record.Time.ToUnixTimeMilliseconds()), ("@c", _time.GetUtcNow().ToUnixTimeMilliseconds()), ("@k", record.Kind),
                ("@ti", record.Title), ("@d", record.Detail), ("@v", JsonSerializer.Serialize(record.Values, StoreJson.Options)));
        }
    }

    public void UpdateUserRecord(UserRecord record)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            Execute(_writer, "UPDATE user_records SET time_ms = @t, kind = @k, title = @ti, detail = @d, values_json = @v WHERE id = @id;", null,
                ("@t", record.Time.ToUnixTimeMilliseconds()), ("@k", record.Kind), ("@ti", record.Title), ("@d", record.Detail),
                ("@v", JsonSerializer.Serialize(record.Values, StoreJson.Options)), ("@id", record.Id));
        }
    }

    public bool DeleteUserRecord(long id)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            return Execute(_writer, "DELETE FROM user_records WHERE id = @id;", null, ("@id", id)) > 0;
        }
    }

    public IReadOnlyList<UserRecord> ListUserRecords(DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = """
            SELECT id, time_ms, created_ms, kind, title, detail, values_json FROM user_records
            WHERE (@f IS NULL OR time_ms >= @f) AND (@t IS NULL OR time_ms <= @t) ORDER BY time_ms, id;
            """;
        command.Parameters.AddWithValue("@f", (object?)from?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        command.Parameters.AddWithValue("@t", (object?)to?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        var list = new List<UserRecord>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new UserRecord
            {
                Id = r.GetInt64(0),
                Time = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)),
                Created = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
                Kind = r.GetString(3),
                Title = r.GetString(4),
                Detail = S(r, 5),
                Values = S(r, 6) is { } json ? JsonSerializer.Deserialize<List<UserValueChange>>(json, StoreJson.Options) ?? [] : [],
            });
        }

        return list;
    }

    // ------------------------------------------------------------------ baselines and comparisons

    public long AddBaseline(Baseline baseline)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            return ScalarLong(_writer, """
                INSERT INTO baselines (name, time_ms, created_ms, description, user_record_id) VALUES (@n, @t, @c, @d, @u) RETURNING id;
                """, null,
                ("@n", baseline.Name), ("@t", baseline.Time.ToUnixTimeMilliseconds()), ("@c", _time.GetUtcNow().ToUnixTimeMilliseconds()),
                ("@d", baseline.Description), ("@u", baseline.UserRecordId));
        }
    }

    public bool DeleteBaseline(long id)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            return Execute(_writer, "DELETE FROM baselines WHERE id = @id;", null, ("@id", id)) > 0;
        }
    }

    public IReadOnlyList<Baseline> ListBaselines()
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT id, name, time_ms, created_ms, description, user_record_id FROM baselines ORDER BY time_ms, id;";
        var list = new List<Baseline>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Baseline
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                Time = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
                Created = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
                Description = S(r, 4),
                UserRecordId = L(r, 5),
            });
        }

        return list;
    }

    public long SaveComparison(long? baselineId, string title, string resultJson)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            return ScalarLong(_writer, "INSERT INTO comparisons (baseline_id, created_ms, title, result_json) VALUES (@b, @c, @t, @j) RETURNING id;", null,
                ("@b", baselineId), ("@c", _time.GetUtcNow().ToUnixTimeMilliseconds()), ("@t", title), ("@j", resultJson));
        }
    }

    public IReadOnlyList<StoredComparison> ListComparisons(long? baselineId = null)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = """
            SELECT id, baseline_id, created_ms, title, result_json FROM comparisons
            WHERE (@b IS NULL OR baseline_id = @b) ORDER BY created_ms DESC, id DESC;
            """;
        command.Parameters.AddWithValue("@b", (object?)baselineId ?? DBNull.Value);
        var list = new List<StoredComparison>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new StoredComparison(r.GetInt64(0), L(r, 1), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)), r.GetString(3), r.GetString(4)));
        }

        return list;
    }

    // ------------------------------------------------------------------ notes

    public void SaveNote(string targetKind, string targetKey, string text)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            if (string.IsNullOrWhiteSpace(text))
            {
                Execute(_writer, "DELETE FROM notes WHERE target_kind = @k AND target_key = @key;", null, ("@k", targetKind), ("@key", targetKey));
                return;
            }

            Execute(_writer, """
                INSERT INTO notes (target_kind, target_key, created_ms, updated_ms, text) VALUES (@k, @key, @now, @now, @text)
                ON CONFLICT(target_kind, target_key) DO UPDATE SET text = excluded.text, updated_ms = excluded.updated_ms;
                """, null, ("@k", targetKind), ("@key", targetKey), ("@now", now), ("@text", text.Trim()));
        }
    }

    public UserNote? GetNote(string targetKind, string targetKey)
        => ListNotes(targetKind).FirstOrDefault(n => n.TargetKey == targetKey);

    public IReadOnlyList<UserNote> ListNotes(string? targetKind = null)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = """
            SELECT id, target_kind, target_key, created_ms, updated_ms, text FROM notes
            WHERE (@k IS NULL OR target_kind = @k) ORDER BY updated_ms DESC;
            """;
        command.Parameters.AddWithValue("@k", (object?)targetKind ?? DBNull.Value);
        var list = new List<UserNote>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new UserNote
            {
                Id = r.GetInt64(0),
                TargetKind = r.GetString(1),
                TargetKey = r.GetString(2),
                Created = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
                Updated = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
                Text = r.GetString(5),
            });
        }

        return list;
    }

    // ------------------------------------------------------------------ dump files

    public void SaveDumpFiles(IReadOnlyList<DumpFileInfo> dumps)
    {
        lock (_writeGate)
        {
            ThrowIfDisposed();
            using var tx = _writer.BeginTransaction();
            foreach (var d in dumps)
            {
                Execute(_writer, """
                    INSERT INTO dump_files (path, size, write_ms, status, status_message, signature, bugcheck, params, dump_ms, dump_type,
                                            processors, build, seen_ms)
                    VALUES (@p, @s, @w, @st, @sm, @sig, @bc, @pa, @dm, @dt, @pr, @b, @seen)
                    ON CONFLICT(path) DO UPDATE SET size = excluded.size, write_ms = excluded.write_ms, status = excluded.status,
                        status_message = excluded.status_message, signature = excluded.signature, bugcheck = excluded.bugcheck,
                        params = excluded.params, dump_ms = excluded.dump_ms, dump_type = excluded.dump_type, processors = excluded.processors,
                        build = excluded.build, seen_ms = excluded.seen_ms;
                    """, tx,
                    ("@p", d.Path), ("@s", d.SizeBytes), ("@w", d.LastWriteTime?.ToUnixTimeMilliseconds()), ("@st", d.Status), ("@sm", d.StatusMessage),
                    ("@sig", d.Signature), ("@bc", d.BugCheckCode is uint code ? (long)code : null),
                    ("@pa", d.BugCheckParameters.Count == 0 ? null : string.Join(',', d.BugCheckParameters.Select(p => "0x" + p.ToString("x", CultureInfo.InvariantCulture)))),
                    ("@dm", d.DumpTime?.ToUnixTimeMilliseconds()), ("@dt", d.DumpType), ("@pr", d.ProcessorCount), ("@b", d.BuildNumber),
                    ("@seen", (d.Seen ?? _time.GetUtcNow()).ToUnixTimeMilliseconds()));
            }

            tx.Commit();
        }
    }

    public IReadOnlyList<DumpFileInfo> ListDumpFiles()
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = """
            SELECT path, size, write_ms, status, status_message, signature, bugcheck, params, dump_ms, dump_type, processors, build, seen_ms
            FROM dump_files ORDER BY COALESCE(dump_ms, write_ms) DESC;
            """;
        var list = new List<DumpFileInfo>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DumpFileInfo
            {
                Path = r.GetString(0),
                SizeBytes = L(r, 1),
                LastWriteTime = Ms(L(r, 2)),
                Status = r.GetString(3),
                StatusMessage = S(r, 4),
                Signature = S(r, 5),
                BugCheckCode = L(r, 6) is long code ? (uint)code : null,
                BugCheckParameters = S(r, 7) is { } p
                    ? p.Split(',').Select(x => ulong.Parse(x.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToList()
                    : [],
                DumpTime = Ms(L(r, 8)),
                DumpType = I(r, 9),
                ProcessorCount = I(r, 10),
                BuildNumber = I(r, 11),
                Seen = Ms(L(r, 12)),
            });
        }

        return list;
    }
}
