namespace Suite.Core.Storage;

/// <summary>A folder a suite product must never use for its own data (another program's or another product's data).</summary>
public sealed record ReservedDataLocation(string Owner, string Directory);

/// <summary>
/// The suite's data folder. Each product keeps everything in its own subfolder <c>&lt;root&gt;\&lt;product&gt;\</c>
/// (for IRON: <c>data\iron\iron-settings.json</c> and <c>data\iron\iron-telemetry.db</c>), separate from PC Control
/// Center's module databases and from the original programs' folders.
/// </summary>
public sealed record ProductDataRoot(string Root, bool IsPortable, string? Note = null)
{
    public const string PortableMarkerFile = "portable.txt";
    public const string PortableDataFolder = "data";

    /// <summary>The installed (non-portable) location: <c>%LOCALAPPDATA%\PcControlSuite\data</c>.</summary>
    public const string SuiteFolderName = "PcControlSuite";

    /// <summary>
    /// Order (as PC Control Center's ShellPaths): explicit override; portable mode (portable.txt next to the EXE) when the
    /// <c>data</c> folder there is writable; otherwise <c>%LOCALAPPDATA%\PcControlSuite\data</c>.
    /// </summary>
    public static ProductDataRoot Resolve(string? overrideDirectory, string executableDirectory, string? localAppData = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return new ProductDataRoot(Path.GetFullPath(overrideDirectory), IsPortable: false);
        }

        var marker = File.Exists(Path.Combine(executableDirectory, PortableMarkerFile));
        var portable = Path.Combine(executableDirectory, PortableDataFolder);
        if (marker && TryEnsureWritable(portable))
        {
            return new ProductDataRoot(Path.GetFullPath(portable), IsPortable: true);
        }

        var note = marker ? "실행 파일 폴더에 쓸 수 없어 포터블 데이터 폴더 대신 사용자 폴더를 사용합니다." : null;
        return new ProductDataRoot(Path.Combine(LocalAppData(localAppData), SuiteFolderName, PortableDataFolder), IsPortable: false, note);
    }

    /// <summary>
    /// The product's own folder. Refused (exception) when it is, contains or lies inside a reserved folder, so a product
    /// can never write next to or into another program's database.
    /// </summary>
    public string ProductDirectory(string productId, IEnumerable<ReservedDataLocation> reserved)
    {
        if (string.IsNullOrWhiteSpace(productId) || productId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || productId.Contains('.') || productId != productId.ToLowerInvariant())
        {
            throw new ArgumentException("제품 ID는 소문자 폴더 이름이어야 합니다.", nameof(productId));
        }

        var directory = Path.GetFullPath(Path.Combine(Root, productId));
        if (Overlapping(directory, reserved) is { } clash)
        {
            throw new InvalidOperationException(
                $"데이터 폴더 '{directory}'가 {clash.Owner}의 데이터 폴더 '{clash.Directory}'와 겹칩니다. 다른 프로그램의 데이터와 섞이지 않도록 다른 폴더를 지정하세요.");
        }

        return directory;
    }

    /// <summary>True when <paramref name="directory"/> is, contains or lies inside one of <paramref name="reserved"/>.</summary>
    public static ReservedDataLocation? Overlapping(string directory, IEnumerable<ReservedDataLocation> reserved)
    {
        var full = Normalize(directory);
        foreach (var location in reserved)
        {
            var other = Normalize(location.Directory);
            if (IsSameOrInside(full, other) || IsSameOrInside(other, full))
            {
                return location;
            }
        }

        return null;
    }

    /// <summary>
    /// Folders no suite product may use for its own data: the original programs' folders (A, B, C, E) and PC Control
    /// Center's module folders, both installed (<c>%LOCALAPPDATA%</c>) and portable (next to the EXE).
    /// </summary>
    public static IReadOnlyList<ReservedDataLocation> KnownReserved(string executableDirectory, string? localAppData = null)
    {
        var local = LocalAppData(localAppData);
        return
        [
            new("Local AI Performance Center", Path.Combine(local, "LocalAiPerformanceCenter")),
            new("GPU Profit & Compute Center", Path.Combine(local, "GpuProfitCenter")),
            new("Windows Diagnostics Lab", Path.Combine(local, "WindowsDiagnosticsLab")),
            new("Windows AI Operator", Path.Combine(local, "WindowsAiOperator")),
            new("PC Control Center (모듈 DB)", Path.Combine(local, "PcControlCenter", "modules")),
            new("PC Control Center (포터블 모듈 DB)", Path.Combine(executableDirectory, PortableDataFolder, "modules")),
        ];
    }

    private static string LocalAppData(string? localAppData)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(localAppData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : localAppData;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;

    private static bool IsSameOrInside(string path, string folder)
        => path.StartsWith(folder, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
