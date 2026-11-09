using System.Globalization;
using Microsoft.Data.Sqlite;
using PcControlCenter.Core;

namespace PcControlCenter.History;

/// <summary>
/// Reads the integrated history for a time range directly from the modules' own databases (lapc.db, gpc.db, wdl.db,
/// wao.db) through read-only connections. Nothing is copied, merged or written; SQLite's WAL mode lets these reads run
/// while the modules record. Each value keeps its source; recorded gaps are returned as gaps, never filled in.
/// </summary>
public sealed class TimelineReader(ShellPaths paths)
{
    public const string SourceA = "로컬 AI (A · lapc.db)";
    public const string SourceB = "GPU 경제성 (B · gpc.db)";
    public const string SourceC = "시스템 진단 (C · wdl.db)";
    public const string SourceE = "AI Operator (E · wao.db)";

    /// <summary>Upper bound per query so a long range stays responsive; the coverage row says when it was reached.</summary>
    public int MaxRowsPerQuery { get; init; } = 200_000;

    public TimelineResult Read(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            throw new ArgumentException("끝 시각이 시작 시각보다 뒤여야 합니다.", nameof(to));
        }

        var series = new List<TimelineSeries>();
        var gaps = new List<TimelineGap>();
        var events = new List<TimelineEvent>();
        var coverage = new List<SourceCoverage>
        {
            ReadSource(SourceB, ModuleIds.GpuEconomics, from, to, series, gaps, events, ReadEconomics),
            ReadSource(SourceA, ModuleIds.LocalAi, from, to, series, gaps, events, ReadLocalAi),
            ReadSource(SourceC, ModuleIds.Diagnostics, from, to, series, gaps, events, ReadDiagnostics),
            ReadSource(SourceE, ModuleIds.Operator, from, to, series, gaps, events, ReadOperator),
        };
        return new TimelineResult(from, to, series, [.. gaps.OrderBy(g => g.From)], [.. events.OrderBy(e => e.Time)], coverage);
    }

    private delegate void SourceReader(SqliteConnection connection, long fromMs, long toMs, Collector collector);

    private SourceCoverage ReadSource(string source, string moduleId, DateTimeOffset from, DateTimeOffset to, List<TimelineSeries> series, List<TimelineGap> gaps,
        List<TimelineEvent> events, SourceReader reader)
    {
        var database = paths.ModuleDatabasePath(moduleId);
        if (!File.Exists(database))
        {
            return new SourceCoverage(source, database, false, "데이터베이스가 없습니다 (모듈을 아직 실행하지 않았거나 꺼져 있음).", 0, 0, null, null, TimeSpan.Zero);
        }

        var collector = new Collector(source, MaxRowsPerQuery);
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5,
            }.ToString());
            connection.Open();
            reader(connection, from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds(), collector);
        }
        catch (SqliteException ex)
        {
            collector.Problem = $"읽지 못했습니다: {ex.Message}";
        }

        series.AddRange(collector.Series);
        gaps.AddRange(collector.Gaps);
        events.AddRange(collector.Events);
        var times = collector.Series.SelectMany(s => s.Points).Select(p => p.Time).ToList();
        var gapTotal = TimeSpan.FromTicks(collector.Gaps.Sum(g => (Min(g.To, to) - Max(g.From, from)).Ticks is var t && t > 0 ? t : 0));
        return new SourceCoverage(source, database, collector.Problem is null, collector.Problem ?? collector.Note, times.Count, collector.Events.Count,
            times.Count == 0 ? null : times.Min(), times.Count == 0 ? null : times.Max(), gapTotal);
    }

    /// <summary>B: continuous GPU power/utilization/temperature, CPU and RAM while the app ran; recorded gaps; work sessions.</summary>
    private static void ReadEconomics(SqliteConnection c, long fromMs, long toMs, Collector collector)
    {
        var interval = TimeSpan.FromMilliseconds(Scalar(c, "SELECT sample_interval_ms FROM app_runs ORDER BY id DESC LIMIT 1") ?? 1000);
        var mock = Scalar(c, "SELECT COUNT(*) FROM system_samples WHERE is_mock = 1 AND t_ms BETWEEN $f AND $t", fromMs, toMs) > 0;
        foreach (var (deviceId, name) in Rows(c, "SELECT id, name FROM devices ORDER BY gpu_index, id", r => (r.GetInt64(0), r.GetString(1))))
        {
            var label = $"GPU #{deviceId} {name}";
            collector.AddSeries($"b.gpu{deviceId}.power", $"{label} 보드 전력", "W", interval, mock,
                Points(c, "SELECT t_ms, power_w FROM gpu_samples WHERE device_id = $d AND t_ms BETWEEN $f AND $t AND power_w IS NOT NULL ORDER BY t_ms", fromMs, toMs, collector.Limit, ("$d", deviceId)));
            collector.AddSeries($"b.gpu{deviceId}.util", $"{label} 사용률", "%", interval, mock,
                Points(c, "SELECT t_ms, util FROM gpu_samples WHERE device_id = $d AND t_ms BETWEEN $f AND $t AND util IS NOT NULL ORDER BY t_ms", fromMs, toMs, collector.Limit, ("$d", deviceId)));
            collector.AddSeries($"b.gpu{deviceId}.temp", $"{label} 온도", "°C", interval, mock,
                Points(c, "SELECT t_ms, temp_c FROM gpu_samples WHERE device_id = $d AND t_ms BETWEEN $f AND $t AND temp_c IS NOT NULL ORDER BY t_ms", fromMs, toMs, collector.Limit, ("$d", deviceId)));
        }

        collector.AddSeries("b.cpu", "CPU 사용률", "%", interval, mock,
            Points(c, "SELECT t_ms, cpu FROM system_samples WHERE t_ms BETWEEN $f AND $t AND cpu IS NOT NULL ORDER BY t_ms", fromMs, toMs, collector.Limit));
        collector.AddSeries("b.ram", "RAM 사용", "GiB", interval, mock,
            Points(c, "SELECT t_ms, ram_used / 1073741824.0 FROM system_samples WHERE t_ms BETWEEN $f AND $t AND ram_used IS NOT NULL ORDER BY t_ms", fromMs, toMs, collector.Limit));
        ReadGaps(c, fromMs, toMs, collector);

        // Periods without any app run are not observed either (the app was closed).
        foreach (var (start, end) in Rows(c, "SELECT started_ms, COALESCE(ended_ms, heartbeat_ms) FROM app_runs WHERE started_ms <= $t AND COALESCE(ended_ms, heartbeat_ms, started_ms) >= $f ORDER BY started_ms",
                     r => (r.GetInt64(0), r.IsDBNull(1) ? (long?)null : r.GetInt64(1)), fromMs, toMs))
        {
            collector.Runs.Add((start, end));
        }

        collector.AddNotRunningGaps(fromMs, toMs, "GPU 경제성 모듈이 실행되지 않은 구간 (측정 없음)");
        foreach (var (id, title, state, started, ended, mockSession) in Rows(c,
                     "SELECT id, COALESCE(title, ''), state, started_ms, ended_ms, is_mock FROM sessions WHERE started_ms <= $t AND COALESCE(ended_ms, $t) >= $f ORDER BY started_ms",
                     r => (r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.IsDBNull(4) ? (long?)null : r.GetInt64(4), r.GetInt32(5) == 1), fromMs, toMs))
        {
            collector.Events.Add(new TimelineEvent(FromMs(started), ended is long e ? FromMs(e) : null, collector.Source, "세션",
                string.IsNullOrWhiteSpace(title) ? $"작업 세션 #{id}" : title, ended is null ? "진행 중" : null, TimelineSeverity.Info, mockSession));
        }
    }

    /// <summary>A: inference speed and engine state recorded in A's sessions; session spans; A's own events.</summary>
    private static void ReadLocalAi(SqliteConnection c, long fromMs, long toMs, Collector collector)
    {
        var interval = TimeSpan.FromMilliseconds(Scalar(c, "SELECT sample_interval_ms FROM sessions ORDER BY id DESC LIMIT 1") ?? 1000);
        var mock = Scalar(c, "SELECT COUNT(*) FROM sessions WHERE is_mock = 1 AND started_ms <= $t AND COALESCE(ended_ms, last_sample_ms, $t) >= $f", fromMs, toMs) > 0;
        collector.AddSeries("a.answer", "답변 생성 속도 (엔진 보고)", "tok/s", interval, mock,
            Points(c, "SELECT t_ms, answer_tok_s FROM runtime_samples WHERE t_ms BETWEEN $f AND $t AND answer_tok_s IS NOT NULL ORDER BY t_ms", fromMs, toMs, collector.Limit));
        collector.AddSeries("a.prefill", "프롬프트 처리 속도 (엔진 보고)", "tok/s", interval, mock,
            Points(c, "SELECT t_ms, prefill_tok_s FROM runtime_samples WHERE t_ms BETWEEN $f AND $t AND prefill_tok_s IS NOT NULL ORDER BY t_ms", fromMs, toMs, collector.Limit));
        foreach (var (id, name, started, ended, last, mockSession) in Rows(c,
                     "SELECT id, name, started_ms, ended_ms, last_sample_ms, is_mock FROM sessions WHERE started_ms <= $t AND COALESCE(ended_ms, last_sample_ms, $t) >= $f ORDER BY started_ms",
                     r => (r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? (long?)null : r.GetInt64(3), r.IsDBNull(4) ? (long?)null : r.GetInt64(4), r.GetInt32(5) == 1), fromMs, toMs))
        {
            collector.Events.Add(new TimelineEvent(FromMs(started), ended is long e ? FromMs(e) : last is long l ? FromMs(l) : null, collector.Source, "기록 세션", name,
                ended is null ? "진행 중" : null, TimelineSeverity.Info, mockSession));
            collector.Runs.Add((started, ended ?? last));
        }

        collector.AddNotRunningGaps(fromMs, toMs, "로컬 AI 기록 세션 밖의 구간 (A는 세션 중에만 저장)");
        foreach (var (t, severity, category, message) in Rows(c, "SELECT t_ms, severity, category, message FROM events WHERE t_ms BETWEEN $f AND $t ORDER BY t_ms LIMIT 2000",
                     r => (r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3)), fromMs, toMs))
        {
            collector.Events.Add(new TimelineEvent(FromMs(t), null, collector.Source, category, message, null, severity switch
            {
                >= 2 => TimelineSeverity.Error,
                1 => TimelineSeverity.Warning,
                _ => TimelineSeverity.Info,
            }, false));
        }
    }

    /// <summary>
    /// C: Windows events (warnings and worse) as C classified them, C's recorded gaps, and C's sensor samples. C records
    /// the same shared sensor readings as B (at its own, longer interval); the history view uses them only when B has none.
    /// </summary>
    private static void ReadDiagnostics(SqliteConnection c, long fromMs, long toMs, Collector collector)
    {
        var interval = TimeSpan.FromMilliseconds(Scalar(c, "SELECT sensor_interval_ms FROM runs ORDER BY id DESC LIMIT 1") ?? 5000);
        var mock = Scalar(c, "SELECT COUNT(*) FROM runs WHERE is_mock = 1 AND started_ms <= $t AND COALESCE(ended_ms, heartbeat_ms, $t) >= $f", fromMs, toMs) > 0;
        collector.AddSeries("c.cpu", "CPU 사용률", "%", interval, mock,
            Points(c, "SELECT t_ms, cpu_util FROM sensor_samples WHERE t_ms BETWEEN $f AND $t AND cpu_util IS NOT NULL ORDER BY t_ms", fromMs, toMs, collector.Limit));
        foreach (var index in Rows(c, "SELECT DISTINCT gpu_index FROM gpu_samples WHERE t_ms BETWEEN $f AND $t ORDER BY gpu_index", r => r.GetInt32(0), fromMs, toMs))
        {
            collector.AddSeries($"c.gpu{index}.power", $"GPU #{index} 보드 전력", "W", interval, mock,
                Points(c, "SELECT t_ms, power_w FROM gpu_samples WHERE gpu_index = $i AND t_ms BETWEEN $f AND $t AND power_w IS NOT NULL ORDER BY t_ms", fromMs, toMs, collector.Limit, ("$i", index)));
            collector.AddSeries($"c.gpu{index}.util", $"GPU #{index} 사용률", "%", interval, mock,
                Points(c, "SELECT t_ms, util FROM gpu_samples WHERE gpu_index = $i AND t_ms BETWEEN $f AND $t AND util IS NOT NULL ORDER BY t_ms", fromMs, toMs, collector.Limit, ("$i", index)));
            collector.AddSeries($"c.gpu{index}.temp", $"GPU #{index} 온도", "°C", interval, mock,
                Points(c, "SELECT t_ms, temp_c FROM gpu_samples WHERE gpu_index = $i AND t_ms BETWEEN $f AND $t AND temp_c IS NOT NULL ORDER BY t_ms", fromMs, toMs, collector.Limit, ("$i", index)));
        }

        var count = 0;
        foreach (var (t, severity, category, title, provider, eventId, source) in Rows(c,
                     "SELECT time_ms, severity, category, title, provider, event_id, source FROM events WHERE time_ms BETWEEN $f AND $t AND severity >= 1 ORDER BY time_ms LIMIT 5000",
                     r => (r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6)), fromMs, toMs))
        {
            count++;
            collector.Events.Add(new TimelineEvent(FromMs(t), null, collector.Source, category, title, $"{provider} {eventId}", severity switch
            {
                >= 3 => TimelineSeverity.Critical,
                2 => TimelineSeverity.Error,
                _ => TimelineSeverity.Warning,
            }, source == "virtual"));
        }

        if (count == 5000)
        {
            collector.Note = "이벤트가 많아 처음 5000건만 표시합니다.";
        }

        ReadGaps(c, fromMs, toMs, collector);
    }

    /// <summary>E: Operator tasks with their status (times are stored as ISO-8601 text).</summary>
    private static void ReadOperator(SqliteConnection c, long fromMs, long toMs, Collector collector)
    {
        foreach (var (request, status, message, created, started, ended, mockTask) in Rows(c,
                     "SELECT request, status, status_message, created_at, started_at, ended_at, is_mock FROM tasks ORDER BY created_at DESC LIMIT 5000",
                     r => (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                         r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6) == 1)))
        {
            if (ParseIso(started ?? created) is not { } begin)
            {
                continue;
            }

            var end = ParseIso(ended);
            if (begin.ToUnixTimeMilliseconds() > toMs || (end ?? begin).ToUnixTimeMilliseconds() < fromMs)
            {
                continue;
            }

            var severity = status switch
            {
                "Failed" or "Interrupted" => TimelineSeverity.Error,
                "Denied" or "Cancelled" or "PartiallyCompleted" => TimelineSeverity.Warning,
                _ => TimelineSeverity.Info,
            };
            collector.Events.Add(new TimelineEvent(begin, end, collector.Source, $"작업 · {status}", request.Length > 120 ? request[..120] + "…" : request, message, severity, mockTask));
        }
    }

    private static void ReadGaps(SqliteConnection c, long fromMs, long toMs, Collector collector)
    {
        foreach (var (scope, kind, gapFrom, gapTo, detected, detail) in Rows(c,
                     "SELECT scope, kind, from_ms, to_ms, detected_ms, detail FROM gaps WHERE COALESCE(from_ms, detected_ms) <= $t AND COALESCE(to_ms, from_ms, detected_ms) >= $f ORDER BY COALESCE(from_ms, detected_ms)",
                     r => (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? (long?)null : r.GetInt64(2), r.IsDBNull(3) ? (long?)null : r.GetInt64(3), r.GetInt64(4),
                         r.IsDBNull(5) ? null : r.GetString(5)), fromMs, toMs))
        {
            var start = gapFrom ?? detected;
            var end = gapTo ?? start;
            collector.Gaps.Add(new TimelineGap(FromMs(start), FromMs(end), collector.Source, $"{scope}/{kind}", detail ?? kind));
        }
    }

    private static List<TimelinePoint> Points(SqliteConnection c, string sql, long fromMs, long toMs, int limit, params (string Name, object Value)[] extra)
        => Rows(c, sql + $" LIMIT {limit}", r => new TimelinePoint(FromMs(r.GetInt64(0)), r.GetDouble(1)), fromMs, toMs, extra)
            .Where(p => double.IsFinite(p.Value)).ToList();

    private static List<T> Rows<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, long fromMs = 0, long toMs = 0, params (string Name, object Value)[] extra)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("$f", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("$f", fromMs);
        }

        if (sql.Contains("$t", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("$t", toMs);
        }

        foreach (var (name, value) in extra)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
        {
            list.Add(map(reader));
        }

        return list;
    }

    private static long? Scalar(SqliteConnection c, string sql, long fromMs = 0, long toMs = 0)
    {
        var rows = Rows(c, sql, r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), fromMs, toMs);
        return rows.Count == 0 ? null : rows[0];
    }

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static DateTimeOffset? ParseIso(string? text)
        => text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private sealed class Collector(string source, int limit)
    {
        public string Source { get; } = source;
        public int Limit { get; } = limit;
        public List<TimelineSeries> Series { get; } = [];
        public List<TimelineGap> Gaps { get; } = [];
        public List<TimelineEvent> Events { get; } = [];
        public List<(long Start, long? End)> Runs { get; } = [];
        public string? Problem { get; set; }
        public string? Note { get; set; }

        public void AddSeries(string key, string label, string unit, TimeSpan interval, bool mock, List<TimelinePoint> points)
        {
            if (points.Count == 0)
            {
                return;
            }

            if (points.Count >= Limit)
            {
                Note = $"기간이 길어 계열마다 처음 {Limit:N0}개만 읽었습니다. 기간을 줄이면 전체가 보입니다.";
            }

            Series.Add(new TimelineSeries(key, label, unit, Source, points, mock, interval));
        }

        /// <summary>Adds the parts of [from, to] that no recorded run or session covers (the module was not observing).</summary>
        public void AddNotRunningGaps(long fromMs, long toMs, string detail)
        {
            var cursor = fromMs;
            foreach (var (start, end) in Runs.OrderBy(r => r.Start))
            {
                if (start > cursor)
                {
                    Gaps.Add(new TimelineGap(FromMs(cursor), FromMs(Math.Min(start, toMs)), Source, "not-running", detail));
                }

                cursor = Math.Max(cursor, end ?? toMs);
                if (cursor >= toMs)
                {
                    break;
                }
            }

            if (cursor < toMs)
            {
                Gaps.Add(new TimelineGap(FromMs(cursor), FromMs(toMs), Source, "not-running", detail));
            }
        }
    }
}
