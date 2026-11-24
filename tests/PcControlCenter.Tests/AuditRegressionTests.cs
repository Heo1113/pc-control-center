using System.Net;
using System.Text;
using System.Text.Json;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Diagnostics;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Runtimes.Strata;
using Microsoft.Data.Sqlite;
using PcControlCenter.Basalt;
using PcControlCenter.Core;
using PcControlCenter.GpuMemory;
using PcControlCenter.Migration;
using PcControlCenter.Modules;
using PcControlCenter.Presentation;

namespace PcControlCenter.Tests;

public class AuditRegressionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Owned_python_listener_is_still_recognized_after_wrapper_and_engine_exit()
    {
        var system = new FakeBasaltSystem();
        await using var controller = BasaltTest.Controller(system);
        Assert.Null(await controller.StartServerAsync(BasaltSettings.QualityMode, Ct));
        Assert.True(controller.Status.StartedHere);
        system.Remove(FakeBasaltSystem.Launcher);
        system.Remove(FakeBasaltSystem.Wrapper);
        system.Remove(FakeBasaltSystem.Engine);

        await controller.RefreshAsync(Ct);

        Assert.True(controller.Status.StartedHere, $"state={controller.Status.State}, pythonAlive={system.Has(FakeBasaltSystem.Server)}");
    }

    [Fact]
    public async Task Failed_health_is_not_shown_as_running()
    {
        var system = new FakeBasaltSystem { Health = new BasaltHealth(500, null, null, null) };
        await using var controller = BasaltTest.Controller(system);
        Assert.Null(await controller.StartServerAsync(BasaltSettings.QualityMode, Ct));
        Assert.NotEqual(BasaltState.Running, controller.Status.State);
    }

    [Fact]
    public async Task Unavailable_status_is_distinct_from_unsupported_uptime()
    {
        var http = new ReplyByPath(p => p == "/v1/status" ? (503, "{}") : (404, "{}"));
        using var reader = new BasaltApiReader(handler: http);
        var result = await reader.ReadAsync(8080, Ct);
        Assert.NotEqual(BasaltFieldState.Unsupported, result.Field(BasaltApiReader.Uptime).State);
    }

    [Fact]
    public async Task Basalt_shaped_legacy_metrics_are_read_when_telemetry_returns_404()
    {
        var http = new ReplyByPath(p => p switch
        {
            "/metrics" => (200, """{"time":1793534400,"engine":{"version":"0.4.1"},"live":{},"totals":{}}"""),
            "/v1/status" => (200, """{"engine":"0.4.1","model":"audit-model","loaded":true,"cache_max_tokens":131072}"""),
            "/health" => (200, """{"service":"basalt","loaded":true}"""),
            _ => (404, "{}")
        });
        using var adapter = new StrataAdapter(new StrataAdapterOptions("http://127.0.0.1:8080", null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30)), handler: http);
        var snapshot = await adapter.PollAsync(Ct);
        Assert.Equal("legacy-adapter", snapshot.Server?.TelemetrySource);
        Assert.Equal("audit-model", snapshot.Model?.Id);
    }

    [Fact]
    public void Missing_process_counters_do_not_explain_the_entire_adapter_as_unattributed()
    {
        var sample = GpuMemoryFixtures.Snapshot(GpuMemoryFixtures.Start, processes: []) with { Variant = ProcessCounterVariant.Unavailable };
        var view = Display(sample);
        Assert.DoesNotContain(view.Adapters.Single().View.Legend, item => item.Label == "나뉘지 않음");
    }

    [Fact]
    public void Basalt_engine_without_a_port_link_omits_the_dense_GGUF_weight_split()
    {
        var at = GpuMemoryFixtures.Start;
        var model = new GgufSummary { Kind = "LLM", Name = "dense-test", GpuWeightBytesAllLayers = 600 * GpuMemoryFixtures.MiB };
        var sample = GpuMemoryFixtures.Snapshot(at, table: new Dictionary<int, ProcessEntry>
        {
            [100] = new(100, 10, "basalt.exe", at.AddMinutes(-1), ProcessIdentityState.Verified),
        }) with
        {
            Details = new Dictionary<int, ProcessDetail>
            {
                [100] = new() { Models = [new ModelFile("dense.gguf", 640_000_000, "GGUF", model)], ScannedAt = at },
            },
        };
        Assert.Empty(Display(sample).Detail!.Breakdown);
    }

    [Fact]
    public void A_new_listener_is_not_labelled_Basalt_from_the_last_port_alone()
    {
        var at = GpuMemoryFixtures.Start;
        var current = GpuMemoryFixtures.Snapshot(at, listeners: [new TcpListenerEntry(8080, 100, false, true)]);
        var view = Display(current, () => new BasaltContext(8080, "이전 Basalt 관측"));
        Assert.Empty(view.Processes.Single().ServerLabel);
    }

    [Fact]
    public async Task Import_rejects_a_staged_copy_that_lost_a_rehearsed_table()
    {
        using var temp = new TempDir();
        var (paths, source) = OperatorPaths(temp);
        var db = Path.Combine(source, "wao.db");
        Sql(db, "CREATE TABLE audit_marker(value TEXT); INSERT INTO audit_marker VALUES ('original');");
        var migrator = new LegacyMigrator(paths, _ => []);
        var rehearsal = migrator.Rehearse(LegacyMigrator.FromFolder(source)!);
        Assert.True(rehearsal.Passed);
        Assert.Equal(1L, rehearsal.TableCounts["audit_marker"]);
        Sql(rehearsal.FinalDatabase!, "DROP TABLE audit_marker;");
        var result = await migrator.ApplyAsync(rehearsal, new ReadyRestarter(), false);
        Assert.False(result.Applied, result.Message);
    }

    [Fact]
    public async Task Import_must_not_report_success_when_the_module_cannot_restart()
    {
        using var temp = new TempDir();
        var (paths, source) = OperatorPaths(temp);
        var migrator = new LegacyMigrator(paths, _ => []);
        var rehearsal = migrator.Rehearse(LegacyMigrator.FromFolder(source)!);
        Assert.True(rehearsal.Passed);
        var supervisor = new ModuleSupervisor(NullLogger.Instance);
        var module = supervisor.Add(new FailingModule());
        var result = await migrator.ApplyAsync(rehearsal, new SupervisorModuleRestarter(supervisor), false);
        Assert.Equal(ModuleState.Failed, module.Status.State);
        Assert.False(result.Applied, result.Message);
    }

    [Fact]
    public async Task Module_does_not_advertise_a_completed_stop_while_its_writer_is_still_open()
    {
        var module = new SlowStop { StopTimeout = TimeSpan.FromMilliseconds(30) };
        await module.StartAsync(Ct);
        try
        {
            await module.StopAsync();
            Assert.False(module.Finish.Task.IsCompleted);
            Assert.NotEqual(ModuleState.Stopped, module.Status.State);
        }
        finally
        {
            module.Finish.TrySetResult();
        }
    }

    [Fact]
    public void Saving_a_known_preference_preserves_future_schema_fields()
    {
        using var temp = new TempDir();
        var path = temp.Sub("shell-settings.json");
        File.WriteAllText(path, """{"SchemaVersion":2,"FutureProfile":{"value":42}}""");
        var store = new ShellSettingsStore(path);
        var loaded = store.Load();
        Assert.False(loaded.WasCreated);
        loaded.Settings.Basalt.Mode = BasaltSettings.FastMode;
        store.Save(loaded.Settings);
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(saved.RootElement.TryGetProperty("FutureProfile", out _));
    }

    [Fact]
    public void Two_GPU_total_power_needs_both_readings()
    {
        using var temp = new TempDir();
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var hub = GpuMemoryFixtures.Hub(time);
        var module = new GpuEconomicsModule(new ModuleContext
        {
            Paths = new ShellPaths(temp.Path, false),
            Settings = new ShellSettings(),
            Hub = hub,
            Intervals = new SensorIntervalCoordinator(hub, TimeSpan.FromSeconds(1)),
        });
        var sample = new HardwareSnapshot
        {
            Timestamp = time.GetUtcNow(),
            Gpus = [new GpuSample { Index = 0, Name = "5080", PowerW = 220 }, new GpuSample { Index = 1, Name = "5060 Ti", PowerW = null }],
        };

        var summary = ModuleSummaries.Economics(module, sample, sample.Timestamp, TimeZoneInfo.Utc);

        Assert.Null(summary.GpuBoardPowerW);
    }

    private static GpuMemoryViewModel Display(GpuMemorySnapshot sample, Func<BasaltContext>? basalt = null)
    {
        var time = new ManualTime(sample.Timestamp);
        var monitor = new GpuMemoryMonitor(() => new ScriptedGpuMemoryProvider(_ => sample), time);
        var view = new GpuMemoryViewModel(monitor, GpuMemoryFixtures.Hub(time), () => new StrataContext(null, null), time, basalt: basalt);
        view.IsVisible = true;
        monitor.ReadNow();
        view.Refresh();
        return view;
    }

    private static (ShellPaths Paths, string Source) OperatorPaths(TempDir temp)
    {
        var source = temp.Sub("WindowsAiOperator");
        Directory.CreateDirectory(source);
        using (new WindowsAiOperator.Storage.OperatorStore(Path.Combine(source, "wao.db"))) { }
        var paths = ShellPaths.Resolve(temp.Sub("pcc"), temp.Path, legacy: []);
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.ModuleDirectory(ModuleIds.Operator));
        return (paths, source);
    }

    private static void Sql(string path, string text)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = text;
        cmd.ExecuteNonQuery();
    }

    private sealed class ReadyRestarter : IModuleRestarter
    {
        public Task StopAsync(string moduleId) => Task.CompletedTask;
        public Task StartAsync(string moduleId) => Task.CompletedTask;
    }

    private sealed class FailingModule() : ShellModuleBase(ModuleIds.Operator, NullLogger.Instance)
    {
        protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.FromException(new IOException("simulated storage open failure"));
        protected override Task OnStopAsync() => Task.CompletedTask;
    }

    private sealed class SlowStop() : ShellModuleBase(ModuleIds.Operator, NullLogger.Instance)
    {
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task OnStopAsync() => Finish.Task;
    }

    private sealed class ReplyByPath(Func<string, (int Status, string Body)> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = response(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
