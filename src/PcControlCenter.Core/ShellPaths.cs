namespace PcControlCenter.Core;

/// <summary>
/// Where PC Control Center keeps its own data. Each module gets its own folder under modules\ with the same file
/// names as the original program (lapc.db, gpc.db, wdl.db, wao.db, settings.json ...), so module code runs unchanged.
/// The original programs' folders are never used as a data folder (they are only read by the migration screen).
/// </summary>
public sealed record ShellPaths(string Root, bool IsPortable, string? Note = null)
{
    public const string AppFolderName = "PcControlCenter";
    public const string PortableMarkerFile = "portable.txt";

    public string SettingsPath => Path.Combine(Root, "shell-settings.json");
    public string LogDirectory => Path.Combine(Root, "logs");
    public string ModulesDirectory => Path.Combine(Root, "modules");
    public string MigrationDirectory => Path.Combine(Root, "migration");
    public string SmokeDirectory => Path.Combine(Root, "smoke");

    public string ModuleDirectory(string moduleId)
    {
        if (!ModuleIds.All.Contains(moduleId))
        {
            throw new ArgumentOutOfRangeException(nameof(moduleId), moduleId, "알 수 없는 모듈");
        }

        return Path.Combine(ModulesDirectory, moduleId);
    }

    public string ModuleDatabasePath(string moduleId) => Path.Combine(ModuleDirectory(moduleId), ModuleIds.DatabaseFile(moduleId));

    /// <summary>
    /// Order (same rules as the original programs): explicit override; portable mode (portable.txt next to the EXE) when
    /// that folder is writable; otherwise %LOCALAPPDATA%\PcControlCenter. A folder that overlaps an original program's
    /// data folder is refused, so the integrated app can never write into lapc.db, gpc.db, wdl.db or wao.db of A/B/C/E.
    /// </summary>
    public static ShellPaths Resolve(string? overrideDirectory, string executableDirectory, IReadOnlyList<LegacyLocation>? legacy = null)
    {
        legacy ??= LegacyDataLocations.Defaults();
        ShellPaths paths;
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            paths = new ShellPaths(Path.GetFullPath(overrideDirectory), IsPortable: false);
        }
        else if (File.Exists(Path.Combine(executableDirectory, PortableMarkerFile)) && TryEnsureWritable(Path.Combine(executableDirectory, "data")))
        {
            paths = new ShellPaths(Path.Combine(executableDirectory, "data"), IsPortable: true);
        }
        else
        {
            var note = File.Exists(Path.Combine(executableDirectory, PortableMarkerFile))
                ? "실행 파일 폴더에 쓸 수 없어 포터블 데이터 폴더 대신 사용자 폴더를 사용합니다."
                : null;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            if (string.IsNullOrEmpty(local))
            {
                local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            }

            paths = new ShellPaths(Path.Combine(local, AppFolderName), IsPortable: false, note);
        }

        if (LegacyDataLocations.Overlapping(paths.Root, legacy) is { } clash)
        {
            throw new InvalidOperationException(
                $"데이터 폴더 '{paths.Root}'가 기존 프로그램 {clash.Program}의 데이터 폴더 '{clash.Directory}'와 겹칩니다. "
                + "통합 앱은 기존 프로그램의 데이터를 직접 쓰지 않으므로 다른 폴더를 지정하세요 (기존 데이터는 '데이터' 화면에서 복사본으로 가져올 수 있습니다).");
        }

        return paths;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ModulesDirectory);
        foreach (var id in ModuleIds.All)
        {
            Directory.CreateDirectory(ModuleDirectory(id));
        }
    }

    private static bool TryEnsureWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
