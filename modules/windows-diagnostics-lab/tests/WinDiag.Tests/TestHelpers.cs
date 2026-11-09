using WinDiag.Core.Events;

namespace WinDiag.Tests;

internal static class Fixtures
{
    public static string Path(params string[] parts)
        => System.IO.Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    public static string Text(params string[] parts) => File.ReadAllText(Path(parts));

    public static RawEvent Event(string name) => EventXmlParser.Parse(Text("events", name));

    /// <summary>
    /// The project C root (contains docs/EVENT_CATALOG.md), found by walking up from the test binaries.
    /// PC Control Center: C now lives in modules/windows-diagnostics-lab and has no vendor/ folder any more.
    /// </summary>
    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "docs", "EVENT_CATALOG.md")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("저장소 루트를 찾지 못했습니다.");
    }
}

/// <summary>A temporary directory deleted after the test. Adapted from project A's test helpers.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wdl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Manually advanced clock for deterministic tests. Wall clock and monotonic clock advance together unless
/// <see cref="JumpWallClock"/> moves only the wall clock (a system time change).
/// </summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    private long _ticks = 1_000_000;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _ticks += by.Ticks;
    }

    public void JumpWallClock(TimeSpan by) => _now += by;
}

internal static class Ev
{
    private static long _record = 1000;

    public static RawEvent Make(string provider, int id, int level = 2, DateTimeOffset? time = null, string channel = "System", long? record = null,
        string? message = null, params (string? Name, string Value)[] data)
        => new()
        {
            Channel = channel,
            Provider = provider,
            EventId = id,
            Level = level,
            RecordId = record ?? Interlocked.Increment(ref _record),
            TimeCreated = time ?? new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero),
            Message = message,
            Data = data.Select(d => new EventDataItem(d.Name, d.Value)).ToList(),
        };
}
