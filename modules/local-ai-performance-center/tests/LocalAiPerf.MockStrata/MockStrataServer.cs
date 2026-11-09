using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LocalAiPerf.MockStrata;

/// <summary>Which server behaviour to imitate. Shapes follow Strata's documented endpoints (docs/TERMINAL_TELEMETRY_FIELDS.md).</summary>
public enum MockMode
{
    /// <summary>Current server: /v1/telemetry schema v1.</summary>
    V1,

    /// <summary>Older server: no /v1/telemetry (404), /metrics + /v1/status.</summary>
    Legacy,

    /// <summary>Very old server: only /v1/status.</summary>
    StatusOnly,

    /// <summary>/v1/telemetry answers with an unknown schema version (simulates an incompatible update).</summary>
    FutureSchema,

    /// <summary>Every endpoint except /health requires a key.</summary>
    AuthRequired,

    /// <summary>Answers HTML instead of JSON.</summary>
    NotJson,

    /// <summary>Answers malformed JSON.</summary>
    MalformedJson,

    /// <summary>HTTP 500 on telemetry.</summary>
    ServerError,

    /// <summary>Delays every response by <see cref="MockStrataServer.Delay"/>.</summary>
    Slow,

    /// <summary>A different HTTP service: every API path is 404, /health says another service.</summary>
    WrongService,
}

public sealed record MockRequest(string Method, string Path, bool HadAuthorization);

/// <summary>
/// In-process imitation of a Strata server for tests and for trying the GUI without Strata.
/// It is a test tool: it is not part of the release package. All values it serves are synthetic.
/// </summary>
public sealed class MockStrataServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<MockRequest> _requests = new();
    private readonly object _gate = new();
    private readonly DateTimeOffset _bootTime = DateTimeOffset.UtcNow;
    private readonly List<Dictionary<string, object?>> _recent = [];
    private readonly List<Dictionary<string, object?>> _events = [];
    private Task? _loop;
    private long _outputCounter;
    private long _requestSeq;
    private long _eventSeq;
    private DateTimeOffset _lastAdvance = DateTimeOffset.UtcNow;
    private double _phase;

    public MockStrataServer(int port = 0, MockMode mode = MockMode.V1)
    {
        Port = port == 0 ? FreePort() : port;
        Mode = mode;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public MockMode Mode { get; set; }
    public string? RequiredKey { get; set; }

    /// <summary>A read-only telemetry key (Strata STRATA_TELEMETRY_KEY): accepted on /v1/telemetry only.</summary>
    public string? TelemetryKey { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(5);
    public string ModelId { get; set; } = "qwen3.8-flash-next-mock";
    public string EngineVersion { get; set; } = "0.1.55-mock";
    public string InstanceId { get; set; } = "0123456789abcdef0123456789abcdef";
    public long Context { get; set; } = 32768;
    public double BaseAnswerTokPerSec { get; set; } = 42.0;

    /// <summary>When true, the mock behaves as if a request were being generated (rates are non-null).</summary>
    public bool Generating { get; set; } = true;

    public IReadOnlyList<MockRequest> Requests => [.. _requests];

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
            }
        }

        _cts.Dispose();
    }

    /// <summary>Completes one synthetic request (adds to recent[] and the activity ring).</summary>
    public void CompleteRequest(long promptTokens = 1200, long outputTokens = 256, double decodeTokPerSec = 40, string finish = "stop")
    {
        lock (_gate)
        {
            _requestSeq++;
            var id = _requestSeq.ToString("x12");
            var decodeMs = outputTokens / decodeTokPerSec * 1000.0;
            var promptMs = promptTokens / 900.0 * 1000.0;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            _recent.Insert(0, new Dictionary<string, object?>
            {
                ["id"] = id, ["time"] = now - (decodeMs + promptMs) / 1000.0, ["duration_s"] = Math.Round((decodeMs + promptMs) / 1000.0, 1),
                ["finish"] = finish, ["prompt_tokens"] = promptTokens, ["reused"] = 0, ["output_tokens"] = outputTokens,
                ["prompt_ms"] = Math.Round(promptMs, 1), ["decode_ms"] = Math.Round(decodeMs, 1), ["first_engine_token_s"] = Math.Round(promptMs / 1000.0 + 0.05, 3),
            });
            if (_recent.Count > 50)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }

            // Like Strata's counter (completed + in-flight tokens), finishing a request does not add tokens again.
            AddEvent(id, "finished", finish == "error" ? "error" : "stop");
        }
    }

    private void AddEvent(string requestId, string kind, string? outcome)
    {
        _eventSeq++;
        _events.Add(new Dictionary<string, object?>
        {
            ["seq"] = _eventSeq, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            ["request_id"] = requestId, ["event"] = kind, ["outcome"] = outcome,
        });
        if (_events.Count > 128)
        {
            _events.RemoveAt(0);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "";
        var auth = request.Headers["Authorization"];
        _requests.Enqueue(new MockRequest(request.HttpMethod, path, !string.IsNullOrEmpty(auth)));
        try
        {
            if (Mode == MockMode.Slow)
            {
                await Task.Delay(Delay, ct).ConfigureAwait(false);
            }

            var (status, body, contentType) = Route(request.HttpMethod, path, auth);
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException)
        {
            try
            {
                context.Response.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private (int Status, string Body, string ContentType) Route(string method, string path, string? auth)
    {
        const string json = "application/json";
        if (method != "GET")
        {
            return (405, Json(new { error = new { message = "mock is read-only" } }), json);
        }

        if (Mode == MockMode.WrongService)
        {
            return path == "/health" ? (200, Json(new { status = "ok", service = "other-llm-server" }), json) : (404, Json(new { error = "not found" }), json);
        }

        var needsKey = Mode == MockMode.AuthRequired || RequiredKey is not null;
        var key = RequiredKey ?? "secret-key";
        var telemetryKeyOk = TelemetryKey is not null && path == "/v1/telemetry" && auth == $"Bearer {TelemetryKey}";
        if (needsKey && path != "/health" && auth != $"Bearer {key}" && !telemetryKeyOk)
        {
            return (401, Json(new { error = new { type = "authentication_error", message = "missing or wrong telemetry key" } }), json);
        }

        Advance();
        return path switch
        {
            "/health" => (200, Json(new
            {
                status = "ok", max_context = Context, model = ModelId, images = false, api_key = needsKey,
                image_generation = false, loaded = true, service = "strata",
            }), json),
            "/v1/telemetry" => Mode switch
            {
                MockMode.Legacy or MockMode.StatusOnly => (404, Json(new { error = new { message = "not found" } }), json),
                MockMode.FutureSchema => (200, Json(new { schema_version = 2, source = "server-v2" }), json),
                MockMode.NotJson => (200, "<html><body>not json</body></html>", "text/html"),
                MockMode.MalformedJson => (200, "{\"schema_version\": 1, \"model\": {", json),
                MockMode.ServerError => (500, Json(new { error = new { message = "engine crashed" } }), json),
                _ => (200, Json(Telemetry()), json),
            },
            "/metrics" => Mode switch
            {
                MockMode.StatusOnly => (404, Json(new { error = new { message = "not found" } }), json),
                _ => (200, Json(LegacyMetrics()), json),
            },
            "/v1/status" => (200, Json(Status()), json),
            "/props" => (200, Json(Props()), json),
            "/v1/models" => (200, Json(new
            {
                @object = "list",
                data = new object[]
                {
                    new { id = ModelId, @object = "model", status = new { value = "loaded" }, meta = new { n_ctx = Context } },
                    new { id = "strata", @object = "model", alias_of = ModelId },
                },
            }), json),
            "/status" => (200, Json(new { busy = false, text = "THE END OF THE LAST ANSWER (must never be requested)" }), json),
            _ => (404, Json(new { error = new { message = "not found" } }), json),
        };
    }

    private void Advance()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var dt = Math.Max(0, (now - _lastAdvance).TotalSeconds);
            _lastAdvance = now;
            _phase += dt;
            if (Generating)
            {
                _outputCounter += (long)Math.Round(CurrentTokPerSec() * dt);
            }
        }
    }

    private double CurrentTokPerSec() => Math.Max(1, BaseAnswerTokPerSec + 6 * Math.Sin(_phase / 7.0));

    private Dictionary<string, object?> Telemetry()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var held = Generating ? 4096 + (long)(_phase * 30 % 20000) : 2048;
            return new Dictionary<string, object?>
            {
                ["schema_version"] = 1,
                ["source"] = "server-v1",
                ["sampled_at"] = now,
                ["instance"] = new Dictionary<string, object?> { ["id"] = InstanceId, ["boot_time"] = _bootTime.ToUnixTimeSeconds(), ["server"] = "strata", ["state"] = "ready" },
                ["model"] = new Dictionary<string, object?> { ["id"] = ModelId, ["loaded"] = true, ["context"] = Context },
                ["work"] = new Dictionary<string, object?>
                {
                    ["slots_requested"] = 1, ["slots_effective"] = 1, ["active"] = Generating ? 1 : 0, ["queued"] = 0, ["waiting"] = 0,
                    ["occupied_slots"] = Generating ? 1 : 0, ["max_queue"] = 16, ["admission_rejected"] = 0, ["queue_timeouts"] = 0,
                    ["client_disconnect_cancels"] = 0, ["oldest_queue_s"] = 0, ["queue_timeout_s"] = 600,
                    ["slots"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["slot"] = 0, ["state"] = Generating ? "decoding" : "idle", ["held_tokens"] = held, ["prompt_tokens"] = 4000,
                            ["prompt_read"] = 4000, ["generated"] = Generating ? (long)(_phase * 40 % 2000) : null, ["tok_s"] = Generating ? Math.Round(CurrentTokPerSec(), 1) : null,
                        },
                    },
                    ["requests"] = Array.Empty<object>(),
                },
                ["rates"] = new Dictionary<string, object?>
                {
                    ["answer_tok_s"] = Generating ? Math.Round(CurrentTokPerSec(), 1) : null,
                    ["answer_window_s"] = Generating ? 3.0 : null,
                    ["prefill_tok_s"] = Generating ? 910.5 : null,
                    ["output_tokens_server_counter"] = _outputCounter,
                },
                ["devices"] = new object[] { new Dictionary<string, object?> { ["index"] = 0, ["name"] = "Mock GPU", ["util"] = 50 } },
                ["ram"] = new Dictionary<string, object?> { ["used"] = 1L << 34, ["total"] = 1L << 36 },
                ["recent"] = _recent.ToArray(),
                ["totals"] = new Dictionary<string, object?> { ["since"] = _bootTime.ToUnixTimeSeconds(), ["requests"] = _requestSeq, ["prompt_tokens"] = _requestSeq * 1200, ["output_tokens"] = _outputCounter },
                ["activity"] = new Dictionary<string, object?> { ["last_seq"] = _eventSeq, ["events"] = _events.TakeLast(32).ToArray() },
                ["quality"] = new Dictionary<string, object?> { ["request_correlation"] = "reported" },
            };
        }
    }

    private Dictionary<string, object?> LegacyMetrics()
    {
        lock (_gate)
        {
            return new Dictionary<string, object?>
            {
                ["engine"] = new Dictionary<string, object?> { ["model"] = ModelId, ["max_context"] = Context, ["version"] = EngineVersion },
                ["live"] = new Dictionary<string, object?>
                {
                    ["state"] = Generating ? "generating" : "idle", ["queued"] = 0,
                    ["tok_s"] = Generating ? Math.Round(CurrentTokPerSec(), 1) : null, ["tok_s_window_s"] = Generating ? 3.0 : null,
                    ["prefill_tok_s_mean"] = Generating ? 905.0 : null,
                },
                ["requests"] = _recent.ToArray(),
                ["totals"] = new Dictionary<string, object?> { ["since"] = _bootTime.ToUnixTimeSeconds(), ["requests"] = _requestSeq, ["prompt_tokens"] = 0, ["output_tokens"] = _outputCounter },
                ["hardware"] = new Dictionary<string, object?> { ["gpu_util"] = 50 },
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            };
        }
    }

    private Dictionary<string, object?> Status() => new()
    {
        ["service"] = "strata", ["model"] = ModelId, ["loaded"] = true, ["auto_load"] = true, ["engine"] = EngineVersion,
        ["started"] = _bootTime.ToUnixTimeSeconds(), ["uptime_s"] = (long)(DateTimeOffset.UtcNow - _bootTime).TotalSeconds,
        ["cache_max_tokens"] = Context, ["context"] = new Dictionary<string, object?> { ["native"] = Context, ["max_positions"] = Context },
        ["concurrency"] = new Dictionary<string, object?> { ["serving"] = 1, ["requested"] = 1 },
        ["vision"] = new Dictionary<string, object?> { ["enabled"] = false, ["available"] = false, ["error"] = null },
        ["activity"] = new Dictionary<string, object?> { ["requests"] = _requestSeq, ["in_flight"] = Generating ? 1 : 0 },
        ["last_timings"] = null,
    };

    private Dictionary<string, object?> Props() => new()
    {
        ["default_generation_settings"] = new Dictionary<string, object?>
        {
            ["n_ctx"] = Context,
            ["params"] = new Dictionary<string, object?> { ["temperature"] = 0.6, ["top_p"] = 0.95, ["top_k"] = 20, ["min_p"] = 0.0, ["repeat_penalty"] = 1.0, ["n_predict"] = -1 },
        },
        ["total_slots"] = 1, ["model_alias"] = ModelId, ["chat_template"] = "{{ messages }}",
        ["modalities"] = new Dictionary<string, object?> { ["vision"] = false }, ["models_autoload"] = true, ["is_sleeping"] = false,
        ["model_path"] = @"C:\Users\someone\Strata\models\Qwen3.8-Flash-Next-UD-Q4_K_XL.gguf",
        ["build_info"] = "Strata " + EngineVersion,
    };

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }
}
