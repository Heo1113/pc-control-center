using System.Buffers.Binary;
using System.Text;
using WinDiag.Core.Crash;

namespace WinDiag.Collection.Crash;

/// <summary>
/// Reads the header of Windows crash dump files. Only the documented/publicly described fields of the 64-bit kernel
/// dump header (DUMP_HEADER64) are interpreted: signature, bug check code and parameters, and — when they pass
/// plausibility checks — dump type, processor count and the crash time. Nothing beyond the header is analysed:
/// the presence of a dump does not identify a cause.
/// </summary>
public static class DumpHeaderParser
{
    public const int HeaderSize = 0x2000;

    private const int OffsetMinorVersion = 0x00C;
    private const int OffsetMachineType = 0x030;
    private const int OffsetProcessors = 0x034;
    private const int OffsetBugCheckCode = 0x038;
    private const int OffsetParameters = 0x040;
    private const int OffsetDumpType = 0xF98;
    private const int OffsetSystemTime = 0xFA8;

    public static DumpFileInfo Parse(string path, ReadOnlySpan<byte> header, long? size, DateTimeOffset? lastWrite, DateTimeOffset now)
    {
        var info = new DumpFileInfo { Path = path, SizeBytes = size, LastWriteTime = lastWrite, Status = DumpStatus.UnknownFormat, Seen = now };
        if (header.Length < 8)
        {
            return info with { StatusMessage = "파일이 너무 작습니다." };
        }

        var signature = Encoding.ASCII.GetString(header[..8]);
        if (signature.StartsWith("MDMP", StringComparison.Ordinal))
        {
            return info with { Signature = "MDMP", StatusMessage = "사용자 모드 미니덤프입니다 (프로그램 충돌 덤프, BSOD 덤프 아님)." };
        }

        if (signature == "PAGEDUMP")
        {
            return info with { Signature = signature, StatusMessage = "32비트 커널 덤프입니다. 이 버전은 64비트 덤프 헤더만 해석합니다." };
        }

        if (signature != "PAGEDU64")
        {
            return info with { Signature = Printable(signature), StatusMessage = "알려진 덤프 서명(PAGEDU64)이 아닙니다." };
        }

        if (header.Length < OffsetParameters + 32)
        {
            return info with { Signature = signature, Status = DumpStatus.ReadError, StatusMessage = "헤더가 잘려 있습니다." };
        }

        var machine = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetMachineType..]);
        var processors = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetProcessors..]);
        var code = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetBugCheckCode..]);
        var parameters = new List<ulong>(4);
        for (var i = 0; i < 4; i++)
        {
            parameters.Add(BinaryPrimitives.ReadUInt64LittleEndian(header[(OffsetParameters + 8 * i)..]));
        }

        int? dumpType = null;
        DateTimeOffset? dumpTime = null;
        if (header.Length >= OffsetSystemTime + 8)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetDumpType..]);
            dumpType = type is >= 1 and <= 7 ? (int)type : null;
            var fileTime = BinaryPrimitives.ReadInt64LittleEndian(header[OffsetSystemTime..]);
            if (fileTime > 0)
            {
                try
                {
                    var time = DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
                    dumpTime = time.Year >= 2000 && time <= now.AddDays(1) ? time : null;
                }
                catch (ArgumentOutOfRangeException)
                {
                    dumpTime = null;
                }
            }
        }

        var build = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetMinorVersion..]);
        return info with
        {
            Signature = signature,
            Status = DumpStatus.Ok,
            StatusMessage = machine is 0x8664 or 0xAA64 ? null : $"알 수 없는 머신 유형 0x{machine:X}",
            BugCheckCode = code,
            BugCheckParameters = parameters,
            DumpType = dumpType,
            DumpTime = dumpTime,
            ProcessorCount = processors is > 0 and <= 4096 ? (int)processors : null,
            BuildNumber = build is > 0 and < 100_000 ? (int)build : null,
        };
    }

    private static string Printable(string text) => new(text.Select(c => c is >= ' ' and <= '~' ? c : '.').ToArray());
}

/// <summary>Where to look for dumps, and which folders could not be read.</summary>
public sealed record DumpScanResult(IReadOnlyList<DumpFileInfo> Files, IReadOnlyList<string> Notes);

/// <summary>
/// Lists crash dumps in the standard Windows locations (%SystemRoot%\Minidump, %SystemRoot%\MEMORY.DMP and
/// %SystemRoot%\LiveKernelReports) and reads their headers. Files are opened read-only with sharing; they are never
/// modified, moved or deleted. A folder the user may not read is reported as such.
/// </summary>
public sealed class DumpScanner(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static IReadOnlyList<string> DefaultLocations()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows))
        {
            return [];
        }

        return [Path.Combine(windows, "Minidump"), Path.Combine(windows, "MEMORY.DMP"), Path.Combine(windows, "LiveKernelReports")];
    }

    public DumpScanResult Scan(IReadOnlyList<string>? locations = null, int maxFiles = 200)
    {
        locations ??= DefaultLocations();
        var files = new List<DumpFileInfo>();
        var notes = new List<string>();
        var now = _time.GetUtcNow();
        foreach (var location in locations)
        {
            try
            {
                if (File.Exists(location))
                {
                    files.Add(ReadOne(location, now));
                }
                else if (Directory.Exists(location))
                {
                    foreach (var path in Directory.EnumerateFiles(location, "*.dmp", SearchOption.AllDirectories).Take(maxFiles))
                    {
                        files.Add(ReadOne(path, now));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                notes.Add($"{location}: 폴더를 읽을 권한이 없습니다 (관리자 권한 필요). 덤프 존재 여부를 확인할 수 없습니다.");
            }
            catch (Exception ex) when (ex is IOException or System.Security.SecurityException)
            {
                notes.Add($"{location}: 읽을 수 없습니다 ({ex.Message}).");
            }
        }

        if (files.Count == 0 && notes.Count == 0)
        {
            notes.Add("표준 위치에서 덤프 파일을 찾지 못했습니다.");
        }

        return new DumpScanResult(files, notes);
    }

    private static DumpFileInfo ReadOne(string path, DateTimeOffset now)
    {
        long? size = null;
        DateTimeOffset? lastWrite = null;
        try
        {
            var info = new FileInfo(path);
            size = info.Length;
            lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[DumpHeaderParser.HeaderSize];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return DumpHeaderParser.Parse(path, buffer.AsSpan(0, read), size, lastWrite, now);
        }
        catch (UnauthorizedAccessException)
        {
            return new DumpFileInfo { Path = path, SizeBytes = size, LastWriteTime = lastWrite, Status = DumpStatus.AccessDenied, Seen = now,
                StatusMessage = "파일은 있지만 내용을 읽을 권한이 없습니다 (관리자 권한 필요)." };
        }
        catch (IOException ex)
        {
            return new DumpFileInfo { Path = path, SizeBytes = size, LastWriteTime = lastWrite, Status = DumpStatus.ReadError, Seen = now, StatusMessage = ex.Message };
        }
    }
}
