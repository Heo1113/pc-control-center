using System.Collections.Concurrent;
using System.Text.Json;
using PcControlCenter.GpuMemory;
using Xunit;

namespace PcControlCenter.Tests;

/// <summary>Answers by the end of the URL, records every request (URL and whether a key was sent), can hold answers back.</summary>
internal sealed class FakeRuntimeHttp : IRuntimeHttp
{
    private readonly ConcurrentQueue<(string Url, string? Bearer)> _calls = new();

    public Dictionary<string, RuntimeHttpResponse> Answers { get; } = [];

    /// <summary>When set, every answer waits for it.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public IReadOnlyList<(string Url, string? Bearer)> Calls => [.. _calls];

    public async Task<RuntimeHttpResponse> GetAsync(string url, string? bearer, CancellationToken ct)
    {
        _calls.Enqueue((url, bearer));
        if (Gate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        return Answers.FirstOrDefault(a => url.EndsWith(a.Key, StringComparison.Ordinal)).Value ?? new RuntimeHttpResponse(404, null, null);
    }

    public static RuntimeHttpResponse Ok(string body) => new(200, body, null);
}

public class RuntimeReportParserTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Strata_engine_keeps_the_start_cache_size_apart_from_the_last_vram_adjustment()
    {
        // The server copies only expert_slots and vram_free_mib from an adjustment to the top level (serve/server.py), so the
        // top-level expert_cache_mib is still the start's and the adjusted size is in engine.vram.
        var r = StrataEngineReport.Parse(Json("""
            {"engine":{"model":"m","max_context":32768,"context":16384,"kv":"int8","expert_slots":1200,"expert_cache_mib":4900,
              "expert_cache_primary_mib":4900,"vram_free_mib":6100,"conversation_cache_mib":"4096","batch_slots":4,"engine":"0.1.9",
              "vram":{"reserve_mib":6000,"expert_slots":1200,"expert_slots_full":3700,"expert_cache_mib":1536,"expert_cache_full_mib":4900,"vram_free_mib":6100}}}
            """));

        Assert.NotNull(r);
        Assert.Equal("int8", r.KvType);
        Assert.Equal(16384, r.Context); // the engine's own context before the server's max_context
        Assert.Equal(4900, r.ExpertCacheMibAtStart);
        Assert.Equal(1200, r.ExpertSlots);
        Assert.Equal(4096, r.ConversationCacheMib); // a number in a string is read too
        Assert.Equal(4, r.BatchSlots);
        Assert.Equal("0.1.9", r.EngineVersion);
        Assert.Equal(new StrataVramAdjustment(6000, 1200, 3700, 1536, 4900, 6100), r.LastAdjustment);
    }

    [Fact]
    public void Strata_engine_without_an_engine_part_or_with_odd_values_reads_what_it_can()
    {
        Assert.Null(StrataEngineReport.Parse(Json("""{"live":{}}""")));
        Assert.Null(StrataEngineReport.Parse(Json("""{"engine":"x"}""")));
        Assert.Null(StrataEngineReport.Parse(Json("[]")));

        var r = StrataEngineReport.Parse(Json("""{"engine":{"max_context":8192,"kv":5,"expert_slots":1e30,"vram_free_mib":12.7,"ds4":true}}"""));
        Assert.NotNull(r);
        Assert.Equal(8192, r.Context); // the server's max_context when the engine gave none
        Assert.Equal("5", r.KvType);
        Assert.Null(r.ExpertSlots); // out of range: not a value
        Assert.Equal(12, r.VramFreeMib);
        Assert.Null(r.LastAdjustment);
    }

    [Fact]
    public void Ollama_models_are_read_as_reported_without_deriving_ram_use()
    {
        var o = OllamaReport.Parse(Json("""
            {"models":[{"name":"gemma3:4b","size":3500000000,"size_vram":1600000000,"context_length":8192,
                        "details":{"parameter_size":"4.3B","quantization_level":"Q4_K_M"}}, 7, {"model":"x"}]}
            """));

        Assert.NotNull(o);
        Assert.Equal(2, o.Models.Count); // the number is skipped
        Assert.Equal(new OllamaModelReport("gemma3:4b", 3_500_000_000, 1_600_000_000, 8192, "4.3B", "Q4_K_M"), o.Models[0]);
        Assert.Equal(new OllamaModelReport("x", null, null, null, null, null), o.Models[1]);
        Assert.Null(OllamaReport.Parse(Json("""{"models":{}}""")));
        Assert.Empty(OllamaReport.Parse(Json("""{"models":[]}"""))!.Models);
    }

    [Fact]
    public void Llama_server_props_give_settings_and_only_the_model_file_name()
    {
        var r = LlamaServerReport.Parse(Json("""
            {"default_generation_settings":{"n_ctx":65536},"total_slots":2,"model_path":"C:\\Users\\someone\\models\\gemma-3-4b-it-Q4_K_M.gguf","build_info":"b7000-abc"}
            """));

        Assert.Equal(new LlamaServerReport(65536, 2, "gemma-3-4b-it-Q4_K_M.gguf", "b7000-abc"), r);
        Assert.Null(LlamaServerReport.Parse(Json("""{"error":"model name is missing from the request"}""")));
    }

    [Fact]
    public void Llama_router_reads_the_effective_allocation_only_never_min_or_max()
    {
        var r = LlamaRouterReport.Parse(Json("""
            {"data":[
              {"id":"gemma","path":"/models/gemma-3-4b-it-Q4_K_M.gguf","memory":{"status":"measured",
                "min":{"n_ctx":4096,"devices":[{"kind":"device","name":"CUDA0","model":1,"context":1,"compute":1}]},
                "max":{"n_ctx":131072,"devices":[{"kind":"device","name":"CUDA0","model":9,"context":9,"compute":9}]},
                "effective":{"n_ctx":131072,"devices":[{"kind":"device","name":"CUDA0","total":17000000000,"free":2000000000,
                  "model":4986635264,"context":536870912,"compute":301989888},{"kind":"host","name":"Host","model":263454720,"context":0,"compute":16793600}]}}},
              {"id":"idle","path":"/models/idle.gguf","memory":{"status":"measured","min":{"n_ctx":4096,"devices":[]},"effective":null}}
            ]}
            """));

        Assert.NotNull(r);
        var loaded = Assert.Single(r.Loaded); // the unloaded model (effective null) is not listed
        Assert.Equal("gemma", loaded.Id);
        Assert.Equal("gemma-3-4b-it-Q4_K_M.gguf", loaded.ModelFile);
        Assert.Equal(131072, loaded.ContextTokens);
        Assert.Equal(new LlamaDeviceMemory("device", "CUDA0", 4986635264, 536870912, 301989888), loaded.Devices[0]);
        Assert.Equal("host", loaded.Devices[1].Kind);
        Assert.Null(LlamaRouterReport.Parse(Json("""{"data":[{"id":"x","memory":{"status":"unmeasured","effective":null}}]}""")));
    }
}

public class RuntimeReportPollerTests
{
    private static readonly RuntimeTarget Strata = new(RuntimeReportSource.StrataEngine, 7340, 18500);

    [Fact]
    public async Task Asks_loopback_only_at_most_every_ten_seconds_and_forgets_targets_no_longer_listed()
    {
        var time = new ManualTime(GpuMemoryFixtures.Start);
        var http = new FakeRuntimeHttp();
        http.Answers["/api/ps"] = FakeRuntimeHttp.Ok("""{"models":[]}""");
        var poller = new RuntimeReportPoller(http, time);
        var ollama = new RuntimeTarget(RuntimeReportSource.Ollama, 8100, 11434);
        var v6 = new RuntimeTarget(RuntimeReportSource.Ollama, 8101, 11435, Ipv6: true);

        poller.Update([ollama, v6]);
        await poller.WhenIdle();
        poller.Update([ollama, v6]); // within the interval: not asked again
        await poller.WhenIdle();

        Assert.Equal(["http://127.0.0.1:11434/api/ps", "http://[::1]:11435/api/ps"], http.Calls.Select(c => c.Url).Order(StringComparer.Ordinal));
        Assert.IsType<OllamaReport>(poller.Results[ollama].Report);
        Assert.Equal(GpuMemoryFixtures.Start, poller.Results[ollama].At);

        time.Advance(RuntimeReportPoller.Interval);
        var version = poller.Version;
        poller.Update([ollama]);
        await poller.WhenIdle();
        Assert.Equal(3, http.Calls.Count);
        Assert.False(poller.Results.ContainsKey(v6)); // forgotten
        Assert.NotEqual(version, poller.Version);

        poller.Update([]);
        Assert.Empty(poller.Results);
    }

    [Fact]
    public async Task An_answer_that_arrives_after_its_target_was_forgotten_is_dropped()
    {
        var http = new FakeRuntimeHttp { Gate = new TaskCompletionSource() };
        http.Answers["/metrics"] = FakeRuntimeHttp.Ok("""{"engine":{"kv":"int8"}}""");
        var poller = new RuntimeReportPoller(http, new ManualTime(GpuMemoryFixtures.Start));

        poller.Update([Strata]);
        poller.Update([]); // the page closed while the request was out
        http.Gate.SetResult();
        await poller.WhenIdle();

        Assert.Empty(poller.Results);
    }

    [Fact]
    public async Task Strata_key_comes_from_the_named_environment_variable_and_a_refusal_says_why()
    {
        var variable = "PCC_TEST_STRATA_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "dummy-test-value");
        try
        {
            var http = new FakeRuntimeHttp();
            http.Answers["/metrics"] = new RuntimeHttpResponse(401, """{"error":"unauthorized"}""", null);
            var poller = new RuntimeReportPoller(http, new ManualTime(GpuMemoryFixtures.Start));
            var target = Strata with { CredentialVariable = variable };

            poller.Update([target]);
            await poller.WhenIdle();

            var call = Assert.Single(http.Calls);
            Assert.Equal("http://127.0.0.1:18500/metrics", call.Url);
            Assert.Equal("dummy-test-value", call.Bearer);
            Assert.Null(poller.Results[target].Report);
            Assert.Contains("API 키", poller.Results[target].Error, StringComparison.Ordinal);
            Assert.DoesNotContain("dummy-test-value", poller.Results[target].Error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData(500, "{}", null, "HTTP 500")]
    [InlineData(0, null, "연결 안 됨", "연결 안 됨")]
    [InlineData(200, "{not json", null, "응답 형식을 읽지 못했습니다")]
    [InlineData(200, """{"live":{}}""", null, "엔진 정보가 없는 응답입니다")]
    public async Task Failures_are_kept_as_reasons_never_as_values(int status, string? body, string? error, string expected)
    {
        var http = new FakeRuntimeHttp();
        http.Answers["/metrics"] = new RuntimeHttpResponse(status, body, error);
        var poller = new RuntimeReportPoller(http, new ManualTime(GpuMemoryFixtures.Start));

        poller.Update([Strata]);
        await poller.WhenIdle();

        Assert.Null(poller.Results[Strata].Report);
        Assert.Equal(expected, poller.Results[Strata].Error);
        Assert.Null(Assert.Single(http.Calls).Bearer); // no variable named: no key sent
    }

    [Fact]
    public async Task Llama_server_is_asked_for_props_and_models()
    {
        var http = new FakeRuntimeHttp();
        http.Answers["/props"] = FakeRuntimeHttp.Ok("""{"default_generation_settings":{"n_ctx":4096},"total_slots":1,"model_path":"/m/a.gguf"}""");
        var poller = new RuntimeReportPoller(http, new ManualTime(GpuMemoryFixtures.Start));
        var target = new RuntimeTarget(RuntimeReportSource.LlamaServer, 900, 8080);

        poller.Update([target]);
        await poller.WhenIdle();

        Assert.Equal(["http://127.0.0.1:8080/models", "http://127.0.0.1:8080/props"], http.Calls.Select(c => c.Url).Order(StringComparer.Ordinal));
        Assert.Equal(new LlamaServerReport(4096, 1, "a.gguf", null), poller.Results[target].Report);
        Assert.Null(poller.Results[target].Extra); // /models answered 404: no router part
    }
}
