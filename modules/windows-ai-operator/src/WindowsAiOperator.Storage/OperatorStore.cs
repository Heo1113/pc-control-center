using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WindowsAiOperator.Core.Actions;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Execution;
using WindowsAiOperator.Core.Security;

namespace WindowsAiOperator.Storage;

/// <summary>
/// SQLite store (WAL, one connection, serialized writes). <c>PRAGMA user_version</c> holds the schema version; a
/// database written by a newer version is opened read-only by refusing writes (data protection), never migrated down.
/// Everything stored is redacted text: no keys, no password values, no screenshots.
/// </summary>
public sealed partial class OperatorStore : IDisposable
{
    public const int SchemaVersion = 1;
    private static readonly string[] OpenStatuses = ["Created", "Planning", "AwaitingPlanReview", "Running", "Paused", "AwaitingApproval"];

    private readonly Lock _gate = new();
    private SqliteConnection _connection;

    public OperatorStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connection = Open(DatabasePath);
        Migrate();
    }

    public string DatabasePath { get; }

    /// <summary>The database was created by a newer version of this program: nothing is written.</summary>
    public bool IsReadOnly { get; private set; }

    public static string Timestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseTime(object? value) =>
        value is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (SqliteException)
            {
            }

            _connection.Dispose();
        }
    }

    // ---------- tasks ----------

    public void SaveTask(TaskRun run)
    {
        if (IsReadOnly)
        {
            return;
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO tasks(id, request, source, workflow_id, model_label, is_mock, target_app, plan_summary, status, status_message,
                                  error_kind, report, created_at, started_at, ended_at, rounds, model_calls, model_errors, approvals_requested,
                                  approvals_granted, interventions, injection_suspected, last_known_state, updated_at)
                VALUES($id, $request, $source, $workflow, $model, $mock, $app, $plan, $status, $message, $error, $report, $created, $started,
                       $ended, $rounds, $calls, $errors, $asked, $granted, $interventions, $injection, $last, $updated)
                ON CONFLICT(id) DO UPDATE SET model_label=$model, target_app=$app, plan_summary=$plan, status=$status, status_message=$message,
                       error_kind=$error, report=$report, started_at=$started, ended_at=$ended, rounds=$rounds, model_calls=$calls,
                       model_errors=$errors, approvals_requested=$asked, approvals_granted=$granted, interventions=$interventions,
                       injection_suspected=$injection, last_known_state=$last, updated_at=$updated;
                """;
            command.Parameters.AddWithValue("$id", run.Id.ToString());
            command.Parameters.AddWithValue("$request", Redactor.Sanitize(run.Request, 2000));
            command.Parameters.AddWithValue("$source", run.Source.ToString());
            command.Parameters.AddWithValue("$workflow", (object?)run.WorkflowId ?? DBNull.Value);
            command.Parameters.AddWithValue("$model", Db(run.ModelLabel, 200));
            command.Parameters.AddWithValue("$mock", run.IsMockDesktop ? 1 : 0);
            command.Parameters.AddWithValue("$app", Db(run.TargetApp, 100));
            command.Parameters.AddWithValue("$plan", Db(run.PlanSummary, 300));
            command.Parameters.AddWithValue("$status", run.Status.ToString());
            command.Parameters.AddWithValue("$message", Db(run.StatusMessage, 1000));
            command.Parameters.AddWithValue("$error", (object?)run.Error?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$report", Db(run.Report, 2000));
            command.Parameters.AddWithValue("$created", Timestamp(run.CreatedAt));
            command.Parameters.AddWithValue("$started", run.StartedAt is { } s ? Timestamp(s) : DBNull.Value);
            command.Parameters.AddWithValue("$ended", run.EndedAt is { } e ? Timestamp(e) : DBNull.Value);
            command.Parameters.AddWithValue("$rounds", run.Rounds);
            command.Parameters.AddWithValue("$calls", run.ModelCalls);
            command.Parameters.AddWithValue("$errors", run.ModelErrors);
            command.Parameters.AddWithValue("$asked", run.ApprovalsRequested);
            command.Parameters.AddWithValue("$granted", run.ApprovalsGranted);
            command.Parameters.AddWithValue("$interventions", run.UserInterventions);
            command.Parameters.AddWithValue("$injection", run.InjectionSuspected ? 1 : 0);
            command.Parameters.AddWithValue("$last", Db(run.LastKnownState, 500));
            command.Parameters.AddWithValue("$updated", Timestamp(DateTimeOffset.Now));
            command.ExecuteNonQuery();
        }
    }

    public void SaveStep(Guid taskId, StepRecord step)
    {
        if (IsReadOnly)
        {
            return;
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO steps(task_id, seq, round, step_id, action, summary, params_json, target_app, target_window, target_element, risk,
                                  decision, decision_reasons, approval, status, attempts, performed, changes_state, verification, method, message,
                                  error_kind, read_value, started_at, ended_at)
                VALUES($task, $seq, $round, $id, $action, $summary, $params, $app, $window, $element, $risk, $decision, $reasons, $approval,
                       $status, $attempts, $performed, $changes, $verification, $method, $message, $error, $read, $started, $ended)
                ON CONFLICT(task_id, seq) DO UPDATE SET target_app=$app, target_window=$window, target_element=$element, risk=$risk,
                       decision=$decision, decision_reasons=$reasons, approval=$approval, status=$status, attempts=$attempts,
                       performed=$performed, verification=$verification, method=$method, message=$message, error_kind=$error,
                       read_value=$read, started_at=$started, ended_at=$ended;
                """;
            command.Parameters.AddWithValue("$task", taskId.ToString());
            command.Parameters.AddWithValue("$seq", step.Sequence);
            command.Parameters.AddWithValue("$round", step.Round);
            command.Parameters.AddWithValue("$id", step.Step.Id);
            command.Parameters.AddWithValue("$action", step.Step.Action.WireName());
            command.Parameters.AddWithValue("$summary", Redactor.Sanitize(step.Step.Summary(), 400));
            command.Parameters.AddWithValue("$params", StepParams(step.Step));
            command.Parameters.AddWithValue("$app", Db(step.TargetApp, 100));
            command.Parameters.AddWithValue("$window", Db(step.TargetWindow, 200));
            command.Parameters.AddWithValue("$element", Db(step.TargetElement, 200));
            command.Parameters.AddWithValue("$risk", (object?)step.Decision?.Risk.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$decision", (object?)step.Decision?.Verdict.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$reasons", Db(step.Decision?.ReasonText, 1000));
            command.Parameters.AddWithValue("$approval", step.Approval.ToString());
            command.Parameters.AddWithValue("$status", step.Status.ToString());
            command.Parameters.AddWithValue("$attempts", step.Attempts);
            command.Parameters.AddWithValue("$performed", step.Performed ? 1 : 0);
            command.Parameters.AddWithValue("$changes", step.ChangesState ? 1 : 0);
            command.Parameters.AddWithValue("$verification", step.Verification.ToString());
            command.Parameters.AddWithValue("$method", Db(step.Method, 100));
            command.Parameters.AddWithValue("$message", Db(step.Message, 1000));
            command.Parameters.AddWithValue("$error", (object?)step.Error?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$read", Db(step.ReadValue, 1500));
            command.Parameters.AddWithValue("$started", step.StartedAt is { } s ? Timestamp(s) : DBNull.Value);
            command.Parameters.AddWithValue("$ended", step.EndedAt is { } e ? Timestamp(e) : DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public void AddEvent(Guid taskId, TaskEventLevel level, string message)
    {
        if (IsReadOnly)
        {
            return;
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO events(task_id, at, level, message) VALUES($task, $at, $level, $message);";
            command.Parameters.AddWithValue("$task", taskId.ToString());
            command.Parameters.AddWithValue("$at", Timestamp(DateTimeOffset.Now));
            command.Parameters.AddWithValue("$level", level.ToString());
            command.Parameters.AddWithValue("$message", Redactor.Sanitize(message, 1000));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<TaskSummary> QueryTasks(HistoryQuery query)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var where = new List<string>();
            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                where.Add("(t.request LIKE $text ESCAPE '\\' OR t.target_app LIKE $text ESCAPE '\\' OR t.status_message LIKE $text ESCAPE '\\')");
                command.Parameters.AddWithValue("$text", "%" + query.Text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            }

            if (!string.IsNullOrWhiteSpace(query.Status))
            {
                where.Add("t.status = $status");
                command.Parameters.AddWithValue("$status", query.Status);
            }

            if (query.Since is { } since)
            {
                where.Add("t.created_at >= $since");
                command.Parameters.AddWithValue("$since", Timestamp(since));
            }

            command.CommandText = $"""
                SELECT t.id, t.request, t.source, t.status, t.status_message, t.error_kind, t.target_app, t.model_label, t.is_mock, t.created_at, t.ended_at,
                       (SELECT COUNT(*) FROM steps s WHERE s.task_id = t.id),
                       (SELECT COUNT(*) FROM steps s WHERE s.task_id = t.id AND s.status = 'Succeeded'),
                       (SELECT COUNT(*) FROM steps s WHERE s.task_id = t.id AND s.performed = 1 AND s.changes_state = 1),
                       t.approvals_requested, t.model_calls
                FROM tasks t
                {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty)}
                ORDER BY t.created_at DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 5000));
            using var reader = command.ExecuteReader();
            var list = new List<TaskSummary>();
            while (reader.Read())
            {
                list.Add(ReadSummary(reader));
            }

            return list;
        }
    }

    public TaskDetail? GetTask(string id)
    {
        lock (_gate)
        {
            TaskSummary? summary = null;
            string? plan = null, report = null, last = null;
            int rounds = 0, errors = 0, granted = 0, interventions = 0;
            var injection = false;
            DateTimeOffset? started = null;
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT t.id, t.request, t.source, t.status, t.status_message, t.error_kind, t.target_app, t.model_label, t.is_mock, t.created_at, t.ended_at,
                           (SELECT COUNT(*) FROM steps s WHERE s.task_id = t.id),
                           (SELECT COUNT(*) FROM steps s WHERE s.task_id = t.id AND s.status = 'Succeeded'),
                           (SELECT COUNT(*) FROM steps s WHERE s.task_id = t.id AND s.performed = 1 AND s.changes_state = 1),
                           t.approvals_requested, t.model_calls,
                           t.plan_summary, t.report, t.rounds, t.model_errors, t.approvals_granted, t.interventions, t.injection_suspected,
                           t.last_known_state, t.started_at
                    FROM tasks t WHERE t.id = $id;
                    """;
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                summary = ReadSummary(reader);
                plan = reader.IsDBNull(16) ? null : reader.GetString(16);
                report = reader.IsDBNull(17) ? null : reader.GetString(17);
                rounds = reader.GetInt32(18);
                errors = reader.GetInt32(19);
                granted = reader.GetInt32(20);
                interventions = reader.GetInt32(21);
                injection = reader.GetInt32(22) != 0;
                last = reader.IsDBNull(23) ? null : reader.GetString(23);
                started = reader.IsDBNull(24) ? null : ParseTime(reader.GetString(24));
            }

            var steps = new List<StepRow>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT seq, round, step_id, action, summary, target_app, target_window, target_element, risk, decision, decision_reasons, approval,
                           status, attempts, performed, changes_state, verification, method, message, error_kind, read_value, params_json, started_at, ended_at
                    FROM steps WHERE task_id = $id ORDER BY seq;
                    """;
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    steps.Add(new StepRow(
                        reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
                        Str(reader, 5), Str(reader, 6), Str(reader, 7), Str(reader, 8), Str(reader, 9), Str(reader, 10), reader.GetString(11),
                        reader.GetString(12), reader.GetInt32(13), reader.GetInt32(14) != 0, reader.GetInt32(15) != 0, reader.GetString(16),
                        Str(reader, 17), Str(reader, 18), Str(reader, 19), Str(reader, 20), Str(reader, 21),
                        ParseTime(Str(reader, 22)), ParseTime(Str(reader, 23))));
                }
            }

            var events = new List<EventRow>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT at, level, message FROM events WHERE task_id = $id ORDER BY id;";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    events.Add(new EventRow(ParseTime(reader.GetString(0)) ?? DateTimeOffset.MinValue, reader.GetString(1), reader.GetString(2)));
                }
            }

            return new TaskDetail(summary!, plan, report, rounds, errors, granted, interventions, injection, last, started, steps, events);
        }
    }

    public int DeleteTask(string id)
    {
        if (IsReadOnly)
        {
            return 0;
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM tasks WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery();
        }
    }

    /// <summary>Removes finished tasks older than the retention period. Unfinished tasks are kept.</summary>
    public int ApplyRetention(int days)
    {
        if (IsReadOnly)
        {
            return 0;
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"DELETE FROM tasks WHERE created_at < $cutoff AND status NOT IN ({string.Join(",", OpenStatuses.Select(s => $"'{s}'"))});";
            command.Parameters.AddWithValue("$cutoff", Timestamp(DateTimeOffset.Now.AddDays(-days)));
            return command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Run at start: tasks that were still running when the program ended are closed as interrupted (or partially
    /// completed when changes had been made). Steps in progress become "uncertain"; nothing is resumed automatically.
    /// </summary>
    public IReadOnlyList<RecoveredTask> RecoverInterruptedTasks()
    {
        if (IsReadOnly)
        {
            return [];
        }

        lock (_gate)
        {
            var open = new List<(string Id, string Request)>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = $"SELECT id, request FROM tasks WHERE status IN ({string.Join(",", OpenStatuses.Select(s => $"'{s}'"))});";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    open.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            var recovered = new List<RecoveredTask>();
            foreach (var (id, request) in open)
            {
                using var transaction = _connection.BeginTransaction();
                Exec("UPDATE steps SET status='Unknown', message='프로그램이 이 단계 도중 종료되어 실행 여부가 불확실합니다. 대상 앱을 직접 확인하세요.' WHERE task_id=$id AND status='Running';", transaction, ("$id", id));
                Exec("UPDATE steps SET status='Cancelled', approval='Denied', message='승인을 기다리는 중 프로그램이 종료되어 실행하지 않았습니다.' WHERE task_id=$id AND status='AwaitingApproval';", transaction, ("$id", id));
                Exec("UPDATE steps SET status='Skipped', message='프로그램이 종료되어 실행하지 않았습니다.' WHERE task_id=$id AND status='Pending';", transaction, ("$id", id));
                var performed = ScalarInt("SELECT COUNT(*) FROM steps WHERE task_id=$id AND performed=1 AND changes_state=1;", transaction, ("$id", id));
                var uncertain = ScalarInt("SELECT COUNT(*) FROM steps WHERE task_id=$id AND status='Unknown';", transaction, ("$id", id));
                var notExecuted = ScalarInt("SELECT COUNT(*) FROM steps WHERE task_id=$id AND status IN ('Skipped','Cancelled');", transaction, ("$id", id));
                var status = performed > 0 || uncertain > 0 ? nameof(TaskRunStatus.PartiallyCompleted) : nameof(TaskRunStatus.Interrupted);
                var message = $"프로그램이 작업 도중 종료되었습니다. 실행된 변경 {performed}개, 실행 여부 불확실 {uncertain}개, 실행하지 않은 단계 {notExecuted}개. 자동으로 다시 실행하지 않습니다.";
                Exec("UPDATE tasks SET status=$status, status_message=$message, error_kind='Interrupted', ended_at=COALESCE(ended_at, updated_at), updated_at=$now WHERE id=$id;", transaction,
                    ("$status", status), ("$message", message), ("$now", Timestamp(DateTimeOffset.Now)), ("$id", id));
                Exec("INSERT INTO events(task_id, at, level, message) VALUES($id, $now, 'Warning', $message);", transaction, ("$id", id), ("$now", Timestamp(DateTimeOffset.Now)), ("$message", "복구: " + message));
                transaction.Commit();
                recovered.Add(new RecoveredTask(id, request, status, performed, uncertain, notExecuted));
            }

            return recovered;
        }
    }

    // ---------- workflows ----------

    public IReadOnlyList<WorkflowRecord> ListWorkflows()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, name, description, target_app, definition_json, builtin, created_at, updated_at FROM workflows ORDER BY builtin DESC, name;";
            using var reader = command.ExecuteReader();
            var list = new List<WorkflowRecord>();
            while (reader.Read())
            {
                list.Add(new WorkflowRecord(reader.GetString(0), reader.GetString(1), Str(reader, 2), Str(reader, 3), reader.GetString(4), reader.GetInt32(5) != 0,
                    ParseTime(reader.GetString(6)) ?? DateTimeOffset.MinValue, ParseTime(reader.GetString(7)) ?? DateTimeOffset.MinValue));
            }

            return list;
        }
    }

    public void SaveWorkflow(WorkflowRecord workflow)
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("새 버전 데이터베이스라 저장하지 않습니다.");
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO workflows(id, name, description, target_app, definition_json, builtin, created_at, updated_at)
                VALUES($id, $name, $description, $app, $json, $builtin, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET name=$name, description=$description, target_app=$app, definition_json=$json, updated_at=$updated;
                """;
            command.Parameters.AddWithValue("$id", workflow.Id);
            command.Parameters.AddWithValue("$name", Redactor.Sanitize(workflow.Name, 120));
            command.Parameters.AddWithValue("$description", Db(workflow.Description, 1000));
            command.Parameters.AddWithValue("$app", Db(workflow.TargetApp, 100));
            command.Parameters.AddWithValue("$json", workflow.DefinitionJson);
            command.Parameters.AddWithValue("$builtin", workflow.BuiltIn ? 1 : 0);
            command.Parameters.AddWithValue("$created", Timestamp(workflow.CreatedAt));
            command.Parameters.AddWithValue("$updated", Timestamp(DateTimeOffset.Now));
            command.ExecuteNonQuery();
        }
    }

    public bool DeleteWorkflow(string id)
    {
        if (IsReadOnly)
        {
            return false;
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM workflows WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    // ---------- model comparison ----------

    public void AddEvalRun(EvalRunRecord run)
    {
        if (IsReadOnly)
        {
            return;
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO eval_runs(batch_id, at, profile_name, model_id, scenario, is_mock, plan_valid, gui_success, steps, duration_ms, model_calls,
                                      model_errors, wrong_targets, interventions, status, note)
                VALUES($batch, $at, $profile, $model, $scenario, $mock, $valid, $success, $steps, $duration, $calls, $errors, $wrong, $interventions, $status, $note);
                """;
            command.Parameters.AddWithValue("$batch", run.BatchId);
            command.Parameters.AddWithValue("$at", Timestamp(run.At));
            command.Parameters.AddWithValue("$profile", Redactor.Sanitize(run.ProfileName, 100));
            command.Parameters.AddWithValue("$model", Redactor.Sanitize(run.ModelId, 200));
            command.Parameters.AddWithValue("$scenario", run.Scenario);
            command.Parameters.AddWithValue("$mock", run.IsMock ? 1 : 0);
            command.Parameters.AddWithValue("$valid", run.PlanValid ? 1 : 0);
            command.Parameters.AddWithValue("$success", run.GuiSuccess ? 1 : 0);
            command.Parameters.AddWithValue("$steps", run.Steps);
            command.Parameters.AddWithValue("$duration", run.DurationMs);
            command.Parameters.AddWithValue("$calls", run.ModelCalls);
            command.Parameters.AddWithValue("$errors", run.ModelErrors);
            command.Parameters.AddWithValue("$wrong", run.WrongTargets);
            command.Parameters.AddWithValue("$interventions", run.Interventions);
            command.Parameters.AddWithValue("$status", run.Status);
            command.Parameters.AddWithValue("$note", Db(run.Note, 500));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<EvalRunRecord> ListEvalRuns(int limit = 500)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT batch_id, at, profile_name, model_id, scenario, is_mock, plan_valid, gui_success, steps, duration_ms, model_calls, model_errors,
                       wrong_targets, interventions, status, note FROM eval_runs ORDER BY id DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var list = new List<EvalRunRecord>();
            while (reader.Read())
            {
                list.Add(new EvalRunRecord(reader.GetString(0), ParseTime(reader.GetString(1)) ?? DateTimeOffset.MinValue, reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetInt32(5) != 0, reader.GetInt32(6) != 0, reader.GetInt32(7) != 0, reader.GetInt32(8), reader.GetInt64(9),
                    reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13), reader.GetString(14), Str(reader, 15)));
            }

            return list;
        }
    }

    // ---------- status, integrity, backup ----------

    public StoreStatus GetStatus()
    {
        lock (_gate)
        {
            var integrity = ScalarText("PRAGMA quick_check;") ?? "unknown";
            var unfinished = new List<string>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = $"SELECT id FROM tasks WHERE status IN ({string.Join(",", OpenStatuses.Select(s => $"'{s}'"))});";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    unfinished.Add(reader.GetString(0));
                }
            }

            return new StoreStatus(
                DatabasePath,
                File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0,
                ScalarInt("PRAGMA user_version;", null),
                integrity,
                ScalarInt("SELECT COUNT(*) FROM tasks;", null),
                ScalarInt("SELECT COUNT(*) FROM steps;", null),
                ScalarInt("SELECT COUNT(*) FROM events;", null),
                ScalarInt("SELECT COUNT(*) FROM workflows;", null),
                ScalarInt("SELECT COUNT(*) FROM eval_runs;", null),
                unfinished);
        }
    }

    public string IntegrityCheck()
    {
        lock (_gate)
        {
            return ScalarText("PRAGMA integrity_check;") ?? "unknown";
        }
    }

    /// <summary>Online backup into the folder; the copy is checked before it is reported.</summary>
    public BackupInfo Backup(string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var path = Path.Combine(backupDirectory, $"wao-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
        lock (_gate)
        {
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            destination.Open();
            _connection.BackupDatabase(destination);
            using var command = destination.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var check = command.ExecuteScalar() as string;
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"백업 파일 검사 실패: {check}");
            }
        }

        return new BackupInfo(path, DateTimeOffset.Now, new FileInfo(path).Length);
    }

    public static IReadOnlyList<BackupInfo> ListBackups(string backupDirectory) =>
        Directory.Exists(backupDirectory)
            ? Directory.EnumerateFiles(backupDirectory, "wao-*.db")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new BackupInfo(f.FullName, new DateTimeOffset(f.LastWriteTime), f.Length))
                .ToList()
            : [];

    /// <summary>Keeps the newest <paramref name="keep"/> backups made automatically; returns how many were removed.</summary>
    public static int PruneBackups(string backupDirectory, int keep)
    {
        var removed = 0;
        foreach (var old in ListBackups(backupDirectory).Skip(keep))
        {
            try
            {
                File.Delete(old.Path);
                removed++;
            }
            catch (IOException)
            {
            }
        }

        return removed;
    }

    /// <summary>
    /// Replaces the database with a checked backup. A safety backup of the current database is made first and its
    /// path returned (the recovery path if the restore was a mistake).
    /// </summary>
    public string RestoreFrom(string backupPath, string backupDirectory)
    {
        using (var probe = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            probe.Open();
            using var command = probe.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("백업 파일이 손상되어 복원하지 않습니다.");
            }

            command.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > SchemaVersion)
            {
                throw new InvalidOperationException("이 프로그램보다 새 버전에서 만든 백업이라 복원하지 않습니다.");
            }
        }

        var safety = Backup(backupDirectory).Path;
        lock (_gate)
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                source.Open();
                source.BackupDatabase(_connection);
            }

            Migrate();
        }

        return safety;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=10000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void Migrate()
    {
        var version = ScalarInt("PRAGMA user_version;", null);
        if (version > SchemaVersion)
        {
            IsReadOnly = true;
            return;
        }

        if (version == SchemaVersion)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        Exec(Schema.V1, transaction);
        Exec($"PRAGMA user_version={SchemaVersion};", transaction);
        transaction.Commit();
    }

    private static TaskSummary ReadSummary(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Str(reader, 4), Str(reader, 5), Str(reader, 6), Str(reader, 7),
        reader.GetInt32(8) != 0, ParseTime(reader.GetString(9)) ?? DateTimeOffset.MinValue, ParseTime(Str(reader, 10)),
        reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15));

    private static string? Str(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static object Db(string? text, int max) => text is null ? DBNull.Value : Redactor.Sanitize(text, max);

    /// <summary>The plan step as JSON with typed text shortened and redacted (the full text is never stored).</summary>
    private static string StepParams(PlanStep step)
    {
        var values = new Dictionary<string, object?>
        {
            ["action"] = step.Action.WireName(),
            ["window"] = step.Window?.ToString(),
            ["element"] = step.Element?.ToString(),
            ["app"] = step.App,
            ["text"] = step.Text is null ? null : $"{Redactor.OneLine(step.Text, 40)} ({step.Text.Length}자)",
            ["mode"] = step.Action == ActionType.SetText ? step.Mode.ToString() : null,
            ["checked"] = step.Checked,
            ["menu_path"] = step.MenuPath,
            ["keys"] = step.Keys?.ToString(),
            ["point"] = step.Point is { } p ? $"{p.X},{p.Y}" : null,
            ["path"] = step.Path,
        };
        return JsonSerializer.Serialize(values.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    private void Exec(string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private int ScalarInt(string sql, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private string? ScalarText(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }
}

internal static class Schema
{
    public const string V1 = """
        CREATE TABLE IF NOT EXISTS tasks(
            id TEXT PRIMARY KEY,
            request TEXT NOT NULL,
            source TEXT NOT NULL,
            workflow_id TEXT,
            model_label TEXT,
            is_mock INTEGER NOT NULL DEFAULT 0,
            target_app TEXT,
            plan_summary TEXT,
            status TEXT NOT NULL,
            status_message TEXT,
            error_kind TEXT,
            report TEXT,
            created_at TEXT NOT NULL,
            started_at TEXT,
            ended_at TEXT,
            rounds INTEGER NOT NULL DEFAULT 0,
            model_calls INTEGER NOT NULL DEFAULT 0,
            model_errors INTEGER NOT NULL DEFAULT 0,
            approvals_requested INTEGER NOT NULL DEFAULT 0,
            approvals_granted INTEGER NOT NULL DEFAULT 0,
            interventions INTEGER NOT NULL DEFAULT 0,
            injection_suspected INTEGER NOT NULL DEFAULT 0,
            last_known_state TEXT,
            updated_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_tasks_created ON tasks(created_at);
        CREATE INDEX IF NOT EXISTS ix_tasks_status ON tasks(status);
        CREATE TABLE IF NOT EXISTS steps(
            task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            round INTEGER NOT NULL,
            step_id INTEGER NOT NULL,
            action TEXT NOT NULL,
            summary TEXT NOT NULL,
            params_json TEXT,
            target_app TEXT,
            target_window TEXT,
            target_element TEXT,
            risk TEXT,
            decision TEXT,
            decision_reasons TEXT,
            approval TEXT NOT NULL,
            status TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            performed INTEGER NOT NULL DEFAULT 0,
            changes_state INTEGER NOT NULL DEFAULT 0,
            verification TEXT NOT NULL,
            method TEXT,
            message TEXT,
            error_kind TEXT,
            read_value TEXT,
            started_at TEXT,
            ended_at TEXT,
            PRIMARY KEY(task_id, seq));
        CREATE TABLE IF NOT EXISTS events(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            at TEXT NOT NULL,
            level TEXT NOT NULL,
            message TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_events_task ON events(task_id);
        CREATE TABLE IF NOT EXISTS workflows(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            description TEXT,
            target_app TEXT,
            definition_json TEXT NOT NULL,
            builtin INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS eval_runs(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id TEXT NOT NULL,
            at TEXT NOT NULL,
            profile_name TEXT NOT NULL,
            model_id TEXT NOT NULL,
            scenario TEXT NOT NULL,
            is_mock INTEGER NOT NULL,
            plan_valid INTEGER NOT NULL,
            gui_success INTEGER NOT NULL,
            steps INTEGER NOT NULL,
            duration_ms INTEGER NOT NULL,
            model_calls INTEGER NOT NULL,
            model_errors INTEGER NOT NULL,
            wrong_targets INTEGER NOT NULL,
            interventions INTEGER NOT NULL,
            status TEXT NOT NULL,
            note TEXT);
        """;
}

/// <summary>Persists executor progress as it happens, so a crash leaves an accurate record behind.</summary>
public sealed class TaskJournal(OperatorStore store, Core.Diagnostics.IOperatorLog? log = null) : ITaskObserver
{
    private readonly Core.Diagnostics.IOperatorLog _log = log ?? Core.Diagnostics.NullOperatorLog.Instance;

    public void TaskChanged(TaskRun run) => Guard(() => store.SaveTask(run));

    public void StepsChanged(TaskRun run) => Guard(() =>
    {
        store.SaveTask(run);
        foreach (var step in run.Steps.ToArray())
        {
            store.SaveStep(run.Id, step);
        }
    });

    public void StepChanged(TaskRun run, StepRecord step) => Guard(() => store.SaveStep(run.Id, step));

    public void Event(TaskRun run, TaskEventLevel level, string message) => Guard(() => store.AddEvent(run.Id, level, message));

    private void Guard(Action write)
    {
        try
        {
            write();
        }
        catch (SqliteException ex)
        {
            _log.Error("journal", "작업 기록 저장 실패", ex);
        }
    }
}
