namespace WindowsAiOperator.Core;

/// <summary>
/// One process per data folder (GUI or command line). Same approach as the user's project B (DataFolderLock.cs):
/// an exclusively opened file that the operating system releases when the process ends in any way, so a stale lock
/// never blocks the next start. The file holds no data.
/// </summary>
public sealed class DataFolderLock : IDisposable
{
    public const string FileName = "wao.lock";

    private FileStream? _stream;

    private DataFolderLock(FileStream stream) => _stream = stream;

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

    private static bool IsHeldElsewhere(IOException ex) => OperatingSystem.IsWindows()
        ? (ex.HResult & 0xFFFF) is 32 or 33
        : ex.HResult is 11 or 35;

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
