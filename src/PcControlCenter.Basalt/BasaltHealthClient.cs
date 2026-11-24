using System.Net;
using System.Text.Json;

namespace PcControlCenter.Basalt;

/// <summary>
/// <c>GET http://127.0.0.1:{port}/health</c> without a proxy, with a short timeout. Only the status code and two fields
/// (<c>service</c>, <c>loaded</c>) are read; the body is not kept or logged. No credential is ever sent.
/// </summary>
public sealed class BasaltHealthClient : IDisposable
{
    private readonly HttpClient _http;

    public BasaltHealthClient(TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(1) })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(2),
        };
    }

    public async Task<BasaltHealth> GetAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri($"http://{IPAddress.Loopback}:{port}/health"), HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            var code = (int)response.StatusCode;
            string? service = null;
            bool? loaded = null;
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                && response.Content.Headers.ContentLength is null or < 65536)
            {
                try
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    using var json = JsonDocument.Parse(bytes);
                    if (json.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (json.RootElement.TryGetProperty("service", out var s) && s.ValueKind == JsonValueKind.String)
                        {
                            var text = s.GetString();
                            service = text is { Length: > 40 } ? text[..40] : text;
                        }

                        if (json.RootElement.TryGetProperty("loaded", out var l) && l.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            loaded = l.GetBoolean();
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }

            return new BasaltHealth(code, service, loaded, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BasaltHealth.NoAnswer("시간 초과");
        }
        catch (HttpRequestException ex)
        {
            return BasaltHealth.NoAnswer(ex.HttpRequestError == HttpRequestError.ConnectionError ? "연결 거부" : "요청 실패");
        }
    }

    public void Dispose() => _http.Dispose();
}
