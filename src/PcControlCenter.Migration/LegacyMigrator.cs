using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcControlCenter.Core;

namespace PcControlCenter.Migration;

public enum CheckState
{
    Passed,
    Warning,
    Failed,
}

public sealed record MigrationCheck(string Name, CheckState State, string Detail);

/// <summary>What was found in an original program's data folder (read only: file system metadata and running processes).</summary>
public sealed record LegacySource(LegacyLocation Location, bool Exists, long DatabaseBytes, bool HasWal, IReadOnlyList<string> RunningProcesses)
{
    public bool OriginalProgramRunning => RunningProcesses.Count > 0;
}

/// <summary>A verified copy ready to be applied. Nothing in the original folder or in the integrated app was changed.</summary>
public sealed record MigrationRehearsal(
    string ModuleId,
    string SourceDatabase,
    bool Passed,
    string StagingDirectory,
    string? FinalDatabase,
    string? SettingsFile,
    IReadOnlyList<MigrationCheck> Checks,
    IReadOnlyDictionary<string, long> TableCounts,
    DateTimeOffset At);

/// <param name="RestoreFailed">A failed apply could not put the module's previous files back. The module was left stopped
/// (never started on files that may not be its own); the kept paths say where the previous files are.</param>
public sealed record MigrationResult(bool Applied, string Message, string? PreviousDatabaseKeptAt, string? PreviousSettingsKeptAt,
    IReadOnlyList<MigrationCheck> Checks, bool RestoreFailed = false);

/// <summary>Stops and starts the integrated module whose data is being replaced (the shell's module supervisor).</summary>
public interface IModuleRestarter
{
    Task StopAsync(string moduleId);
    Task StartAsync(string moduleId);
}

/// <summary>
/// Brings an original program's data into the integrated app, one module at a time:
/// <list type="number">
/// <item>Inspect: file metadata only; refuses while the original program (GUI or command line) runs.</item>
/// <item>Rehearse: byte copy of the database (and its WAL) into PC Control Center's migration folder, SHA-256 of the
/// original before and after compared with the copy (nothing may change underneath), then — on the copy only — integrity
/// check, schema version (never newer than the module supports), the module's own backup validator, an exported final
/// copy through SQLite's backup API, and identical row counts in every table.</item>
/// <item>Apply (only on explicit request): the module stops, its current database and settings are moved aside (kept),
/// the verified copy takes their place (A/B/C through their own RestoreFrom), the counts are checked once more and the
/// module starts again. On any failure the previous files (database, -wal, -shm, settings) are put back and checked; if
/// that is not possible the result says so and the module stays stopped.</item>
/// </list>
/// The original program's folder is never written: no SQLite connection is opened on the original files.
/// </summary>
public sealed class LegacyMigrator(ShellPaths paths, Func<string, IReadOnlyList<string>>? runningProcesses = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, IReadOnlyList<string>> _running = runningProcesses ?? RunningOriginalProcesses;

    /// <summary>Process names of each original program (GUI and command-line tool).</summary>
    public static IReadOnlyList<string> ProcessNames(string moduleId) => moduleId switch
    {
        ModuleIds.LocalAi => ["LocalAiPerformanceCenter", "lapc"],
        ModuleIds.GpuEconomics => ["GpuProfitCenter", "gpc"],
        ModuleIds.Diagnostics => ["WindowsDiagnosticsLab", "wdl"],
        ModuleIds.Operator => ["WindowsAiOperator", "wao"],
        _ => [],
    };

    public LegacySource Inspect(LegacyLocation location)
    {
        var file = new FileInfo(location.DatabasePath);
        return new LegacySource(location, file.Exists, file.Exists ? file.Length : 0, File.Exists(location.DatabasePath + "-wal"), _running(location.ModuleId));
    }

    /// <summary>An original program's folder chosen by hand (portable installation): the database file name tells which program.</summary>
    public static LegacyLocation? FromFolder(string folder)
    {
        foreach (var candidate in new[] { folder, Path.Combine(folder, "data") })
        {
            foreach (var id in ModuleIds.All)
            {
                var name = ModuleIds.DatabaseFile(id);
                if (File.Exists(Path.Combine(candidate, name)))
                {
                    return new LegacyLocation(id, ModuleIds.Origin(id), candidate, name);
                }
            }
        }

        return null;
    }

    public MigrationRehearsal Rehearse(LegacyLocation location)
    {
        var checks = new List<MigrationCheck>();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var staging = Path.Combine(paths.MigrationDirectory, location.ModuleId, stamp);
        MigrationRehearsal Result(bool passed, string? final = null, string? settings = null)
        {
            var rehearsal = new MigrationRehearsal(location.ModuleId, location.DatabasePath, passed, staging, final, settings, checks, counts, _time.GetUtcNow());
            TryWriteReport(staging, rehearsal);
            return rehearsal;
        }

        if (LegacyDataLocations.Overlapping(paths.Root, [location]) is not null)
        {
            checks.Add(new MigrationCheck("원본 위치", CheckState.Failed, "원본 폴더가 통합 앱의 데이터 폴더와 겹칩니다."));
            return Result(false);
        }

        var source = Inspect(location);
        if (!source.Exists)
        {
            checks.Add(new MigrationCheck("원본 데이터베이스", CheckState.Failed, $"{location.DatabasePath} 이(가) 없습니다."));
            return Result(false);
        }

        if (source.OriginalProgramRunning)
        {
            checks.Add(new MigrationCheck("원래 프로그램 실행 여부", CheckState.Failed,
                $"원래 프로그램이 실행 중입니다({string.Join(", ", source.RunningProcesses)}). 기록 중인 데이터와 충돌하지 않도록 종료한 뒤 다시 시도하세요."));
            return Result(false);
        }

        checks.Add(new MigrationCheck("원래 프로그램 실행 여부", CheckState.Passed, "실행 중이 아님"));
        Directory.CreateDirectory(staging);
        var raw = Path.Combine(staging, "raw");
        Directory.CreateDirectory(raw);
        var rawDb = Path.Combine(raw, location.DatabaseFile);
        try
        {
            // 1. Byte copy, original read with sharing so nothing is locked; hashes before and after must equal the copy.
            var files = new[] { location.DatabasePath, location.DatabasePath + "-wal" }.Where(File.Exists).ToList();
            var before = files.ToDictionary(f => f, Sha256);
            foreach (var file in files)
            {
                CopyShared(file, Path.Combine(raw, Path.GetFileName(file)));
            }

            var after = files.ToDictionary(f => f, Sha256);
            var copied = files.ToDictionary(f => f, f => Sha256(Path.Combine(raw, Path.GetFileName(f))));
            if (files.Any(f => before[f] != after[f]))
            {
                checks.Add(new MigrationCheck("원본 변경 여부", CheckState.Failed, "복사하는 동안 원본이 바뀌었습니다(다른 프로그램이 기록 중). 원래 프로그램을 종료한 뒤 다시 시도하세요."));
                return Result(false);
            }

            if (files.Any(f => before[f] != copied[f]))
            {
                checks.Add(new MigrationCheck("복사본 일치", CheckState.Failed, "복사본의 SHA-256이 원본과 다릅니다."));
                return Result(false);
            }

            checks.Add(new MigrationCheck("복사본 일치", CheckState.Passed,
                $"원본과 바이트 단위로 같음 (SHA-256 {before[location.DatabasePath][..16]}…, {files.Count}개 파일{(source.HasWal ? ", WAL 포함" : "")})"));

            // 2. Checks on the copy only.
            using (var connection = OpenCopy(rawDb))
            {
                var integrity = ScalarText(connection, "PRAGMA integrity_check;");
                checks.Add(new MigrationCheck("무결성 검사 (복사본)", integrity == "ok" ? CheckState.Passed : CheckState.Failed, integrity ?? "-"));
                var version = ScalarLong(connection, "PRAGMA user_version;");
                var supported = SupportedSchema(location.ModuleId);
                checks.Add(new MigrationCheck("스키마 버전", version >= 1 && version <= supported ? CheckState.Passed : CheckState.Failed,
                    $"{version} (통합 앱이 지원하는 버전 {supported}){(version < supported ? " — 모듈이 처음 열 때 원래 프로그램과 같은 방식으로 올림" : "")}"));
                if (checks.Any(c => c.State == CheckState.Failed))
                {
                    return Result(false);
                }

                ScalarText(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                foreach (var table in Tables(connection))
                {
                    counts[table] = ScalarLong(connection, $"SELECT COUNT(*) FROM \"{table}\";");
                }
            }

            // 3. Final copy through SQLite's backup API, then the module's own validator and identical counts.
            var final = Path.Combine(staging, location.DatabaseFile);
            using (var from = OpenCopy(rawDb))
            using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = final, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                to.Open();
                from.BackupDatabase(to);
                ScalarText(to, "PRAGMA journal_mode=DELETE;");
            }

            var validation = Validate(location.ModuleId, final);
            checks.Add(new MigrationCheck("모듈 자체 검증", validation.Ok ? CheckState.Passed : CheckState.Failed, validation.Detail));
            using (var connection = OpenCopy(final, readOnly: true))
            {
                var mismatches = counts.Where(c => ScalarLong(connection, $"SELECT COUNT(*) FROM \"{c.Key}\";") != c.Value).Select(c => c.Key).ToList();
                checks.Add(new MigrationCheck("행 수 비교 (모든 테이블)", mismatches.Count == 0 ? CheckState.Passed : CheckState.Failed,
                    mismatches.Count == 0 ? $"{counts.Count}개 테이블, 합계 {counts.Values.Sum():N0}행 동일" : "다른 테이블: " + string.Join(", ", mismatches)));
                foreach (var (name, sql) in Aggregates(location.ModuleId))
                {
                    using var check = OpenCopy(rawDb, readOnly: true);
                    var expected = ScalarText(check, sql);
                    var actual = ScalarText(connection, sql);
                    checks.Add(new MigrationCheck($"주요 집계: {name}", expected == actual ? CheckState.Passed : CheckState.Failed, $"원본 복사본 {expected ?? "-"} / 이전본 {actual ?? "-"}"));
                }
            }

            string? settings = null;
            var legacySettings = Path.Combine(location.Directory, "settings.json");
            if (File.Exists(legacySettings))
            {
                settings = Path.Combine(staging, "settings.json");
                CopyShared(legacySettings, settings);
                checks.Add(new MigrationCheck("설정 파일", CheckState.Passed, "settings.json 복사본 준비 (적용할 때 함께 옮길 수 있음). API 키(secrets.dat)는 옮기지 않습니다."));
            }

            File.Delete(rawDb);
            foreach (var extra in Directory.EnumerateFiles(raw))
            {
                File.Delete(extra);
            }

            Directory.Delete(raw);
            var passed = checks.All(c => c.State != CheckState.Failed);
            return Result(passed, passed ? final : null, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            checks.Add(new MigrationCheck("리허설", CheckState.Failed, $"중단했습니다: {ex.Message} (원본은 바꾸지 않았습니다)"));
            return Result(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>
    /// Replaces the integrated module's data with a passed rehearsal. Only call this after the user confirmed. The module's
    /// current database (and settings when <paramref name="includeSettings"/>) are moved aside and kept; on any failure they
    /// are put back and the module is started again with them. If they cannot all be put back, nothing is deleted, the
    /// module is not started and the result reports <see cref="MigrationResult.RestoreFailed"/>.
    /// </summary>
    public async Task<MigrationResult> ApplyAsync(MigrationRehearsal rehearsal, IModuleRestarter modules, bool includeSettings)
    {
        var checks = new List<MigrationCheck>();
        if (!rehearsal.Passed || rehearsal.FinalDatabase is not { } final || !File.Exists(final))
        {
            return new MigrationResult(false, "검증을 통과한 리허설이 없어 적용하지 않았습니다.", null, null, checks);
        }

        if (_running(rehearsal.ModuleId) is { Count: > 0 } running)
        {
            return new MigrationResult(false, $"원래 프로그램이 실행 중이라 적용하지 않았습니다({string.Join(", ", running)}).", null, null, checks);
        }

        var target = paths.ModuleDatabasePath(rehearsal.ModuleId);
        var targetSettings = Path.Combine(paths.ModuleDirectory(rehearsal.ModuleId), "settings.json");
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var replace = new ReplaceProgress();
        string? keptSettings = null;
        var settingsPlaced = false;
        await modules.StopAsync(rehearsal.ModuleId).ConfigureAwait(false);

        // What the module left once it closed its database: a rollback must bring back exactly these files.
        SqliteConnection.ClearAllPools();
        var before = DatabaseFiles.Where(s => File.Exists(target + s)).ToList();
        var hadSettings = File.Exists(targetSettings);
        try
        {
            Replace(rehearsal.ModuleId, final, target, stamp, replace);
            checks.Add(new MigrationCheck("데이터베이스 교체", CheckState.Passed, replace.Kept is null ? "새로 놓음 (이전 파일 없음)" : $"이전 파일 보관: {Path.GetFileName(replace.Kept)}"));
            if (includeSettings && rehearsal.SettingsFile is { } settings && File.Exists(settings))
            {
                if (hadSettings)
                {
                    var aside = UniqueAside(targetSettings, $"before-import-{stamp}");
                    File.Move(targetSettings, aside);
                    keptSettings = aside;
                }

                File.Copy(settings, targetSettings);
                settingsPlaced = true;
                checks.Add(new MigrationCheck("설정 교체", CheckState.Passed, keptSettings is null ? "새로 놓음" : $"이전 설정 보관: {Path.GetFileName(keptSettings)}"));
            }

            using (var connection = OpenCopy(target, readOnly: true))
            {
                var lower = rehearsal.TableCounts.Where(c => TableExists(connection, c.Key) && ScalarLong(connection, $"SELECT COUNT(*) FROM \"{c.Key}\";") < c.Value).Select(c => c.Key).ToList();
                checks.Add(new MigrationCheck("적용 후 행 수", lower.Count == 0 ? CheckState.Passed : CheckState.Failed,
                    lower.Count == 0 ? "모든 테이블이 리허설 이상" : "줄어든 테이블: " + string.Join(", ", lower)));
                if (lower.Count > 0)
                {
                    throw new InvalidOperationException("적용 후 확인에서 행 수가 맞지 않았습니다.");
                }
            }

            SqliteConnection.ClearAllPools();
            await modules.StartAsync(rehearsal.ModuleId).ConfigureAwait(false);
            var result = new MigrationResult(true, $"{ModuleIds.DisplayName(rehearsal.ModuleId)} 데이터를 가져왔습니다. 원본은 그대로이고, 통합 앱의 이전 데이터는 보관했습니다.",
                replace.Kept, keptSettings, checks);
            TryWriteReport(rehearsal.StagingDirectory, result, "apply-report.json");
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure (including InvalidDataException from a module's own restore): put the previous files back.
            SqliteConnection.ClearAllPools();
            var failures = RestoreDatabase(target, before, replace);
            failures.AddRange(RestoreSettings(targetSettings, hadSettings, keptSettings, settingsPlaced));
            if (failures.Count == 0)
            {
                checks.Add(new MigrationCheck("되돌리기", CheckState.Passed, "통합 앱의 이전 데이터와 설정을 그대로 되돌렸습니다(데이터베이스·WAL·SHM 파일 확인)."));
                try
                {
                    await modules.StartAsync(rehearsal.ModuleId).ConfigureAwait(false);
                }
                catch (Exception restartError) when (restartError is not OutOfMemoryException)
                {
                    checks.Add(new MigrationCheck("모듈 재시작", CheckState.Failed, restartError.Message));
                }

                var reverted = new MigrationResult(false, $"적용하지 못했습니다: {ex.Message} 통합 앱의 이전 데이터로 되돌렸습니다.", null, null, checks);
                TryWriteReport(rehearsal.StagingDirectory, reverted, "apply-report.json");
                return reverted;
            }

            // Never claim the previous state is back, and never start the module on files that may not be its own.
            var kept = string.Join(", ", new[] { replace.Kept, keptSettings }.OfType<string>());
            checks.Add(new MigrationCheck("되돌리기", CheckState.Failed, string.Join(" / ", failures) + (kept.Length > 0 ? $" (보관 파일: {kept})" : "")));
            var failed = new MigrationResult(false,
                $"적용하지 못했고, 통합 앱의 이전 데이터도 자동으로 되돌리지 못했습니다: {ex.Message} " +
                $"{ModuleIds.DisplayName(rehearsal.ModuleId)} 모듈은 멈춘 채로 두었습니다. 보관 파일로 직접 복구한 뒤 모듈을 다시 시작하세요(아무 파일도 삭제하지 않았습니다).",
                replace.Kept, keptSettings, checks, RestoreFailed: true);
            TryWriteReport(rehearsal.StagingDirectory, failed, "apply-report.json");
            return failed;
        }
    }

    /// <summary>A SQLite database and the files that belong to it.</summary>
    private static readonly string[] DatabaseFiles = ["", "-wal", "-shm"];

    /// <summary>Where <see cref="Replace"/> moved the module's files, recorded as each step happens.</summary>
    internal sealed class ReplaceProgress
    {
        /// <summary>Path the module's previous database was moved to (its -wal/-shm next to it with the same suffixes).</summary>
        public string? Kept { get; set; }

        /// <summary>Which of "", "-wal", "-shm" were moved to <see cref="Kept"/>.</summary>
        public List<string> Moved { get; } = [];

        /// <summary>The new database is at the target path (every previous file had been moved aside before).</summary>
        public bool Placed { get; set; }
    }

    private static void Replace(string moduleId, string final, string target, string stamp, ReplaceProgress progress)
    {
        // A, B, C: their own restore validates the file, moves the current database and its -wal/-shm aside, copies, and
        // puts the previous files back itself if the copy fails.
        string? aside = null;
        switch (moduleId)
        {
            case ModuleIds.LocalAi:
                aside = LocalAiPerf.Storage.TelemetryStore.RestoreFrom(final, target);
                break;
            case ModuleIds.GpuEconomics:
                aside = GpuProfit.Storage.ProfitStore.RestoreFrom(final, target);
                break;
            case ModuleIds.Diagnostics:
                aside = WinDiag.Storage.DiagnosticsStore.RestoreFrom(final, target);
                break;
            default:
                ReplaceByHand(final, target, stamp, progress);
                return;
        }

        progress.Kept = aside;
        progress.Moved.AddRange(aside is null ? [] : DatabaseFiles.Where(s => File.Exists(aside + s)));
        progress.Placed = true;
    }

    /// <summary>
    /// E has no file-level restore (its restore runs inside the open store). The new database is copied next to the target
    /// first, so a copy that fails leaves the module's files untouched; then the current files are moved aside one at a
    /// time (each move recorded) and the copy is renamed into place.
    /// </summary>
    private static void ReplaceByHand(string final, string target, string stamp, ReplaceProgress progress)
    {
        var incoming = $"{target}.incoming-{Guid.NewGuid():N}";
        try
        {
            File.Copy(final, incoming);
            var existing = DatabaseFiles.Where(s => File.Exists(target + s)).ToList();
            if (existing.Count > 0)
            {
                progress.Kept = UniqueAside(target, $"before-import-{stamp}");
                foreach (var suffix in existing)
                {
                    File.Move(target + suffix, progress.Kept + suffix);
                    progress.Moved.Add(suffix);
                }
            }

            File.Move(incoming, target);
            progress.Placed = true;
        }
        finally
        {
            if (File.Exists(incoming))
            {
                File.Delete(incoming); // our own staged copy that was not placed
            }
        }
    }

    /// <summary>
    /// Puts the module's previous database files back exactly as they were, whatever step failed, and checks the result:
    /// every file that existed before must be back and no file of the new database may remain. Returns the problems.
    /// </summary>
    internal static List<string> RestoreDatabase(string target, IReadOnlyCollection<string> before, ReplaceProgress progress)
    {
        var failures = new List<string>();
        try
        {
            if (progress.Placed)
            {
                // Every previous file was moved aside before the new database was placed: what is at the target now is new.
                foreach (var suffix in DatabaseFiles)
                {
                    if (File.Exists(target + suffix))
                    {
                        File.Delete(target + suffix);
                    }
                }
            }

            // Only what was moved goes back. If a move stopped half way, files that were not moved are still the module's
            // own (for example its -wal) and are left exactly as they are.
            if (progress.Kept is { } kept)
            {
                foreach (var suffix in progress.Moved)
                {
                    File.Move(kept + suffix, target + suffix);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add("데이터베이스 파일을 되돌리지 못했습니다: " + ex.Message);
        }

        var missing = before.Where(s => !File.Exists(target + s)).ToList();
        if (missing.Count > 0)
        {
            failures.Add("돌아오지 않은 파일: " + string.Join(", ", missing.Select(s => Path.GetFileName(target + s))));
        }

        var extra = DatabaseFiles.Except(before).Where(s => File.Exists(target + s)).ToList();
        if (extra.Count > 0)
        {
            failures.Add("남아 있는 새 파일: " + string.Join(", ", extra.Select(s => Path.GetFileName(target + s))));
        }

        return failures;
    }

    /// <summary>Puts the previous settings file back, or removes one the failed import added. Returns the problems.</summary>
    internal static List<string> RestoreSettings(string targetSettings, bool hadSettings, string? keptSettings, bool placed)
    {
        var failures = new List<string>();
        try
        {
            if (placed && File.Exists(targetSettings))
            {
                File.Delete(targetSettings); // the imported copy (the staged one stays in the migration folder)
            }

            if (keptSettings is not null && File.Exists(keptSettings))
            {
                File.Move(keptSettings, targetSettings);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add("설정 파일을 되돌리지 못했습니다: " + ex.Message);
        }

        if (hadSettings != File.Exists(targetSettings))
        {
            failures.Add(hadSettings ? "이전 설정 파일이 돌아오지 않았습니다." : "가져오던 설정 파일이 남아 있습니다.");
        }

        return failures;
    }

    /// <summary>A free "name.label.ext" (or "name.label-N.ext") next to <paramref name="path"/>, free for its -wal/-shm too.</summary>
    private static string UniqueAside(string path, string label)
    {
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var n = 0; ; n++)
        {
            var aside = Path.Combine(directory, n == 0 ? $"{name}.{label}{extension}" : $"{name}.{label}-{n}{extension}");
            if (!DatabaseFiles.Any(s => File.Exists(aside + s)))
            {
                return aside;
            }
        }
    }

    private static (bool Ok, string Detail) Validate(string moduleId, string database)
    {
        switch (moduleId)
        {
            case ModuleIds.LocalAi:
            {
                var v = LocalAiPerf.Storage.TelemetryStore.ValidateBackup(database);
                return (v.IsValid, $"A의 백업 검증: {v.Message} (세션 {v.Sessions})");
            }

            case ModuleIds.GpuEconomics:
            {
                var v = GpuProfit.Storage.ProfitStore.ValidateBackup(database);
                return (v.IsValid, $"B의 백업 검증: {v.Message} (세션 {v.Sessions}, GPU 샘플 {v.GpuSamples})");
            }

            case ModuleIds.Diagnostics:
            {
                var v = WinDiag.Storage.DiagnosticsStore.ValidateBackup(database);
                return (v.IsValid, $"C의 백업 검증: {v.Message} (이벤트 {v.Events})");
            }

            default:
            {
                using var c = OpenCopy(database, readOnly: true);
                var missing = new[] { "tasks", "steps", "events", "workflows" }.Where(t => !TableExists(c, t)).ToList();
                var quick = ScalarText(c, "PRAGMA quick_check;");
                var ok = missing.Count == 0 && quick == "ok";
                return (ok, ok ? $"E의 필수 테이블 확인, quick_check ok (작업 {ScalarLong(c, "SELECT COUNT(*) FROM tasks;")})" : $"필수 테이블 없음: {string.Join(", ", missing)} / {quick}");
            }
        }
    }

    /// <summary>
    /// The newest schema each module opens (A, B, C keep it internal; a test checks these against databases the modules
    /// create, and the modules' own validators re-check the version).
    /// </summary>
    internal static int SupportedSchema(string moduleId) => moduleId switch
    {
        ModuleIds.LocalAi => 2,
        ModuleIds.GpuEconomics => 1,
        ModuleIds.Diagnostics => 1,
        _ => WindowsAiOperator.Storage.OperatorStore.SchemaVersion,
    };

    /// <summary>Totals that must survive the copy exactly (beyond row counts).</summary>
    private static IEnumerable<(string Name, string Sql)> Aggregates(string moduleId) => moduleId switch
    {
        ModuleIds.LocalAi => [("세션 샘플 수 합", "SELECT COALESCE(SUM(sample_count), 0) FROM sessions;"), ("마지막 GPU 샘플 시각", "SELECT COALESCE(MAX(t_ms), 0) FROM gpu_samples;")],
        ModuleIds.GpuEconomics => [("GPU 전력 합 (W·샘플)", "SELECT printf('%.3f', COALESCE(SUM(power_w), 0)) FROM gpu_samples;"), ("마지막 샘플 시각", "SELECT COALESCE(MAX(t_ms), 0) FROM gpu_samples;"),
            ("수익 금액 (전체 목록)", "SELECT COALESCE(GROUP_CONCAT(amount, '|'), '') FROM (SELECT amount FROM revenue_entries ORDER BY id);"),
            ("비용 금액 (전체 목록)", "SELECT COALESCE(GROUP_CONCAT(amount, '|'), '') FROM (SELECT amount FROM cost_entries ORDER BY id);")],
        ModuleIds.Diagnostics => [("마지막 이벤트 시각", "SELECT COALESCE(MAX(time_ms), 0) FROM events;"), ("이벤트 묶음 수", "SELECT COUNT(DISTINCT group_key) FROM events;")],
        _ => [("마지막 작업 시각", "SELECT COALESCE(MAX(created_at), '') FROM tasks;"), ("단계 수", "SELECT COUNT(*) FROM steps;")],
    };

    private static IReadOnlyList<string> RunningOriginalProcesses(string moduleId)
    {
        var found = new List<string>();
        foreach (var name in ProcessNames(moduleId))
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0)
            {
                found.Add($"{name} (PID {string.Join(", ", processes.Select(p => p.Id))})");
            }

            foreach (var p in processes)
            {
                p.Dispose();
            }
        }

        return found;
    }

    private static void CopyShared(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }

    private static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static SqliteConnection OpenCopy(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static IEnumerable<string> Tables(SqliteConnection c)
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static bool TableExists(SqliteConnection c, string table)
    {
        using var command = c.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $n;";
        command.Parameters.AddWithValue("$n", table);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static long ScalarLong(SqliteConnection c, string sql) => Convert.ToInt64(ScalarObject(c, sql) ?? 0L, CultureInfo.InvariantCulture);

    private static string? ScalarText(SqliteConnection c, string sql) => ScalarObject(c, sql) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    private static object? ScalarObject(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void TryWriteReport(string directory, object report, string name = "rehearsal-report.json")
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The report is a convenience; the checks were returned to the caller.
        }
    }
}
