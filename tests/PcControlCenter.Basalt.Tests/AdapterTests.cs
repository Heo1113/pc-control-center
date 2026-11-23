using System.Net;
using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Runtimes.Strata;
using PcControlCenter.GpuMemory;

namespace PcControlCenter.Basalt.Tests;

/// <summary>The Basalt page's API reader: values in Strata's field names, "미지원" when no address has them, never guessed.</summary>
public class BasaltApiReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Health = """{"service":"basalt","loaded":true,"max_context":65536,"model":"qwen3-coder","api_key":true,"images":false}""";

    private const string Status = """
        {"service":"basalt","engine":"0.4.1","uptime_s":3725,"started":1793530800.0,"cache_max_tokens":131072,
         "concurrency":{"serving":2,"requested":2},"vision":{"enabled":true},"auto_load":true,"loaded":true,"model":"Qwen3-Coder-30B"}
        """;

    private const string Props = """
        {"default_generation_settings":{"n_ctx":131072,"params":{"temperature":0.7}},"total_slots":2,"model_alias":"qwen3",
         "model_path":"C:\\Users\\someone\\models\\Qwen3-Coder-30B-A3B-Q4_K_M.gguf","modalities":{"vision":true},"build_info":"b5000-abc123"}
        """;

    private const string Models = """{"object":"list","data":[{"id":"q-alias","alias_of":"Qwen3-Coder-30B"},{"id":"Qwen3-Coder-30B"}]}""";

    private static async Task<BasaltApiReport> Read(MockHttp http, TimeSpan? timeout = null)
    {
        using var reader = new BasaltApiReader(http, timeout);
        return await reader.ReadAsync(8080, Ct);
    }

    [Fact]
    public async Task All_four_addresses_give_every_value_in_strata_field_names()
    {
        var http = new MockHttp().Json("/health", Health).Json("/v1/status", Status).Json("/props", Props).Json("/v1/models", Models);

        var report = await Read(http);

        Assert.Equal("basalt", report.Field(BasaltApiReader.Service).Text);
        Assert.Equal("0.4.1", report.Field(BasaltApiReader.Engine).Text);
        Assert.Equal("Qwen3-Coder-30B", report.Field(BasaltApiReader.Model).Text); // /v1/status first
        Assert.Equal("Qwen3-Coder-30B-A3B-Q4_K_M.gguf", report.Field(BasaltApiReader.ModelFile).Text); // file name only: no user name
        Assert.Equal("예", report.Field(BasaltApiReader.Loaded).Text);
        Assert.Equal("131,072 토큰", report.Field(BasaltApiReader.Context).Text);
        Assert.Equal("2", report.Field(BasaltApiReader.Slots).Text);
        Assert.Equal("예", report.Field(BasaltApiReader.Vision).Text);
        Assert.Equal("1시간 2분", report.Field(BasaltApiReader.Uptime).Text);
        Assert.Equal("있음", report.Field(BasaltApiReader.KeySet).Text);
        Assert.All(report.Fields, f => Assert.Equal(BasaltFieldState.Reported, f.State));
        Assert.DoesNotContain(report.Fields, f => f.Text.Contains("someone", StringComparison.Ordinal));
        Assert.Equal(["/health", "/v1/status", "/props", "/v1/models"], report.Endpoints.Select(e => e.Path));
        Assert.All(report.Endpoints, e => Assert.Equal(200, e.Status));
    }

    [Fact]
    public async Task Values_no_address_reports_are_unsupported_and_others_fall_back_in_order()
    {
        // Only /health exists (as with a server that dropped the other addresses).
        var http = new MockHttp().Json("/health", """{"service":"basalt","loaded":false,"max_context":32768}""");

        var report = await Read(http);

        Assert.Equal("basalt", report.Field(BasaltApiReader.Service).Text);
        Assert.Equal("아니요", report.Field(BasaltApiReader.Loaded).Text);
        Assert.Equal("32,768 토큰", report.Field(BasaltApiReader.Context).Text); // from /health when /v1/status has none
        foreach (var label in new[] { BasaltApiReader.Engine, BasaltApiReader.Model, BasaltApiReader.ModelFile, BasaltApiReader.Slots,
                     BasaltApiReader.Vision, BasaltApiReader.Uptime, BasaltApiReader.KeySet })
        {
            Assert.Equal(BasaltFieldState.Unsupported, report.Field(label).State);
            Assert.Equal("미지원", report.Field(label).Text);
        }

        Assert.Equal(404, report.Endpoints.Single(e => e.Path == "/props").Status);
    }

    [Fact]
    public async Task Fields_of_the_wrong_type_are_not_read_as_values()
    {
        var http = new MockHttp().Json("/health", """{"service":42,"loaded":"yes","max_context":"big","api_key":1}""").Json("/v1/status", """{"uptime_s":-5}""");

        var report = await Read(http);

        Assert.Equal("미지원", report.Field(BasaltApiReader.Service).Text);
        Assert.Equal("미지원", report.Field(BasaltApiReader.Loaded).Text);
        Assert.Equal("미지원", report.Field(BasaltApiReader.Context).Text);
        Assert.Equal("미지원", report.Field(BasaltApiReader.KeySet).Text);
        Assert.Equal("미지원", report.Field(BasaltApiReader.Uptime).Text);
    }

    [Fact]
    public async Task A_key_protected_address_says_so_and_no_key_is_ever_sent()
    {
        var http = new MockHttp().Json("/health", """{"service":"basalt"}""")
            .Status("/v1/status", HttpStatusCode.Unauthorized).Status("/props", HttpStatusCode.Unauthorized).Status("/v1/models", HttpStatusCode.Forbidden);

        var report = await Read(http);

        Assert.Equal("basalt", report.Field(BasaltApiReader.Service).Text);
        Assert.Equal(BasaltFieldState.AuthRequired, report.Field(BasaltApiReader.Engine).State);
        Assert.Equal("인증 필요 (키를 보내지 않음)", report.Field(BasaltApiReader.Uptime).Text);
        Assert.All(http.Requests, r => Assert.False(r.HadAuthorization));
    }

    [Fact]
    public async Task Malformed_answers_are_format_errors_not_values()
    {
        var http = new MockHttp().Text("/health", "<html>not json</html>").Json("/v1/status", "[1,2,3]");

        var report = await Read(http);

        Assert.Equal(BasaltFieldState.Invalid, report.Field(BasaltApiReader.Service).State);
        Assert.Equal("형식 오류", report.Field(BasaltApiReader.Service).Text);
        Assert.False(report.Endpoints.Single(e => e.Path == "/health").Json);
    }

    [Fact]
    public async Task A_hung_server_times_out_and_every_value_is_no_answer()
    {
        var http = new MockHttp { Delay = TimeSpan.FromSeconds(30) }.Json("/health", Health);

        var started = DateTime.UtcNow;
        var report = await Read(http, TimeSpan.FromMilliseconds(300));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10)); // the four addresses are asked at once
        Assert.All(report.Fields, f => Assert.Equal("응답 없음", f.Text));
        Assert.All(report.Endpoints, e => Assert.Equal("시간 초과", e.Error));
    }

    [Fact]
    public async Task Nothing_listening_is_no_answer()
    {
        var report = await Read(new MockHttp { Refuse = true });

        Assert.All(report.Fields, f => Assert.Equal(BasaltFieldState.NoAnswer, f.State));
        Assert.All(report.Endpoints, e => Assert.Equal("연결 거부", e.Error));
    }
}

/// <summary>Basalt's /health as the controller reads it, against the in-memory handler.</summary>
public class BasaltHealthClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Status_service_and_loaded_are_read_and_a_busy_server_is_not_up()
    {
        using var ok = new BasaltHealthClient(handler: new MockHttp().Json("/health", """{"service":"basalt","loaded":false,"api_key":"x"}"""));
        Assert.Equal(new BasaltHealth(200, "basalt", false, null), await ok.GetAsync(8080, Ct));

        using var loading = new BasaltHealthClient(handler: new MockHttp().Status("/health", HttpStatusCode.ServiceUnavailable));
        var busy = await loading.GetAsync(8080, Ct);
        Assert.Equal(503, busy.StatusCode);
        Assert.False(busy.IsUp);

        using var auth = new BasaltHealthClient(handler: new MockHttp().Status("/health", HttpStatusCode.Unauthorized));
        Assert.True((await auth.GetAsync(8080, Ct)).IsUp); // something answers: the server is up, it wants a key
    }

    [Fact]
    public async Task Timeout_and_refusal_are_no_answer_with_the_reason()
    {
        using var hung = new BasaltHealthClient(TimeSpan.FromMilliseconds(200), new MockHttp { Delay = TimeSpan.FromSeconds(30) });
        var timedOut = await hung.GetAsync(8080, Ct);
        Assert.Null(timedOut.StatusCode);
        Assert.Equal("시간 초과", timedOut.Error);

        using var off = new BasaltHealthClient(handler: new MockHttp { Refuse = true });
        var refused = await off.GetAsync(8080, Ct);
        Assert.False(refused.IsUp);
        Assert.Equal("연결 거부", refused.Error);
    }
}

/// <summary>
/// The local AI module's own Strata adapter (not changed) given Basalt-like answers: what happens if Basalt kept Strata's
/// telemetry, changed its schema, or answers only /health. Decides whether a Basalt adapter is needed once the real answers
/// are known (basalt-probe.ps1).
/// </summary>
public class StrataAdapterWithBasaltTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string TelemetryV1 = """
        {"schema_version":1,"source":"server-v1","sampled_at":1793534400.25,
         "instance":{"id":"4f1c2a9b8e7d6c5b4a3928170615aabb","boot_time":1793530800,"server":"basalt","state":"ready"},
         "model":{"id":"Qwen3-Coder-30B","loaded":true,"context":131072},
         "work":{"slots_requested":2,"slots_effective":2,"active":1,"queued":0,"waiting":0,"slots":[]},
         "rates":{"answer_tok_s":41.5,"answer_window_s":3.0,"prefill_tok_s":1500.0},
         "recent":[],"totals":{}}
        """;

    private static StrataAdapter Adapter(MockHttp http, TimeSpan? timeout = null)
        => new(new StrataAdapterOptions("http://127.0.0.1:8080", null, timeout ?? TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30)), handler: http);

    [Fact]
    public async Task Basalt_keeping_strata_telemetry_v1_connects_without_an_adapter_even_named_basalt()
    {
        var http = new MockHttp().Json("/v1/telemetry", TelemetryV1).Json("/health", """{"service":"basalt","loaded":true}""");
        using var adapter = Adapter(http);

        var snapshot = await adapter.PollAsync(Ct);

        Assert.Equal(RuntimeConnectionState.Connected, snapshot.State);
        Assert.Equal("Qwen3-Coder-30B", snapshot.Model?.Id);
        Assert.Equal(131072, snapshot.Model?.ContextTokens);
        Assert.Equal("Strata", adapter.DisplayName); // the module's screens keep saying Strata (not changed, as agreed)
        Assert.All(http.Requests, r => Assert.False(r.HadAuthorization)); // no key configured: none sent
    }

    [Fact]
    public async Task A_changed_telemetry_schema_is_reported_as_incompatible_not_misread()
    {
        var http = new MockHttp().Json("/v1/telemetry", TelemetryV1.Replace("\"schema_version\":1", "\"schema_version\":2", StringComparison.Ordinal));
        using var adapter = Adapter(http);

        var snapshot = await adapter.PollAsync(Ct);

        Assert.Equal(RuntimeConnectionState.Incompatible, snapshot.State);
        Assert.Contains("스키마 버전 2", snapshot.Error?.Message, StringComparison.Ordinal);
        Assert.Null(snapshot.Model);
    }

    [Fact]
    public async Task Only_health_named_basalt_is_incompatible_and_says_it_is_not_strata()
    {
        var http = new MockHttp().Json("/health", """{"service":"basalt","loaded":true}""");
        using var adapter = Adapter(http);

        var snapshot = await adapter.PollAsync(Ct);

        Assert.Equal(RuntimeConnectionState.Incompatible, snapshot.State);
        Assert.Contains("service: basalt", snapshot.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_answer_and_a_hung_server_are_unreachable_and_timeout()
    {
        using (var off = Adapter(new MockHttp { Refuse = true }))
        {
            Assert.Equal(RuntimeConnectionState.Unreachable, (await off.PollAsync(Ct)).State);
        }

        using var hung = Adapter(new MockHttp { Delay = TimeSpan.FromSeconds(30) }, TimeSpan.FromMilliseconds(300));
        var snapshot = await hung.PollAsync(Ct);
        Assert.Equal(RuntimeConnectionState.Timeout, snapshot.State);
    }
}

/// <summary>Which executable names count as Basalt (the server control and the GPU memory page use the same rule).</summary>
public class BasaltNameTests
{
    [Theory]
    [InlineData("basalt.exe", true)]
    [InlineData("Basalt.EXE", true)]
    [InlineData("basalt-engine.exe", true)]
    [InlineData("basalt", true)]
    [InlineData("python.exe", false)]
    [InlineData("strata.exe", false)]
    [InlineData("my-basalt.exe", false)]
    [InlineData("", false)]
    public void Basalt_names_start_with_basalt_in_any_case(string name, bool expected)
    {
        Assert.Equal(expected, BasaltProcessTree.IsBasaltName(name));
        Assert.Equal(expected, ProcessKinds.IsBasaltName(name));
    }

    [Theory]
    [InlineData("basalt.exe", ProcessKind.Llm)]
    [InlineData("basalt-engine.exe", ProcessKind.Llm)]
    [InlineData("strata.exe", ProcessKind.Llm)]
    [InlineData("python.exe", ProcessKind.App)]
    [InlineData("pythonw.exe", ProcessKind.App)]
    public void Without_a_port_link_only_the_name_decides(string name, ProcessKind kind)
        => Assert.Equal(kind, ProcessKinds.Classify(1234, name, null, null, false).Kind);

    [Fact]
    public void Basalts_python_server_is_an_llm_runtime_only_through_the_port_link()
    {
        var linked = ProcessKinds.Classify(1234, "python.exe", null, null, true, "Basalt");
        Assert.Equal(ProcessKind.Llm, linked.Kind);
        Assert.Equal("Basalt 서버 포트와 프로세스 트리", linked.Basis);
    }
}
