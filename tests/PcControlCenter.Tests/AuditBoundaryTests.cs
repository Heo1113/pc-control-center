using System.Text.Json;
using LocalAiPerf.Core.Hardware;
using PcControlCenter.Basalt;
using PcControlCenter.Core;
using PcControlCenter.GpuMemory;
using PcControlCenter.Presentation;

namespace PcControlCenter.Tests;

public class AuditBoundaryTests
{
    private sealed class DelayedHttp : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { if (ct.IsCancellationRequested) Cancelled.TrySetResult(); }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Hiding_the_basalt_page_cancels_inflight_json_and_only_the_exact_dashboard_origin_is_allowed()
    {
        using var temp = new TempDir();
        var system = new FakeBasaltSystem();
        var settings = new ShellSettings();
        var http = new DelayedHttp();
        await using var controller = BasaltTest.Controller(system);
        using var vm = new BasaltViewModel(controller, new ShellSettingsStore(temp.Sub("settings.json")), settings,
            new RecordingShellDialogs(), new LockedDispatcher(), temp.Path, api: new PcControlCenter.Basalt.BasaltApiReader(http));
        await controller.StartServerAsync(BasaltSettings.QualityMode, TestContext.Current.CancellationToken);
        Assert.True(vm.IsDashboardAddress(new Uri("http://127.0.0.1:8080/dashboard.js")));
        Assert.False(vm.IsDashboardAddress(new Uri("http://localhost:8080/dashboard.js")));
        Assert.False(vm.IsDashboardAddress(new Uri("http://127.0.0.1:8081/dashboard.js")));
        vm.IsVisible = true;
        vm.Tick();
        await http.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        vm.IsVisible = false;
        await http.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Empty(vm.ApiFields);
    }

    [Fact]
    public void Dual_gpu_pairing_uses_pci_location_with_unique_uuid_and_uncertain_pairs_remain_absent()
    {
        var a = new GpuAdapterInfo(14, "RTX 5080", GpuVendors.Nvidia, 16L << 30, 0, false) { PciAddress = new(null, 2, 0, 0) };
        var b = a with { Luid = 37, Name = "RTX 5060 Ti", PciAddress = new(null, 19, 0, 0) };
        GpuAdapterMemory[] adapters = [new() { Luid = a.Luid, Info = a }, new() { Luid = b.Luid, Info = b }];
        var first = new GpuSample { Index = 1, DeviceUuid = "GPU-1", PciAddress = GpuPciAddress.FromNvml("00000000:02:00.0"), MemoryUsedBytes = 1L << 30 };
        var second = new GpuSample { Index = 0, DeviceUuid = "GPU-2", PciAddress = GpuPciAddress.FromNvml("00000000:13:00.0"), MemoryUsedBytes = 5L << 30 };
        Assert.Same(first, GpuMemoryViewModel.MatchNvml(a, adapters, [second, first]));
        Assert.Same(second, GpuMemoryViewModel.MatchNvml(b, adapters, [second, first]));
        Assert.Null(GpuMemoryViewModel.MatchNvml(b, adapters, [first, second with { DeviceUuid = "GPU-1" }]));
        Assert.Null(GpuMemoryViewModel.MatchNvml(b, adapters, [first, second with { PciAddress = first.PciAddress }]));
        Assert.Null(GpuMemoryViewModel.MatchNvml(b, adapters, [first, second with { PciAddress = new(1, 19, 0, 0) }]));
        Assert.Null(GpuMemoryViewModel.MatchNvml(b, adapters, [first]));
    }

    [Fact]
    public void Settings_roundtrip_keeps_nested_unknown_fields_and_distinct_basalt_profiles()
    {
        using var temp = new TempDir();
        var file = temp.Sub("settings.json");
        File.WriteAllText(file, """{"SchemaVersion":2,"Basalt":{"NewProfile":{"ids":[2,5]}}}""");
        var store = new ShellSettingsStore(file);
        var loaded = store.Load();
        var settings = loaded.Settings.Basalt;
        settings.DualConfig = "local-dual.json";
        settings.Mode = BasaltSettings.DualMode;
        settings.EmbeddedDashboard = false;
        store.Save(loaded.Settings);
        using var copy = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal(2, copy.RootElement.GetProperty("Basalt").GetProperty("NewProfile").GetProperty("ids").GetArrayLength());
        var saved = store.Load().Settings.Basalt;
        Assert.Equal("local-dual.json", saved.ConfigFor(saved.Mode));
        Assert.Equal("basalt_config.json", saved.ConfigFor(BasaltSettings.QualityMode));
        Assert.Equal("basalt_config.fast.json", saved.ConfigFor(BasaltSettings.FastMode));
        Assert.False(saved.EmbeddedDashboard);
    }

    [Fact]
    public void Owned_venv_parent_is_included_after_launcher_exit_while_reused_protected_and_unrelated_processes_stay_out()
    {
        var t0 = new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
        const int stubId = 515;
        const int listenerId = 520;
        const int bystanderId = 701;
        const int protectedChildId = 702;
        var stub = new BasaltProcess(stubId, FakeBasaltSystem.Launcher, "python.exe", t0.AddSeconds(1));
        var listener = new BasaltProcess(listenerId, stubId, "python.exe", t0.AddSeconds(2));
        var bystander = new BasaltProcess(bystanderId, FakeBasaltSystem.Explorer, "python.exe", t0.AddSeconds(2));
        var protectedChild = new BasaltProcess(protectedChildId, FakeBasaltSystem.Explorer, "other-app.exe", t0.AddSeconds(3));
        BasaltProcess[] baseProcesses =
        [
            new(FakeBasaltSystem.Explorer, 4, "explorer.exe", t0.AddHours(-1)),
            new(FakeBasaltSystem.Self, FakeBasaltSystem.Explorer, "testhost.exe", t0.AddMinutes(-10)),
            stub,
            listener,
            bystander,
            protectedChild,
        ];
        BasaltListener[] listeners = [new(8080, listenerId)];
        var ownedSet = new HashSet<(int Id, long Ticks)>
        {
            (stub.Id, stub.StartTime!.Value.UtcTicks),
            (listener.Id, listener.StartTime!.Value.UtcTicks),
        };
        bool Owns(BasaltProcess p) => p.StartTime is { } s && ownedSet.Contains((p.Id, s.UtcTicks));

        // 1. Launcher already gone: listener + owned venv stub both included (listener ended first), bystander excluded.
        var tree = BasaltProcessTree.Identify(8080, baseProcesses, listeners, launch: null, FakeBasaltSystem.Self, Owns);
        Assert.True(tree.IsBasalt);
        Assert.Equal([listenerId, stubId], tree.Members.Select(m => m.Process.Id));
        Assert.Equal(listenerId, tree.ConsoleCandidates[0].Id);
        Assert.Contains(tree.ConsoleCandidates, p => p.Id == stubId);
        Assert.DoesNotContain(tree.Members, m => m.Process.Id is bystanderId or protectedChildId or FakeBasaltSystem.Explorer or FakeBasaltSystem.Self);

        // 2. PID reuse on the stub parent (newer start time) or unknown StartTime: stub must NOT be included.
        var reusedStub = stub with { Name = "unrelated.exe", StartTime = t0.AddSeconds(10) };
        var treeReused = BasaltProcessTree.Identify(8080, [baseProcesses[0], baseProcesses[1], reusedStub, listener, bystander], listeners, null, FakeBasaltSystem.Self, Owns);
        Assert.True(treeReused.IsBasalt);
        Assert.Equal([listenerId], treeReused.Members.Select(m => m.Process.Id));

        var unreadableStub = stub with { StartTime = null };
        var treeUnreadable = BasaltProcessTree.Identify(8080, [baseProcesses[0], baseProcesses[1], unreadableStub, listener], listeners, null, FakeBasaltSystem.Self, _ => true);
        Assert.Equal([listenerId], treeUnreadable.Members.Select(m => m.Process.Id));

        // 3. Even if a loose predicate claims a protected ancestor is owned, neither it nor its unrelated children enter Members.
        var treeLoose = BasaltProcessTree.Identify(8080, baseProcesses, listeners, null, FakeBasaltSystem.Self,
            p => p.Id is listenerId or FakeBasaltSystem.Explorer or FakeBasaltSystem.Self);
        Assert.Equal([listenerId], treeLoose.Members.Select(m => m.Process.Id));

        // 4. External Basalt on the port (named basalt.exe, not owned) must NOT absorb a stale previouslyOwned process.
        var extWrapper = new BasaltProcess(810, FakeBasaltSystem.Explorer, "basalt.exe", t0.AddMinutes(1));
        var extListener = new BasaltProcess(820, 810, "python.exe", t0.AddMinutes(1).AddSeconds(1));
        var treeExternal = BasaltProcessTree.Identify(8080, [baseProcesses[0], baseProcesses[1], stub, extWrapper, extListener],
            [new BasaltListener(8080, extListener.Id)], null, FakeBasaltSystem.Self, Owns);
        Assert.True(treeExternal.IsBasalt);
        Assert.Equal([extListener.Id, extWrapper.Id], treeExternal.Members.Select(m => m.Process.Id));
        Assert.DoesNotContain(treeExternal.Members, m => m.Process.Id == stubId);
    }

    [Fact(Timeout = 30_000)]
    public async Task Owned_venv_server_sends_both_stub_and_listener_to_console_signal_after_launcher_exit_and_restart()
    {
        using var temp = new TempDir();
        var ownershipFile = temp.Sub("basalt-owned.json");
        var system = new FakeBasaltSystem { StartCreatesTree = false };
        const int stubId = 515;
        const int listenerId = 520;
        const int bystanderId = 701;

        await using (var first = new BasaltController(system, new BasaltSettings(), timings: BasaltTest.Fast, selfId: FakeBasaltSystem.Self, ownershipFile: ownershipFile))
        {
            Assert.Null(await first.StartServerAsync(BasaltSettings.QualityMode, TestContext.Current.CancellationToken));
            var t = DateTimeOffset.UtcNow.AddMilliseconds(50);
            system.Add(new BasaltProcess(stubId, FakeBasaltSystem.Launcher, "python.exe", t));
            system.Add(new BasaltProcess(listenerId, stubId, "python.exe", t.AddMilliseconds(50)));
            system.Add(new BasaltProcess(bystanderId, FakeBasaltSystem.Explorer, "python.exe", t.AddMilliseconds(50)));
            system.Listen(listenerId);

            var running = await BasaltTest.WaitFor(first, s => s.State == BasaltState.Running && s.StartedHere);
            Assert.Contains(running.Tree!.Members, m => m.Process.Id == stubId);
            Assert.Contains(running.Tree!.Members, m => m.Process.Id == listenerId);
        }

        // Launcher window exits while venv stub + python listener remain running.
        system.Remove(FakeBasaltSystem.Launcher);

        await using var second = new BasaltController(system, new BasaltSettings(), timings: BasaltTest.Fast, selfId: FakeBasaltSystem.Self, ownershipFile: ownershipFile);
        var restored = await BasaltTest.WaitFor(second, s => s.State == BasaltState.Running && s.StartedHere);
        Assert.Equal([listenerId, stubId], restored.Tree!.Members.Select(m => m.Process.Id));

        var report = await second.StopServerAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(report);
        Assert.True(report.Graceful, report.Signal);
        Assert.True(report.Clean, report.Summary);
        var passedOwned = Assert.Single(system.SignalOwned);
        Assert.Equal([listenerId, stubId], passedOwned.Select(p => p.Id));
        Assert.True(system.Has(bystanderId));
        Assert.False(File.Exists(ownershipFile));
    }
}
