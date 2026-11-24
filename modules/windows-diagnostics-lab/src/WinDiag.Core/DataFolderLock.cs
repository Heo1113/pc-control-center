namespace WinDiag.Core;

/// <summary>
/// One process per data folder, whether the GUI or the <c>wdl</c> command line. A second process on the same database
/// would close the running one's session as "ended uncleanly" when it starts, and both would collect the same logs.
/// <para>
/// The lock is an exclusively opened file (<c>wdl.lock</c>) in the data folder. The operating system releases it when
/// the process ends in any way, including being killed or a crash, so a stale lock never blocks the next start, and
/// unlike a mutex it is not tied to the thread that took it. The file is left in place; it holds no data.
/// </para>
/// </summary>
public sealed class DataFolderLock : IDisposable
{
    public const string FileName = "wdl.lock";

    private FileStream? _stream;

    private DataFolderLock(FileStream stream) => _stream = stream;

    /// <summary>Takes the lock, or returns null when another process (or another lock in this process) holds it.</summary>
    public static DataFolderLock? TryAcquire(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        try
        {
            return new DataFolderLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None));
        }
        catch (IOException ex) when (IsHeldElsewhere(ex))
        {
            return null;
        }
    }

    /// <summary>Sharing or lock violation (Windows), or the advisory lock being taken (Unix: EWOULDBLOCK). Other I/O errors are real errors.</summary>
    private static bool IsHeldElsewhere(IOException ex) => OperatingSystem.IsWindows()
        ? (ex.HResult & 0xFFFF) is 32 or 33
        : ex.HResult is 11 or 35;

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
