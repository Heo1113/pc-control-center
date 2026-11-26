using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Iron.ProcessIdentity;

/// <summary>What the resolver needs from the operating system. <see cref="WindowsProcessQuery"/> on Windows; fakes in tests.</summary>
public interface INativeProcessQuery : IHostContext
{
    /// <summary>All processes with parent and creation time, without opening any process.</summary>
    IReadOnlyList<ProcessEntry> Snapshot();

    string? QueryImagePath(int pid, out string? failure);
    string? QueryCommandLine(int pid, out string? failure);
    (string? PackageFullName, string? AppUserModelId) QueryPackage(int pid);

    /// <summary>PID → services; null when the service manager cannot be queried.</summary>
    IReadOnlyDictionary<int, IReadOnlyList<ServiceEntry>>? QueryServices();

    /// <summary>Loaded module paths (used for dllhost shell-extension surrogates only); null when denied or exited.</summary>
    IReadOnlyList<string>? QueryModulePaths(int pid);

    /// <summary>Version strings and icon of a file, uncached (the resolver caches it).</summary>
    FileIdentityInfo? ReadFile(string path);

    /// <summary>Last-write time and length of a file folded into one number; null when unreadable.</summary>
    long? FileStamp(string path);
}

public sealed record ResolverOptions
{
    /// <summary>Worker threads (1 or 2), below-normal priority.</summary>
    public int Workers { get; init; } = 1;

    /// <summary>Bounded queue; when full, new work is dropped and retried on a later poll (the polling loop never waits).</summary>
    public int QueueCapacity { get; init; } = 256;

    public int FileCacheCapacity { get; init; } = 512;
    public int ProcessCacheCapacity { get; init; } = 4096;
    public int ComCacheCapacity { get; init; } = 256;
    public TimeSpan EvictInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Second module check of a dllhost shell-extension surrogate (at most two per instance: at once and after this delay).</summary>
    public TimeSpan ModuleRecheckDelay { get; init; } = TimeSpan.FromSeconds(3);
}

public sealed record ResolverStats(long Enqueued, long Dropped, long Resolved, long Failed, long ModuleChecks, int ProcessCache, int FileCache, int ServiceRefreshes);

/// <summary>
/// Resolves process identities off the polling loop. <see cref="Observe"/> only hands new process instances to a bounded
/// channel (never blocks); 1–2 below-normal worker threads read the system and fill the per-instance cache, which the
/// loop reads with <see cref="TryGet"/>. Exited instances are evicted; a reused PID is a new instance (creation time).
/// </summary>
public sealed class ProcessIdentityResolver : IDisposable
{
    internal sealed record WorkItem(ProcessEntry Entry, int ModuleCheck);

    private readonly INativeProcessQuery _native;
    private readonly ResolverOptions _options;
    private readonly RoleDictionary _dict;
    private readonly TimeProvider _time;
    private readonly Channel<WorkItem> _channel;
    private readonly ConcurrentDictionary<ProcessKey, byte> _pending = new();
    private readonly ConcurrentDictionary<ProcessKey, int> _moduleChecks = new();
    private readonly ConcurrentDictionary<ProcessKey, byte> _scheduled = new();
    private readonly CachedHostContext _ctx;
    private readonly List<Thread> _threads = [];
    private readonly CancellationTokenSource _stop = new();
    private volatile IReadOnlyDictionary<int, ProcessEntry> _byPid = new Dictionary<int, ProcessEntry>();
    private long _lastEvict;
    private long _enqueued;
    private long _dropped;
    private long _resolved;
    private long _failed;
    private long _moduleCheckCount;

    public ProcessIdentityResolver(INativeProcessQuery native, ResolverOptions? options = null, RoleDictionary? dict = null, TimeProvider? time = null, bool startWorkers = true)
    {
        _native = native;
        _options = options ?? new ResolverOptions();
        _dict = dict ?? RoleDictionary.Default;
        _time = time ?? TimeProvider.System;
        Cache = new ProcessIdentityCache(_options.ProcessCacheCapacity);
        Files = new FileIdentityCache(native.ReadFile, native.FileStamp, _options.FileCacheCapacity, _time);
        Services = new ServiceSnapshotCache(native.QueryServices, _time);
        _ctx = new CachedHostContext(native, Files, _options.ComCacheCapacity);
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            // Wait mode + TryWrite: a full queue makes TryWrite return false at once (Drop* modes would report success).
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = _options.Workers <= 1,
            SingleWriter = false,
        });
        _lastEvict = _time.GetTimestamp();
        if (startWorkers)
        {
            for (var i = 0; i < Math.Clamp(_options.Workers, 1, 2); i++)
            {
                var t = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "IRON identity " + (i + 1) };
                _threads.Add(t);
                t.Start();
            }
        }
    }

    public ProcessIdentityCache Cache { get; }
    public FileIdentityCache Files { get; }
    public ServiceSnapshotCache Services { get; }

    /// <summary>Raised on a worker thread after an identity is stored (UI code must marshal to its own thread).</summary>
    public event Action<ProcessIdentityInfo>? Resolved;

    public ResolverStats Stats => new(Interlocked.Read(ref _enqueued), Interlocked.Read(ref _dropped), Interlocked.Read(ref _resolved), Interlocked.Read(ref _failed),
        Interlocked.Read(ref _moduleCheckCount), Cache.Count, Files.Count, Services.Refreshes);

    public bool TryGet(ProcessKey key, out ProcessIdentityInfo info) => Cache.TryGet(key, out info);

    /// <summary>Called by the polling loop with the current process list. Never blocks.</summary>
    public void Observe(IReadOnlyList<ProcessEntry> snapshot)
    {
        _byPid = snapshot.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.First());
        foreach (var e in snapshot)
        {
            if (!Cache.TryGet(e.Key, out _) && _pending.TryAdd(e.Key, 0))
            {
                Enqueue(new WorkItem(e, 0));
            }
        }

        if (_time.GetElapsedTime(_lastEvict) >= _options.EvictInterval)
        {
            _lastEvict = _time.GetTimestamp();
            var alive = snapshot.Select(e => e.Key).ToHashSet();
            Cache.EvictExcept(alive);
            foreach (var k in _moduleChecks.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                _moduleChecks.TryRemove(k, out _);
                _scheduled.TryRemove(k, out _);
            }

            foreach (var k in _pending.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                _pending.TryRemove(k, out _);
            }
        }
    }

    private void Enqueue(WorkItem item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _enqueued);
        }
        else
        {
            Interlocked.Increment(ref _dropped);
            _pending.TryRemove(item.Entry.Key, out _); // retried on the next poll
        }
    }

    private void Work()
    {
        var reader = _channel.Reader;
        try
        {
            while (reader.WaitToReadAsync(_stop.Token).AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var item))
                {
                    Process(item);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    internal void Process(WorkItem item)
    {
        var key = item.Entry.Key;
        try
        {
            if (item.ModuleCheck == 0 && Cache.TryGet(key, out _))
            {
                return; // already resolved (duplicate work for the same instance)
            }

            // Module checks of a shell-extension surrogate: at most two per instance (now, and once after the delay).
            if (item.ModuleCheck > 0 && _moduleChecks.AddOrUpdate(key, 1, (_, v) => v + 1) > 2)
            {
                return;
            }

            var info = ResolveNow(item.Entry, item.ModuleCheck, out var wantsModules);
            Cache.Set(info);
            Interlocked.Increment(ref _resolved);
            if (wantsModules)
            {
                var done = _moduleChecks.GetValueOrDefault(key);
                if (done == 0)
                {
                    Process(item with { ModuleCheck = 1 });
                    return;
                }

                if (done == 1 && _scheduled.TryAdd(key, 0))
                {
                    ITimer? timer = null;
                    timer = _time.CreateTimer(_ =>
                    {
                        Enqueue(item with { ModuleCheck = 2 });
                        timer?.Dispose();
                    }, null, _options.ModuleRecheckDelay, Timeout.InfiniteTimeSpan);
                }
            }

            Resolved?.Invoke(info);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failed);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>Reads what a standard user may read and composes the identity; every failure becomes a note, never an exception.</summary>
    internal ProcessIdentityInfo ResolveNow(ProcessEntry e, int moduleCheck, out bool wantsModules)
    {
        var notes = new List<string>();
        var pid = e.Pid;
        string? imagePath = null, cmd = null, package = null, aumid = null;
        if (pid > 4)
        {
            imagePath = Safe(() => _native.QueryImagePath(pid, out var f1) is { } p ? p : Note(notes, "실행 파일 경로", f1));
            cmd = Safe(() => _native.QueryCommandLine(pid, out var f2) is { } c ? c : Note(notes, "명령줄", f2));
            (package, aumid) = Safe(() => _native.QueryPackage(pid));
        }

        var file = imagePath is null ? null : Safe(() => Files.Get(imagePath));
        var isSvchost = e.ImageName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase);
        var services = Safe(() => Services.Get(pid, isSvchost));
        var parent = _byPid.TryGetValue(e.ParentPid, out var p0) && p0.CreateTimeTicks <= e.CreateTimeTicks ? p0.ImageName : null;

        IReadOnlyList<ModuleInfo>? modules = null;
        var attempted = false;
        if (moduleCheck > 0 && e.ImageName.Equals("dllhost.exe", StringComparison.OrdinalIgnoreCase))
        {
            attempted = true;
            Interlocked.Increment(ref _moduleCheckCount);
            modules = Safe(() => _native.QueryModulePaths(pid)?.Select(m => new ModuleInfo(m, SharedHostClassifier.IsNonWindowsModule(m) ? Files.Get(m) : null)).ToList());
        }

        var facts = new NativeFacts(e, imagePath, cmd, file, package, aumid, services, parent, modules, attempted, notes);
        // The raw command line lives only in this call; the stored identity keeps its redacted form.
        var info = IdentityComposer.Compose(facts, _ctx, _dict, out wantsModules);
        return WithOwnService(info, services, isSvchost);
    }

    /// <summary>A service running in its own executable: its display name is a confirmed, more specific name than the file name.</summary>
    private static ProcessIdentityInfo WithOwnService(ProcessIdentityInfo info, IReadOnlyList<ServiceEntry>? services, bool isSvchost)
    {
        if (isSvchost || services is not { Count: > 0 })
        {
            return info;
        }

        var ordered = services.OrderBy(s => s.DisplayName, StringComparer.Ordinal).ToList();
        var hosted = info.Hosted.Concat(ordered.Select(s => new HostedItem("서비스", s.DisplayName, IdentityConfidence.Confirmed, s.Name))).ToList();
        var title = info.Title.Confidence == IdentityConfidence.Confirmed && info.Title.Basis != "실행 파일 이름" && info.Title.Basis != "버전 정보"
            ? info.Title
            : new IdentityText(ordered[0].DisplayName + (ordered.Count > 1 ? $" 외 {ordered.Count - 1}개" : ""), IdentityConfidence.Confirmed, "서비스 관리자");
        return info with { Title = title, Hosted = hosted };
    }

    private static string? Note(List<string> notes, string what, string? failure)
    {
        notes.Add(what + ": " + (failure ?? "읽을 수 없음"));
        return null;
    }

    private static T? Safe<T>(Func<T?> f)
    {
        try
        {
            return f();
        }
        catch (Exception)
        {
            return default;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _channel.Writer.TryComplete();
        foreach (var t in _threads)
        {
            t.Join(TimeSpan.FromSeconds(2));
        }

        _stop.Dispose();
    }

    /// <summary>The classifier's view of the system with cached files and COM registrations.</summary>
    private sealed class CachedHostContext(INativeProcessQuery native, FileIdentityCache files, int comCapacity) : IHostContext
    {
        private readonly BoundedCache<Guid, ComAppIdInfo?> _appIds = new(comCapacity);
        private readonly BoundedCache<Guid, ComClsidInfo?> _clsids = new(comCapacity);

        public ComAppIdInfo? LookupAppId(Guid id)
        {
            if (_appIds.TryGet(id, 0, out var v))
            {
                return v;
            }

            v = native.LookupAppId(id);
            _appIds.Set(id, v, 0);
            return v;
        }

        public ComClsidInfo? LookupClsid(Guid id)
        {
            if (_clsids.TryGet(id, 0, out var v))
            {
                return v;
            }

            v = native.LookupClsid(id);
            _clsids.Set(id, v, 0);
            return v;
        }

        public FileIdentityInfo? GetFile(string path) => files.Get(path);
        public string? ResolveSystemPath(string fileName) => native.ResolveSystemPath(fileName);
        public string? ReadPackageName(string directory) => native.ReadPackageName(directory);
        public bool FileExists(string path) => native.FileExists(path);
        public string? ServiceDescription(string serviceName) => native.ServiceDescription(serviceName);
    }
}
