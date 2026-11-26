using System.Diagnostics;
using System.Text.Json;
using Iron.ProcessIdentity;

namespace Iron.ProcessIdentity.Tests;

public class CacheTests
{
    [Fact]
    public void BoundedCache_DropsLeastRecentlyUsedAboveCapacity()
    {
        var c = new BoundedCache<string, int>(10);
        for (var i = 0; i < 10; i++)
        {
            c.Set("k" + i, i, 0);
        }

        Assert.True(c.TryGet("k0", 0, out _)); // k0 is now recent
        c.Set("k10", 10, 0); // over capacity → trimmed to 9 (90 %)
        Assert.True(c.Count <= 10);
        Assert.True(c.TryGet("k0", 0, out _));
        Assert.False(c.TryGet("k1", 0, out _));
        Assert.True(c.Evictions >= 1);
    }

    [Fact]
    public void BoundedCache_StampMismatchInvalidates()
    {
        var c = new BoundedCache<string, string>(4);
        c.Set("a", "v1", stamp: 1);
        Assert.False(c.TryGet("a", 2, out _));
        Assert.Equal(1, c.Invalidations);
        Assert.Equal(0, c.Count);
    }

    [Fact]
    public void FileCache_ReusesUntilFileChangesAndRevalidatesAtMostEveryMinute()
    {
        var time = new FakeTime();
        var reads = 0;
        long stamp = 100;
        var cache = new FileIdentityCache(p => { reads++; return new FileIdentityInfo(p, "d" + reads, null, null, null, null, null); }, _ => stamp, 8, time);
        Assert.Equal("d1", cache.Get(@"C:\a.exe")!.FileDescription);
        Assert.Equal("d1", cache.Get(@"c:\A.EXE")!.FileDescription); // path compared case-insensitively
        stamp = 200; // file replaced, but not yet re-checked
        Assert.Equal("d1", cache.Get(@"C:\a.exe")!.FileDescription);
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("d2", cache.Get(@"C:\a.exe")!.FileDescription);
        Assert.Equal(1, cache.Reloads);
        for (var i = 0; i < 20; i++)
        {
            cache.Get($@"C:\f{i}.exe");
        }

        Assert.True(cache.Count <= 8);
    }

    [Fact]
    public void ProcessCache_KeyIncludesCreationTimeAndEvictsExitedAndReusedPids()
    {
        var c = new ProcessIdentityCache();
        var oldKey = new ProcessKey(100, 1111);
        var newKey = new ProcessKey(100, 2222);
        c.Set(IdentityComposer.ComposeMock(100, "a.exe", null) with { Key = oldKey });
        Assert.False(c.TryGet(newKey, out _)); // same PID, another process
        c.Set(IdentityComposer.ComposeMock(100, "b.exe", null) with { Key = newKey });
        Assert.Equal(1, c.EvictExcept(new HashSet<ProcessKey> { newKey }));
        Assert.True(c.TryGet(newKey, out var still));
        Assert.Equal("b.exe", still.ImageName);
        Assert.False(c.TryGet(oldKey, out _));
    }

    [Fact]
    public void ServiceSnapshot_RefreshesOnTtlOrNewSvchostOnly()
    {
        var time = new FakeTime();
        var calls = 0;
        var data = new Dictionary<int, IReadOnlyList<ServiceEntry>> { [10] = [new ServiceEntry("A", "에이")] };
        var c = new ServiceSnapshotCache(() => { calls++; return data; }, time);
        Assert.Single(c.Get(10, true)!);
        Assert.Empty(c.Get(11, false)!); // not svchost: no refresh
        Assert.Equal(1, calls);
        Assert.Empty(c.Get(12, true)!); // unknown svchost → one refresh
        Assert.Empty(c.Get(12, true)!); // not again for the same PID
        Assert.Equal(2, calls);
        time.Advance(TimeSpan.FromSeconds(16));
        c.Get(10, true);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void ServiceSnapshot_FailureReturnsNullAndRetriesAfterTtl()
    {
        var time = new FakeTime();
        var calls = 0;
        var c = new ServiceSnapshotCache(() => { calls++; return null; }, time);
        Assert.Null(c.Get(10, true));
        Assert.Null(c.Get(10, true));
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(16));
        Assert.Null(c.Get(10, true));
        Assert.Equal(2, calls);
    }
}

public class ResolverTests
{
    private static FakeNativeQuery Native()
    {
        var n = new FakeNativeQuery();
        n.Processes.AddRange([
            new ProcessEntry(4, 0, "System", 10),
            new ProcessEntry(800, 4, "svchost.exe", 20),
            new ProcessEntry(900, 4, "explorer.exe", 30),
            new ProcessEntry(1200, 900, "cmd.exe", 40),
            new ProcessEntry(1300, 900, "python.exe", 50),
        ]);
        n.ImagePaths[800] = @"C:\Windows\System32\svchost.exe";
        n.CommandLines[800] = @"C:\Windows\system32\svchost.exe -k netsvcs -p";
        n.Services = new Dictionary<int, IReadOnlyList<ServiceEntry>> { [800] = [new ServiceEntry("Schedule", "작업 스케줄러")] };
        n.ImagePaths[1200] = @"C:\Windows\System32\cmd.exe";
        n.CommandLines[1200] = "cmd.exe /c C:\\work\\deploy.bat --password=hunter2 --token abc123";
        n.Files[@"C:\Windows\System32\cmd.exe"] = new FileIdentityInfo(@"C:\Windows\System32\cmd.exe", "Windows 명령 프로세서", "Microsoft® Windows® Operating System", "Microsoft Corporation", "10.0", "ko-KR", null);
        n.ImagePaths[1300] = @"C:\Users\kim\proj\shop\.venv\Scripts\python.exe";
        n.CommandLines[1300] = @"C:\Users\kim\proj\shop\.venv\Scripts\python.exe C:\Users\kim\proj\shop\main.py --api-key=SECRETKEY";
        n.Existing.Add(@"C:\Users\kim\proj\shop\.venv\pyvenv.cfg");
        return n;
    }

    private static ProcessIdentityResolver NewResolver(FakeNativeQuery n, FakeTime? time = null, ResolverOptions? o = null)
        => new(n, o ?? new ResolverOptions(), RoleDictionary.Default, time ?? new FakeTime(), startWorkers: false);

    private static void Drain(ProcessIdentityResolver r, FakeNativeQuery n)
    {
        foreach (var e in n.Processes)
        {
            r.Process(new ProcessIdentityResolver.WorkItem(e, 0));
        }
    }

    [Fact]
    public void ResolvesWithParentAndNeverStoresSecrets()
    {
        var n = Native();
        using var r = NewResolver(n);
        r.Observe(n.Snapshot());
        Drain(r, n);
        Assert.True(r.TryGet(new ProcessKey(1200, 40), out var cmd));
        Assert.Equal("배치 파일 deploy.bat · 부모 explorer.exe", cmd.Executor!.Text);
        Assert.Equal("Windows 명령 프로세서", cmd.FileDescription);
        Assert.True(r.TryGet(new ProcessKey(1300, 50), out var py));
        Assert.Equal("main.py · 프로젝트 shop · 가상환경 .venv", py.Executor!.Text);
        Assert.True(r.TryGet(new ProcessKey(800, 20), out var svc));
        Assert.Equal("작업 스케줄러", svc.Title.Text);
        Assert.True(svc.IsSharedHost);
        Assert.Contains(ProcessIdentityInfo.SharedHostNotice, svc.ToToolTipText(), StringComparison.Ordinal);

        // Nothing kept anywhere contains a secret or the profile name.
        var all = JsonSerializer.Serialize(new[] { cmd, py, svc });
        foreach (var secret in new[] { "hunter2", "abc123", "SECRETKEY", "\\\\kim\\\\" })
        {
            Assert.DoesNotContain(secret, all);
        }
    }

    [Fact]
    public void AccessDeniedAndMissingServicesBecomeNotesNotErrors()
    {
        var n = Native();
        n.Denied.Add(800);
        n.Services = null;
        using var r = NewResolver(n);
        r.Observe(n.Snapshot());
        Drain(r, n);
        Assert.True(r.TryGet(new ProcessKey(800, 20), out var svc));
        Assert.Contains(svc.Notes, x => x.Contains("권한 부족", StringComparison.Ordinal));
        Assert.Contains(svc.Notes, x => x.Contains("서비스 목록", StringComparison.Ordinal));
        Assert.Equal(0, r.Stats.Failed);
    }

    [Fact]
    public void ReusedPidIsANewInstanceAndExitedOnesAreEvicted()
    {
        var n = Native();
        var time = new FakeTime();
        using var r = NewResolver(n, time);
        r.Observe(n.Snapshot());
        Drain(r, n);
        n.Processes.RemoveAll(p => p.Pid == 1200);
        n.Processes.Add(new ProcessEntry(1200, 900, "notepad.exe", 999)); // PID reused
        time.Advance(TimeSpan.FromSeconds(11));
        r.Observe(n.Snapshot());
        Assert.False(r.TryGet(new ProcessKey(1200, 40), out _)); // old instance evicted
        Assert.False(r.TryGet(new ProcessKey(1200, 999), out _)); // new one queued, not yet resolved
        r.Process(new ProcessIdentityResolver.WorkItem(n.Processes[^1], 0));
        Assert.True(r.TryGet(new ProcessKey(1200, 999), out var np));
        Assert.Equal("notepad.exe", np.ImageName);
    }

    [Fact]
    public void ObserveNeverBlocksAndDropsWhenQueueIsFull()
    {
        var n = new FakeNativeQuery { Delay = TimeSpan.FromMilliseconds(200) };
        for (var i = 0; i < 500; i++)
        {
            n.Processes.Add(new ProcessEntry(1000 + i, 4, "app" + i + ".exe", i + 1));
        }

        using var r = new ProcessIdentityResolver(n, new ResolverOptions { QueueCapacity = 16, Workers = 1 }, RoleDictionary.Default, new FakeTime());
        var sw = Stopwatch.StartNew();
        r.Observe(n.Snapshot());
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 200, $"Observe took {sw.ElapsedMilliseconds} ms");
        Assert.True(r.Stats.Dropped > 0);
        Assert.True(r.Stats.Enqueued <= 17);
    }

    [Fact]
    public void ShellSurrogateModulesAreCheckedAtMostTwicePerInstance()
    {
        var n = new FakeNativeQuery();
        var dll = new ProcessEntry(2000, 900, "dllhost.exe", 77);
        n.Processes.Add(dll);
        n.CommandLines[2000] = "dllhost.exe /Processid:{AB8902B4-09CA-4BB6-B78D-A8F59079A8D5}";
        n.Modules[2000] = [@"C:\Windows\System32\ntdll.dll", @"C:\Program Files\Icaros\IcarosThumbnailProvider.dll"];
        n.Files[@"C:\Program Files\Icaros\IcarosThumbnailProvider.dll"] = new FileIdentityInfo("", "Icaros Thumbnail Provider", "Icaros", "Xanashi", null, null, null);
        var time = new FakeTime();
        using var r = NewResolver(n, time, new ResolverOptions { ModuleRecheckDelay = TimeSpan.FromSeconds(3) });
        r.Observe(n.Snapshot());
        r.Process(new ProcessIdentityResolver.WorkItem(dll, 0));
        Assert.Equal(1, n.ModuleQueries); // immediate check
        Assert.Equal(1, time.PendingTimers);
        time.Advance(TimeSpan.FromSeconds(3)); // second check queued (workers are off: run it by hand)
        r.Process(new ProcessIdentityResolver.WorkItem(dll, 2));
        Assert.Equal(2, n.ModuleQueries);
        r.Process(new ProcessIdentityResolver.WorkItem(dll, 0)); // same instance again: ignored (already resolved)
        r.Process(new ProcessIdentityResolver.WorkItem(dll, 2)); // a third check is refused
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, time.PendingTimers);
        Assert.Equal(2, n.ModuleQueries);
        Assert.True(r.TryGet(dll.Key, out var info));
        Assert.Contains(info.Hosted, h => h.Kind == SharedHostClassifier.KindExtension && h.Name == "Icaros Thumbnail Provider");
        Assert.DoesNotContain(info.ToToolTipText(), "%", StringComparison.Ordinal); // no resource share is ever attributed
    }
}

public class ComposerTests
{
    [Fact]
    public void TitlePriorityAndMockNeverConfirms()
    {
        var file = new FileIdentityInfo("", "Windows 탐색기", "Microsoft® Windows® Operating System", "Microsoft Corporation", null, "ko-KR", null);
        var facts = new NativeFacts(new ProcessEntry(10, 4, "explorer.exe", 1), @"C:\Windows\explorer.exe", "explorer.exe", file, null, null, [], null, null, false, []);
        var real = IdentityComposer.Compose(facts, new FakeHostContext(), RoleDictionary.Default, out _);
        Assert.Equal("Windows 탐색기", real.Title.Text);
        Assert.Equal(IdentityConfidence.Confirmed, real.Title.Confidence);
        Assert.Equal(IdentityConfidence.Inferred, real.Role!.Confidence);

        var unknownFile = facts with { File = null, Entry = new ProcessEntry(11, 4, "nothing-known.exe", 1) };
        Assert.Equal("nothing-known.exe", IdentityComposer.Compose(unknownFile, new FakeHostContext(), RoleDictionary.Default, out _).Title.Text);

        var mock = IdentityComposer.ComposeMock(7716, "msedgewebview2.exe", @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe --type=gpu-process --webview-exe-name=Antigravity.exe");
        Assert.Equal(IdentitySource.Mock, mock.Source);
        Assert.All(new[] { mock.Title, mock.Executor! }, t => Assert.NotEqual(IdentityConfidence.Confirmed, t.Confidence));
        Assert.Contains("모의", mock.ToToolTipText(), StringComparison.Ordinal);
        Assert.Empty(mock.Notes);
        Assert.Equal(0, mock.Key.CreateTimeTicks);
    }

    [Fact]
    public void DictionaryLoadsAndCoversSharedHosts()
    {
        var d = RoleDictionary.Default;
        Assert.True(d.ImageCount >= 40);
        foreach (var host in new[] { "svchost.exe", "dllhost.exe", "msedgewebview2.exe", "taskhostw.exe", "WmiPrvSE.exe" })
        {
            Assert.True(d.Image(host)!.Shared, host);
        }

        Assert.True(d.AppId(SharedHostClassifier.ThumbnailCacheId)!.ShellExtensionSurrogate);
        Assert.Equal("네트워크·시스템 서비스 그룹", d.SvchostGroup("netsvcs"));
    }
}
