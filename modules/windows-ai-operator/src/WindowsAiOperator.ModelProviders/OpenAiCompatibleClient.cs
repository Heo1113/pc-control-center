using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Planning;
using WindowsAiOperator.Core.Security;
using WindowsAiOperator.Core.Settings;

namespace WindowsAiOperator.ModelProviders;

/// <summary>
/// POST {base}/chat/completions on any OpenAI-compatible server (Strata, llama.cpp, LM Studio, Ollama, vLLM...).
/// No redirects, a response size limit, errors classified instead of thrown. When a server rejects structured output
/// (400), the request is retried once with a simpler format and the simpler format is kept for this client.
/// </summary>
public sealed class OpenAiCompatibleClient : IModelClient, IDisposable
{
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    private readonly ModelProfile _profile;
    private readonly string? _apiKey;
    private readonly HttpClient _http;
    private readonly IOperatorLog _log;
    private readonly bool _allowImages;
    private ResponseFormatKind? _formatCeiling;

    public OpenAiCompatibleClient(ModelProfile profile, string? apiKey, ModelDescriptor descriptor, HttpMessageHandler? handler = null, IOperatorLog? log = null, bool allowImagesRegardless = false, ResponseFormatKind? knownFormatCeiling = null)
    {
        _formatCeiling = knownFormatCeiling;
        _profile = profile;
        _apiKey = apiKey;
        Descriptor = descriptor;
        _log = log ?? NullOperatorLog.Instance;
        _allowImages = allowImagesRegardless || descriptor.CanSendImages;
        _http = new HttpClient(handler ?? CreateHandler(), disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, profile.TimeoutSeconds)),
        };
    }

    public ModelDescriptor Descriptor { get; }

    /// <summary>The structured-output format the server accepted last (after any fallback).</summary>
    public ResponseFormatKind? EffectiveFormat => _formatCeiling;

    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var endpoint = EndpointPolicy.Check(_profile);
        if (endpoint.Problem is not null || endpoint.BaseUri is null)
        {
            return ModelResponse.Fail(endpoint.IsRemote ? ModelErrorKind.RemoteNotAllowed : ModelErrorKind.Configuration, endpoint.Problem ?? "주소 오류", stopwatch.Elapsed, calls: 0);
        }

        if (string.IsNullOrWhiteSpace(Descriptor.ModelId))
        {
            return ModelResponse.Fail(ModelErrorKind.Configuration, "모델이 선택되지 않았습니다. 모델 화면에서 연결을 확인하고 모델을 고르세요.", stopwatch.Elapsed, calls: 0);
        }

        var hasImages = request.Messages.Any(m => m.Images is { Count: > 0 });
        if (hasImages && !_allowImages)
        {
            return ModelResponse.Fail(ModelErrorKind.RemoteNotAllowed, "이 연결에는 이미지(스크린샷)를 보낼 수 없습니다.", stopwatch.Elapsed, calls: 0);
        }

        var format = request.ResponseFormat;
        if (_formatCeiling is { } ceiling && ceiling < format)
        {
            format = ceiling;
        }

        var calls = 0;
        while (true)
        {
            calls++;
            var body = BuildBody(request, format);
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUri, "chat/completions"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(_apiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ModelResponse.Fail(ModelErrorKind.Cancelled, "취소되었습니다.", stopwatch.Elapsed, calls: calls);
            }
            catch (TaskCanceledException)
            {
                return ModelResponse.Fail(ModelErrorKind.Timeout, $"{_http.Timeout.TotalSeconds:0}초 안에 응답이 없습니다(모델이 크거나 서버가 바쁠 수 있음).", stopwatch.Elapsed, calls: calls);
            }
            catch (HttpRequestException ex)
            {
                return ModelResponse.Fail(ModelErrorKind.Connection, $"서버에 연결하지 못했습니다: {Redactor.OneLine(ex.Message, 160)}", stopwatch.Elapsed, calls: calls);
            }

            using (response)
            {
                var (text, tooLarge) = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
                if (tooLarge)
                {
                    return ModelResponse.Fail(ModelErrorKind.ResponseTooLarge, "응답이 너무 큽니다.", stopwatch.Elapsed, (int)response.StatusCode, calls);
                }

                var status = (int)response.StatusCode;
                if (status == 400 && format != ResponseFormatKind.None && calls < 3)
                {
                    var downgraded = format == ResponseFormatKind.JsonSchema ? ResponseFormatKind.JsonObject : ResponseFormatKind.None;
                    _log.Warn("model", $"server rejected response_format {format} (400): {ErrorText(text)}; retrying with {downgraded}");
                    format = downgraded;
                    _formatCeiling = downgraded;
                    continue;
                }

                if (status is >= 300 and < 400)
                {
                    return ModelResponse.Fail(ModelErrorKind.Configuration, "서버가 다른 주소로 이동시키려 합니다(리디렉션은 따르지 않음). 주소를 확인하세요.", stopwatch.Elapsed, status, calls);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var kind = status switch
                    {
                        401 or 403 => ModelErrorKind.Authentication,
                        404 => ModelErrorKind.NotFound,
                        >= 500 => ModelErrorKind.ServerError,
                        _ => ModelErrorKind.BadRequest,
                    };
                    return ModelResponse.Fail(kind, $"HTTP {status}: {ErrorText(text)}", stopwatch.Elapsed, status, calls);
                }

                _formatCeiling ??= format;
                return ParseSuccess(text, stopwatch.Elapsed, status, calls, format);
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private JsonObject BuildBody(ModelRequest request, ResponseFormatKind format)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var role = message.Role switch
            {
                ChatRole.System => "system",
                ChatRole.Assistant => "assistant",
                _ => "user",
            };
            if (message.Images is { Count: > 0 } images)
            {
                var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = message.Text } };
                foreach (var image in images)
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(image.Png) },
                    });
                }

                messages.Add(new JsonObject { ["role"] = role, ["content"] = parts });
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = message.Text });
            }
        }

        var body = new JsonObject
        {
            ["model"] = Descriptor.ModelId,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = false,
        };
        switch (format)
        {
            case ResponseFormatKind.JsonSchema when request.JsonSchema is not null:
                body["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = request.SchemaName, ["schema"] = request.JsonSchema.DeepClone() },
                };
                break;
            case ResponseFormatKind.JsonObject:
            case ResponseFormatKind.JsonSchema:
                body["response_format"] = new JsonObject { ["type"] = "json_object" };
                break;
        }

        return body;
    }

    private static ModelResponse ParseSuccess(string text, TimeSpan elapsed, int status, int calls, ResponseFormatKind format)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return ModelResponse.Fail(ModelErrorKind.InvalidResponse, "응답에 choices가 없습니다(OpenAI 호환 서버가 아닐 수 있음).", elapsed, status, calls);
            }

            var choice = choices[0];
            string? finish = choice.TryGetProperty("finish_reason", out var finishElement) && finishElement.ValueKind == JsonValueKind.String ? finishElement.GetString() : null;
            if (!choice.TryGetProperty("message", out var messageElement) || messageElement.ValueKind != JsonValueKind.Object)
            {
                return ModelResponse.Fail(ModelErrorKind.InvalidResponse, "응답에 message가 없습니다.", elapsed, status, calls);
            }

            var content = ExtractContent(messageElement);
            if (string.IsNullOrWhiteSpace(content))
            {
                var reasoningOnly = messageElement.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String && reasoning.GetString()!.Length > 0;
                return ModelResponse.Fail(ModelErrorKind.InvalidResponse,
                    reasoningOnly ? "모델이 추론만 하고 답을 내지 않았습니다(최대 출력 토큰을 늘리세요)." : "모델 응답이 비어 있습니다.", elapsed, status, calls);
            }

            int? prompt = null, completion = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                prompt = usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv) ? pv : null;
                completion = usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var cv) ? cv : null;
            }

            return new ModelResponse
            {
                Success = true,
                Content = content,
                Elapsed = elapsed,
                StatusCode = status,
                PromptTokens = prompt,
                CompletionTokens = completion,
                FinishReason = finish,
                Calls = calls,
                UsedFormat = format,
            };
        }
        catch (JsonException)
        {
            return ModelResponse.Fail(ModelErrorKind.InvalidResponse, "응답이 JSON이 아닙니다.", elapsed, status, calls);
        }
    }

    private static string? ExtractContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    builder.Append(t.GetString());
                }
            }

            return builder.ToString();
        }

        return null;
    }

    internal static async Task<(string Text, bool TooLarge)> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken, int limit = MaxResponseBytes)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return (string.Empty, true);
            }

            buffer.Write(chunk, 0, read);
        }

        return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), false);
    }

    internal static string ErrorText(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(본문 없음)";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return Redactor.OneLine(error.GetString(), 300);
                }

                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    return Redactor.OneLine(m.GetString(), 300);
                }
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var detail))
            {
                return Redactor.OneLine(detail.ToString(), 300);
            }
        }
        catch (JsonException)
        {
        }

        return Redactor.OneLine(body, 300);
    }
}
