using Iron.ProcessIdentity;

namespace Iron.ProcessIdentity.Tests;

/// <summary>Registry, files and packages as a test describes them.</summary>
internal class FakeHostContext : IHostContext
{
    public Dictionary<Guid, ComAppIdInfo> AppIds { get; } = [];
    public Dictionary<Guid, ComClsidInfo> Clsids { get; } = [];
    public Dictionary<string, FileIdentityInfo> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string SystemDir { get; set; } = @"C:\Windows\System32";

    public ComAppIdInfo? LookupAppId(Guid id) => AppIds.GetValueOrDefault(id);
    public ComClsidInfo? LookupClsid(Guid id) => Clsids.GetValueOrDefault(id);
    public virtual FileIdentityInfo? GetFile(string path) => Files.GetValueOrDefault(path);
    public string? ResolveSystemPath(string fileName) => Files.ContainsKey(SystemDir + "\\" + fileName) ? SystemDir + "\\" + fileName : null;
    public string? ReadPackageName(string directory) => Packages.GetValueOrDefault(directory.TrimEnd('\\'));
    public bool FileExists(string path) => Existing.Contains(path) || Files.ContainsKey(path);
    public Dictionary<string, string> ServiceDescriptions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ServiceDescription(string serviceName) => ServiceDescriptions.GetValueOrDefault(serviceName);
}

/// <summary>A process table and system answers for the resolver; counts calls and can be slow or deny access.</summary>
internal sealed class FakeNativeQuery : FakeHostContext, INativeProcessQuery
{
    public List<ProcessEntry> Processes { get; } = [];
    public Dictionary<int, string> ImagePaths { get; } = [];
    public Dictionary<int, string> CommandLines { get; } = [];
    public HashSet<int> Denied { get; } = [];
    public Dictionary<int, IReadOnlyList<ServiceEntry>>? Services { get; set; } = [];
    public Dictionary<int, List<string>> Modules { get; } = [];
    public Dictionary<string, long> Stamps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public TimeSpan Delay { get; set; }
    public int ServiceQueries;
    public int ModuleQueries;
    public int FileReads;
    public int CommandLineQueries;

    public IReadOnlyList<ProcessEntry> Snapshot() => [.. Processes];

    public string? QueryImagePath(int pid, out string? failure)
    {
        Slow();
        failure = Denied.Contains(pid) ? "권한 부족" : null;
        return failure is null ? ImagePaths.GetValueOrDefault(pid) : null;
    }

    public string? QueryCommandLine(int pid, out string? failure)
    {
        Interlocked.Increment(ref CommandLineQueries);
        failure = Denied.Contains(pid) ? "권한 부족" : null;
        return failure is null ? CommandLines.GetValueOrDefault(pid) : null;
    }

    public (string? PackageFullName, string? AppUserModelId) QueryPackage(int pid) => (null, null);

    public IReadOnlyDictionary<int, IReadOnlyList<ServiceEntry>>? QueryServices()
    {
        Interlocked.Increment(ref ServiceQueries);
        return Services;
    }

    public IReadOnlyList<string>? QueryModulePaths(int pid)
    {
        Interlocked.Increment(ref ModuleQueries);
        return Denied.Contains(pid) ? null : Modules.GetValueOrDefault(pid);
    }

    public FileIdentityInfo? ReadFile(string path)
    {
        Interlocked.Increment(ref FileReads);
        return Files.GetValueOrDefault(path);
    }

    public long? FileStamp(string path) => Stamps.TryGetValue(path, out var s) ? s : 1;

    private void Slow()
    {
        if (Delay > TimeSpan.Zero)
        {
            Thread.Sleep(Delay);
        }
    }
}

/// <summary>Manual clock with timers (no extra NuGet package).</summary>
internal sealed class FakeTime : TimeProvider
{
    private readonly List<FakeTimer> _timers = [];
    private long _ticks = 1_000_000;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero).AddTicks(Interlocked.Read(ref _ticks));

    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref _ticks, by.Ticks);
        List<FakeTimer> due;
        lock (_timers)
        {
            due = _timers.Where(t => t.Due <= _ticks && !t.Disposed).ToList();
            foreach (var t in due)
            {
                t.Disposed = true;
            }
        }

        foreach (var t in due)
        {
            t.Callback(t.State);
        }
    }

    public int PendingTimers
    {
        get
        {
            lock (_timers)
            {
                return _timers.Count(t => !t.Disposed);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new FakeTimer(callback, state, _ticks + dueTime.Ticks);
        lock (_timers)
        {
            _timers.Add(t);
        }

        return t;
    }

    private sealed class FakeTimer(TimerCallback callback, object? state, long due) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long Due { get; } = due;
        public bool Disposed { get; set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
