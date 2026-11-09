namespace PcControlCenter.Core;

/// <summary>A data folder of one of the original programs (A, B, C, E). This app only ever reads these.</summary>
public sealed record LegacyLocation(string ModuleId, string Program, string Directory, string DatabaseFile)
{
    public string DatabasePath => Path.Combine(Directory, DatabaseFile);
}

/// <summary>
/// Default data folders of the four original programs (their AppPaths: %LOCALAPPDATA%\&lt;AppFolderName&gt;). Portable
/// installations keep data next to their EXE and can be chosen by hand on the data screen.
/// </summary>
public static class LegacyDataLocations
{
    public static IReadOnlyList<LegacyLocation> Defaults(string? localAppData = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return
        [
            new LegacyLocation(ModuleIds.LocalAi, "Local AI Performance Center", Path.Combine(localAppData, "LocalAiPerformanceCenter"), "lapc.db"),
            new LegacyLocation(ModuleIds.GpuEconomics, "GPU Profit & Compute Center", Path.Combine(localAppData, "GpuProfitCenter"), "gpc.db"),
            new LegacyLocation(ModuleIds.Diagnostics, "Windows Diagnostics Lab", Path.Combine(localAppData, "WindowsDiagnosticsLab"), "wdl.db"),
            new LegacyLocation(ModuleIds.Operator, "Windows AI Operator", Path.Combine(localAppData, "WindowsAiOperator"), "wao.db"),
        ];
    }

    /// <summary>True when <paramref name="directory"/> is, contains or lies inside one of the original programs' folders.</summary>
    public static LegacyLocation? Overlapping(string directory, IEnumerable<LegacyLocation> locations)
    {
        var full = Normalize(directory);
        foreach (var location in locations)
        {
            var legacy = Normalize(location.Directory);
            if (IsSameOrInside(full, legacy) || IsSameOrInside(legacy, full))
            {
                return location;
            }
        }

        return null;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;

    private static bool IsSameOrInside(string path, string folder)
        => path.StartsWith(folder, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
