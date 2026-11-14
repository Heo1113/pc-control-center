using System.Globalization;
using System.Net;

namespace PcControlCenter.GpuMemory;

/// <summary>A process that belongs to a local AI server: the one listening on its port (depth 0) or a verified descendant.</summary>
public sealed record LinkedProcess(ProcessEntry Process, int Depth, IReadOnlyList<GpuProcessMemory> Memory);

/// <summary>
/// Which processes on this PC serve a local endpoint, and their measured GPU memory. The bytes are Windows' per-process
/// counters (measured); the association with the server is derived from the port owner and the process tree.
/// </summary>
public sealed record ServerProcessLink
{
    public required bool IsLinked { get; init; }

    /// <summary>Why no process could be linked (null when linked).</summary>
    public string? Reason { get; init; }

    public int? Port { get; init; }
    public ProcessEntry? Listener { get; init; }

    /// <summary>The listener first, then its verified descendants (breadth first).</summary>
    public IReadOnlyList<LinkedProcess> Processes { get; init; } = [];

    /// <summary>How each link was established, one line per process.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>Descendants that may belong to the server but could not be verified (start time unknown): not linked.</summary>
    public IReadOnlyList<string> Unverified { get; init; } = [];
}

/// <summary>
/// Links a local server endpoint (for example Strata's <c>http://127.0.0.1:18500</c>) to its processes:
/// <list type="number">
/// <item>the one process that owns a listening socket on that port (TCP table at the moment of the sample);</item>
/// <item>its descendants up to <see cref="MaxDepth"/> levels, accepted only when parent and child start times are known and
/// the child did not start before the parent (a reused parent PID cannot adopt an older process).</item>
/// </list>
/// Strata's HTTP server starts its engine as a child process, and that child holds the model in GPU memory, so the
/// listener alone would show almost nothing.
/// </summary>
public static class ServerProcessLinker
{
    public const int MaxDepth = 3;

    public static ServerProcessLink Link(string? endpoint, GpuMemorySnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return NotLinked("서버 주소가 없습니다.");
        }

        if (!IsLocalHost(uri.Host))
        {
            return NotLinked($"서버 주소({uri.Host})가 이 PC가 아니라서 이 PC의 프로세스와 연결하지 않습니다.");
        }

        var port = uri.Port;
        var owners = snapshot.Listeners.Where(l => l.Port == port).Select(l => l.ProcessId).Distinct().ToList();
        if (owners.Count == 0)
        {
            return NotLinked($"포트 {port}에서 수신 대기 중인 프로세스를 찾지 못했습니다 (서버가 종료되었거나 TCP 목록을 읽지 못함).", port);
        }

        if (owners.Count > 1)
        {
            return NotLinked($"포트 {port}에서 여러 프로세스(PID {string.Join(", ", owners)})가 수신 대기 중이라 어느 것인지 정할 수 없습니다.", port);
        }

        var pid = owners[0];
        if (pid is 0 or 4)
        {
            return NotLinked($"포트 {port}의 소유자가 Windows 커널(PID {pid}, 예: http.sys)이라 서버 프로세스를 정할 수 없습니다.", port);
        }

        if (snapshot.Process(pid) is not { } listener)
        {
            return NotLinked($"포트 {port}에서 수신 대기하는 PID {pid}의 프로세스 정보를 읽지 못했습니다.", port);
        }

        if (listener.State is ProcessIdentityState.Exited or ProcessIdentityState.ReusedAfterSample)
        {
            return NotLinked($"포트 {port}의 프로세스(PID {pid})가 측정 직후 종료되었거나 바뀌어 연결하지 않습니다.", port);
        }

        var children = snapshot.ProcessTable.Values
            .Where(p => p.ProcessId != p.ParentProcessId)
            .GroupBy(p => p.ParentProcessId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.ProcessId).ToList());
        var linked = new List<LinkedProcess> { new(listener, 0, MemoryOf(snapshot, pid)) };
        var evidence = new List<string> { $"PID {pid} {listener.Name}: 포트 {port}에서 수신 대기하는 프로세스 (TCP 목록, GetExtendedTcpTable)" };
        var unverified = new List<string>();
        var seen = new HashSet<int> { pid };
        var queue = new Queue<(ProcessEntry Parent, int Depth)>();
        queue.Enqueue((listener, 0));
        while (queue.Count > 0)
        {
            var (parent, depth) = queue.Dequeue();
            if (depth >= MaxDepth || !children.TryGetValue(parent.ProcessId, out var kids))
            {
                continue;
            }

            foreach (var child in kids)
            {
                if (!seen.Add(child.ProcessId))
                {
                    continue;
                }

                if (parent.StartTime is not { } parentStart || child.StartTime is not { } childStart || child.State != ProcessIdentityState.Verified)
                {
                    unverified.Add($"PID {child.ProcessId} {child.Name}: 부모 PID는 {parent.ProcessId}이지만 시작 시각을 확인할 수 없어 연결하지 않음");
                    continue;
                }

                if (childStart < parentStart)
                {
                    // The parent PID was reused by a newer process after this child started: not its child.
                    continue;
                }

                linked.Add(new LinkedProcess(child, depth + 1, MemoryOf(snapshot, child.ProcessId)));
                evidence.Add($"PID {child.ProcessId} {child.Name}: PID {parent.ProcessId} {parent.Name}의 하위 프로세스 "
                             + $"(부모 PID와 시작 시각 확인: {Clock(childStart)} ≥ {Clock(parentStart)})");
                queue.Enqueue((child, depth + 1));
            }
        }

        return new ServerProcessLink
        {
            IsLinked = true,
            Port = port,
            Listener = listener,
            Processes = linked,
            Evidence = evidence,
            Unverified = unverified,
        };
    }

    public static bool IsLocalHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    private static IReadOnlyList<GpuProcessMemory> MemoryOf(GpuMemorySnapshot snapshot, int pid)
        => [.. snapshot.Processes.Where(p => p.ProcessId == pid)];

    private static ServerProcessLink NotLinked(string reason, int? port = null) => new() { IsLinked = false, Reason = reason, Port = port };

    private static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
