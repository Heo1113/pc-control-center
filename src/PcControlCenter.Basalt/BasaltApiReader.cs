using System.Globalization;
using System.Net;
using System.Text.Json;

namespace PcControlCenter.Basalt;

/// <summary>How a value on the Basalt page was obtained.</summary>
public enum BasaltFieldState
{
    /// <summary>Basalt reported it.</summary>
    Reported,

    /// <summary>Every address that could carry it answered without it (or does not exist): "미지원". Never guessed.</summary>
    Unsupported,

    /// <summary>An address that could carry it asked for a key; this app sends none.</summary>
    AuthRequired,

    /// <summary>The address answered with something that is not the expected JSON.</summary>
    Invalid,

    /// <summary>Nothing answered (refused, timed out).</summary>
    NoAnswer,
}

public sealed record BasaltField(string Label, BasaltFieldState State, string? Value, string? Source)
{
    public string Text => State switch
    {
        BasaltFieldState.Reported => Value ?? "",
        BasaltFieldState.Unsupported => "미지원",
        BasaltFieldState.AuthRequired => "인증 필요 (키를 보내지 않음)",
        BasaltFieldState.Invalid => "형식 오류",
        _ => "응답 없음",
    };
}

/// <summary>What one address answered: an HTTP status, or why nothing came.</summary>
public sealed record BasaltEndpointResult(string Path, int? Status, string? Error, bool Json);

public sealed record BasaltApiReport(DateTimeOffset ReadAt, IReadOnlyList<BasaltField> Fields, IReadOnlyList<BasaltEndpointResult> Endpoints)
{
    public BasaltField Field(string label) => Fields.Single(f => f.Label == label);
}

/// <summary>
/// Reads Basalt through the read-only addresses Strata documents and Basalt (a Strata fork) is expected to keep:
/// <c>/health</c>, <c>/v1/status</c>, <c>/props</c>, <c>/v1/models</c>, on 127.0.0.1 only, without any key. Each value is
/// taken from the first address that reports it, in Strata's field names (the same ones the local AI module reads); a value
/// no address reports is "미지원". Which addresses Basalt really has is checked on the user's PC (basalt-probe.ps1).
/// The model path is reduced to its file name (a full path can contain the Windows user name).
/// </summary>
public sealed class BasaltApiReader : IDisposable
{
    public const string Service = "서비스";
    public const string Engine = "엔진 버전";
    public const string Model = "모델";
    public const string ModelFile = "모델 파일";
    public const string Loaded = "모델 올림";
    public const string Context = "컨텍스트";
    public const string Slots = "동시 처리 슬롯";
    public const string Vision = "이미지 입력";
    public const string Uptime = "실행 시간";
    public const string KeySet = "API 키 설정";

    private static readonly string[] Paths = ["/health", "/v1/status", "/props", "/v1/models"];
    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public BasaltApiReader(HttpMessageHandler? handler = null, TimeSpan? timeout = null, TimeProvider? time = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(1) })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(2),
        };
        _time = time ?? TimeProvider.System;
    }

    public async Task<BasaltApiReport> ReadAsync(int port, CancellationToken cancellationToken)
    {
        var answers = await Task.WhenAll(Paths.Select(p => GetAsync(port, p, cancellationToken))).ConfigureAwait(false);
        try
        {
            var byPath = Paths.Zip(answers).ToDictionary(x => x.First, x => x.Second);
            var health = byPath["/health"];
            var status = byPath["/v1/status"];
            var props = byPath["/props"];
            var models = byPath["/v1/models"];
            var fields = new List<BasaltField>
            {
                Pick(Service, [(health, r => Str(r, "service", 40)), (status, r => Str(r, "service", 40))]),
                Pick(Engine, [(status, r => Str(r, "engine", 64)), (props, r => Str(r, "build_info", 64))]),
                Pick(Model, [(status, r => Str(r, "model", 120)), (health, r => Str(r, "model", 120)), (models, FirstModel), (props, r => Str(r, "model_alias", 120))]),
                Pick(ModelFile, [(props, r => Str(r, "model_path", 1024) is { Length: > 0 } path ? Clip(path.Replace('\\', '/').Split('/')[^1], 120) : null)]),
                Pick(Loaded, [(health, r => YesNo(Bool(r, "loaded"))), (status, r => YesNo(Bool(r, "loaded")))]),
                Pick(Context, [(status, r => Tokens(Long(r, "cache_max_tokens"))), (health, r => Tokens(Long(r, "max_context"))),
                    (props, r => Tokens(Long(Obj(r, "default_generation_settings"), "n_ctx")))]),
                Pick(Slots, [(status, r => Long(Obj(r, "concurrency"), "serving") is long n ? n.ToString(CultureInfo.InvariantCulture) : null),
                    (props, r => Long(r, "total_slots") is long t ? t.ToString(CultureInfo.InvariantCulture) : null)]),
                Pick(Vision, [(status, r => YesNo(Bool(Obj(r, "vision"), "enabled"))), (health, r => YesNo(Bool(r, "images"))), (props, r => YesNo(Bool(Obj(r, "modalities"), "vision")))]),
                Pick(Uptime, [(status, r => Long(r, "uptime_s") is long s and >= 0 ? Duration(s) : null)]),
                Pick(KeySet, [(health, r => Bool(r, "api_key") is bool b ? (b ? "있음" : "없음") : null)]),
            };
            return new BasaltApiReport(_time.GetUtcNow(), fields,
                [.. Paths.Zip(answers).Select(x => new BasaltEndpointResult(x.First, x.Second.Status, x.Second.Error, x.Second.Root is not null))]);
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Document?.Dispose();
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record Answer(int? Status, string? Error, JsonDocument? Document, bool BadJson)
    {
        public JsonElement? Root => Document is { RootElement.ValueKind: JsonValueKind.Object } d ? d.RootElement : null;
    }

    private async Task<Answer> GetAsync(int port, string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri($"http://{IPAddress.Loopback}:{port}{path}"), ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return new Answer(code, null, null, false);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 1 << 20)
            {
                return new Answer(code, "응답이 너무 큼", null, true);
            }

            try
            {
                var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    document.Dispose();
                    return new Answer(code, "JSON 객체가 아님", null, true);
                }

                return new Answer(code, null, document, false);
            }
            catch (JsonException)
            {
                return new Answer(code, "JSON이 아님", null, true);
            }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Answer(null, "시간 초과", null, false);
        }
        catch (HttpRequestException ex)
        {
            return new Answer(null, ex.HttpRequestError == HttpRequestError.ConnectionError ? "연결 거부" : "요청 실패", null, false);
        }
    }

    /// <summary>The first source that reports the value; otherwise why there is none, without guessing.</summary>
    private static BasaltField Pick(string label, (Answer Answer, Func<JsonElement, string?> Read)[] sources)
    {
        foreach (var (answer, read) in sources)
        {
            if (answer.Root is { } root && read(root) is { Length: > 0 } value)
            {
                return new BasaltField(label, BasaltFieldState.Reported, value, null);
            }
        }

        if (sources.Any(s => s.Answer.Status is 401 or 403))
        {
            return new BasaltField(label, BasaltFieldState.AuthRequired, null, null);
        }

        if (sources.Any(s => s.Answer.BadJson))
        {
            return new BasaltField(label, BasaltFieldState.Invalid, null, null);
        }

        // Answered (with the value missing, or "no such address") = not supported; nothing answered at all = no answer.
        return sources.Any(s => s.Answer.Status is not null)
            ? new BasaltField(label, BasaltFieldState.Unsupported, null, null)
            : new BasaltField(label, BasaltFieldState.NoAnswer, null, null);
    }

    private static string? FirstModel(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (Str(item, "id", 120) is { } id && Str(item, "alias_of", 120) is null)
            {
                return id;
            }
        }

        return null;
    }

    private static JsonElement Obj(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static string? Str(JsonElement root, string name, int max)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Clip(value.GetString(), max)
            : null;

    private static bool? Bool(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static long? Long(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
            ? n
            : null;

    private static string? Clip(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

    private static string? YesNo(bool? value) => value switch { true => "예", false => "아니요", _ => null };

    private static string? Tokens(long? value) => value is long n and > 0 ? n.ToString("N0", CultureInfo.InvariantCulture) + " 토큰" : null;

    private static string Duration(long seconds)
        => seconds >= 3600 ? $"{seconds / 3600}시간 {seconds % 3600 / 60}분" : seconds >= 60 ? $"{seconds / 60}분 {seconds % 60}초" : $"{seconds}초";
}
