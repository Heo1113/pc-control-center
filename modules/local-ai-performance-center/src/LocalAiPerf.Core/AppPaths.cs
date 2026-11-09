namespace LocalAiPerf.Core;

/// <summary>Where the app keeps its own data. The app never writes into a runtime's (Strata's) folders.</summary>
public sealed record AppPaths(string DataDirectory, bool IsPortable, string? Note = null)
{
    public const string AppFolderName = "LocalAiPerformanceCenter";
    public const string PortableMarkerFile = "portable.txt";

    public string DatabasePath => Path.Combine(DataDirectory, "lapc.db");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string BackupDirectory => Path.Combine(DataDirectory, "backups");
    public string ExportDirectory => Path.Combine(DataDirectory, "exports");

    /// <summary>
    /// Order: explicit override; portable mode (marker next to the executable) when that folder is writable;
    /// otherwise %LOCALAPPDATA%\LocalAiPerformanceCenter.
    /// </summary>
    public static AppPaths Resolve(string? overrideDirectory, string executableDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            var full = Path.GetFullPath(overrideDirectory);
            Directory.CreateDirectory(full);
            return new AppPaths(full, IsPortable: false);
        }

        string? note = null;
        if (File.Exists(Path.Combine(executableDirectory, PortableMarkerFile)))
        {
            var portableDir = Path.Combine(executableDirectory, "data");
            if (TryEnsureWritable(portableDir))
            {
                return new AppPaths(portableDir, IsPortable: true);
            }

            note = "실행 파일 폴더에 쓸 수 없어 포터블 데이터 폴더 대신 사용자 폴더를 사용합니다.";
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        var dir = Path.Combine(local, AppFolderName);
        Directory.CreateDirectory(dir);
        return new AppPaths(dir, IsPortable: false, note);
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(ExportDirectory);
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
