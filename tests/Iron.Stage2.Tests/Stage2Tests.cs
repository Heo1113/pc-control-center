using System.Diagnostics;
using System.Net;
using System.Text;
using Iron.ProcessIdentity;
using Iron.Stage2;
using Iron.Stage2.HwInfo;
using LocalAiPerf.Core.Hardware;
using PcControlCenter.GpuMemory;

namespace Iron.Stage2.Tests;

/// <summary>Answers /api/sensors/sdb and /api/sensors/read from the RC3 fixtures; can fail or be slow on chosen calls.</summary>
internal sealed class FakeHwInfo : HttpMessageHandler
{
    public static readonly string Sdb = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "sdb.json"));
    public static readonly string Read = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "read.json"));
    private readonly object _gate = new();

    /// <summary>Read calls that fail (after the delay) — 1-based call numbers.</summary>
    public HashSet<int> FailReads { get; } = [];
    public TimeSpan FailDelay { get; set; } = TimeSpan.Zero;
    public List<(double AtMs, string Path, bool Ok)> Calls { get; } = [];
    public Stopwatch Clock { get; } = Stopwatch.StartNew();
    private int _reads;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var at = Clock.Elapsed.TotalMilliseconds;
        var fail = false;
        lock (_gate)
        {
            if (path == "/api/sensors/read")
            {
                fail = FailReads.Contains(++_reads);
            }
        }

        if (fail)
        {
            await Task.Delay(FailDelay, ct);
            lock (_gate)
            {
                Calls.Add((at, path, false));
            }

            throw new HttpRequestException("connection refused (fake)");
        }

        lock (_gate)
        {
            Calls.Add((at, path, true));
        }

        var body = path == "/api/sensors/sdb" ? Sdb : Read;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

public class HwInfoTests
{
    private static readonly Uri Url = new("http://127.0.0.1:8764/");

    [Fact]
    public void MapsTheRc3FixtureToWhatIronShows()
    {
        using var src = new HwInfoRestSource(Url, TimeSpan.FromSeconds(1), handler: new FakeHwInfo());
        using var sdb = System.Text.Json.JsonDocument.Parse(FakeHwInfo.Sdb);
        using var read = System.Text.Json.JsonDocument.Parse(FakeHwInfo.Read);
        var v = src.Map(read.RootElement, IronPoc.IronModel.ParseSdb(sdb.RootElement.Clone()), DateTimeOffset.UnixEpoch);
        Assert.Equal(64.8, v.CpuTctlC);
        Assert.Equal(86.4, v.CpuPackagePowerW);
        Assert.Equal(48, v.NvmeMaxTempC);
        Assert.True(v.WireView.Connected);
        Assert.Equal("COMPLETE", v.WireView.Completeness);
        Assert.Equal([4.62, 4.71, 4.55, 4.8, 4.49, 4.66], v.WireView.PinCurrentsA);
        Assert.Equal(334.8, v.WireView.PowerW);
        Assert.Equal(12.07, v.WireView.VoltageV);
        Assert.Null(v.WireView.ConnectorInC); // one unnamed "Connector Temperature": not guessed as in or out
    }

    [Fact]
    public void RefusesNonLoopbackAndTheBasaltPort()
    {
        Assert.Throws<ArgumentException>(() => new HwInfoRestSource(new Uri("http://192.168.0.10:8764/"), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new HwInfoRestSource(new Uri("http://127.0.0.1:8080/"), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ReadsLiveValues()
    {
        var fake = new FakeHwInfo();
        using var src = new HwInfoRestSource(Url, TimeSpan.FromMilliseconds(500), handler: fake);
        src.Start();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (src.Latest is null && DateTime.UtcNow < until)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(SourceState.Live, src.Status.State);
        Assert.Equal(64.8, src.Latest!.CpuTctlC);
    }

    /// <summary>
    /// The outage pattern seen on Windows: a read fails only after ~1.2 s (refused connection retried). The client must
    /// not send the missed periods back to back afterwards (RC3 did), and it reads the sensor list again on reconnect.
    /// </summary>
    [Fact]
    public async Task OutageDoesNotPileUpRequests()
    {
        var fake = new FakeHwInfo { FailDelay = TimeSpan.FromMilliseconds(1200) };
        fake.FailReads.UnionWith([3, 4, 5]);
        using var src = new HwInfoRestSource(Url, TimeSpan.FromMilliseconds(500), handler: fake);
        src.Start();
        await Task.Delay(TimeSpan.FromSeconds(9), TestContext.Current.CancellationToken);
        src.Dispose();
        List<(double AtMs, string Path, bool Ok)> calls;
        lock (fake.Calls)
        {
            calls = [.. fake.Calls];
        }

        var reads = calls.Where(c => c.Path == "/api/sensors/read").Select(c => c.AtMs).OrderBy(t => t).ToList();
        // no two reads within 0.8 of an interval (normal spacing is one interval; RC3 sent catch-up reads back to back)
        var gaps = reads.Zip(reads.Skip(1), (a, b) => b - a).ToList();
        Assert.True(gaps.Min() >= 400, "reads too close: " + string.Join(", ", gaps.Select(g => g.ToString("0"))));
        Assert.True(src.Stats.SkippedLate > 0); // the periods spent waiting for the failing reads were skipped, not caught up
        Assert.True(src.Stats.SdbLoads >= 2); // sensor list read again after the outage
        Assert.True(calls.Count(c => c.Path == "/api/sensors/read" && c.Ok) >= 5);
    }
}

public class ComposerTests
{
    private static GpuMemorySnapshot Memory(params GpuProcessMemory[] p) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        Source = "test",
        Adapters =
        [
            new GpuAdapterMemory { Luid = 1, Info = new GpuAdapterInfo(1, "NVIDIA GeForce RTX 5080", 0x10DE, 16L << 30, 0, false) },
            new GpuAdapterMemory { Luid = 2, Info = new GpuAdapterInfo(2, "NVIDIA GeForce RTX 5060 Ti", 0x10DE, 16L << 30, 0, false) },
            new GpuAdapterMemory { Luid = 3, Info = new GpuAdapterInfo(3, "Microsoft Basic Render Driver", 0x1414, 0, 0, true) },
        ],
        Processes = p,
        ProcessTable = new Dictionary<int, PcControlCenter.GpuMemory.ProcessEntry>
        {
            [100] = new PcControlCenter.GpuMemory.ProcessEntry(100, 4, "python.exe", DateTimeOffset.UnixEpoch, ProcessIdentityState.Verified),
            [200] = new PcControlCenter.GpuMemory.ProcessEntry(200, 4, "dwm.exe", DateTimeOffset.UnixEpoch, ProcessIdentityState.Verified),
        },
    };

    [Fact]
    public void NothingIsInventedAndOnlyRedactedCommandLinesAreUsed()
    {
        var hw = new HardwareSnapshot
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            System = SystemSample.Empty with { CpuUtilizationPercent = 12.5, MemoryUsedBytes = 40L << 30, MemoryTotalBytes = 128L << 30 },
            Gpus = [new GpuSample { Index = 0, Name = "NVIDIA GeForce RTX 5080", TemperatureC = 61, PowerW = 210, MemoryUsedBytes = 9L << 30, MemoryTotalBytes = 16L << 30 }],
        };
        var python = new ProcessIdentityInfo
        {
            Key = new Iron.ProcessIdentity.ProcessKey(100, 5),
            ImageName = "python.exe",
            Source = IdentitySource.Native,
            Title = new IdentityText("Python", IdentityConfidence.Confirmed, "버전 정보"),
            RedactedCommandLine = Redactor.Redact(@"python.exe train.py --api-key=sk-live-1234567890abcdef"),
        };
        var frame = Composer.Compose(DateTimeOffset.UnixEpoch, hw, gpuReaderAvailable: true,
            Memory(new GpuProcessMemory { ProcessId = 100, Luid = 1, DedicatedBytes = 6L << 30, SharedBytes = 1L << 29 },
                   new GpuProcessMemory { ProcessId = 100, Luid = 2, DedicatedBytes = 1L << 30 },
                   new GpuProcessMemory { ProcessId = 200, Luid = 3, DedicatedBytes = 1L << 28 }),
            hwinfo: null, diskReadBytesPerSec: null, pid => pid == 100 ? python : null, []);
        Assert.Equal(Stage2Phase.Ready, frame.Phase);
        var gpu = Assert.Single(frame.Gpus);
        Assert.Equal("RTX 5080", gpu.Name);
        Assert.Equal(9, gpu.VramUsedGb);
        Assert.Null(gpu.ClockMhz); // not read: stays N/A
        Assert.Equal(12.5, frame.Cpu.LoadPercent);
        Assert.Null(frame.Cpu.TctlC); // no HWiNFO: no CPU temperature
        Assert.Null(frame.SsdTemperatureC);
        Assert.False(frame.WireView.Connected);
        Assert.Equal(40, frame.RamUsedGb);
        var py = frame.Processes.Single(p => p.Pid == 100);
        Assert.Equal("RTX 5080", py.Adapter); // the adapter with the most dedicated memory; never summed across adapters
        Assert.Equal(6, py.DedicatedGb);
        Assert.DoesNotContain("sk-live", py.CommandLine, StringComparison.Ordinal);
        Assert.Contains("--api-key=***", py.CommandLine, StringComparison.Ordinal);
        var dwm = frame.Processes.Single(p => p.Pid == 200);
        Assert.Equal("", dwm.CommandLine); // not identified yet: no command line at all
        Assert.True(dwm.IsSoftwareAdapter);
    }

    [Fact]
    public void PhasesFollowTheReaders()
    {
        Assert.Equal(Stage2Phase.Initializing, Composer.Compose(DateTimeOffset.UnixEpoch, null, false, null, null, null, _ => null, []).Phase);
        var noGpu = new HardwareSnapshot { Timestamp = DateTimeOffset.UnixEpoch, System = SystemSample.Empty with { CpuUtilizationPercent = 3 } };
        var f = Composer.Compose(DateTimeOffset.UnixEpoch, noGpu, false, null, null, null, _ => null, []);
        Assert.Equal(Stage2Phase.NoDevice, f.Phase);
        Assert.Equal(3, f.Cpu.LoadPercent); // CPU still read without a GPU reader
        Assert.Equal("RTX 5060 Ti", Composer.ShortGpuName("NVIDIA GeForce RTX 5060 Ti"));
    }
}
