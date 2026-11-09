using LocalAiPerf.Core.Runtime;
using LocalAiPerf.MockStrata;
using LocalAiPerf.Runtimes.Strata;
using Xunit;

namespace LocalAiPerf.Tests.Runtimes;

public class StrataAdapterTests
{
    internal static StrataAdapter Adapter(string baseUrl, string? key = null, int timeoutMs = 2000, TimeProvider? time = null)
        => new(new StrataAdapterOptions(baseUrl, key, TimeSpan.FromMilliseconds(timeoutMs), TimeSpan.FromSeconds(30), "STRATA_TELEMETRY_KEY"), time);

    internal static async Task<RuntimeSnapshot> PollUntil(StrataAdapter adapter, Func<RuntimeSnapshot, bool> condition, int attempts = 40)
    {
        RuntimeSnapshot snapshot = await adapter.PollAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < attempts && !condition(snapshot); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            snapshot = await adapter.PollAsync(TestContext.Current.CancellationToken);
        }

        return snapshot;
    }

    [Fact]
    public async Task V1_server_is_connected_and_metadata_fills_in()
    {
        await using var server = new MockStrataServer();
        server.Start();
        using var adapter = Adapter(server.BaseUrl);

        var snapshot = await PollUntil(adapter, s => s.Server?.EngineVersion is not null && s.Settings.Count > 0);

        Assert.Equal(RuntimeConnectionState.Connected, snapshot.State);
        Assert.Null(snapshot.Error);
        Assert.Equal("server-v1", snapshot.Server!.TelemetrySource);
        Assert.Equal(1, snapshot.Server.SchemaVersion);
        Assert.Equal("0.1.55-mock", snapshot.Server.EngineVersion);
        Assert.Equal(server.ModelId, snapshot.Model!.Id);
        Assert.Equal(32768, snapshot.Model.ContextTokens);
        Assert.Equal("Qwen3.8-Flash-Next-UD-Q4_K_XL.gguf", snapshot.Model.ModelFileName);
        Assert.Contains("strata", snapshot.Model.Aliases);
        Assert.NotNull(snapshot.Rates!.AnswerTokPerSec);
        Assert.Contains(snapshot.Settings, s => s.Key == "temperature" && s.Value == "0.6");
    }

    [Fact]
    public async Task Aggregate_output_rate_is_derived_from_two_counter_readings_only()
    {
        await using var server = new MockStrataServer();
        server.Start();
        using var adapter = Adapter(server.BaseUrl);

        var first = await adapter.PollAsync(TestContext.Current.CancellationToken);
        Assert.Null(first.Rates!.AggregateOutputTokPerSec);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var second = await adapter.PollAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(second.Rates!.AggregateOutputTokPerSec);
        Assert.True(second.Rates.AggregateOutputTokPerSec >= 0);
    }

    [Fact]
    public async Task Legacy_server_falls_back_to_metrics_after_404()
    {
        await using var server = new MockStrataServer(mode: MockMode.Legacy);
        server.Start();
        using var adapter = Adapter(server.BaseUrl);

        var snapshot = await adapter.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeConnectionState.Connected, snapshot.State);
        Assert.Equal("legacy-adapter", snapshot.Server!.TelemetrySource);
        Assert.Equal("0.1.55-mock", snapshot.Server.EngineVersion);
        Assert.NotNull(snapshot.Rates!.AnswerTokPerSec);
    }

    [Fact]
    public async Task Status_only_server_is_limited_and_has_no_rates()
    {
        await using var server = new MockStrataServer(mode: MockMode.StatusOnly);
        server.Start();
        using var adapter = Adapter(server.BaseUrl);

        var snapshot = await adapter.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeConnectionState.Limited, snapshot.State);
        Assert.Equal(server.ModelId, snapshot.Model!.Id);
        Assert.Null(snapshot.Rates!.AnswerTokPerSec);
    }

    [Theory]
    [InlineData(MockMode.FutureSchema, "스키마")]
    [InlineData(MockMode.NotJson, "JSON")]
    [InlineData(MockMode.MalformedJson, "JSON")]
    public async Task Format_changes_are_reported_as_api_incompatibility_not_connection_errors(MockMode mode, string expected)
    {
        await using var server = new MockStrataServer(mode: mode);
        server.Start();
        using var adapter = Adapter(server.BaseUrl);

        var snapshot = await adapter.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeConnectionState.Incompatible, snapshot.State);
        Assert.Equal(RuntimeErrorCategory.Compatibility, snapshot.Error!.Category);
        Assert.Contains(expected, snapshot.Error.Message);
    }

    [Fact]
    public async Task Server_error_is_its_own_category()
    {
        await using var server = new MockStrataServer(mode: MockMode.ServerError);
        server.Start();
        using var adapter = Adapter(server.BaseUrl);

        var snapshot = await adapter.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeConnectionState.ServerError, snapshot.State);
        Assert.Equal(500, snapshot.Error!.HttpStatus);
    }

    [Fact]
    public async Task Wrong_service_is_identified()
    {
        await using var server = new MockStrataServer(mode: MockMode.WrongService);
        server.Start();
        using var adapter = Adapter(server.BaseUrl);

        var snapshot = await adapter.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeConnectionState.Incompatible, snapshot.State);
        Assert.Contains("Strata가 아닌", snapshot.Error!.Message);
    }

    [Fact]
    public async Task Missing_key_is_an_authentication_error_and_the_right_key_connects()
    {
        await using var server = new MockStrataServer(mode: MockMode.AuthRequired) { RequiredKey = "tele-key-123" };
        server.Start();

        using (var noKey = Adapter(server.BaseUrl))
        {
            var snapshot = await noKey.PollAsync(TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeConnectionState.AuthRequired, snapshot.State);
            Assert.Equal(RuntimeErrorCategory.Authentication, snapshot.Error!.Category);
            Assert.Contains("STRATA_TELEMETRY_KEY", snapshot.Error.Hint);
        }

        using var withKey = Adapter(server.BaseUrl, key: "tele-key-123");
        var ok = await withKey.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeConnectionState.Connected, ok.State);
        Assert.DoesNotContain("tele-key-123", ok.ToString());
    }

    [Fact]
    public async Task Slow_server_times_out()
    {
        await using var server = new MockStrataServer(mode: MockMode.Slow) { Delay = TimeSpan.FromSeconds(3) };
        server.Start();
        using var adapter = Adapter(server.BaseUrl, timeoutMs: 300);

        var snapshot = await adapter.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeConnectionState.Timeout, snapshot.State);
        Assert.Equal(RuntimeErrorCategory.Timeout, snapshot.Error!.Category);
    }

    [Fact]
    public async Task Not_running_is_a_connection_error_with_backoff()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        int port;
        await using (var probe = new MockStrataServer())
        {
            port = probe.Port; // free port, nothing listening once disposed
        }

        using var adapter = Adapter($"http://127.0.0.1:{port}", time: time);
        var first = await adapter.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeConnectionState.Unreachable, first.State);
        Assert.Equal(RuntimeErrorCategory.Connection, first.Error!.Category);

        time.Advance(TimeSpan.FromSeconds(1.5));
        var second = await adapter.PollAsync(TestContext.Current.CancellationToken); // attempt 2 -> back-off 2 s
        Assert.NotNull(second.NextRetryAt);
        var cached = await adapter.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(second.NextRetryAt, cached.NextRetryAt);
        Assert.Equal(RuntimeConnectionState.Unreachable, cached.State);
    }

    [Fact]
    public async Task Adapter_only_sends_get_requests_to_documented_endpoints_and_never_status()
    {
        await using var server = new MockStrataServer();
        server.Start();
        using var adapter = Adapter(server.BaseUrl);

        await PollUntil(adapter, s => s.Settings.Count > 0);

        Assert.NotEmpty(server.Requests);
        Assert.All(server.Requests, r => Assert.Equal("GET", r.Method));
        Assert.All(server.Requests, r => Assert.Contains(r.Path, StrataHttpClient.AllowedPaths));
        Assert.DoesNotContain(server.Requests, r => r.Path == "/status");
    }

    [Fact]
    public async Task Health_is_requested_without_credentials()
    {
        await using var server = new MockStrataServer { RequiredKey = "k" };
        server.Start();
        using var adapter = Adapter(server.BaseUrl, key: "k");

        await PollUntil(adapter, s => s.Server?.Service is not null && server.Requests.Any(r => r.Path == "/health"));

        Assert.All(server.Requests.Where(r => r.Path == "/health"), r => Assert.False(r.HadAuthorization));
        Assert.All(server.Requests.Where(r => r.Path == "/v1/telemetry"), r => Assert.True(r.HadAuthorization));
    }

    [Fact]
    public void Remote_plain_http_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Adapter("http://192.168.0.10:8080"));
    }
}

public class StrataConnectTests
{
    [Fact]
    public async Task A_connect_that_never_completes_is_a_connection_error_not_a_server_timeout()
    {
        // Windows retries a refused loopback connect for about two seconds; simulate a connect that hangs.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () => await StrataHttpClient.ConnectAsync(
            new System.Net.DnsEndPoint("127.0.0.1", 9), TimeSpan.FromMilliseconds(100),
            (_, _, ct) => new ValueTask(Task.Delay(Timeout.Infinite, ct)), TestContext.Current.CancellationToken));

        Assert.Equal(HttpRequestError.ConnectionError, ex.HttpRequestError);
        Assert.Equal(System.Net.Sockets.SocketError.TimedOut, Assert.IsType<System.Net.Sockets.SocketException>(ex.InnerException).SocketErrorCode);
    }

    [Fact]
    public void Connect_deadline_fires_before_the_request_deadline()
        => Assert.True(StrataHttpClient.ConnectDeadline(TimeSpan.FromSeconds(2)) < TimeSpan.FromSeconds(2));

    [Fact]
    public async Task Port_search_follows_the_engine_to_a_new_port_without_changing_the_setting()
    {
        await using var other = new MockStrataServer(mode: MockMode.WrongService); // something else on an engine port
        other.Start();
        await using var engine = new MockStrataServer(mode: MockMode.AuthRequired) { RequiredKey = "tele-key" };
        engine.Start();
        using var adapter = new StrataAdapter(new StrataAdapterOptions("http://127.0.0.1:9", "tele-key", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30),
            "STRATA_TELEMETRY_KEY", AutoFindPort: true, ScanPorts: [other.Port, engine.Port]));

        var snapshot = await adapter.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeConnectionState.Connected, snapshot.State);
        Assert.Equal(engine.BaseUrl, adapter.EndpointDisplay);
        Assert.Equal("http://127.0.0.1:9", adapter.ConfiguredEndpoint);
        Assert.Equal(engine.BaseUrl, snapshot.Endpoint);
        // The search itself only asked /health, and never sent the key to the other program.
        Assert.All(other.Requests, r => Assert.Equal(("GET", "/health", false), (r.Method, r.Path, r.HadAuthorization)));
    }

    [Fact]
    public async Task Port_search_prefers_the_engine_that_accepts_the_key_and_is_off_when_disabled()
    {
        await using var foreign = new MockStrataServer(mode: MockMode.AuthRequired) { RequiredKey = "someone-elses-key" };
        foreign.Start();
        await using var mine = new MockStrataServer(mode: MockMode.AuthRequired) { RequiredKey = "tele-key" };
        mine.Start();
        var ports = new[] { foreign.Port, mine.Port }.Order().ToArray();

        using (var adapter = new StrataAdapter(new StrataAdapterOptions("http://127.0.0.1:9", "tele-key", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30),
                   "STRATA_TELEMETRY_KEY", AutoFindPort: true, ScanPorts: ports)))
        {
            var snapshot = await adapter.PollAsync(TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeConnectionState.Connected, snapshot.State);
            Assert.Equal(mine.BaseUrl, adapter.EndpointDisplay);
        }

        var before = mine.Requests.Count;
        using var off = new StrataAdapter(new StrataAdapterOptions("http://127.0.0.1:9", "tele-key", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30),
            "STRATA_TELEMETRY_KEY", AutoFindPort: false, ScanPorts: ports));
        var unreachable = await off.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeConnectionState.Unreachable, unreachable.State);
        Assert.Null(off.ConfiguredEndpoint);
        Assert.Equal(before, mine.Requests.Count);
    }

    [Fact]
    public async Task Wrong_key_hint_explains_the_desktop_app_setup()
    {
        await using var server = new MockStrataServer(mode: MockMode.AuthRequired) { RequiredKey = "tele-key" };
        server.Start();
        using var adapter = new StrataAdapter(new StrataAdapterOptions(server.BaseUrl, "wrong", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), "MY_KEY_VAR"));
        var snapshot = await adapter.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeConnectionState.AuthRequired, snapshot.State);
        Assert.Contains("STRATA_TELEMETRY_KEY", snapshot.Error!.Hint); // the variable Strata itself reads
        Assert.DoesNotContain("{key-variable}", snapshot.Error.Hint);
        Assert.DoesNotContain("wrong", snapshot.Error.Hint);

        using var noKey = new StrataAdapter(new StrataAdapterOptions(server.BaseUrl, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), "MY_KEY_VAR"));
        var missing = await noKey.PollAsync(TestContext.Current.CancellationToken);
        Assert.Contains("MY_KEY_VAR", missing.Error!.Hint); // the variable this app reads
    }

    [Fact]
    public async Task Telemetry_only_key_connects_marks_metadata_as_not_provided_and_stops_asking_for_it()
    {
        await using var server = new MockStrataServer(mode: MockMode.AuthRequired) { RequiredKey = "inference-key", TelemetryKey = "tele-only" };
        server.Start();
        using var adapter = StrataAdapterTests.Adapter(server.BaseUrl, key: "tele-only");

        var snapshot = await StrataAdapterTests.PollUntil(adapter, s => s.Server?.MetadataRestricted == true);

        Assert.Equal(RuntimeConnectionState.Connected, snapshot.State);
        Assert.True(snapshot.Server!.MetadataRestricted);
        Assert.Null(snapshot.Server.EngineVersion);
        Assert.Equal(server.ModelId, snapshot.Model?.Id);
        Assert.NotNull(snapshot.Model?.ContextTokens); // from telemetry and /health, which the key can read
        Assert.DoesNotContain(server.Requests, r => r.Path is "/props" or "/v1/models"); // refused once at /v1/status: not asked again
    }

    [Fact]
    public void Last_completed_request_is_the_newest_successful_one()
    {
        var snapshot = new RuntimeSnapshot
        {
            RuntimeKind = "strata",
            Timestamp = DateTimeOffset.UnixEpoch,
            State = RuntimeConnectionState.Connected,
            RecentRequests =
            [
                new CompletedRequest { Id = "a", StartedUnix = 100, OutputTokens = 100, DecodeMs = 1000 },
                new CompletedRequest { Id = "b", StartedUnix = 300, Finish = "error" },
                new CompletedRequest { Id = "c", StartedUnix = 200, OutputTokens = 300, DecodeMs = 2000 },
            ],
        };

        Assert.Equal("c", snapshot.LastCompletedRequest?.Id);
        Assert.Equal(150, snapshot.LastCompletedRequest?.DecodeTokPerSec);
    }
}

