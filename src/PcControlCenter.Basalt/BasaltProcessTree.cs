using System.Globalization;

namespace PcControlCenter.Basalt;

/// <summary>Why a process belongs to the server.</summary>
public enum TreeRole
{
    /// <summary>Listens on Basalt's port (Basalt's Python server).</summary>
    Listener,

    /// <summary>Started by the listener or by one of its children (the engine).</summary>
    Descendant,

    /// <summary>A basalt* process above the listener (basalt.exe, the wrapper).</summary>
    Wrapper,

    /// <summary>Started by a wrapper, beside the listener (an engine started by basalt.exe).</summary>
    WrapperDescendant,

    /// <summary>The PowerShell window this app started.</summary>
    Launcher,

    /// <summary>Started by that window (when nothing listens yet).</summary>
    LauncherDescendant,

    /// <summary>A basalt* process found by name while nothing listens on the port (left behind, or still starting).</summary>
    Named,
}

public sealed record TreeMember(BasaltProcess Process, TreeRole Role, int Depth, string Evidence);

/// <summary>
/// The processes that make up a running Basalt server, found from the process listening on its port. Basalt runs as
/// basalt.exe (a wrapper) with a Python HTTP server and an engine; the Python server can restart the engine, so ending
/// only the basalt* processes once left the Python server running and it started the engine again. The members are kept
/// in the order they are ended: the listener first (so nothing restarts the engine), then the wrappers, then every
/// descendant deepest first, then this app's own PowerShell window.
/// </summary>
public sealed record BasaltTree
{
    public required int Port { get; init; }
    public IReadOnlyList<BasaltListener> Listeners { get; init; } = [];

    /// <summary>The process listening on the port (null when nothing listens or the owner is ambiguous).</summary>
    public BasaltProcess? Owner { get; init; }

    public IReadOnlyList<TreeMember> Members { get; init; } = [];

    /// <summary>
    /// The listener (or, with nothing listening, the processes found) can be told to be Basalt: a basalt* name in its tree,
    /// or it runs under the window this app started. Only then is anything ever ended.
    /// </summary>
    public required bool IsBasalt { get; init; }

    /// <summary>Why the listener is not taken for Basalt (shown when the port is busy).</summary>
    public string? NotBasaltReason { get; init; }

    /// <summary>Processes that may belong to the server but could not be verified (start time unknown): never ended.</summary>
    public IReadOnlyList<string> Unverified { get; init; } = [];

    /// <summary>Whose console Ctrl+C goes to, in order: the listener, the wrappers, this app's window, the rest.</summary>
    public IReadOnlyList<BasaltProcess> ConsoleCandidates { get; init; } = [];

    public bool HasMembers => Members.Count > 0;
}

public static class BasaltProcessTree
{
    /// <summary>Descendants are followed this many levels down (wrapper → server → engine → helpers).</summary>
    public const int MaxDepth = 5;

    /// <summary>Console hosts belong to the console window, not to the server; they go when the window's last process goes.</summary>
    private static readonly IReadOnlySet<string> ConsoleHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "conhost.exe", "openconsole.exe",
    };

    /// <summary>Executables whose name says Basalt: basalt.exe, basalt-engine.exe, ...</summary>
    public static bool IsBasaltName(string name) => name.StartsWith("basalt", StringComparison.OrdinalIgnoreCase);

    /// <param name="selfId">This app's own PID: never part of a tree (nor are its ancestors).</param>
    public static BasaltTree Identify(int port, IReadOnlyList<BasaltProcess> processes, IReadOnlyList<BasaltListener> listeners, BasaltLaunch? launch,
        int selfId, Func<BasaltProcess, bool>? previouslyOwned = null)
    {
        var byId = new Dictionary<int, BasaltProcess>();
        foreach (var p in processes)
        {
            byId[p.Id] = p;
        }

        var children = processes.Where(p => p.Id != p.ParentId).GroupBy(p => p.ParentId).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).ToList());
        var protectedIds = Protected(selfId, byId);
        var onPort = listeners.Where(l => l.Port == port).ToList();
        var owners = onPort.Select(l => l.ProcessId).Distinct().ToList();
        var unverified = new List<string>();
        var members = new List<TreeMember>();
        var seen = new HashSet<int>();

        void Add(BasaltProcess process, TreeRole role, int depth, string evidence)
        {
            if (!protectedIds.Contains(process.Id) && !ConsoleHosts.Contains(process.Name) && seen.Add(process.Id))
            {
                members.Add(new TreeMember(process, role, depth, evidence));
            }
        }

        void AddDescendants(BasaltProcess root, TreeRole role)
        {
            var queue = new Queue<(BasaltProcess Parent, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                var (parent, depth) = queue.Dequeue();
                if (depth >= MaxDepth || !children.TryGetValue(parent.Id, out var kids))
                {
                    continue;
                }

                foreach (var child in kids)
                {
                    if (seen.Contains(child.Id) || protectedIds.Contains(child.Id))
                    {
                        continue;
                    }

                    if (parent.StartTime is not { } parentStart || child.StartTime is not { } childStart)
                    {
                        unverified.Add($"{child.Label}: 부모가 {parent.Label}이지만 시작 시각을 확인할 수 없어 Basalt로 보지 않음");
                        continue;
                    }

                    if (childStart < parentStart)
                    {
                        continue; // the parent's PID was reused by a newer process after this one started: not its child
                    }

                    Add(child, role, depth + 1, $"{child.Label}: {parent.Label}의 하위 프로세스 (부모 PID와 시작 시각 확인)");
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        var launcher = launch is not null && byId.TryGetValue(launch.ProcessId, out var l0) && launch.StartTime is not null && l0.StartTime == launch.StartTime
            ? l0
            : null;

        if (owners.Count > 1)
        {
            return new BasaltTree
            {
                Port = port,
                Listeners = onPort,
                IsBasalt = false,
                NotBasaltReason = $"포트 {port}에서 여러 프로세스(PID {string.Join(", ", owners)})가 수신 대기 중이라 어느 것이 Basalt인지 정할 수 없습니다.",
            };
        }

        if (owners.Count == 1)
        {
            var ownerId = owners[0];
            if (ownerId is 0 or 4 || !byId.TryGetValue(ownerId, out var owner))
            {
                return new BasaltTree
                {
                    Port = port,
                    Listeners = onPort,
                    IsBasalt = false,
                    NotBasaltReason = ownerId is 0 or 4
                        ? $"포트 {port}의 소유자가 Windows 커널(PID {ownerId}, 예: http.sys)입니다."
                        : $"포트 {port}에서 수신 대기하는 PID {ownerId}의 프로세스 정보를 읽지 못했습니다.",
                };
            }

            if (protectedIds.Contains(ownerId))
            {
                return new BasaltTree
                {
                    Port = port,
                    Listeners = onPort,
                    Owner = owner,
                    IsBasalt = false,
                    NotBasaltReason = $"포트 {port}를 이 프로그램({owner.Label}) 쪽 프로세스가 쓰고 있습니다.",
                };
            }

            Add(owner, TreeRole.Listener, 0, $"{owner.Label}: 포트 {port}에서 수신 대기 (TCP 목록)");

            // basalt* processes right above the listener (the wrapper), each verified to have started before its child.
            var wrappers = new List<BasaltProcess>();
            for (var (child, steps) = (owner, 0); steps < MaxDepth; steps++)
            {
                if (!byId.TryGetValue(child.ParentId, out var parent) || parent.Id == child.Id || !IsBasaltName(parent.Name)
                    || (launcher is not null && parent.Id == launcher.Id) || protectedIds.Contains(parent.Id))
                {
                    break;
                }

                if (parent.StartTime is not { } ps || child.StartTime is not { } cs || ps > cs)
                {
                    unverified.Add($"{parent.Label}: {child.Label}의 부모로 보이지만 시작 시각을 확인할 수 없어 Basalt로 보지 않음");
                    break;
                }

                Add(parent, TreeRole.Wrapper, -(steps + 1), $"{parent.Label}: {child.Label}의 부모 (basalt 래퍼, 시작 시각 확인)");
                wrappers.Add(parent);
                child = parent;
            }

            AddDescendants(owner, TreeRole.Descendant);
            foreach (var wrapper in wrappers)
            {
                AddDescendants(wrapper, TreeRole.WrapperDescendant);
            }

            var underLauncher = launcher is not null && IsBelow(owner, launcher, byId);
            if (launcher is not null && (underLauncher || members.Any(m => IsBelow(m.Process, launcher, byId))))
            {
                AddDescendants(launcher, TreeRole.LauncherDescendant);
                Add(launcher, TreeRole.Launcher, -MaxDepth - 1, $"{launcher.Label}: 이 프로그램이 Basalt 시작 스크립트를 실행한 창");
            }

            var named = members.Any(m => IsBasaltName(m.Process.Name));
            var isBasalt = named || underLauncher || previouslyOwned?.Invoke(owner) == true;
            return new BasaltTree
            {
                Port = port,
                Listeners = onPort,
                Owner = owner,
                Members = isBasalt ? Ordered(members) : [],
                IsBasalt = isBasalt,
                NotBasaltReason = isBasalt
                    ? null
                    : $"포트 {port}를 {owner.Label}이(가) 쓰고 있지만 Basalt로 확인되지 않습니다 (프로세스 트리에 basalt 실행 파일이 없고, 이 프로그램이 켠 창의 하위도 아님).",
                Unverified = unverified,
                ConsoleCandidates = isBasalt ? Candidates(members) : [],
            };
        }

        // Nothing listens: what is left of (or still starting) Basalt - this app's window and its children, and basalt* processes.
        if (launcher is not null)
        {
            Add(launcher, TreeRole.Launcher, -MaxDepth - 1, $"{launcher.Label}: 이 프로그램이 Basalt 시작 스크립트를 실행한 창");
            AddDescendants(launcher, TreeRole.LauncherDescendant);
        }

        foreach (var process in processes.Where(p => previouslyOwned?.Invoke(p) == true))
        {
            Add(process, TreeRole.LauncherDescendant, 0, $"{process.Label}: 기록된 PID와 시작 시각이 일치합니다.");
            AddDescendants(process, TreeRole.Descendant);
        }

        foreach (var p in processes.Where(p => IsBasaltName(p.Name)).OrderBy(p => p.StartTime ?? DateTimeOffset.MaxValue))
        {
            if (seen.Contains(p.Id))
            {
                continue;
            }

            if (p.StartTime is null)
            {
                unverified.Add($"{p.Label}: 이름은 basalt이지만 시작 시각을 확인할 수 없어 Basalt로 보지 않음");
                continue;
            }

            Add(p, TreeRole.Named, 0, $"{p.Label}: 이름이 basalt로 시작하는 프로세스 (포트에서 수신 대기하지 않음)");
            AddDescendants(p, TreeRole.Descendant);
        }

        return new BasaltTree
        {
            Port = port,
            Listeners = onPort,
            Members = Ordered(members),
            IsBasalt = members.Count > 0,
            Unverified = unverified,
            ConsoleCandidates = Candidates(members),
        };
    }

    /// <summary>
    /// The members that are still running, judged from a newer process list: same PID and same start time. A member whose
    /// start time is unknown counts as running while a process with its PID and name exists (it cannot be ruled out).
    /// </summary>
    public static IReadOnlyList<BasaltProcess> StillRunning(IEnumerable<BasaltProcess> members, IReadOnlyList<BasaltProcess> now)
    {
        var byId = now.ToDictionary(p => p.Id);
        return [.. members.Where(m => byId.TryGetValue(m.Id, out var p)
                                     && (m.StartTime is null ? string.Equals(p.Name, m.Name, StringComparison.OrdinalIgnoreCase) : p.StartTime == m.StartTime))];
    }

    /// <summary>
    /// Processes started by one of <paramref name="parents"/> after it (a restarted engine, a new helper) that are not in
    /// <paramref name="known"/>: found again while the server is being ended. Console hosts and this app are left out.
    /// </summary>
    public static IReadOnlyList<BasaltProcess> NewChildren(IReadOnlyCollection<BasaltProcess> parents, IReadOnlyList<BasaltProcess> now, IReadOnlySet<int> known,
        int selfId)
    {
        var byId = now.ToDictionary(p => p.Id);
        var protectedIds = Protected(selfId, byId);
        var result = new List<BasaltProcess>();
        var frontier = parents.Where(p => byId.TryGetValue(p.Id, out var current) && p.SameAs(current)).ToList();
        for (var depth = 0; depth < MaxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<BasaltProcess>();
            foreach (var p in now)
            {
                if (known.Contains(p.Id) || protectedIds.Contains(p.Id) || ConsoleHosts.Contains(p.Name) || result.Any(r => r.Id == p.Id) || p.StartTime is not { } start)
                {
                    continue;
                }

                if (frontier.Any(parent => parent.Id == p.ParentId && parent.Id != p.Id && parent.StartTime is { } ps && start >= ps))
                {
                    result.Add(p);
                    next.Add(p);
                }
            }

            frontier = next;
        }

        return result;
    }

    /// <summary>Possible children first seen after their parent disappeared. Observation only.</summary>
    internal static IEnumerable<BasaltProcess> UnverifiedChildren(IReadOnlyList<BasaltProcess> previous, IReadOnlyList<BasaltProcess> now, int selfId)
    {
        var current = now.ToDictionary(p => p.Id);
        var protectedIds = Protected(selfId, current);
        var missing = previous.Where(p => p.StartTime.HasValue && !current.ContainsKey(p.Id)).ToLookup(p => p.Id);
        return now.Where(p => !protectedIds.Contains(p.Id) && !ConsoleHosts.Contains(p.Name) &&
            !previous.Any(old => old.SameAs(p)) && missing[p.ParentId].Any(parent => p.StartTime >= parent.StartTime));
    }

    private static IReadOnlyList<TreeMember> Ordered(List<TreeMember> members)
        => [.. members.OrderBy(m => m.Role switch
            {
                TreeRole.Listener => 0,
                TreeRole.Named => 1,
                TreeRole.Wrapper => 2,
                TreeRole.Launcher => 4,
                _ => 3,
            })
            .ThenBy(m => -m.Depth) // wrappers (depth -1, -2, ...): closest first; descendants (1, 2, ...): deepest first
            .ThenBy(m => m.Process.Id)];

    private static IReadOnlyList<BasaltProcess> Candidates(List<TreeMember> members)
        => [.. members.OrderBy(m => m.Role switch
            {
                TreeRole.Listener => 0,
                TreeRole.Named => 1,
                TreeRole.Wrapper => 2,
                TreeRole.Launcher => 3,
                _ => 4,
            }).ThenBy(m => Math.Abs(m.Depth)).Select(m => m.Process)];

    /// <summary>Whether <paramref name="process"/> runs under <paramref name="ancestor"/> (parent chain, start times in order).</summary>
    private static bool IsBelow(BasaltProcess process, BasaltProcess ancestor, Dictionary<int, BasaltProcess> byId)
    {
        var current = process;
        for (var i = 0; i < MaxDepth + 3; i++)
        {
            if (current.ParentId == ancestor.Id)
            {
                return current.StartTime is { } cs && ancestor.StartTime is { } a && cs >= a;
            }

            if (!byId.TryGetValue(current.ParentId, out var parent) || parent.Id == current.Id
                || parent.StartTime is not { } ps || current.StartTime is not { } cst || ps > cst)
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>This app and every process above it: never part of a tree, whatever their names.</summary>
    private static HashSet<int> Protected(int selfId, Dictionary<int, BasaltProcess> byId)
    {
        var result = new HashSet<int> { selfId, 0, 4 };
        var current = byId.GetValueOrDefault(selfId);
        for (var i = 0; current is not null && i < 32; i++)
        {
            if (!result.Add(current.ParentId) && current.ParentId != selfId)
            {
                break;
            }

            current = byId.GetValueOrDefault(current.ParentId);
        }

        return result;
    }

    internal static string Describe(IEnumerable<BasaltProcess> processes)
        => string.Join(", ", processes.Select(p => p.Label));

    public static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
}
