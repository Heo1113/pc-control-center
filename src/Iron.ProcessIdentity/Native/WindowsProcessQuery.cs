using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using static Iron.ProcessIdentity.Native.NativeProcessInterop;

namespace Iron.ProcessIdentity;

/// <summary>
/// Reads process identity facts on Windows with standard-user rights. Every call fails soft: a denied or vanished
/// process gives null and a reason ("권한 부족", "종료됨"), never an exception, a UAC prompt or a driver.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessQuery : INativeProcessQuery, IDisposable
{
    private const int MaxModules = 2048;
    private readonly Lock _snapLock = new();
    private readonly ConcurrentDictionary<string, string> _indirect = new(StringComparer.Ordinal);
    private IntPtr _snapBuffer;
    private int _snapSize = 1 << 20;

    // ---------- process list ----------

    public IReadOnlyList<ProcessEntry> Snapshot()
    {
        if (IntPtr.Size != 8)
        {
            return ToolhelpSnapshot();
        }

        lock (_snapLock)
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (_snapBuffer == IntPtr.Zero)
                {
                    _snapBuffer = Marshal.AllocHGlobal(_snapSize);
                }

                var st = NtQuerySystemInformation(SystemProcessInformation, _snapBuffer, _snapSize, out var need);
                if (st is STATUS_INFO_LENGTH_MISMATCH or STATUS_BUFFER_TOO_SMALL)
                {
                    Marshal.FreeHGlobal(_snapBuffer);
                    _snapBuffer = IntPtr.Zero;
                    _snapSize = Math.Max(need + (1 << 16), _snapSize * 2);
                    continue;
                }

                if (st != STATUS_SUCCESS)
                {
                    break;
                }

                // SYSTEM_PROCESS_INFORMATION (x64): NextEntryOffset @0, CreateTime @32, ImageName (UNICODE_STRING) @56,
                // UniqueProcessId @80, InheritedFromUniqueProcessId @88.
                var list = new List<ProcessEntry>(512);
                var offset = 0;
                while (true)
                {
                    var p = _snapBuffer + offset;
                    var next = Marshal.ReadInt32(p, 0);
                    var create = Marshal.ReadInt64(p, 32);
                    var nameLen = (ushort)Marshal.ReadInt16(p, 56);
                    var nameBuf = Marshal.ReadIntPtr(p, 64);
                    var pid = (int)Marshal.ReadIntPtr(p, 80);
                    var ppid = (int)Marshal.ReadIntPtr(p, 88);
                    var name = nameBuf == IntPtr.Zero ? (pid == 0 ? "System Idle Process" : "System") : Marshal.PtrToStringUni(nameBuf, nameLen / 2);
                    list.Add(new ProcessEntry(pid, ppid, name, create));
                    if (next == 0)
                    {
                        break;
                    }

                    offset += next;
                }

                return list;
            }
        }

        return ToolhelpSnapshot();
    }

    private static List<ProcessEntry> ToolhelpSnapshot()
    {
        var list = new List<ProcessEntry>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE)
        {
            return list;
        }

        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref e))
            {
                do
                {
                    list.Add(new ProcessEntry((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile, 0));
                }
                while (Process32NextW(snap, ref e));
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return list;
    }

    // ---------- per process ----------

    private static string Reason(int error) => error switch
    {
        ERROR_ACCESS_DENIED => "권한 부족",
        ERROR_INVALID_PARAMETER => "종료됨",
        _ => "오류 " + error.ToString(CultureInfo.InvariantCulture),
    };

    private static IntPtr Open(int pid, uint access, out string? failure)
    {
        var h = OpenProcess(access, false, pid);
        failure = h == IntPtr.Zero ? Reason(Marshal.GetLastWin32Error()) : null;
        return h;
    }

    public string? QueryImagePath(int pid, out string? failure)
    {
        var h = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION, out failure);
        if (h == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref size))
            {
                return sb.ToString(0, (int)size);
            }

            failure = Reason(Marshal.GetLastWin32Error());
            return null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>ProcessCommandLineInformation (60): works with PROCESS_QUERY_LIMITED_INFORMATION; the buffer grows to the reported size.</summary>
    public string? QueryCommandLine(int pid, out string? failure)
    {
        var h = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION, out failure);
        if (h == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = 1024;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    var st = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, size, out var ret);
                    if (st is STATUS_INFO_LENGTH_MISMATCH or STATUS_BUFFER_TOO_SMALL or STATUS_BUFFER_OVERFLOW)
                    {
                        size = ret > size ? ret : size * 2;
                        if (size > 1 << 20)
                        {
                            break;
                        }

                        continue;
                    }

                    if (st != STATUS_SUCCESS)
                    {
                        failure = st == unchecked((int)0xC0000022) ? "권한 부족" : "NTSTATUS 0x" + st.ToString("X8", CultureInfo.InvariantCulture);
                        return null;
                    }

                    var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
                    return us.Buffer == IntPtr.Zero || us.Length == 0 ? "" : Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }

            failure = "버퍼 한도 초과";
            return null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public (string? PackageFullName, string? AppUserModelId) QueryPackage(int pid)
    {
        var h = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION, out _);
        if (h == IntPtr.Zero)
        {
            return (null, null);
        }

        try
        {
            return (Package(h, GetPackageFullName), Package(h, GetApplicationUserModelId));
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private delegate int PackageQuery(IntPtr process, ref uint length, StringBuilder? value);

    private static string? Package(IntPtr h, PackageQuery query)
    {
        uint len = 0;
        var rc = query(h, ref len, null);
        if (rc != ERROR_INSUFFICIENT_BUFFER || len == 0)
        {
            return null; // APPMODEL_ERROR_NO_PACKAGE / NO_APPLICATION: not a packaged app
        }

        var sb = new StringBuilder((int)len);
        return query(h, ref len, sb) == 0 ? sb.ToString() : null;
    }

    public IReadOnlyDictionary<int, IReadOnlyList<ServiceEntry>>? QueryServices()
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Enumerate(scm, SERVICE_WIN32 | SERVICE_USER_SERVICE | SERVICE_USERSERVICE_INSTANCE) ?? Enumerate(scm, SERVICE_WIN32);
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private Dictionary<int, IReadOnlyList<ServiceEntry>>? Enumerate(IntPtr scm, uint types)
    {
        var map = new Dictionary<int, List<ServiceEntry>>();
        uint resume = 0;
        var size = 64 * 1024;
        var entrySize = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESSW>();
        for (var round = 0; round < 64; round++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var ok = EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, types, SERVICE_ACTIVE, buf, (uint)size, out var needed, out var count, ref resume, null);
                var err = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && err != ERROR_MORE_DATA)
                {
                    return null;
                }

                for (var i = 0; i < count; i++)
                {
                    var e = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESSW>(buf + i * entrySize);
                    var pid = (int)e.Status.ProcessId;
                    if (pid == 0)
                    {
                        continue;
                    }

                    var name = Marshal.PtrToStringUni(e.ServiceName) ?? "";
                    var display = LoadIndirect(Marshal.PtrToStringUni(e.DisplayName) ?? name);
                    if (!map.TryGetValue(pid, out var list))
                    {
                        map[pid] = list = [];
                    }

                    list.Add(new ServiceEntry(name, display));
                }

                if (ok)
                {
                    break;
                }

                size = (int)Math.Max(needed + 4096, (uint)size);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ServiceEntry>)kv.Value);
    }

    public IReadOnlyList<string>? QueryModulePaths(int pid)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
            if (snap == INVALID_HANDLE_VALUE)
            {
                if (Marshal.GetLastWin32Error() == 24)
                {
                    continue; // ERROR_BAD_LENGTH: the module list changed while reading
                }

                return null;
            }

            try
            {
                var list = new List<string>();
                var e = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
                if (Module32FirstW(snap, ref e))
                {
                    do
                    {
                        list.Add(e.szExePath);
                    }
                    while (list.Count < MaxModules && Module32NextW(snap, ref e));
                }

                return list;
            }
            finally
            {
                CloseHandle(snap);
            }
        }

        return null;
    }

    // ---------- files ----------

    public long? FileStamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.LastWriteTimeUtc.Ticks * 31 + fi.Length : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Version strings with Korean MUI first (FILE_VER_GET_LOCALISED | FILE_VER_GET_NEUTRAL), then the icon.</summary>
    public FileIdentityInfo? ReadFile(string path)
    {
        if (!FileExists(path))
        {
            return null;
        }

        string? desc = null, product = null, company = null, version = null, language = null;
        try
        {
            const uint flags = FILE_VER_GET_LOCALISED | FILE_VER_GET_NEUTRAL;
            var size = GetFileVersionInfoSizeExW(flags, path, out _);
            if (size > 0)
            {
                var data = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetFileVersionInfoExW(flags, path, 0, size, data))
                    {
                        var (lang, cp) = ChooseTranslation(data);
                        string? Q(string key) =>
                            VerQueryValueW(data, $"\\StringFileInfo\\{lang:X4}{cp:X4}\\{key}", out var v, out var len) && len > 1
                                ? Marshal.PtrToStringUni(v)?.Trim() is { Length: > 0 } s ? s : null
                                : null;
                        desc = Q("FileDescription");
                        product = Q("ProductName");
                        company = Q("CompanyName");
                        version = Q("FileVersion");
                        try
                        {
                            language = lang == 0 ? "neutral" : CultureInfo.GetCultureInfo(lang).Name;
                        }
                        catch (CultureNotFoundException)
                        {
                            language = lang.ToString("X4", CultureInfo.InvariantCulture);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(data);
                }
            }
        }
        catch (Exception)
        {
            // no version resource
        }

        return new FileIdentityInfo(path, desc, product, company, version, language, ReadIcon(path));
    }

    private static (ushort Lang, ushort Cp) ChooseTranslation(IntPtr data)
    {
        var pairs = new List<(ushort, ushort)>();
        if (VerQueryValueW(data, "\\VarFileInfo\\Translation", out var p, out var len) && len >= 4)
        {
            for (var i = 0; i < len / 4; i++)
            {
                pairs.Add(((ushort)Marshal.ReadInt16(p, i * 4), (ushort)Marshal.ReadInt16(p, i * 4 + 2)));
            }
        }

        if (pairs.Count == 0)
        {
            return (0x0409, 0x04B0);
        }

        var ui = GetUserDefaultUILanguage();
        foreach (var want in new ushort[] { 0x0412, ui, 0x0409, 0x0000 })
        {
            foreach (var pr in pairs)
            {
                if (pr.Item1 == want)
                {
                    return pr;
                }
            }
        }

        return pairs[0];
    }

    /// <summary>Large icon as BGRA pixels; every HICON and bitmap is released here (GDI handles do not leak).</summary>
    private static IconPixels? ReadIcon(string path)
    {
        var icon = IntPtr.Zero;
        try
        {
            if (ExtractIconExW(path, 0, out var large, out var small, 1) > 0)
            {
                if (small != IntPtr.Zero)
                {
                    DestroyIcon(small);
                }

                icon = large;
            }

            if (icon == IntPtr.Zero)
            {
                var info = new SHFILEINFOW();
                if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON | SHGFI_LARGEICON) != IntPtr.Zero)
                {
                    icon = info.hIcon;
                }
            }

            return icon == IntPtr.Zero ? null : IconToPixels(icon);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (icon != IntPtr.Zero)
            {
                DestroyIcon(icon);
            }
        }
    }

    internal static IconPixels? IconToPixels(IntPtr icon)
    {
        if (!GetIconInfo(icon, out var ii))
        {
            return null;
        }

        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (ii.hbmColor == IntPtr.Zero || GetObjectW(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0 || bm.bmWidth > 256)
            {
                return null;
            }

            var w = bm.bmWidth;
            var h = bm.bmHeight;
            var header = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[w * h * 4];
            if (GetDIBits(dc, ii.hbmColor, 0, (uint)h, bits, ref header, 0) == 0)
            {
                return null;
            }

            var hasAlpha = false;
            for (var i = 3; i < bits.Length; i += 4)
            {
                if (bits[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            if (!hasAlpha && ii.hbmMask != IntPtr.Zero)
            {
                var mask = new byte[w * h * 4];
                var mh = new BITMAPINFOHEADER { biSize = header.biSize, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                if (GetDIBits(dc, ii.hbmMask, 0, (uint)h, mask, ref mh, 0) != 0)
                {
                    for (var i = 0; i < bits.Length; i += 4)
                    {
                        bits[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
                    }
                }
            }

            return new IconPixels(w, h, bits);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
            if (ii.hbmColor != IntPtr.Zero)
            {
                DeleteObject(ii.hbmColor);
            }

            if (ii.hbmMask != IntPtr.Zero)
            {
                DeleteObject(ii.hbmMask);
            }
        }
    }

    // ---------- registry / indirect strings ----------

    /// <summary>"@%SystemRoot%\system32\xyz.dll,-123" → the localized (Korean MUI) string; anything else unchanged.</summary>
    public string LoadIndirect(string value)
    {
        if (!value.StartsWith('@'))
        {
            return value;
        }

        if (_indirect.TryGetValue(value, out var hit))
        {
            return hit;
        }

        var sb = new StringBuilder(1024);
        var s = SHLoadIndirectString(value, sb, (uint)sb.Capacity, IntPtr.Zero) >= 0 && sb.Length > 0 ? sb.ToString() : value;
        if (_indirect.Count > 1024)
        {
            _indirect.Clear();
        }

        _indirect[value] = s;
        return s;
    }

    public ComAppIdInfo? LookupAppId(Guid id)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"AppID\" + id.ToString("B"));
            if (key is null)
            {
                return null;
            }

            var name = key.GetValue(null) as string;
            return new ComAppIdInfo(id, name is null ? null : LoadIndirect(name), key.GetValue("DllSurrogate") is not null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public ComClsidInfo? LookupClsid(Guid id)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + id.ToString("B"));
            if (key is null)
            {
                return null;
            }

            var name = key.GetValue("LocalizedString") as string ?? key.GetValue(null) as string;
            using var inproc = key.OpenSubKey("InprocServer32");
            var server = inproc?.GetValue(null) as string;
            Guid? appId = key.GetValue("AppID") is string a && Guid.TryParse(a, out var g) ? g : null;
            return new ComClsidInfo(id, name is null ? null : LoadIndirect(name), server is null ? null : Environment.ExpandEnvironmentVariables(server), appId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public FileIdentityInfo? GetFile(string path) => ReadFile(path);

    public string? ResolveSystemPath(string fileName)
    {
        try
        {
            var p = Path.Combine(Environment.SystemDirectory, fileName);
            return File.Exists(p) ? p : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public string? ReadPackageName(string directory)
    {
        try
        {
            var dir = directory;
            for (var i = 0; i < 3 && !string.IsNullOrEmpty(dir); i++)
            {
                var f = new FileInfo(Path.Combine(dir, "package.json"));
                if (f.Exists && f.Length < 256 * 1024)
                {
                    using var s = f.OpenRead();
                    using var doc = JsonDocument.Parse(s);
                    return doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception)
        {
            // unreadable package.json
        }

        return null;
    }

    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_snapLock)
        {
            if (_snapBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_snapBuffer);
                _snapBuffer = IntPtr.Zero;
            }
        }
    }
}
