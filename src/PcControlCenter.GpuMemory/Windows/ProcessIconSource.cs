using System.Runtime.InteropServices;
using static PcControlCenter.GpuMemory.Windows.NativeMethods;

namespace PcControlCenter.GpuMemory.Windows;

/// <summary>
/// Small icons of executables for the GPU memory page's table. A process's image path is read with the same minimal query
/// right as its start time (PROCESS_QUERY_LIMITED_INFORMATION) and used only to ask the shell for that file's icon; it is
/// never shown, logged or written anywhere. Protected processes and other users' processes simply get no path.
/// </summary>
public static class ProcessIconSource
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeNormal = 0x80;

    /// <summary>The full path of the process's executable, or null when it cannot be read.</summary>
    public static unsafe string? ImagePath(int pid)
    {
        if (pid <= 4)
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            const int Capacity = 1024;
            var buffer = stackalloc char[Capacity];
            var size = (uint)Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, &size) && size > 0 ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// A small icon handle for the file (the caller destroys it with <see cref="DestroyIcon"/>), or 0. With
    /// <paramref name="genericForExtension"/> the file is not touched: the shell answers with the icon of the extension
    /// (".exe" gives the generic program icon).
    /// </summary>
    public static unsafe nint SmallIcon(string path, bool genericForExtension = false)
    {
        var info = default(ShFileInfo);
        var flags = ShgfiIcon | ShgfiSmallIcon | (genericForExtension ? ShgfiUseFileAttributes : 0);
        return SHGetFileInfo(path, genericForExtension ? FileAttributeNormal : 0, &info, (uint)sizeof(ShFileInfo), flags) != 0 ? info.hIcon : 0;
    }

    public static void DestroyIcon(nint icon)
    {
        if (icon != 0)
        {
            NativeMethods.DestroyIcon(icon);
        }
    }
}
