using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Iron.Stage2.Windows;

/// <summary>
/// "\PhysicalDisk(_Total)\Disk Read Bytes/sec" through PDH with the English counter name (works on Korean Windows), read
/// as a standard user. The first collection only primes the rate; null until the second.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class DiskReadCounter : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private IntPtr _query;
    private IntPtr _counter;
    private bool _primed;

    private DiskReadCounter(IntPtr query, IntPtr counter)
    {
        _query = query;
        _counter = counter;
    }

    public static DiskReadCounter? TryOpen(out string? error)
    {
        error = null;
        if (PdhOpenQueryW(null, IntPtr.Zero, out var q) != 0)
        {
            error = "PDH 쿼리를 열 수 없음";
            return null;
        }

        var st = PdhAddEnglishCounterW(q, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out var c);
        if (st != 0)
        {
            PdhCloseQuery(q);
            error = "디스크 읽기 카운터 없음 (PDH 0x" + st.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")";
            return null;
        }

        return new DiskReadCounter(q, c);
    }

    /// <summary>Bytes per second since the previous call; null on the first call or on an error.</summary>
    public double? Read()
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0)
        {
            return null;
        }

        if (!_primed)
        {
            _primed = true;
            return null;
        }

        return PdhGetFormattedCounterValue(_counter, PdhFmtDouble, out _, out var value) == 0 && value.CStatus == 0 ? value.DoubleValue : null;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFmtCounterValueDouble
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(IntPtr query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValueDouble value);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(IntPtr query);
}
