namespace WinDiag.Core.Crash;

public static class DumpStatus
{
    public const string Ok = "ok";
    public const string AccessDenied = "access_denied";
    public const string UnknownFormat = "unknown_format";
    public const string ReadError = "read_error";

    public static string Name(string status) => status switch
    {
        Ok => "헤더 읽음",
        AccessDenied => "권한 부족 (관리자 권한 필요)",
        UnknownFormat => "알 수 없는 형식",
        ReadError => "읽기 오류",
        _ => status,
    };
}

/// <summary>
/// Metadata of a crash dump file, read from its header only (read-only open, the file is never modified or deleted).
/// </summary>
public sealed record DumpFileInfo
{
    public required string Path { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public long? SizeBytes { get; init; }
    public DateTimeOffset? LastWriteTime { get; init; }
    public required string Status { get; init; }
    public string? StatusMessage { get; init; }

    /// <summary>"PAGEDU64" (64-bit kernel dump) etc.</summary>
    public string? Signature { get; init; }

    public uint? BugCheckCode { get; init; }
    public IReadOnlyList<ulong> BugCheckParameters { get; init; } = [];

    /// <summary>Crash time written by the kernel into the dump header.</summary>
    public DateTimeOffset? DumpTime { get; init; }

    public int? DumpType { get; init; }
    public int? ProcessorCount { get; init; }

    /// <summary>MinorVersion of the dump header (the Windows build number for 64-bit kernel dumps).</summary>
    public int? BuildNumber { get; init; }

    public DateTimeOffset? Seen { get; init; }

    public string DumpTypeName => DumpType switch
    {
        null => "N/A",
        1 => "전체 메모리 덤프 (1)",
        2 => "커널 메모리 덤프 (2)",
        4 => "작은 메모리 덤프/미니덤프 (4)",
        5 => "전체 메모리 덤프, 비트맵 (5)",
        6 => "커널 메모리 덤프, 비트맵 (6)",
        _ => $"유형 {DumpType}",
    };
}
