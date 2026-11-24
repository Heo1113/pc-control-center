using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using LocalAiPerf.Core;
using LocalAiPerf.Core.Runtime;

namespace LocalAiPerf.Runtimes.Strata;

internal sealed record StrataResponse(string Path, int? HttpStatus, JsonDocument? Json, RuntimeError? Error) : IDisposable
{
    public bool IsSuccess => Error is null && Json is not null;
    public bool IsNotFound => HttpStatus == 404;

    public void Dispose() => Json?.Dispose();
}

/// <summary>
/// Read-only HTTP access to a Strata server. Only GET requests to an allow-list of documented endpoints are sent.
/// Redirects are refused, responses are capped at 2 MiB and must be JSON (the same limits as Strata's own monitor client).
/// </summary>
public sealed class StrataHttpClient : IDisposable
{
    public const int MaxBodyBytes = 2 * 1024 * 1024;

    /// <summary>Replaced by the adapter with the name of the environment variable this app reads the key from.</summary>
    public const string KeyVariablePlaceholder = "{key-variable}";

    /// <summary>
    /// Documented read endpoints this app may call. /status is deliberately absent: it can contain the end of the last answer.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedPaths = new HashSet<string>(StringComparer.Ordinal)
    {
        StrataEndpoints.Telemetry, StrataEndpoints.Metrics, StrataEndpoints.V1Status, StrataEndpoints.Health,
        StrataEndpoints.Props, StrataEndpoints.Models,
    };

    private readonly HttpClient _http;
    private readonly string? _credential;
    private readonly TimeSpan _timeout;

    public StrataHttpClient(Uri baseUri, string? credential, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        BaseUri = baseUri;
        _credential = string.IsNullOrWhiteSpace(credential) ? null : credential.Trim();
        _timeout = timeout;
        var ownsHandler = handler is null;
        if (handler is null)
        {
            handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                // The TCP connect gets its own, shorter deadline. Windows retries a refused connect for about two
                // seconds, so without this a stopped Strata would look like a server that accepted and then hung.
                ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, ConnectDeadline(timeout), ct),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                // A local server must not be reached through a system proxy.
                UseProxy = !Core.Settings.EndpointValidator.IsLoopback(baseUri),
                AutomaticDecompression = DecompressionMethods.None,
            };
        }

        _http = new HttpClient(handler, disposeHandler: ownsHandler) { Timeout = Timeout.InfiniteTimeSpan, BaseAddress = baseUri };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LocalAiPerformanceCenter", AppInfo.Version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Uri BaseUri { get; }

    /// <summary>Connect deadline: 80 % of the request timeout so it always fires before the request deadline.</summary>
    internal static TimeSpan ConnectDeadline(TimeSpan requestTimeout) => TimeSpan.FromTicks(requestTimeout.Ticks * 4 / 5);

    internal static ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, TimeSpan deadline, CancellationToken cancellationToken)
        => ConnectAsync(endPoint, deadline, (socket, ep, ct) => socket.ConnectAsync(ep, ct), cancellationToken);

    /// <param name="connect">The socket connect step (replaceable in tests to simulate a connect that never completes).</param>
    internal static async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, TimeSpan deadline,
        Func<Socket, DnsEndPoint, CancellationToken, ValueTask> connect, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(deadline);
        try
        {
            await connect(socket, endPoint, connectCts.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new HttpRequestException(HttpRequestError.ConnectionError,
                $"TCP connection to {endPoint.Host}:{endPoint.Port} was not established within {deadline.TotalSeconds:0.#} s.",
                new SocketException((int)SocketError.TimedOut));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
    public bool HasCredential => _credential is not null;

    internal async Task<StrataResponse> GetAsync(string path, bool sendCredential, CancellationToken cancellationToken)
    {
        if (!AllowedPaths.Contains(path))
        {
            throw new InvalidOperationException($"'{path}' is not an allowed read-only endpoint.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (sendCredential && _credential is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credential);
        }

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                return Fail(path, status, RuntimeErrorCategory.Configuration,
                    $"서버가 다른 주소로 리디렉션했습니다 (HTTP {status}). 리디렉션은 따르지 않습니다.",
                    "설정의 Strata 주소를 최종 서버 주소로 바꾸세요.");
            }

            if (status is 401 or 403)
            {
                return Fail(path, status, RuntimeErrorCategory.Authentication,
                    _credential is null ? "Strata 서버가 인증을 요구합니다." : "Strata 서버가 API 키를 거부했습니다.",
                    (_credential is null
                        ? "읽기 전용 텔레메트리 키를 환경 변수 {key-variable}에 넣고 이 앱을 다시 시작하세요. "
                        : "키가 이 엔진의 텔레메트리 키와 다릅니다. ")
                    + "Strata 데스크톱 앱: 같은 값을 사용자 환경 변수 STRATA_TELEMETRY_KEY로 설정한 뒤 Strata 앱을 완전히 종료했다가 다시 시작하고 모델을 로드하세요 "
                    + "(엔진이 시작할 때 이 변수를 텔레메트리 키로 받습니다. Serving API 키와 같은 값은 안 됩니다). "
                    + "'strata launch'로 실행한 엔진은 같은 변수를 쓰거나 'strata launch --show-key'로 키를 확인할 수 있습니다.");
            }

            if (status == 404)
            {
                return new StrataResponse(path, status, null, new RuntimeError(RuntimeErrorCategory.Compatibility,
                    $"엔드포인트 {path}가 없습니다 (HTTP 404).", null, path, status));
            }

            if (status >= 500)
            {
                return Fail(path, status, RuntimeErrorCategory.Server, $"Strata 서버 오류 (HTTP {status}).",
                    status == 503 ? "엔진이 아직 시작 중이거나 모델이 로드되지 않았을 수 있습니다." : null);
            }

            if (status is < 200 or >= 300)
            {
                return Fail(path, status, RuntimeErrorCategory.Compatibility,
                    $"예상하지 못한 응답 (HTTP {status}). Strata 업데이트로 API가 바뀌었을 수 있습니다.", null);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(path, status, RuntimeErrorCategory.Compatibility,
                    $"JSON이 아닌 응답입니다 ({mediaType ?? "Content-Type 없음"}). 이 포트에서 다른 프로그램이 응답하고 있을 수 있습니다.", null);
            }

            if (response.Content.Headers.ContentLength is long declared && declared > MaxBodyBytes)
            {
                return Fail(path, status, RuntimeErrorCategory.Compatibility, "응답이 2 MiB 제한을 넘습니다.", null);
            }

            var body = await ReadBoundedAsync(response.Content, timeoutCts.Token).ConfigureAwait(false);
            if (body is null)
            {
                return Fail(path, status, RuntimeErrorCategory.Compatibility, "응답이 2 MiB 제한을 넘습니다.", null);
            }

            try
            {
                var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
                return new StrataResponse(path, status, json, null);
            }
            catch (JsonException)
            {
                return Fail(path, status, RuntimeErrorCategory.Compatibility, "형식이 잘못된 JSON 응답입니다.", null);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(path, null, RuntimeErrorCategory.Timeout,
                $"Strata 서버가 {_timeout.TotalSeconds:0.#}초 안에 응답하지 않았습니다.", "서버가 바쁘거나 멈췄을 수 있습니다.");
        }
        catch (HttpRequestException ex)
        {
            return Classify(path, ex);
        }
    }

    public void Dispose() => _http.Dispose();

    private static StrataResponse Classify(string path, HttpRequestException ex)
    {
        var socket = FindSocketError(ex);
        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => Fail(path, null, RuntimeErrorCategory.Connection,
                "서버 이름을 찾을 수 없습니다.", "설정의 Strata 주소를 확인하세요."),
            HttpRequestError.SecureConnectionError => Fail(path, null, RuntimeErrorCategory.Configuration,
                "TLS(https) 연결에 실패했습니다.", "인증서 또는 https 사용 여부를 확인하세요."),
            HttpRequestError.InvalidResponse => Fail(path, null, RuntimeErrorCategory.Compatibility,
                "HTTP 응답 형식이 아닙니다. 이 포트에서 다른 프로그램이 응답하고 있을 수 있습니다.", null),
            _ when socket is SocketError.ConnectionRefused => Fail(path, null, RuntimeErrorCategory.Connection,
                "Strata 서버에 연결할 수 없습니다 (연결 거부).", "Strata가 실행 중인지, 주소와 포트가 맞는지 확인하세요 (strata launch 기본 8080, 데스크톱 앱 엔진 18500~18520은 포트 자동 찾기로 연결)."),
            _ when socket is SocketError.TimedOut => Fail(path, null, RuntimeErrorCategory.Connection,
                "Strata 서버에 연결할 수 없습니다 (연결이 수립되지 않음).", "Strata가 실행 중인지, 주소와 포트가 맞는지 확인하세요 (strata launch 기본 8080, 데스크톱 앱 엔진 18500~18520은 포트 자동 찾기로 연결)."),
            _ => Fail(path, null, RuntimeErrorCategory.Connection,
                $"Strata 서버와 통신하지 못했습니다 ({ex.HttpRequestError}).", "Strata가 실행 중인지 확인하세요."),
        };
    }

    private static SocketError? FindSocketError(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is SocketException socketException)
            {
                return socketException.SocketErrorCode;
            }

            ex = ex.InnerException;
        }

        return null;
    }

    private static StrataResponse Fail(string path, int? status, RuntimeErrorCategory category, string message, string? hint)
        => new(path, status, null, new RuntimeError(category, message, hint, path, status));

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }
}

public static class StrataEndpoints
{
    public const string Telemetry = "/v1/telemetry";
    public const string Metrics = "/metrics";
    public const string V1Status = "/v1/status";
    public const string Health = "/health";
    public const string Props = "/props";
    public const string Models = "/v1/models";
}
