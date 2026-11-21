using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PcControlCenter.Basalt.Mock;

/// <summary>A response of <see cref="LoopbackHttpServer"/>.</summary>
public sealed record LoopbackResponse(int Status, string ContentType, string Body);

/// <summary>
/// A minimal HTTP/1.1 server on 127.0.0.1 (a TCP socket of this process, so the TCP table names this process as the
/// listener). GET only, one request per connection. For the MOCK Basalt page and for tests; never used with real data.
/// </summary>
public sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, LoopbackResponse> _respond;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private int _disposed;

    /// <param name="port">0 = a free port chosen by Windows.</param>
    public LoopbackHttpServer(Func<string, LoopbackResponse> respond, int port = 0)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    /// <summary>Requests answered so far (tests).</summary>
    public int Requests;

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), _cts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                var head = Encoding.ASCII.GetString(buffer, 0, total);
                var parts = head.Split(' ', 3);
                Interlocked.Increment(ref Requests);
                var response = parts.Length >= 2 && parts[0] == "GET"
                    ? _respond(parts[1].Split('?')[0])
                    : new LoopbackResponse(405, "text/plain; charset=utf-8", "GET only");
                var body = Encoding.UTF8.GetBytes(response.Body);
                var header = $"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Status")}\r\nContent-Type: {response.ContentType}\r\n"
                             + $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token).ConfigureAwait(false);
                await stream.WriteAsync(body, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cts.Cancel();
        _listener.Stop();
        try
        {
            _accept.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }
}
