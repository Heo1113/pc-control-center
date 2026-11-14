using System.Globalization;
using System.Text.RegularExpressions;

namespace PcControlCenter.GpuMemory;

/// <summary>One instance of a counter array: the instance name as Windows spells it and its value in bytes.</summary>
public readonly record struct CounterItem(string Instance, long Value);

/// <summary>
/// Turns the counter arrays of one sample into per-process and per-adapter rows. Platform-neutral, so the parsing and the
/// null rules are tested on Linux with instance names in the format Windows uses.
/// </summary>
/// <remarks>
/// Instance names: <c>pid_1234_luid_0x00000000_0x0000C8D1_phys_0</c> (GPU Process Memory) and
/// <c>luid_0x00000000_0x0000C8D1_phys_0</c> (GPU Adapter Memory). A linked adapter can have several physical parts
/// (<c>phys_0</c>, <c>phys_1</c>); their values are added for the same process and LUID. A counter that was not available
/// gives null for everyone; a process missing from one array but present in another gets null for that value, not zero.
/// </remarks>
public static partial class CounterInstances
{
    public static bool TryParseProcess(string instance, out int processId, out long luid)
    {
        processId = 0;
        luid = 0;
        var match = ProcessRegex().Match(instance);
        if (!match.Success
            || !uint.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid > int.MaxValue
            || !TryLuid(match.Groups[2].ValueSpan, match.Groups[3].ValueSpan, out luid))
        {
            return false;
        }

        processId = (int)pid;
        return true;
    }

    public static bool TryParseAdapter(string instance, out long luid)
    {
        luid = 0;
        var match = AdapterRegex().Match(instance);
        return match.Success && TryLuid(match.Groups[1].ValueSpan, match.Groups[2].ValueSpan, out luid);
    }

    /// <summary>
    /// Merges the arrays of one sample. Null arguments are counters that could not be added (their values stay null).
    /// <paramref name="unrecognized"/> counts instance names that matched neither format (reported, never guessed).
    /// </summary>
    public static (IReadOnlyList<GpuProcessMemory> Processes, IReadOnlyDictionary<long, (long? Dedicated, long? Shared)> Adapters) Merge(
        IReadOnlyList<CounterItem>? processDedicated,
        IReadOnlyList<CounterItem>? processShared,
        IReadOnlyList<CounterItem>? processCommitted,
        IReadOnlyList<CounterItem>? adapterDedicated,
        IReadOnlyList<CounterItem>? adapterShared,
        out int unrecognized)
    {
        unrecognized = 0;
        var dedicated = SumByProcess(processDedicated, ref unrecognized);
        var shared = SumByProcess(processShared, ref unrecognized);
        var committed = SumByProcess(processCommitted, ref unrecognized);
        var keys = new HashSet<(int Pid, long Luid)>();
        foreach (var map in new[] { dedicated, shared, committed })
        {
            if (map is not null)
            {
                keys.UnionWith(map.Keys);
            }
        }

        var processes = keys
            .OrderBy(k => k.Luid).ThenBy(k => k.Pid)
            .Select(k => new GpuProcessMemory
            {
                ProcessId = k.Pid,
                Luid = k.Luid,
                DedicatedBytes = Lookup(dedicated, k),
                SharedBytes = Lookup(shared, k),
                CommittedBytes = Lookup(committed, k),
            })
            .ToList();

        var adapterDedicatedMap = SumByAdapter(adapterDedicated, ref unrecognized);
        var adapterSharedMap = SumByAdapter(adapterShared, ref unrecognized);
        var adapterKeys = new HashSet<long>();
        if (adapterDedicatedMap is not null)
        {
            adapterKeys.UnionWith(adapterDedicatedMap.Keys);
        }

        if (adapterSharedMap is not null)
        {
            adapterKeys.UnionWith(adapterSharedMap.Keys);
        }

        var adapters = adapterKeys.ToDictionary(
            luid => luid,
            luid => (Lookup(adapterDedicatedMap, luid), Lookup(adapterSharedMap, luid)));
        return (processes, adapters);
    }

    private static Dictionary<(int Pid, long Luid), long>? SumByProcess(IReadOnlyList<CounterItem>? items, ref int unrecognized)
    {
        if (items is null)
        {
            return null;
        }

        var map = new Dictionary<(int, long), long>();
        foreach (var item in items)
        {
            if (!TryParseProcess(item.Instance, out var pid, out var luid))
            {
                unrecognized++;
                continue;
            }

            map[(pid, luid)] = map.GetValueOrDefault((pid, luid)) + Math.Max(0, item.Value);
        }

        return map;
    }

    private static Dictionary<long, long>? SumByAdapter(IReadOnlyList<CounterItem>? items, ref int unrecognized)
    {
        if (items is null)
        {
            return null;
        }

        var map = new Dictionary<long, long>();
        foreach (var item in items)
        {
            if (!TryParseAdapter(item.Instance, out var luid))
            {
                unrecognized++;
                continue;
            }

            map[luid] = map.GetValueOrDefault(luid) + Math.Max(0, item.Value);
        }

        return map;
    }

    private static long? Lookup<TKey>(Dictionary<TKey, long>? map, TKey key) where TKey : notnull
        => map is not null && map.TryGetValue(key, out var value) ? value : null;

    private static bool TryLuid(ReadOnlySpan<char> high, ReadOnlySpan<char> low, out long luid)
    {
        luid = 0;
        if (!uint.TryParse(high, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var h)
            || !uint.TryParse(low, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var l))
        {
            return false;
        }

        luid = GpuLuid.From(h, l);
        return true;
    }

    [GeneratedRegex(@"^pid_(\d{1,10})_luid_0x([0-9A-Fa-f]{1,8})_0x([0-9A-Fa-f]{1,8})_phys_\d{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessRegex();

    [GeneratedRegex(@"^luid_0x([0-9A-Fa-f]{1,8})_0x([0-9A-Fa-f]{1,8})_phys_\d{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterRegex();
}
