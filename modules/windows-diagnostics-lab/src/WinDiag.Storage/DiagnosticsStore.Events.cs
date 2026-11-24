using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinDiag.Core.Events;
using static LocalAiPerf.Storage.SqliteHelpers;

namespace WinDiag.Storage;

public sealed partial class DiagnosticsStore
{
    private const string EventColumns = """
        e.id, e.channel, e.provider, e.event_source, e.event_id, e.qualifiers, e.level, e.record_id, e.time_ms, e.ingested_ms,
        e.category, e.severity, e.group_key, e.signature, e.title, e.message, e.message_error, e.data_json, e.process_id,
        e.thread_id, e.source, e.is_masked
        """;

    /// <summary>
    /// Stores events; an event already stored (same channel, record ID, provider, event ID and time) is skipped, so
    /// re-reading a range after a restart never creates duplicates. The channel progress is saved in the same
    /// transaction, so a crash can never record progress for events that were not stored.
    /// </summary>
    public InsertResult InsertEvents(IReadOnlyList<NewEvent> events, ChannelState? progress = null)
    {
        if (events.Count == 0 && progress is null)
        {
            return InsertResult.Empty;
        }

        lock (_writeGate)
        {
            ThrowIfDisposed();
            var ingested = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var ids = new List<long>();
            var duplicates = 0;
            using var tx = _writer.BeginTransaction();
            using (var command = _writer.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = """
                    INSERT OR IGNORE INTO events (channel, provider, event_source, event_id, qualifiers, level, record_id, time_ms,
                        ingested_ms, category, severity, group_key, signature, title, message, message_error, data_json, process_id,
                        thread_id, source, is_masked)
                    VALUES (@channel, @provider, @source_name, @event_id, @qualifiers, @level, @record_id, @time_ms, @ingested_ms,
                        @category, @severity, @group_key, @signature, @title, @message, @message_error, @data_json, @process_id,
                        @thread_id, @source, @is_masked)
                    RETURNING id;
                    """;
                var p = new Dictionary<string, SqliteParameter>();
                foreach (var name in new[]
                         {
                             "@channel", "@provider", "@source_name", "@event_id", "@qualifiers", "@level", "@record_id", "@time_ms", "@ingested_ms",
                             "@category", "@severity", "@group_key", "@signature", "@title", "@message", "@message_error", "@data_json",
                             "@process_id", "@thread_id", "@source", "@is_masked",
                         })
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name; // type inferred from each value (integers stay integers)
                    command.Parameters.Add(parameter);
                    p[name] = parameter;
                }

                foreach (var item in events)
                {
                    var e = item.Event;
                    var c = item.Classification;
                    p["@channel"].Value = e.Channel;
                    p["@provider"].Value = e.Provider;
                    p["@source_name"].Value = Db(e.EventSourceName);
                    p["@event_id"].Value = e.EventId;
                    p["@qualifiers"].Value = Db(e.Qualifiers);
                    p["@level"].Value = e.Level;
                    p["@record_id"].Value = e.RecordId;
                    p["@time_ms"].Value = e.TimeCreated.ToUnixTimeMilliseconds();
                    p["@ingested_ms"].Value = ingested;
                    p["@category"].Value = c.Category;
                    p["@severity"].Value = (int)c.Severity;
                    p["@group_key"].Value = c.GroupKey;
                    p["@signature"].Value = c.Signature;
                    p["@title"].Value = c.Title;
                    p["@message"].Value = Db(e.Message);
                    p["@message_error"].Value = Db(e.MessageError);
                    p["@data_json"].Value = Db(e.Data.Count == 0 ? null : SerializeData(e.Data));
                    p["@process_id"].Value = Db(e.ProcessId);
                    p["@thread_id"].Value = Db(e.ThreadId);
                    p["@source"].Value = item.Source;
                    p["@is_masked"].Value = item.IsMasked ? 1 : 0;
                    var result = command.ExecuteScalar();
                    if (result is null or DBNull)
                    {
                        duplicates++;
                    }
                    else
                    {
                        ids.Add(Convert.ToInt64(result, CultureInfo.InvariantCulture));
                    }
                }
            }

            if (progress is not null)
            {
                UpsertChannelState(progress, tx);
            }

            tx.Commit();
            return new InsertResult(ids.Count, duplicates, ids);
        }
    }

    /// <summary>Up to <paramref name="limit"/> events with an ID above <paramref name="afterId"/>, in ID order (for paging through all events).</summary>
    public IReadOnlyList<StoredEvent> ReadEventPage(long afterId, int limit)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = $"SELECT {EventColumns} FROM events e WHERE e.id > @after ORDER BY e.id LIMIT @limit;";
        command.Parameters.AddWithValue("@after", afterId);
        command.Parameters.AddWithValue("@limit", limit);
        using var r = command.ExecuteReader();
        var list = new List<StoredEvent>();
        while (r.Read())
        {
            list.Add(ReadEvent(r));
        }

        return list;
    }

    /// <summary>Replaces the stored classification of events (after the classification rules changed), in one transaction.</summary>
    public void UpdateClassifications(IReadOnlyList<(long Id, EventClassification Classification)> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        lock (_writeGate)
        {
            ThrowIfDisposed();
            using var tx = _writer.BeginTransaction();
            using var command = _writer.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                UPDATE events SET category = @category, severity = @severity, group_key = @group_key, signature = @signature, title = @title
                WHERE id = @id;
                """;
            var names = new[] { "@category", "@severity", "@group_key", "@signature", "@title", "@id" };
            var p = names.ToDictionary(n => n, n =>
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = n;
                command.Parameters.Add(parameter);
                return parameter;
            });
            foreach (var (id, c) in changes)
            {
                p["@category"].Value = c.Category;
                p["@severity"].Value = (int)c.Severity;
                p["@group_key"].Value = c.GroupKey;
                p["@signature"].Value = c.Signature;
                p["@title"].Value = c.Title;
                p["@id"].Value = id;
                command.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public StoredEvent? GetEvent(long id)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = $"SELECT {EventColumns} FROM events e WHERE e.id = @id;";
        command.Parameters.AddWithValue("@id", id);
        using var r = command.ExecuteReader();
        return r.Read() ? ReadEvent(r) : null;
    }

    public IReadOnlyList<StoredEvent> QueryEvents(EventQuery query)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        var where = BuildWhere(query, command);
        command.CommandText = $"""
            SELECT {EventColumns} FROM events e {where}
            ORDER BY e.time_ms {(query.Descending ? "DESC" : "ASC")}, e.id {(query.Descending ? "DESC" : "ASC")}
            LIMIT @limit OFFSET @offset;
            """;
        command.Parameters.AddWithValue("@limit", Math.Clamp(query.Limit, 1, 100_000));
        command.Parameters.AddWithValue("@offset", Math.Max(0, query.Offset));
        var list = new List<StoredEvent>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(ReadEvent(r));
        }

        return list;
    }

    public long CountEvents(EventQuery query)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        var where = BuildWhere(query, command);
        command.CommandText = $"SELECT COUNT(*) FROM events e {where};";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Repeated events grouped by signature over the filtered range, most frequent first.</summary>
    public IReadOnlyList<EventGroupRow> QueryGroups(EventQuery query)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        var where = BuildWhere(query, command);
        command.CommandText = $"""
            SELECT g.group_key, x.category, x.provider, x.event_id, x.title, x.signature, g.max_sev, g.cnt, g.first_ms, g.last_ms, g.latest_id,
                   (SELECT MIN(e2.time_ms) FROM events e2 WHERE e2.group_key = g.group_key) AS first_ever, g.days
            FROM (
                SELECT e.group_key, COUNT(*) AS cnt, MIN(e.time_ms) AS first_ms, MAX(e.time_ms) AS last_ms, MAX(e.severity) AS max_sev,
                       MAX(e.id) AS latest_id, COUNT(DISTINCT e.time_ms / 86400000) AS days
                FROM events e {where}
                GROUP BY e.group_key
            ) g JOIN events x ON x.id = g.latest_id
            ORDER BY g.max_sev DESC, g.cnt DESC, g.last_ms DESC
            LIMIT @limit OFFSET @offset;
            """;
        command.Parameters.AddWithValue("@limit", Math.Clamp(query.Limit, 1, 100_000));
        command.Parameters.AddWithValue("@offset", Math.Max(0, query.Offset));
        var rows = new List<EventGroupRow>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new EventGroupRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5),
                (DiagSeverity)r.GetInt32(6), r.GetInt64(7),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)),
                r.GetInt64(10), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(11)), r.GetInt32(12)));
        }

        return rows;
    }

    /// <summary>Lightweight references to every event matching the filter (for analysis), oldest first.</summary>
    public IReadOnlyList<EventPoint> LoadEventPoints(EventQuery query)
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        var where = BuildWhere(query, command);
        command.CommandText = $"""
            SELECT e.id, e.time_ms, e.category, e.severity, e.group_key, e.provider, e.event_id, e.channel, e.record_id, e.source
            FROM events e {where} ORDER BY e.time_ms, e.id LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", Math.Clamp(query.Limit, 1, 2_000_000));
        var list = new List<EventPoint>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EventPoint(r.GetInt64(0), r.GetInt64(1), r.GetString(2), (DiagSeverity)r.GetInt32(3), r.GetString(4),
                r.GetString(5), r.GetInt32(6), r.GetString(7), r.GetInt64(8), r.GetString(9) == EventSources.Virtual));
        }

        return list;
    }

    public IReadOnlyList<StoredEvent> GetEvents(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        var names = new List<string>();
        var i = 0;
        foreach (var id in ids.Take(5000))
        {
            var name = "@i" + i++.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            command.Parameters.AddWithValue(name, id);
        }

        command.CommandText = $"SELECT {EventColumns} FROM events e WHERE e.id IN ({string.Join(',', names)}) ORDER BY e.time_ms, e.id;";
        var list = new List<StoredEvent>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add(ReadEvent(r));
        }

        return list;
    }

    public IReadOnlyList<(string Provider, long Count)> ListProviders()
    {
        using var reader = OpenReader();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT provider, COUNT(*) FROM events GROUP BY provider ORDER BY COUNT(*) DESC, provider;";
        var list = new List<(string, long)>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetString(0), r.GetInt64(1)));
        }

        return list;
    }

    public long MaxEventId()
    {
        using var reader = OpenReader();
        return ScalarLong(reader, "SELECT COALESCE(MAX(id), 0) FROM events;");
    }

    /// <summary>The user confirmed everything stored so far: later events count as "new".</summary>
    public long Acknowledge()
    {
        var max = MaxEventId();
        SetMeta(MetaLastAckEventId, max.ToString(CultureInfo.InvariantCulture));
        SetMeta(MetaLastAckTime, _time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return max;
    }

    public long LastAcknowledgedEventId => GetMetaLong(MetaLastAckEventId);

    private static string BuildWhere(EventQuery q, SqliteCommand command)
    {
        var clauses = new List<string>();
        if (q.From is { } from)
        {
            clauses.Add("e.time_ms >= @from");
            command.Parameters.AddWithValue("@from", from.ToUnixTimeMilliseconds());
        }

        if (q.To is { } to)
        {
            clauses.Add("e.time_ms <= @to");
            command.Parameters.AddWithValue("@to", to.ToUnixTimeMilliseconds());
        }

        if (q.MinSeverity is { } severity)
        {
            clauses.Add("e.severity >= @sev");
            command.Parameters.AddWithValue("@sev", (int)severity);
        }

        AddIn(clauses, command, "e.category", "@cat", q.Categories);
        AddIn(clauses, command, "e.provider", "@prov", q.Providers);
        AddIn(clauses, command, "e.channel", "@chan", q.Channels);
        if (q.EventId is int id)
        {
            clauses.Add("e.event_id = @eid");
            command.Parameters.AddWithValue("@eid", id);
        }

        if (!string.IsNullOrWhiteSpace(q.GroupKey))
        {
            clauses.Add("e.group_key = @gk");
            command.Parameters.AddWithValue("@gk", q.GroupKey);
        }

        if (q.AfterId is long after)
        {
            clauses.Add("e.id > @after");
            command.Parameters.AddWithValue("@after", after);
        }

        if (!q.IncludeVirtual)
        {
            clauses.Add("e.source <> 'virtual'");
        }

        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var text = q.Text.Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                clauses.Add("(e.event_id = @tnum OR e.title LIKE @t ESCAPE '\\' OR e.message LIKE @t ESCAPE '\\')");
                command.Parameters.AddWithValue("@tnum", number);
            }
            else
            {
                clauses.Add("(e.title LIKE @t ESCAPE '\\' OR e.message LIKE @t ESCAPE '\\' OR e.provider LIKE @t ESCAPE '\\' OR e.signature LIKE @t ESCAPE '\\')");
            }

            command.Parameters.AddWithValue("@t", "%" + EscapeLike(text) + "%");
        }

        return clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
    }

    private static void AddIn(List<string> clauses, SqliteCommand command, string column, string prefix, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return;
        }

        if (values.Count == 0)
        {
            clauses.Add("0");
            return;
        }

        var names = new List<string>();
        for (var i = 0; i < values.Count; i++)
        {
            var name = prefix + i.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            command.Parameters.AddWithValue(name, values[i]);
        }

        clauses.Add($"{column} IN ({string.Join(',', names)})");
    }

    private static string EscapeLike(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '%' or '_' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static StoredEvent ReadEvent(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Channel = r.GetString(1),
        Provider = r.GetString(2),
        EventSourceName = S(r, 3),
        EventId = r.GetInt32(4),
        Qualifiers = I(r, 5),
        Level = r.GetInt32(6),
        RecordId = r.GetInt64(7),
        TimeCreated = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)),
        Ingested = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)),
        Category = r.GetString(10),
        Severity = (DiagSeverity)r.GetInt32(11),
        GroupKey = r.GetString(12),
        Signature = r.GetString(13),
        Title = r.GetString(14),
        Message = S(r, 15),
        MessageError = S(r, 16),
        Data = DeserializeData(S(r, 17)),
        ProcessId = I(r, 18),
        ThreadId = I(r, 19),
        Source = r.GetString(20),
        IsMasked = r.GetInt64(21) != 0,
    };

    private sealed record DataJson(string? N, string V);

    internal static string SerializeData(IReadOnlyList<EventDataItem> data)
        => JsonSerializer.Serialize(data.Select(d => new DataJson(d.Name, d.Value)).ToList(), StoreJson.Options);

    internal static IReadOnlyList<EventDataItem> DeserializeData(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<DataJson>>(json, StoreJson.Options)?.Select(d => new EventDataItem(d.N, d.V)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [new EventDataItem("(손상된 데이터)", json.Length > 200 ? json[..200] : json)];
        }
    }
}

internal static class StoreJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
