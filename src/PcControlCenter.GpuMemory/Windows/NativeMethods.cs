using System.Runtime.InteropServices;

namespace PcControlCenter.GpuMemory.Windows;

/// <summary>
/// Documented Win32 functions used by the Windows provider. Every one of them works for a normal user: PDH performance
/// counters, the process list (Toolhelp), a process's start time with the minimal query right, the TCP listener table and
/// DXGI adapter descriptions. Nothing reads another process's memory, command line or files.
/// </summary>
internal static unsafe partial class NativeMethods
{
    // ---- PDH (pdh.dll) ----
    public const uint PdhFmtLarge = 0x00000400;
    public const uint PdhFmtNoCap100 = 0x00008000;
    public const uint PdhMoreData = 0x800007D2;
    public const uint PdhNoData = 0x800007D5;
    public const uint PdhCstatusNoInstance = 0x800007D1;
    public const uint PdhCstatusValidData = 0x00000000;
    public const uint PdhCstatusNewData = 0x00000001;

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    /// <summary>English counter path whatever the Windows display language (a Korean Windows names the counters in Korean).</summary>
    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll", EntryPoint = "PdhRemoveCounter")]
    public static partial uint PdhRemoveCounter(nint counter);

    [LibraryImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);

    [LibraryImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    public static partial uint PdhCloseQuery(nint query);

    /// <summary>PDH_FMT_COUNTERVALUE: DWORD status, then an 8-byte aligned union (LONGLONG with PDH_FMT_LARGE).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PdhFmtCounterValue
    {
        public uint CStatus;
        public long LargeValue;
    }

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W: the instance name (points into the same buffer) and its value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PdhFmtCounterValueItem
    {
        public nint Name;
        public PdhFmtCounterValue Value;
    }

    // ---- Processes (kernel32.dll) ----
    public const uint Th32csSnapProcess = 0x00000002;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public static readonly nint InvalidHandleValue = -1;

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32First(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32Next(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    /// <summary>FILETIME values as 64-bit integers (UTC, 100 ns since 1601).</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    // ---- TCP listeners (iphlpapi.dll) ----
    public const uint AfInet = 2;
    public const uint AfInet6 = 23;
    public const int TcpTableOwnerPidListener = 3;
    public const uint ErrorInsufficientBuffer = 122;

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedTcpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint addressFamily,
        int tableClass, uint reserved);

    /// <summary>MIB_TCPROW_OWNER_PID (addresses and ports in network byte order).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    /// <summary>MIB_TCP6ROW_OWNER_PID.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Tcp6RowOwnerPid
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public fixed byte RemoteAddr[16];
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    // ---- DXGI (dxgi.dll) ----
    public const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    public const uint DxgiAdapterFlagSoftware = 2;
    public static readonly Guid IidIdxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [LibraryImport("dxgi.dll")]
    public static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    /// <summary>DXGI_ADAPTER_DESC1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiAdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLowPart;
        public int AdapterLuidHighPart;
        public uint Flags;
    }
}
