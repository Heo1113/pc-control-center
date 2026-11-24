using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace PcControlCenter.Operator;

/// <summary>The shell's end of the line protocol to one Operator host process.</summary>
public sealed class HostConnection : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly Stream _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<HostResponse>> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private Task? _readLoop;
    private long _nextId;
    private int _isClosed;

    private HostConnection(Stream fromHost, Stream toHost)
    {
        _reader = new StreamReader(fromHost, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
        _writer = toHost;
    }

    public HelloResult Hello { get; private set; } = null!;

    public bool IsOpen => Volatile.Read(ref _isClosed) == 0;

    public string? CloseReason { get; private set; }

    /// <summary>Raised once when the connection ends (host exited, pipe broken, or disposed).</summary>
    public event Action<string>? Closed;

    public static async Task<HostConnection> OpenAsync(Stream fromHost, Stream toHost, TimeSpan helloTimeout, CancellationToken cancellationToken = default)
    {
        var connection = new HostConnection(fromHost, toHost);
        connection._readLoop = Task.Run(connection.ReadLoopAsync, CancellationToken.None);
        try
        {
            connection.Hello = await connection.CallAsync<HelloResult>(HostOp.Hello, null, helloTimeout, cancellationToken).ConfigureAwait(false)
                               ?? throw new HostUnavailableException("Operator 실행 프로세스가 시작 응답을 보내지 않았습니다.");
            if (connection.Hello.ProtocolVersion != HostProtocol.Version)
            {
                throw new HostUnavailableException($"Operator 실행 프로세스의 프로토콜 버전({connection.Hello.ProtocolVersion})이 다릅니다.");
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    /// <summary>
    /// Sends one request and waits for its answer. Cancelling asks the host to cancel the request too. Throws
    /// <see cref="HostUnavailableException"/> when the host is gone, <see cref="TimeoutException"/> after
    /// <paramref name="timeout"/>, <see cref="HostCallException"/> when the host refused or failed it.
    /// </summary>
    public async Task<T?> CallAsync<T>(HostOp op, object? args, TimeSpan? timeout, CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
        {
            throw new HostUnavailableException(CloseReason ?? "Operator 실행 프로세스와 연결되어 있지 않습니다.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new HostRequest(id, op, args is null ? null : HostProtocol.ToElement(args))).ConfigureAwait(false);
            await using var registration = cancellationToken.Register(() =>
            {
                _ = SendCancelAsync(id);
                completion.TrySetCanceled(cancellationToken);
            }).ConfigureAwait(false);
            var response = timeout is { } t
                ? await completion.Task.WaitAsync(t, CancellationToken.None).ConfigureAwait(false)
                : await completion.Task.ConfigureAwait(false);
            if (!response.Ok)
            {
                if (response.Kind == HostErrorKind.Cancelled)
                {
                    throw new OperationCanceledException(response.Error);
                }

                throw new HostCallException(response.Kind ?? HostErrorKind.Failed, response.Error ?? "Operator 실행 프로세스 오류");
            }

            return HostProtocol.Read<T>(response.Result);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Fire-and-forget control message (emergency stop, resume) with a short bounded wait.</summary>
    public async Task<bool> TrySendControlAsync(HostOp op, TimeSpan timeout)
    {
        try
        {
            await CallAsync<object>(op, null, timeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HostUnavailableException or TimeoutException or HostCallException or OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close("연결을 닫았습니다.");
        await _closed.CancelAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            await Task.WhenAny(_readLoop, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
    }

    private async Task SendCancelAsync(long id)
    {
        try
        {
            await WriteAsync(new HostRequest(Interlocked.Increment(ref _nextId), HostOp.Cancel, HostProtocol.ToElement(new CancelArgs(id)))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HostUnavailableException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task WriteAsync(HostRequest request)
    {
        var bytes = Encoding.UTF8.GetBytes(HostProtocol.Serialize(request) + "\n");
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(bytes).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close("Operator 실행 프로세스에 요청을 보내지 못했습니다(종료됨).");
            throw new HostUnavailableException(CloseReason!);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync(_closed.Token).ConfigureAwait(false);
                if (line is null)
                {
                    Close("Operator 실행 프로세스가 종료되었습니다.");
                    return;
                }

                if (line.Length == 0 || line.Length > HostProtocol.MaxLineChars)
                {
                    continue;
                }

                HostResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<HostResponse>(line, HostProtocol.Json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (response is not null && _pending.TryGetValue(response.Id, out var completion))
                {
                    completion.TrySetResult(response);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            Close("Operator 실행 프로세스와의 연결이 끊겼습니다.");
        }
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _isClosed, 1) == 1)
        {
            return;
        }

        CloseReason = reason;
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(new HostUnavailableException(reason));
        }

        try
        {
            Closed?.Invoke(reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A subscriber failing must not keep the connection half-open.
        }
    }
}
