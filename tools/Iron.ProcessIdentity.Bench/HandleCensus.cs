using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
/// The handles of this process by object type ("Thread", "Event", "Key", "File" ...): NtQueryInformationProcess(
/// ProcessHandleInformation = 51) on the current process lists them, NtQueryObject(ObjectTypeInformation = 2) names each
/// type once. Own process only; no other process is opened.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class HandleCensus
{
    private const int ProcessHandleInformation = 51;
    private const int ObjectTypeInformation = 2;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private static readonly Dictionary<uint, string> TypeNames = [];

    public static SortedDictionary<string, int> Take()
    {
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var size = 64 * 1024;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var st = NtQueryInformationProcess(new IntPtr(-1), ProcessHandleInformation, buf, size, out var needed);
                if (st == StatusInfoLengthMismatch)
                {
                    size = Math.Max(needed + 4096, size * 2);
                    continue;
                }

                if (st != 0)
                {
                    result["(조회 실패 0x" + st.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")"] = 0;
                    return result;
                }

                var count = (long)Marshal.ReadIntPtr(buf);
                const int header = 16; // NumberOfHandles + Reserved
                const int entry = 40;  // PROCESS_HANDLE_TABLE_ENTRY_INFO on x64
                for (long i = 0; i < count; i++)
                {
                    var e = buf + header + (int)(i * entry);
                    var handle = Marshal.ReadIntPtr(e);
                    var typeIndex = (uint)Marshal.ReadInt32(e + 28);
                    var name = TypeName(handle, typeIndex);
                    result[name] = result.GetValueOrDefault(name) + 1;
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        return result;
    }

    private static string TypeName(IntPtr handle, uint index)
    {
        if (TypeNames.TryGetValue(index, out var known))
        {
            return known;
        }

        var buf = Marshal.AllocHGlobal(1024);
        try
        {
            if (NtQueryObject(handle, ObjectTypeInformation, buf, 1024, out _) == 0)
            {
                var length = Marshal.ReadInt16(buf); // UNICODE_STRING.Length (bytes)
                var text = Marshal.ReadIntPtr(buf + 8);
                var name = text == IntPtr.Zero ? "?" : Marshal.PtrToStringUni(text, length / 2);
                TypeNames[index] = name;
                return name;
            }

            return "#" + index;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Least-squares slope of y over x (per unit of x); 0 for fewer than two points.</summary>
    public static double Slope(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 2)
        {
            return 0;
        }

        var mx = points.Average(p => p.X);
        var my = points.Average(p => p.Y);
        var den = points.Sum(p => (p.X - mx) * (p.X - mx));
        return den == 0 ? 0 : points.Sum(p => (p.X - mx) * (p.Y - my)) / den;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returnLength);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryObject(IntPtr handle, int infoClass, IntPtr buffer, int length, out int returnLength);
}
