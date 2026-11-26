using System.Collections.Concurrent;

namespace Iron.ProcessIdentity;

/// <summary>
/// Thread-safe cache with a size limit (least recently used entries are dropped above it) and a validity stamp
/// (an entry whose stamp no longer matches is treated as missing and removed).
/// </summary>
public sealed class BoundedCache<TKey, TValue>(int capacity, IEqualityComparer<TKey>? comparer = null)
    where TKey : notnull
{
    private sealed record Entry(TValue Value, long Stamp, long LastAccess);

    private readonly ConcurrentDictionary<TKey, Entry> _map = new(comparer);
    private readonly Lock _trimLock = new();
    private long _clock;
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _invalidations;

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    public int Count => _map.Count;
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long Evictions => Interlocked.Read(ref _evictions);
    public long Invalidations => Interlocked.Read(ref _invalidations);

    public bool TryGet(TKey key, long stamp, out TValue value)
    {
        if (_map.TryGetValue(key, out var e))
        {
            if (e.Stamp == stamp)
            {
                _map.TryUpdate(key, e with { LastAccess = Interlocked.Increment(ref _clock) }, e);
                Interlocked.Increment(ref _hits);
                value = e.Value;
                return true;
            }

            if (_map.TryRemove(new KeyValuePair<TKey, Entry>(key, e)))
            {
                Interlocked.Increment(ref _invalidations);
            }
        }

        Interlocked.Increment(ref _misses);
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value, long stamp)
    {
        _map[key] = new Entry(value, stamp, Interlocked.Increment(ref _clock));
        if (_map.Count > Capacity)
        {
            Trim();
        }
    }

    public bool Remove(TKey key) => _map.TryRemove(key, out _);

    public int RemoveWhere(Func<TKey, bool> predicate)
    {
        var n = 0;
        foreach (var k in _map.Keys)
        {
            if (predicate(k) && _map.TryRemove(k, out _))
            {
                n++;
            }
        }

        return n;
    }

    public void Clear() => _map.Clear();

    /// <summary>Drops the least recently used entries down to 90 % of the capacity.</summary>
    private void Trim()
    {
        lock (_trimLock)
        {
            var excess = _map.Count - Capacity * 9 / 10;
            if (excess <= 0)
            {
                return;
            }

            foreach (var kv in _map.OrderBy(kv => kv.Value.LastAccess).Take(excess).ToList())
            {
                if (_map.TryRemove(kv))
                {
                    Interlocked.Increment(ref _evictions);
                }
            }
        }
    }
}

/// <summary>
/// File identity per path (version strings and icon pixels). Limited in size (least recently used dropped); an entry is
/// re-checked against the file's last-write time and length at most once per <see cref="RevalidateAfter"/> and read
/// again when the file changed (update, reinstall).
/// </summary>
public sealed class FileIdentityCache(Func<string, FileIdentityInfo?> read, Func<string, long?> stamp, int capacity = 512, TimeProvider? time = null)
{
    private sealed record FileEntry(FileIdentityInfo? Info, long? Stamp, long CheckedAt);

    private readonly BoundedCache<string, FileEntry> _cache = new(capacity, StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _reloads;

    public static TimeSpan RevalidateAfter { get; } = TimeSpan.FromSeconds(60);

    public int Count => _cache.Count;
    public int Capacity => _cache.Capacity;
    public long Hits => _cache.Hits;
    public long Evictions => _cache.Evictions;

    /// <summary>Reads caused by a changed file (not first reads).</summary>
    public long Reloads => Interlocked.Read(ref _reloads);

    public FileIdentityInfo? Get(string path)
    {
        var now = _time.GetTimestamp();
        if (_cache.TryGet(path, 0, out var hit))
        {
            if (_time.GetElapsedTime(hit.CheckedAt, now) < RevalidateAfter)
            {
                return hit.Info;
            }

            var current = stamp(path);
            if (current == hit.Stamp)
            {
                _cache.Set(path, hit with { CheckedAt = now }, 0);
                return hit.Info;
            }

            Interlocked.Increment(ref _reloads);
        }

        var st = stamp(path);
        var info = read(path);
        _cache.Set(path, new FileEntry(info, st, now), 0);
        return info;
    }
}

/// <summary>Identity per process instance (PID + creation time). Exited processes are evicted from the polling loop.</summary>
public sealed class ProcessIdentityCache(int capacity = 4096)
{
    private readonly ConcurrentDictionary<ProcessKey, ProcessIdentityInfo> _map = new();

    public int Capacity { get; } = capacity;
    public int Count => _map.Count;
    public long Evicted { get; private set; }

    public bool TryGet(ProcessKey key, out ProcessIdentityInfo info) => _map.TryGetValue(key, out info!);

    public void Set(ProcessIdentityInfo info)
    {
        _map[info.Key] = info;
        if (_map.Count > Capacity)
        {
            // Over the limit (should not happen with eviction running): drop the oldest PIDs by creation time.
            foreach (var k in _map.Keys.OrderBy(k => k.CreateTimeTicks).Take(_map.Count - Capacity).ToList())
            {
                _map.TryRemove(k, out _);
                Evicted++;
            }
        }
    }

    /// <summary>Removes every instance that is not in the current process list. A reused PID has another creation time, so the old entry goes.</summary>
    public int EvictExcept(IReadOnlySet<ProcessKey> alive)
    {
        var n = 0;
        foreach (var k in _map.Keys)
        {
            if (!alive.Contains(k) && _map.TryRemove(k, out _))
            {
                n++;
            }
        }

        Evicted += n;
        return n;
    }
}

/// <summary>
/// PID → services from one service-manager enumeration. Refreshed only when the snapshot is older than the TTL (15 s)
/// or when a svchost PID appears that the snapshot does not know (once per such PID).
/// </summary>
public sealed class ServiceSnapshotCache(Func<IReadOnlyDictionary<int, IReadOnlyList<ServiceEntry>>?> query, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly HashSet<int> _refreshedFor = [];
    private readonly Lock _lock = new();
    private IReadOnlyDictionary<int, IReadOnlyList<ServiceEntry>>? _snapshot;
    private long _takenAt;
    private bool _failed;

    public static TimeSpan Ttl { get; } = TimeSpan.FromSeconds(15);
    public int Refreshes { get; private set; }

    /// <summary>Services of a PID; null when the service manager could not be queried.</summary>
    public IReadOnlyList<ServiceEntry>? Get(int pid, bool isSvchost)
    {
        lock (_lock)
        {
            var stale = _snapshot is null && !_failed || _time.GetElapsedTime(_takenAt) >= Ttl;
            var unknownHost = isSvchost && _snapshot is not null && !_snapshot.ContainsKey(pid) && _refreshedFor.Add(pid);
            if (stale || unknownHost)
            {
                Refresh();
            }

            if (_failed)
            {
                return null;
            }

            return _snapshot!.TryGetValue(pid, out var list) ? list : [];
        }
    }

    private void Refresh()
    {
        Refreshes++;
        _takenAt = _time.GetTimestamp();
        IReadOnlyDictionary<int, IReadOnlyList<ServiceEntry>>? s = null;
        try
        {
            s = query();
        }
        catch (Exception)
        {
            s = null;
        }

        _failed = s is null;
        _snapshot = s ?? _snapshot;
        if (_refreshedFor.Count > 512)
        {
            _refreshedFor.Clear();
        }
    }
}
