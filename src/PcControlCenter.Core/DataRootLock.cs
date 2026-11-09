namespace PcControlCenter.Core;

/// <summary>
/// One PC Control Center process per data folder. Same mechanism as projects B, C and E (DataFolderLock): an exclusively
/// opened file that the operating system releases when the process ends in any way, so a crash never blocks the next start.
/// </summary>
public sealed class DataRootLock : IDisposable
{
    public const string FileName = "pcc.lock";

    private FileStream? _stream;

    private DataRootLock(FileStream stream) => _stream = stream;

    /// <summary>Takes the lock, or returns null when another process (or another lock in this process) holds it.</summary>
    public static DataRootLock? TryAcquire(string root)
    {
        Directory.CreateDirectory(root);
        try
        {
            return new DataRootLock(new FileStream(Path.Combine(root, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.None));
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
