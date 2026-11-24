using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PcControlCenter.Basalt.Tests;

/// <summary>An HTTP handler in memory: answers by path, can wait (for timeouts) or fail (refused). Records each request.</summary>
internal sealed class MockHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<(string Path, bool HadAuthorization)> Requests { get; } = [];

    /// <summary>Every request waits this long first (a hung server).</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Every request fails as a refused connection (nothing listens).</summary>
    public bool Refuse { get; set; }

    public MockHttp Json(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[path] = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        return this;
    }

    public MockHttp Status(string path, HttpStatusCode status)
    {
        _routes[path] = () => new HttpResponseMessage(status) { Content = new StringContent("", Encoding.UTF8, "text/plain") };
        return this;
    }

    public MockHttp Text(string path, string body)
    {
        _routes[path] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization is not null));
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Refuse)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused));
        }

        return _routes.TryGetValue(request.RequestUri!.AbsolutePath, out var route)
            ? route()
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
    }
}
