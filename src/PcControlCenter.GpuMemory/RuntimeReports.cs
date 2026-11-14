using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PcControlCenter.GpuMemory;

/// <summary>Which runtime a report comes from, and so which endpoint is read.</summary>
public enum RuntimeReportSource
{
    /// <summary>Strata's <c>GET /metrics</c>, its <c>engine</c> part only.</summary>
    StrataEngine,

    /// <summary>Ollama's <c>GET /api/ps</c>.</summary>
    Ollama,

    /// <summary>llama.cpp's server: <c>GET /props</c>, and <c>GET /models</c> for a router's loaded instances.</summary>
    LlamaServer,
}

/// <summary>
/// What Strata's engine said about itself, from the <c>engine</c> part of <c>/metrics</c>. The engine reports its facts in an
/// <c>INFO</c> line when it starts; a VRAM adjustment later answers in a <c>VRAM</c> line that the server keeps whole in
/// <c>engine.vram</c> and copies only <c>expert_slots</c> and <c>vram_free_mib</c> from. So <see cref="ExpertCacheMibAtStart"/>
/// stays the start value after an adjustment, and the adjusted cache size is in <see cref="LastAdjustment"/>.
/// None of these are the current VRAM use: that is the Windows measurement.
/// </summary>
public sealed record StrataEngineReport
{
    public string? KvType { get; init; }
    public long? Context { get; init; }
    public long? ExpertCacheMibAtStart { get; init; }
    public long? BatchSlots { get; init; }
    public string? EngineVersion { get; init; }

    /// <summary>Last reported, at the start or by the last adjustment.</summary>
    public long? ExpertSlots { get; init; }

    /// <summary>The engine's view of free VRAM when it last reported (start or adjustment).</summary>
    public long? VramFreeMib { get; init; }

    /// <summary>The expert cache on the main GPU at the start, when the engine also uses other GPUs or remote tiers.</summary>
    public long? ExpertCachePrimaryMibAtStart { get; init; }

    /// <summary>The RAM budget for parked conversations, set at the start (0: off). Host RAM, not VRAM.</summary>
    public long? ConversationCacheMib { get; init; }

    /// <summary>Experts kept on the CPU and in RAM (DeepSeek engine).</summary>
    public long? CpuExperts { get; init; }
    public long? RamExperts { get; init; }

    public StrataVramAdjustment? LastAdjustment { get; init; }

    /// <summary>The <c>engine</c> part of a <c>/metrics</c> answer; null when it has none.</summary>
    public static StrataEngineReport? Parse(JsonElement metrics)
    {
        if (metrics.ValueKind != JsonValueKind.Object || !metrics.TryGetProperty("engine", out var engine) || engine.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        StrataVramAdjustment? adjustment = null;
        if (engine.TryGetProperty("vram", out var vram) && vram.ValueKind == JsonValueKind.Object)
        {
            adjustment = new StrataVramAdjustment(Json.Long(vram, "reserve_mib"), Json.Long(vram, "expert_slots"), Json.Long(vram, "expert_slots_full"),
                Json.Long(vram, "expert_cache_mib"), Json.Long(vram, "expert_cache_full_mib"), Json.Long(vram, "vram_free_mib"));
        }

        return new StrataEngineReport
        {
            KvType = Json.Text(engine, "kv"),
            Context = Json.Long(engine, "context") ?? Json.Long(engine, "max_context"),
            ExpertCacheMibAtStart = Json.Long(engine, "expert_cache_mib"),
            BatchSlots = Json.Long(engine, "batch_slots"),
            EngineVersion = Json.Text(engine, "engine") ?? Json.Text(engine, "version"),
            ExpertSlots = Json.Long(engine, "expert_slots"),
            VramFreeMib = Json.Long(engine, "vram_free_mib"),
            ExpertCachePrimaryMibAtStart = Json.Long(engine, "expert_cache_primary_mib"),
            ConversationCacheMib = Json.Long(engine, "conversation_cache_mib"),
            CpuExperts = Json.Long(engine, "cpu_experts"),
            RamExperts = Json.Long(engine, "ram_experts"),
            LastAdjustment = adjustment,
        };
    }
}

/// <summary>What the engine answered to the last VRAM adjustment (at that moment, not now).</summary>
public sealed record StrataVramAdjustment(long? ReserveMib, long? ExpertSlots, long? ExpertSlotsFull, long? ExpertCacheMib, long? ExpertCacheFullMib,
    long? VramFreeMib);

/// <summary>
/// One model in Ollama's <c>/api/ps</c>. <see cref="Size"/> and <see cref="SizeVram"/> are Ollama's own figures: it reads them
/// from llama-server's load log and, without that log, uses the model file's size for both. They are not a measurement of
/// RAM: <c>size − size_vram</c> is not the RAM in use.
/// </summary>
public sealed record OllamaModelReport(string Name, long? Size, long? SizeVram, long? ContextLength, string? ParameterSize, string? Quantization);

public sealed record OllamaReport(IReadOnlyList<OllamaModelReport> Models)
{
    public static OllamaReport? Parse(JsonElement ps)
    {
        if (ps.ValueKind != JsonValueKind.Object || !ps.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<OllamaModelReport>();
        foreach (var m in models.EnumerateArray().Take(16))
        {
            if (m.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var details = m.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            list.Add(new OllamaModelReport(Json.Text(m, "name") ?? Json.Text(m, "model") ?? "?", Json.Long(m, "size"), Json.Long(m, "size_vram"),
                Json.Long(m, "context_length"), details.ValueKind == JsonValueKind.Object ? Json.Text(details, "parameter_size") : null,
                details.ValueKind == JsonValueKind.Object ? Json.Text(details, "quantization_level") : null));
        }

        return new OllamaReport(list);
    }
}

/// <summary>A llama.cpp server's settings from <c>/props</c>: its context (<c>n_ctx</c>), slots, model file (name only), build.</summary>
public sealed record LlamaServerReport(long? ContextTokens, long? TotalSlots, string? ModelFile, string? Build)
{
    public static LlamaServerReport? Parse(JsonElement props)
    {
        if (props.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var settings = props.TryGetProperty("default_generation_settings", out var g) && g.ValueKind == JsonValueKind.Object ? g : default;
        var context = settings.ValueKind == JsonValueKind.Object ? Json.Long(settings, "n_ctx") : null;
        var report = new LlamaServerReport(context, Json.Long(props, "total_slots"), LlamaRouterReport.FileName(Json.Text(props, "model_path")),
            Json.Text(props, "build_info"));
        return report is { ContextTokens: null, TotalSlots: null, ModelFile: null } ? null : report;
    }
}

/// <summary>Memory one loaded router instance reports it allocated on a device (<c>memory.effective</c>), in bytes.</summary>
public sealed record LlamaDeviceMemory(string Kind, string Name, long? Model, long? Context, long? Compute);

/// <summary>A loaded router instance: its id, the model file (name only), the context and the memory it reported per device.</summary>
public sealed record LlamaRouterModel(string Id, string? ModelFile, long? ContextTokens, IReadOnlyList<LlamaDeviceMemory> Devices);

/// <summary>
/// A llama.cpp router's loaded models with the memory each instance reported allocating (<c>memory.effective</c>). The
/// <c>min</c> and <c>max</c> figures are what a model would need at the smallest and largest context, not what it holds:
/// they are not read.
/// </summary>
public sealed record LlamaRouterReport(IReadOnlyList<LlamaRouterModel> Loaded)
{
    public static LlamaRouterReport? Parse(JsonElement models)
    {
        if (models.ValueKind != JsonValueKind.Object || !models.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var loaded = new List<LlamaRouterModel>();
        foreach (var m in data.EnumerateArray().Take(32))
        {
            if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("memory", out var memory) || memory.ValueKind != JsonValueKind.Object
                || !memory.TryGetProperty("effective", out var effective) || effective.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var devices = new List<LlamaDeviceMemory>();
            if (effective.TryGetProperty("devices", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var dev in list.EnumerateArray().Take(16))
                {
                    if (dev.ValueKind == JsonValueKind.Object)
                    {
                        devices.Add(new LlamaDeviceMemory(Json.Text(dev, "kind") ?? "device", Json.Text(dev, "name") ?? "?", Json.Long(dev, "model"),
                            Json.Long(dev, "context"), Json.Long(dev, "compute")));
                    }
                }
            }

            loaded.Add(new LlamaRouterModel(Json.Text(m, "id") ?? "?", FileName(Json.Text(m, "path")), Json.Long(effective, "n_ctx"), devices));
        }

        return loaded.Count == 0 ? null : new LlamaRouterReport(loaded);
    }

    /// <summary>The file name of a model path (a path in an answer is never shown or kept whole).</summary>
    internal static string? FileName(string? path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path.Replace('\\', '/'));
}

/// <summary>Local AI servers the page asks about themselves, recognised by their executable name.</summary>
public static class LocalAiServers
{
    public const int OllamaDefaultPort = 11434;

    public static bool IsServerName(string name) => SourceOf(name) is not null;

    /// <summary>Ollama (its server and runners are ollama*.exe) or llama.cpp's server (llama-server*.exe); null otherwise.</summary>
    public static RuntimeReportSource? SourceOf(string name)
        => name.StartsWith("ollama", StringComparison.OrdinalIgnoreCase) ? RuntimeReportSource.Ollama
            : name.StartsWith("llama-server", StringComparison.OrdinalIgnoreCase) ? RuntimeReportSource.LlamaServer
            : null;
}

/// <summary>A local server to ask, the process that listens for it, and (Strata) the environment variable holding its key.</summary>
public sealed record RuntimeTarget(RuntimeReportSource Source, int OwnerPid, int Port, string? CredentialVariable = null, bool Ipv6 = false)
{
    /// <summary>Always the loopback address: nothing is sent off this computer.</summary>
    public string BaseUrl => $"http://{(Ipv6 ? "[::1]" : "127.0.0.1")}:{Port.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>The newest answer of a target: when, the parsed reports, or why there is none.</summary>
public sealed record RuntimeReportResult(DateTimeOffset At, object? Report, object? Extra, string? Error);

public sealed record RuntimeHttpResponse(int Status, string? Body, string? Error);

/// <summary>Loopback GETs only (the poller builds every URL from 127.0.0.1 and a port).</summary>
public interface IRuntimeHttp
{
    Task<RuntimeHttpResponse> GetAsync(string url, string? bearer, CancellationToken ct);
}

/// <summary>The real client: no proxy, a short timeout, a bounded answer, never logs what it sends or receives.</summary>
public sealed class HttpRuntimeClient : IRuntimeHttp, IDisposable
{
    private const int MaxBytes = 4 << 20;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(1) })
    {
        Timeout = TimeSpan.FromSeconds(2),
        MaxResponseContentBufferSize = MaxBytes,
    };

    public async Task<RuntimeHttpResponse> GetAsync(string url, string? bearer, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(bearer))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new RuntimeHttpResponse((int)response.StatusCode, body, null);
        }
        catch (HttpRequestException ex)
        {
            return new RuntimeHttpResponse(0, null, ex.HttpRequestError == HttpRequestError.ConnectionError ? "연결 안 됨" : "요청 실패 (응답이 너무 크거나 끊김)");
        }
        catch (TaskCanceledException)
        {
            return new RuntimeHttpResponse(0, null, "응답 없음 (2초)");
        }
        catch (InvalidOperationException)
        {
            return new RuntimeHttpResponse(0, null, "요청 실패");
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Asks the local runtimes about themselves while the GPU memory page is open: each target at most every
/// <see cref="Interval"/>, one request at a time per target, on the thread pool (never the reader's or the UI's thread).
/// Read-only GETs to 127.0.0.1; a model path in an answer is reduced to its file name; a key is read from its environment
/// variable for the request and kept nowhere.
/// </summary>
public sealed class RuntimeReportPoller(IRuntimeHttp http, TimeProvider? time = null)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<RuntimeTarget, RuntimeReportResult> _results = new();
    private readonly ConcurrentDictionary<RuntimeTarget, DateTimeOffset> _asked = new();
    private readonly ConcurrentDictionary<RuntimeTarget, Task> _running = new();
    private int _version;

    /// <summary>The newest result per target asked for in the last <see cref="Update"/>.</summary>
    public IReadOnlyDictionary<RuntimeTarget, RuntimeReportResult> Results => _results;

    /// <summary>Changes whenever an answer arrives or a target is forgotten (the page rebuilds its detail then).</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Completes when the requests in flight have answered (tests wait on it).</summary>
    public Task WhenIdle() => Task.WhenAll(_running.Values.ToList());

    /// <summary>Asks the targets that are due; forgets targets no longer listed (the process ended or the page closed).</summary>
    public void Update(IReadOnlyCollection<RuntimeTarget> targets)
    {
        foreach (var gone in _results.Keys.Concat(_asked.Keys).Distinct().Where(t => !targets.Contains(t)).ToList())
        {
            if (_results.TryRemove(gone, out _))
            {
                Interlocked.Increment(ref _version);
            }

            _asked.TryRemove(gone, out _);
        }

        var now = _time.GetUtcNow();
        foreach (var target in targets)
        {
            if (_running.ContainsKey(target) || (_asked.TryGetValue(target, out var at) && now - at < Interval))
            {
                continue;
            }

            // Registered before it starts and unregistered before it completes, so a finished request never blocks the next one.
            _asked[target] = now;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _running[target] = done.Task;
            _ = Task.Run(async () =>
            {
                try
                {
                    await AskAsync(target).ConfigureAwait(false);
                }
                finally
                {
                    _running.TryRemove(KeyValuePair.Create(target, done.Task));
                    done.SetResult();
                }
            });
        }
    }

    private async Task AskAsync(RuntimeTarget target)
    {
        RuntimeReportResult result;
        try
        {
            result = target.Source switch
            {
                RuntimeReportSource.StrataEngine => await AskStrata(target).ConfigureAwait(false),
                RuntimeReportSource.Ollama => await AskOllama(target).ConfigureAwait(false),
                _ => await AskLlama(target).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            result = new RuntimeReportResult(_time.GetUtcNow(), null, null, "응답 형식을 읽지 못했습니다");
        }

        // An answer that arrives after its target was forgotten (the page closed, another process selected) is dropped.
        if (_asked.ContainsKey(target))
        {
            _results[target] = result;
            Interlocked.Increment(ref _version);
        }
    }

    private async Task<RuntimeReportResult> AskStrata(RuntimeTarget target)
    {
        var key = string.IsNullOrWhiteSpace(target.CredentialVariable) ? null : Environment.GetEnvironmentVariable(target.CredentialVariable);
        var response = await http.GetAsync(target.BaseUrl + "/metrics", key, CancellationToken.None).ConfigureAwait(false);
        if (response.Status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
        {
            return Failed("Strata에 API 키가 설정되어 있어 엔진 정보를 읽지 못했습니다. 로컬 AI 설정의 키 환경 변수에 API 키가 있으면 읽습니다 (텔레메트리 키로는 읽을 수 없음).");
        }

        return Parsed(response, StrataEngineReport.Parse, "엔진 정보가 없는 응답입니다");
    }

    private async Task<RuntimeReportResult> AskOllama(RuntimeTarget target)
        => Parsed(await http.GetAsync(target.BaseUrl + "/api/ps", null, CancellationToken.None).ConfigureAwait(false), OllamaReport.Parse,
            "Ollama의 /api/ps 응답이 아닙니다");

    private async Task<RuntimeReportResult> AskLlama(RuntimeTarget target)
    {
        var props = await http.GetAsync(target.BaseUrl + "/props", null, CancellationToken.None).ConfigureAwait(false);
        var models = await http.GetAsync(target.BaseUrl + "/models", null, CancellationToken.None).ConfigureAwait(false);
        var server = ParseBody(props, LlamaServerReport.Parse);
        var router = ParseBody(models, LlamaRouterReport.Parse);
        return server is not null || router is not null ? new RuntimeReportResult(_time.GetUtcNow(), server, router, null)
            : props.Status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden
                ? Failed("llama.cpp 서버에 API 키(--api-key)가 설정되어 있어 읽지 못했습니다.")
            : Failed(props.Error ?? models.Error ?? "llama.cpp 서버의 /props 응답이 아닙니다");
    }

    private RuntimeReportResult Parsed<T>(RuntimeHttpResponse response, Func<JsonElement, T?> parse, string notThis) where T : class
        => response.Error is { } error ? Failed(error)
            : response.Status != 200 ? Failed($"HTTP {response.Status.ToString(CultureInfo.InvariantCulture)}")
            : ParseBody(response, parse) is { } report ? new RuntimeReportResult(_time.GetUtcNow(), report, null, null)
            : Failed(notThis);

    private static T? ParseBody<T>(RuntimeHttpResponse response, Func<JsonElement, T?> parse) where T : class
    {
        if (response.Status != 200 || string.IsNullOrEmpty(response.Body))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(response.Body, new JsonDocumentOptions { MaxDepth = 32 });
        return parse(doc.RootElement);
    }

    private RuntimeReportResult Failed(string error) => new(_time.GetUtcNow(), null, null, error);
}

/// <summary>
/// MOCK answers for --mock-sensors: an Ollama server with one partly offloaded model, and Strata's engine part of
/// <c>/metrics</c> after one VRAM adjustment. Labelled MOCK in the model names.
/// </summary>
public sealed class MockRuntimeHttp : IRuntimeHttp
{
    public const int OllamaPort = LocalAiServers.OllamaDefaultPort;

    public Task<RuntimeHttpResponse> GetAsync(string url, string? bearer, CancellationToken ct)
        => Task.FromResult(
            url.EndsWith($":{OllamaPort}/api/ps", StringComparison.Ordinal) ? new RuntimeHttpResponse(200, """
                {"models":[{"name":"MOCK gemma3:4b","model":"MOCK gemma3:4b","size":3500000000,"size_vram":1600000000,"context_length":8192,
                  "details":{"parameter_size":"4.3B","quantization_level":"Q4_K_M"}}]}
                """, null)
            : url.EndsWith("/metrics", StringComparison.Ordinal) ? new RuntimeHttpResponse(200, """
                {"engine":{"model":"MOCK Qwen3 14B","max_context":32768,"images":false,"context":32768,"kv":"int8","kv_resident":0,
                  "expert_slots":1200,"expert_cache_mib":4900,"expert_slots_primary":3700,"expert_cache_primary_mib":4900,"vram_free_mib":6100,
                  "conversation_cache_mib":4096,"conversation_cache_slots":4,"vram_elastic":1,"engine":"MOCK 0.1.9","version":"MOCK 0.1.9",
                  "vram":{"reserve_mib":6000,"expert_slots":1200,"expert_slots_full":3700,"expert_cache_mib":1536,"expert_cache_full_mib":4900,
                    "vram_free_mib":6100,"prompt_chunk":2048}},"live":{}}
                """, null)
            : new RuntimeHttpResponse(404, null, null));
}

internal static class Json
{
    public static long? Long(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) switch
    {
        true when v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) => l,
        true when v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && Math.Abs(d) < 9e18 => (long)d,
        true when v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
        _ => null,
    };

    public static string? Text(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) switch
    {
        true when v.ValueKind == JsonValueKind.String => Short(v.GetString()),
        true when v.ValueKind == JsonValueKind.Number => v.GetRawText(),
        _ => null,
    };

    private static string? Short(string? s) => s is null ? null : s.Length <= 200 ? s : s[..200];
}
