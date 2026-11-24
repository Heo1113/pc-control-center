using System.Text.Json;
using LocalAiPerf.Core.Hardware;
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
}
