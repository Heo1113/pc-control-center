namespace Suite.Core.Storage;

/// <summary>
/// One process per product data folder (same mechanism as PC Control Center's DataRootLock): an exclusively opened file
/// that the operating system releases when the process ends in any way, so a crash never blocks the next start.
/// </summary>
public sealed class DataFolderLock : IDisposable
{
    private FileStream? _stream;

    private DataFolderLock(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    public string Path { get; }

    /// <summary>Takes the lock, or returns null when another process (or another lock in this process) holds it.</summary>
    public static DataFolderLock? TryAcquire(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, fileName);
        try
        {
            return new DataFolderLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None), path);
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
