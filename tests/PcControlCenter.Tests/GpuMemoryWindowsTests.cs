using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using LocalAiPerf.Core.Hardware;
using PcControlCenter.GpuMemory;
using PcControlCenter.GpuMemory.Windows;

namespace PcControlCenter.Tests;

/// <summary>
/// The Windows provider against the real APIs of the machine (the Windows CI runner: no NVIDIA GPU, so this checks the
/// bindings, the byte order and the "unavailable with a reason" path, not RTX 5080 values). Skipped elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
public class GpuMemoryWindowsTests
{
    private const string WindowsOnly = "Windows API (PDH, DXGI, Toolhelp, iphlpapi)";

    [Fact(Timeout = 60_000)]
    public void The_windows_counters_give_a_reading_or_say_why()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var provider = new WindowsGpuMemoryProvider();
        var started = Stopwatch.GetTimestamp();
        var first = provider.Read(GpuMemoryReadRequest.None);
        var second = provider.Read(GpuMemoryReadRequest.None);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"unavailable: {provider.UnavailableReason ?? "-"}; variant {second.Variant}; committed {second.CommittedAvailable}; adapter counters {second.AdapterCountersAvailable}");
        output?.WriteLine($"adapters: {string.Join(" | ", second.Adapters.Select(a => $"{a.Info?.Name ?? "?"} {GpuLuid.Format(a.Luid)} soft={a.Info?.IsSoftware} ded={a.DedicatedUsageBytes} shr={a.SharedUsageBytes}"))}");
        output?.WriteLine($"processes: {second.Processes.Count}, listed {second.Processes.Count(GpuMemoryRules.IsListed)}, table {second.ProcessTable.Count}; cost {first.CollectionTime.TotalMilliseconds:0.0} / {second.CollectionTime.TotalMilliseconds:0.0} ms");
        output?.WriteLine("issues: " + string.Join(" | ", second.Issues.Select(i => $"[{i.Scope}] {i.Message}")));

        Assert.True(second.Timestamp >= first.Timestamp);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"two readings took {elapsed.TotalSeconds:0.0} s");
        if (provider.UnavailableReason is { } reason)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains(second.Issues, i => i.Status == SensorStatus.Unavailable);
            return;
        }

        Assert.True(second.Variant != ProcessCounterVariant.Unavailable || second.AdapterCountersAvailable);
        // Every process row names an adapter the reading lists, and its identity was looked up.
        Assert.All(second.Processes, p => Assert.Contains(second.Adapters, a => a.Luid == p.Luid));
        Assert.All(second.Processes, p => Assert.True(second.ProcessTable.ContainsKey(p.ProcessId), $"PID {p.ProcessId} has no identity"));
        Assert.All(second.Processes, p => Assert.True(p.DedicatedBytes is null or >= 0 && p.SharedBytes is null or >= 0));
    }

    [Fact(Timeout = 60_000)]
    public void Engine_activity_and_process_details_are_read_only_when_asked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var provider = new WindowsGpuMemoryProvider();
        Assert.SkipWhen(provider.UnavailableReason is not null, "no GPU counters on this machine");
        var plain = provider.Read(GpuMemoryReadRequest.None);
        Assert.False(plain.EnginesRead);
        Assert.Empty(plain.Details);
        Assert.All(plain.Processes, p => Assert.Null(p.Engines));

        var request = GpuMemoryReadRequest.None with { IncludeDetails = true };
        provider.Read(request); // engine utilization is a rate: the first collection is the baseline
        var detailed = provider.Read(request);
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"engines read {detailed.EnginesRead}; cost {detailed.CollectionTime.TotalMilliseconds:0.0} ms; details {detailed.Details.Count}");
        output?.WriteLine("adapters: " + string.Join(" | ", detailed.Adapters.Select(a => $"{a.Info?.Name ?? "?"} {a.Engines}")));
        foreach (var (pid, detail) in detailed.Details)
        {
            output?.WriteLine($"PID {pid} {detailed.Process(pid)?.Name}: denied={detail.AccessDenied} partial={detail.Partial} libraries={detail.LoadedLibraries} "
                              + $"runtimes=[{string.Join(", ", detail.Runtimes)}] models={detail.Models.Count}");
        }

        output?.WriteLine("issues: " + string.Join(" | ", detailed.Issues.Select(i => $"[{i.Scope}] {i.Message}")));
        if (detailed.EnginesRead)
        {
            Assert.All(detailed.Adapters, a => Assert.NotNull(a.Engines));
            Assert.All(detailed.Processes.Where(p => p.Engines is not null), p => Assert.InRange(p.Engines!.Busiest, 0, 100));
        }
        else
        {
            Assert.Contains(detailed.Issues, i => i.Scope.StartsWith("engine", StringComparison.Ordinal)); // says why
        }

        Assert.All(detailed.Details.Values, d => Assert.All(d.Models, m => Assert.DoesNotContain('\\', m.FileName)));
        Assert.True(detailed.CollectionTime < TimeSpan.FromSeconds(3), $"a detailed reading took {detailed.CollectionTime.TotalMilliseconds:0} ms");
    }

    [Fact(Timeout = 60_000)]
    public void The_scanner_lists_the_libraries_of_a_process_with_drive_letter_paths()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var scan = ProcessScanner.Scan(Environment.ProcessId, DosDevices.Load(), TimeSpan.FromSeconds(10));

        Assert.False(scan.AccessDenied);
        Assert.False(scan.Partial);
        Assert.Contains(scan.Libraries, l => l.EndsWith(@"\ntdll.dll", StringComparison.OrdinalIgnoreCase) && l.Length > 3 && l[1] == ':');
        Assert.Contains(scan.Libraries, l => l.EndsWith(@"\coreclr.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RawProcessScan.Denied, ProcessScanner.Scan(4, DosDevices.Load(), TimeSpan.FromSeconds(1))); // the kernel's System process
    }

    [Fact(Timeout = 60_000)]
    public void A_memory_mapped_gguf_file_is_found_by_name_and_its_header_read()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var temp = new TempDir();
        // The runner's TEMP can be a short 8.3 path while Windows reports the long one: matched by a unique file name.
        var name = $"Tiny-{Guid.NewGuid():N}-Q8_0.gguf";
        var path = temp.Sub(name);
        File.WriteAllBytes(path, GgufFixtures.Qwen3().Build());
        using var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        var devices = DosDevices.Load();

        var scan = ProcessScanner.Scan(Environment.ProcessId, devices, TimeSpan.FromSeconds(10));
        Assert.Contains(scan.MappedFiles, f => f.EndsWith(@"\" + name, StringComparison.OrdinalIgnoreCase));

        // The inspector with the real scan: a GPU memory row and a verified identity make this process a candidate.
        var inspector = new ProcessInspector((pid, budget) => ProcessScanner.Scan(pid, devices, budget));
        var table = ProcessQueries.Table([Environment.ProcessId], DateTimeOffset.UtcNow.AddSeconds(1), new List<SensorIssue>());
        var details = inspector.Inspect([new GpuProcessMemory { ProcessId = Environment.ProcessId, Luid = 1, DedicatedBytes = 1 }], table, TimeSpan.FromSeconds(10));
        var model = Assert.Single(details[Environment.ProcessId].Models);
        Assert.Equal(name, model.FileName);
        Assert.Equal("GGUF", model.Format);
        Assert.Equal("LLM", model.Gguf!.Kind);
        Assert.Equal(114_688, model.Gguf.KvBytesPerTokenF16);
    }

    [Fact]
    public void Dxgi_lists_the_adapters_with_luids()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var adapters = DxgiAdapters.Enumerate(out var error);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(" | ", adapters.Select(a => $"{a.Name} {a.VendorName} {a.LuidText} soft={a.IsSoftware} ded={a.DedicatedVideoMemoryBytes}")));
        Assert.Null(error);
        // Windows always lists at least one adapter (the Microsoft Basic Render Driver when there is no GPU).
        Assert.NotEmpty(adapters);
        Assert.All(adapters, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.Equal(adapters.Count, adapters.Select(a => a.Luid).Distinct().Count());
    }

    [Fact]
    public void The_own_process_is_identified_with_the_same_start_time_as_dotnet_reports()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var issues = new List<SensorIssue>();
        var table = ProcessQueries.Table([Environment.ProcessId], DateTimeOffset.UtcNow.AddSeconds(1), issues);

        var self = table[Environment.ProcessId];
        Assert.Empty(issues);
        Assert.Equal(ProcessIdentityState.Verified, self.State);
        Assert.EndsWith(".exe", self.Name, StringComparison.OrdinalIgnoreCase);
        using var current = Process.GetCurrentProcess();
        Assert.Equal(current.StartTime.ToUniversalTime(), self.StartTime!.Value.UtcDateTime, TimeSpan.FromMilliseconds(1));
        Assert.True(table.ContainsKey(self.ParentProcessId) || self.ParentProcessId <= 4, "the parent chain is part of the table");

        // A sample taken before the process started cannot be this process's: the PID is reported as reused.
        var before = ProcessQueries.Table([Environment.ProcessId], self.StartTime.Value.AddSeconds(-1), issues);
        Assert.Equal(ProcessIdentityState.ReusedAfterSample, before[Environment.ProcessId].State);
    }

    [Fact]
    public void Loopback_listeners_are_found_with_their_owner_and_port_in_host_order()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var v4 = new TcpListener(IPAddress.Loopback, 0);
        var v6 = new TcpListener(IPAddress.IPv6Loopback, 0);
        v4.Start();
        v6.Start();
        try
        {
            var port4 = ((IPEndPoint)v4.LocalEndpoint).Port;
            var port6 = ((IPEndPoint)v6.LocalEndpoint).Port;
            var issues = new List<SensorIssue>();
            var listeners = ProcessQueries.Listeners(issues);

            Assert.Empty(issues);
            Assert.Contains(listeners, l => l.Port == port4 && l.ProcessId == Environment.ProcessId && !l.IsIpv6 && l.IsLoopbackOnly);
            Assert.Contains(listeners, l => l.Port == port6 && l.ProcessId == Environment.ProcessId && l.IsIpv6 && l.IsLoopbackOnly);
        }
        finally
        {
            v4.Stop();
            v6.Stop();
        }
    }

    [Fact(Timeout = 60_000)]
    public void A_real_child_process_of_a_listening_process_is_linked_by_parent_pid_and_start_time()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var issues = new List<SensorIssue>();
            var sampleTime = DateTimeOffset.UtcNow;
            var listeners = ProcessQueries.Listeners(issues).Where(l => l.Port == port).ToList();
            var table = ProcessQueries.Table([child.Id, .. listeners.Select(l => l.ProcessId)], sampleTime, issues);
            var snapshot = new GpuMemorySnapshot
            {
                Timestamp = sampleTime,
                Source = "test",
                Processes = [new GpuProcessMemory { ProcessId = child.Id, Luid = 1, DedicatedBytes = 123 }], // the bytes are made up; the tree is real
                ProcessTable = table,
                Listeners = listeners,
            };

            var link = ServerProcessLinker.Link($"http://127.0.0.1:{port}", snapshot);

            Assert.True(link.IsLinked, link.Reason);
            Assert.Equal(Environment.ProcessId, link.Listener!.ProcessId);
            var linkedChild = Assert.Single(link.Processes, p => p.Process.ProcessId == child.Id);
            Assert.Equal(1, linkedChild.Depth);
            Assert.Equal("PING.EXE", linkedChild.Process.Name.ToUpperInvariant());
        }
        finally
        {
            try
            {
                child.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            listener.Stop();
        }
    }
}
