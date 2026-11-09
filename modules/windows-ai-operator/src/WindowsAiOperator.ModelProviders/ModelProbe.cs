using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Security;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.ModelProviders;

public sealed record ProbeModel(string Id, bool? AcceptsImages, string? Note);

public sealed record ProbeResult
{
    public bool Reachable { get; init; }
    public ModelErrorKind? Error { get; init; }
    public string Message { get; init; } = string.Empty;
    public string ServerKind { get; init; } = "알 수 없음";
    public EndpointScope Scope { get; init; }
    public IReadOnlyList<ProbeModel> Models { get; init; } = [];
    public bool? SelectedModelAcceptsImages { get; init; }
    public string VisionReason { get; init; } = "확인 안 됨";
    public IReadOnlyList<string> StructuredFormats { get; init; } = [];
    public string? ServerModel { get; init; }
    public bool? ModelLoaded { get; init; }
    public TimeSpan Elapsed { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Reads which models a server offers and what they accept, without sending any user data:
/// GET {base}/models, and for Strata/llama.cpp also /health, /v1/status and /props. Image support is read from the
/// model list (<c>architecture.input_modalities</c> etc., same fields Strata's own vision probe reads), Strata's
/// <c>vision.enabled</c> or llama.cpp's <c>modalities.vision</c>. Unknown stays unknown (vision then stays off).
/// </summary>
public static class ModelProbe
{
    private const int MaxProbeBytes = 2 * 1024 * 1024;

    public static async Task<ProbeResult> ProbeAsync(ModelProfile profile, string? apiKey, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var endpoint = EndpointPolicy.Check(profile);
        if (endpoint.Problem is not null || endpoint.BaseUri is null)
        {
            return new ProbeResult
            {
                Error = endpoint.IsRemote ? ModelErrorKind.RemoteNotAllowed : ModelErrorKind.Configuration,
                Message = endpoint.Problem ?? "주소 오류",
                Scope = endpoint.Scope,
                Elapsed = stopwatch.Elapsed,
            };
        }

        using var http = new HttpClient(handler ?? OpenAiCompatibleClient.CreateHandler(), disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(profile.TimeoutSeconds, 5, 20)),
        };

        var models = await GetJsonAsync(http, new Uri(endpoint.BaseUri, "models"), apiKey, cancellationToken).ConfigureAwait(false);
        if (!models.Ok)
        {
            return new ProbeResult
            {
                Error = models.Error,
                Message = models.Message,
                Scope = endpoint.Scope,
                Elapsed = stopwatch.Elapsed,
            };
        }

        var list = ParseModels(models.Root);
        var root = EndpointPolicy.Root(endpoint.BaseUri);
        var serverKind = "OpenAI 호환";
        bool? statusVision = null;
        string? serverModel = null;
        bool? loaded = null;
        var formats = new List<string>();

        var health = await GetJsonAsync(http, new Uri(root, "health"), null, cancellationToken).ConfigureAwait(false);
        if (health.Ok && health.Root.ValueKind == JsonValueKind.Object && health.Root.TryGetProperty("service", out var service) && service.ValueKind == JsonValueKind.String
            && string.Equals(service.GetString(), "strata", StringComparison.OrdinalIgnoreCase))
        {
            serverKind = "Strata";
        }

        if (serverKind == "Strata" || profile.Kind == ProviderKind.Strata)
        {
            var status = await GetJsonAsync(http, new Uri(root, "v1/status"), apiKey, cancellationToken).ConfigureAwait(false);
            if (status.Ok && status.Root.ValueKind == JsonValueKind.Object)
            {
                serverKind = "Strata";
                if (status.Root.TryGetProperty("vision", out var vision) && vision.ValueKind == JsonValueKind.Object
                    && vision.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    statusVision = enabled.GetBoolean();
                }

                if (status.Root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object
                    && structured.TryGetProperty("formats", out var formatList) && formatList.ValueKind == JsonValueKind.Array)
                {
                    formats.AddRange(formatList.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!));
                }

                if (status.Root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                {
                    serverModel = Redactor.OneLine(model.GetString(), 120);
                }

                if (status.Root.TryGetProperty("loaded", out var isLoaded) && isLoaded.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    loaded = isLoaded.GetBoolean();
                }
            }
        }

        if (serverKind != "Strata" && list.All(m => m.AcceptsImages is null))
        {
            var props = await GetJsonAsync(http, new Uri(root, "props"), apiKey, cancellationToken).ConfigureAwait(false);
            if (props.Ok && props.Root.ValueKind == JsonValueKind.Object && props.Root.TryGetProperty("modalities", out var modalities)
                && modalities.ValueKind == JsonValueKind.Object && modalities.TryGetProperty("vision", out var propsVision)
                && propsVision.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                serverKind = "llama.cpp 호환";
                statusVision = propsVision.GetBoolean();
            }
        }

        var selected = profile.Model is null ? list.FirstOrDefault() : list.FirstOrDefault(m => string.Equals(m.Id, profile.Model, StringComparison.Ordinal));
        bool? selectedVision = selected?.AcceptsImages;
        string reason;
        if (selectedVision is not null)
        {
            reason = selectedVision.Value ? "서버 모델 목록에 이미지 입력 지원 표시" : "서버 모델 목록상 텍스트 전용";
        }
        else if (statusVision is not null && (list.Count <= 1 || serverKind == "Strata"))
        {
            selectedVision = statusVision;
            reason = statusVision.Value ? $"{serverKind} 서버가 이미지 입력 사용 가능으로 보고" : $"{serverKind} 서버가 이미지 입력 없음으로 보고";
        }
        else
        {
            reason = "서버가 이미지 지원 여부를 알려 주지 않음(이미지 입력 실제 테스트로 확인 가능)";
        }

        var message = list.Count == 0
            ? "연결되었지만 사용할 수 있는 모델이 없습니다(모델이 로드되지 않았을 수 있음)."
            : profile.Model is not null && selected is null
                ? $"연결됨. 선택한 모델 '{profile.Model}'이(가) 목록에 없습니다. 모델을 다시 고르세요."
                : $"연결됨: 모델 {list.Count}개";

        return new ProbeResult
        {
            Reachable = true,
            Message = message,
            ServerKind = serverKind,
            Scope = endpoint.Scope,
            Models = list,
            SelectedModelAcceptsImages = selectedVision,
            VisionReason = reason,
            StructuredFormats = formats,
            ServerModel = serverModel,
            ModelLoaded = loaded,
            Elapsed = stopwatch.Elapsed,
        };
    }

    /// <summary>True/false/null from one row of a model list.</summary>
    public static bool? AcceptsImages(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var list in CandidateLists(row))
        {
            return list.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() is { } s && (s.Equals("image", StringComparison.OrdinalIgnoreCase) || s.Equals("vision", StringComparison.OrdinalIgnoreCase) || s.Equals("images", StringComparison.OrdinalIgnoreCase)));
        }

        if (row.TryGetProperty("modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Object
            && modalities.TryGetProperty("vision", out var vision) && vision.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return vision.GetBoolean();
        }

        foreach (var holder in new[] { row, row.TryGetProperty("meta", out var meta) ? meta : default })
        {
            if (holder.ValueKind == JsonValueKind.Object && holder.TryGetProperty("capabilities", out var caps))
            {
                if (caps.ValueKind == JsonValueKind.Array)
                {
                    return caps.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() is { } s
                        && (s.Contains("vision", StringComparison.OrdinalIgnoreCase) || s.Contains("image", StringComparison.OrdinalIgnoreCase) || s.Contains("multimodal", StringComparison.OrdinalIgnoreCase)));
                }

                if (caps.ValueKind == JsonValueKind.Object && caps.TryGetProperty("vision", out var capVision) && capVision.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return capVision.GetBoolean();
                }
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> CandidateLists(JsonElement row)
    {
        if (row.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object
            && arch.TryGetProperty("input_modalities", out var a) && a.ValueKind == JsonValueKind.Array)
        {
            yield return a;
        }

        if (row.TryGetProperty("input_modalities", out var b) && b.ValueKind == JsonValueKind.Array)
        {
            yield return b;
        }

        if (row.TryGetProperty("modalities", out var m))
        {
            if (m.ValueKind == JsonValueKind.Array)
            {
                yield return m;
            }
            else if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
            {
                yield return input;
            }
        }
    }

    private static List<ProbeModel> ParseModels(JsonElement root)
    {
        var models = new List<ProbeModel>();
        JsonElement data = default;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
        {
            data = d;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array)
        {
            data = m;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            data = root;
        }

        if (data.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (var row in data.EnumerateArray().Take(200))
        {
            string? id = null;
            if (row.ValueKind == JsonValueKind.Object)
            {
                id = (row.TryGetProperty("id", out var idElement) ? idElement : row.TryGetProperty("name", out var n) ? n : row.TryGetProperty("model", out var mm) ? mm : default) is { ValueKind: JsonValueKind.String } value
                    ? value.GetString()
                    : null;
            }
            else if (row.ValueKind == JsonValueKind.String)
            {
                id = row.GetString();
            }

            if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
            {
                continue;
            }

            var outputsImagesOnly = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object
                                    && arch.TryGetProperty("output_modalities", out var outputs) && outputs.ValueKind == JsonValueKind.Array
                                    && outputs.EnumerateArray().All(o => o.ValueKind == JsonValueKind.String && o.GetString() == "image");
            models.Add(new ProbeModel(Redactor.OneLine(id, 200), AcceptsImages(row), outputsImagesOnly ? "이미지 생성 모델(계획에 사용 불가)" : null));
        }

        return models;
    }

    private static async Task<(bool Ok, JsonElement Root, ModelErrorKind? Error, string Message)> GetJsonAsync(HttpClient http, Uri uri, string? apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var (text, tooLarge) = await OpenAiCompatibleClient.ReadLimitedAsync(response, cancellationToken, MaxProbeBytes).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (tooLarge)
            {
                return (false, default, ModelErrorKind.ResponseTooLarge, "응답이 너무 큽니다.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var kind = status switch
                {
                    401 or 403 => ModelErrorKind.Authentication,
                    404 => ModelErrorKind.NotFound,
                    >= 300 and < 400 => ModelErrorKind.Configuration,
                    >= 500 => ModelErrorKind.ServerError,
                    _ => ModelErrorKind.BadRequest,
                };
                var hint = kind switch
                {
                    ModelErrorKind.Authentication => " — API 키가 필요하거나 틀렸습니다.",
                    ModelErrorKind.NotFound => " — 주소 끝에 /v1이 맞는지 확인하세요.",
                    _ => string.Empty,
                };
                return (false, default, kind, $"HTTP {status}{hint} {OpenAiCompatibleClient.ErrorText(text)}");
            }

            using var document = JsonDocument.Parse(text);
            return (true, document.RootElement.Clone(), null, "ok");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, default, ModelErrorKind.Cancelled, "취소되었습니다.");
        }
        catch (TaskCanceledException)
        {
            return (false, default, ModelErrorKind.Timeout, "응답 시간 초과");
        }
        catch (HttpRequestException ex)
        {
            return (false, default, ModelErrorKind.Connection, $"연결 실패: {Redactor.OneLine(ex.Message, 160)} — 서버가 실행 중인지, 주소와 포트가 맞는지 확인하세요.");
        }
        catch (JsonException)
        {
            return (false, default, ModelErrorKind.InvalidResponse, "JSON이 아닌 응답입니다(OpenAI 호환 서버가 아닐 수 있음).");
        }
    }
}
