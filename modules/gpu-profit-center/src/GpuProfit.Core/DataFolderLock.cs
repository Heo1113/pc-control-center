namespace GpuProfit.Core;

/// <summary>
/// One process per data folder, whether the GUI or the <c>gpc</c> command line. Copied from project C
/// (WinDiag.Core/DataFolderLock.cs, commit 06cc9e5) with the lock file name changed.
/// <para>
/// The lock is an exclusively opened file in the data folder. The operating system releases it when the process ends in
/// any way, including being killed or a crash, so a stale lock never blocks the next start. The file holds no data.
/// </para>
/// </summary>
public sealed class DataFolderLock : IDisposable
{
    public const string FileName = "gpc.lock";

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

    /// <summary>Sharing or lock violation (Windows), or the advisory lock being taken (Unix: EWOULDBLOCK).</summary>
    private static bool IsHeldElsewhere(IOException ex) => OperatingSystem.IsWindows()
        ? (ex.HResult & 0xFFFF) is 32 or 33
        : ex.HResult is 11 or 35;

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
