using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalAiPerf.Core.Hardware;
using static PcControlCenter.GpuMemory.Windows.NativeMethods;

namespace PcControlCenter.GpuMemory.Windows;

/// <summary>Process names, parents and start times, read right after a counter sample (normal user rights only).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class ProcessQueries
{
    private const string Source = "Windows 프로세스 목록";

    /// <summary>Every running process: PID → (parent PID, executable file name). Null when the snapshot cannot be taken.</summary>
    public static Dictionary<int, (int ParentId, string Name)>? List(ICollection<SensorIssue> issues)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue || snapshot == 0)
        {
            issues.Add(new SensorIssue(Source, "processes", SensorStatus.Error,
                $"프로세스 목록을 읽지 못했습니다 (Win32 {Marshal.GetLastPInvokeError()}). 프로세스 이름은 N/A로, PID만 표시합니다."));
            return null;
        }

        try
        {
            var result = new Dictionary<int, (int, string)>();
            ProcessEntry32 entry = default;
            entry.dwSize = (uint)sizeof(ProcessEntry32);
            for (var ok = Process32First(snapshot, &entry); ok; ok = Process32Next(snapshot, &entry))
            {
                var name = DxgiAdapters.Text(new ReadOnlySpan<char>(entry.szExeFile, 260));
                result[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, name);
                entry.dwSize = (uint)sizeof(ProcessEntry32);
            }

            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>
    /// The start time of a process, or null when it cannot be read (protected processes and other users' processes refuse
    /// even the minimal query right). Opens the process with PROCESS_QUERY_LIMITED_INFORMATION only.
    /// </summary>
    public static DateTimeOffset? StartTime(int pid)
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
            return GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0
                ? new DateTimeOffset(DateTime.FromFileTimeUtc(creation), TimeSpan.Zero)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Builds the process table of a sample: the GPU processes, the TCP listener owners and their ancestors (up to
    /// <see cref="ServerProcessLinker.MaxDepth"/> levels), each with its identity state relative to the sample time.
    /// </summary>
    public static Dictionary<int, ProcessEntry> Table(IEnumerable<int> pids, DateTimeOffset sampleTime, ICollection<SensorIssue> issues)
    {
        var table = new Dictionary<int, ProcessEntry>();
        var all = List(issues);
        foreach (var pid in pids.Distinct())
        {
            var current = pid;
            for (var level = 0; level <= ServerProcessLinker.MaxDepth && !table.ContainsKey(current); level++)
            {
                var entry = Describe(current, all, sampleTime);
                table[current] = entry;
                if (entry.State == ProcessIdentityState.Exited || entry.ParentProcessId == current || entry.ParentProcessId <= 4)
                {
                    break;
                }

                current = entry.ParentProcessId;
            }
        }

        return table;
    }

    private static ProcessEntry Describe(int pid, Dictionary<int, (int ParentId, string Name)>? all, DateTimeOffset sampleTime)
    {
        if (pid == 0)
        {
            return new ProcessEntry(0, 0, "System Idle Process", null, ProcessIdentityState.StartTimeUnavailable);
        }

        if (pid == 4)
        {
            return new ProcessEntry(4, 0, "System", null, ProcessIdentityState.StartTimeUnavailable);
        }

        if (all is null)
        {
            // No process list: the start time still tells whether the counters are this process's; the parent stays unknown.
            var alone = StartTime(pid);
            return new ProcessEntry(pid, 0, $"PID {pid}", alone, StateOf(alone, sampleTime));
        }

        if (!all.TryGetValue(pid, out var known))
        {
            return new ProcessEntry(pid, 0, "(종료됨)", null, ProcessIdentityState.Exited);
        }

        var start = StartTime(pid);
        return new ProcessEntry(pid, known.ParentId, known.Name.Length == 0 ? $"PID {pid}" : known.Name, start, StateOf(start, sampleTime));
    }

    private static ProcessIdentityState StateOf(DateTimeOffset? start, DateTimeOffset sampleTime)
        => start is null ? ProcessIdentityState.StartTimeUnavailable
            : start > sampleTime ? ProcessIdentityState.ReusedAfterSample
            : ProcessIdentityState.Verified;

    /// <summary>
    /// TCP sockets in the listening state that accept loopback connections (bound to a loopback or the any address), with
    /// their owning PID, for IPv4 and IPv6. Sockets bound to one LAN address cannot serve 127.0.0.1 and are left out.
    /// </summary>
    public static List<TcpListenerEntry> Listeners(ICollection<SensorIssue> issues)
    {
        var result = new List<TcpListenerEntry>();
        ReadTable(AfInet, issues, result);
        ReadTable(AfInet6, issues, result);
        return result;
    }

    private static void ReadTable(uint family, ICollection<SensorIssue> issues, List<TcpListenerEntry> result)
    {
        uint size = 0;
        var status = GetExtendedTcpTable(0, ref size, false, family, TcpTableOwnerPidListener, 0);
        for (var attempt = 0; attempt < 4 && status == ErrorInsufficientBuffer; attempt++)
        {
            size += 4096; // the table can grow between the two calls
            var buffer = NativeMemory.Alloc(size);
            try
            {
                status = GetExtendedTcpTable((nint)buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (status != 0)
                {
                    continue;
                }

                var count = *(uint*)buffer;
                if (family == AfInet)
                {
                    var rows = (TcpRowOwnerPid*)((byte*)buffer + sizeof(uint));
                    for (var i = 0; i < count; i++)
                    {
                        var address = new IPAddress(rows[i].LocalAddr);
                        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
                        {
                            result.Add(new TcpListenerEntry(Port(rows[i].LocalPort), (int)rows[i].OwningPid, IsIpv6: false, IPAddress.IsLoopback(address)));
                        }
                    }
                }
                else
                {
                    var rows = (Tcp6RowOwnerPid*)((byte*)buffer + sizeof(uint));
                    for (var i = 0; i < count; i++)
                    {
                        var address = new IPAddress(new ReadOnlySpan<byte>(rows[i].LocalAddr, 16));
                        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any))
                        {
                            result.Add(new TcpListenerEntry(Port(rows[i].LocalPort), (int)rows[i].OwningPid, IsIpv6: true, IPAddress.IsLoopback(address)));
                        }
                    }
                }

                return;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        if (status != 0)
        {
            issues.Add(new SensorIssue("Windows TCP 목록", family == AfInet ? "tcp4" : "tcp6", SensorStatus.Error,
                $"TCP 수신 대기 목록을 읽지 못했습니다 (Win32 {status}). 로컬 AI 서버와 프로세스를 연결할 수 없습니다."));
        }
    }

    /// <summary>The port is stored in network byte order in the low 16 bits.</summary>
    internal static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
}
