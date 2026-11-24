namespace WindowsAiOperator.Core.Files;

public sealed record DirectoryEntryInfo(string Name, bool IsDirectory, long? SizeBytes, DateTimeOffset LastModified, string Extension);

public sealed record DirectoryListing(string Path, IReadOnlyList<DirectoryEntryInfo> Entries, bool Truncated, int TotalCount);

/// <summary>
/// Read-only folder listing for <c>list_directory</c>: only inside folders the user granted, never system or profile
/// data folders, one level deep, names/sizes/dates only (file contents are never read). Adapted idea: UFO²'s
/// path_validator (sensitive directories, traversal checks), rewritten for this program's grant model.
/// </summary>
public static class FolderAccess
{
    public const int MaxEntries = 500;

    public static string? CheckAccess(string? requestedPath, IEnumerable<string> grantedFolders, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return "폴더 경로가 비어 있습니다.";
        }

        if (requestedPath.Contains('*') || requestedPath.Contains('?'))
        {
            return "와일드카드 경로는 허용하지 않습니다.";
        }

        if (requestedPath.StartsWith(@"\\", StringComparison.Ordinal) || requestedPath.StartsWith("//", StringComparison.Ordinal))
        {
            return "네트워크(UNC) 경로는 허용하지 않습니다.";
        }

        if (!Path.IsPathFullyQualified(requestedPath))
        {
            return "전체 경로(예: C:\\Users\\...\\Documents)를 지정해야 합니다.";
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "올바르지 않은 경로입니다.";
        }

        var sensitive = SensitiveRoots().FirstOrDefault(root => IsSameOrInside(full, root));
        if (sensitive is not null)
        {
            return "시스템·프로그램·앱 데이터 폴더는 읽지 않습니다.";
        }

        var granted = grantedFolders
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => Path.TrimEndingDirectorySeparator(Path.GetFullPath(g)))
            .Any(g => IsSameOrInside(full, g));
        if (!granted)
        {
            return "사용자가 허용한 폴더가 아닙니다. 작업 화면에서 폴더를 선택하거나 안전 설정에 추가하세요.";
        }

        fullPath = full;
        return null;
    }

    public static DirectoryListing List(string fullPath, int maxEntries = MaxEntries)
    {
        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException("폴더가 없습니다.");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
            ReturnSpecialDirectories = false,
        };
        var entries = new List<DirectoryEntryInfo>();
        var total = 0;
        foreach (var info in directory.EnumerateFileSystemInfos("*", options))
        {
            total++;
            if (entries.Count >= maxEntries)
            {
                continue;
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var isDirectory = info is DirectoryInfo;
            entries.Add(new DirectoryEntryInfo(
                info.Name,
                isDirectory,
                isDirectory ? null : ((FileInfo)info).Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                isDirectory ? string.Empty : info.Extension.ToLowerInvariant()));
        }

        return new DirectoryListing(fullPath, entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), total > entries.Count, total);
    }

    private static IEnumerable<string> SensitiveRoots()
    {
        var roots = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            roots.Add(Path.Combine(home, ".ssh"));
            roots.Add(Path.Combine(home, ".gnupg"));
            roots.Add(Path.Combine(home, ".aws"));
        }

        if (!OperatingSystem.IsWindows())
        {
            roots.AddRange(["/etc", "/bin", "/sbin", "/usr", "/boot", "/proc", "/sys", "/dev", "/root"]);
        }

        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r!)));
    }

    private static bool IsSameOrInside(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(path, root, comparison))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }
}
