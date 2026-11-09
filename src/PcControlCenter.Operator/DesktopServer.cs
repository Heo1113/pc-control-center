using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using WindowsAiOperator.Core.Automation;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Security;

namespace PcControlCenter.Operator;

/// <summary>
/// Runs inside PcControlCenter.OperatorHost.exe: serves project E's desktop automation (FlaUI/Win32, or the simulated
/// desktop) to the shell over the host's stdin/stdout. Every changing request (input, focus, program start) passes
/// <see cref="HostGuard"/> first. An emergency stop cancels everything in flight and latches the guard.
/// </summary>
public sealed class DesktopServer
{
    private readonly IDesktopAutomation _desktop;
    private readonly HostGuard _guard;
    private readonly IOperatorLog _log;
    private readonly StreamReader _reader;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _inFlight = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _hostElevated;

    public DesktopServer(IDesktopAutomation desktop, Stream input, Stream output, HostGuard guard, IOperatorLog? log = null, bool hostElevated = false)
    {
        _desktop = desktop;
        _guard = guard;
        _log = log ?? NullOperatorLog.Instance;
        _reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
        _output = output;
        _hostElevated = hostElevated;
    }

    /// <summary>Serves requests until the shell sends Shutdown or closes the pipe (the shell ended).</summary>
    public async Task RunAsync()
    {
        var running = new List<Task>();
        _log.Info("host", $"serving desktop '{_desktop.Name}' (mock={_desktop.IsMock}, elevated={_hostElevated})");
        while (!_stop.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                break;
            }

            if (line is null)
            {
                _log.Info("host", "input closed: the shell ended");
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length > HostProtocol.MaxLineChars)
            {
                _log.Warn("host", "request line too long; ignored");
                continue;
            }

            HostRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<HostRequest>(line, HostProtocol.Json);
            }
            catch (JsonException ex)
            {
                _log.Warn("host", "malformed request ignored", ex);
                continue;
            }

            if (request is null)
            {
                continue;
            }

            switch (request.Op)
            {
                case HostOp.Cancel:
                    if (HostProtocol.Read<CancelArgs>(request.Args) is { } cancel && _inFlight.TryGetValue(cancel.RequestId, out var cts))
                    {
                        await cts.CancelAsync().ConfigureAwait(false);
                    }

                    await WriteAsync(new HostResponse(request.Id, true)).ConfigureAwait(false);
                    break;
                case HostOp.EmergencyStop:
                    _guard.Latch();
                    foreach (var pending in _inFlight.Values)
                    {
                        await pending.CancelAsync().ConfigureAwait(false);
                    }

                    _log.Warn("host", "emergency stop: all requests cancelled, changes refused until resume");
                    await WriteAsync(new HostResponse(request.Id, true)).ConfigureAwait(false);
                    break;
                case HostOp.Resume:
                    _guard.Resume();
                    _log.Info("host", "resumed after emergency stop");
                    await WriteAsync(new HostResponse(request.Id, true)).ConfigureAwait(false);
                    break;
                case HostOp.Shutdown:
                    await WriteAsync(new HostResponse(request.Id, true)).ConfigureAwait(false);
                    await _stop.CancelAsync().ConfigureAwait(false);
                    break;
                default:
                    var source = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    _inFlight[request.Id] = source;
                    running.Add(Task.Run(() => HandleAsync(request, source.Token)));
                    running.RemoveAll(t => t.IsCompleted);
                    break;
            }
        }

        foreach (var pending in _inFlight.Values)
        {
            await pending.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        _log.Info("host", "stopped");
    }

    private async Task HandleAsync(HostRequest request, CancellationToken cancellationToken)
    {
        HostResponse response;
        try
        {
            response = new HostResponse(request.Id, true, await DispatchAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            response = new HostResponse(request.Id, false, Error: "취소되었습니다.", Kind: HostErrorKind.Cancelled);
        }
        catch (HostCallException ex)
        {
            response = new HostResponse(request.Id, false, Error: ex.Message, Kind: ex.Kind);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("host", $"{request.Op} failed", ex);
            response = new HostResponse(request.Id, false, Error: Redactor.OneLine(ex.Message, 300), Kind: HostErrorKind.Failed);
        }
        finally
        {
            if (_inFlight.TryRemove(request.Id, out var source))
            {
                source.Dispose();
            }
        }

        await WriteAsync(response).ConfigureAwait(false);
    }

    private async Task<JsonElement?> DispatchAsync(HostRequest request, CancellationToken ct)
    {
        T Args<T>() => HostProtocol.Read<T>(request.Args) ?? throw new HostCallException(HostErrorKind.BadRequest, $"{request.Op}: 인자가 없습니다.");
        switch (request.Op)
        {
            case HostOp.Hello:
                return HostProtocol.ToElement(new HelloResult(HostProtocol.Version, Environment.ProcessId, _desktop.Name, _desktop.IsMock, _desktop.Status, _hostElevated,
                    _guard.IsLatched, _desktop.OwnProcessId));
            case HostOp.Status:
                return HostProtocol.ToElement(_desktop.Status);
            case HostOp.ListWindows:
                return HostProtocol.ToElement(_desktop.ListWindows());
            case HostOp.GetWindow:
                return HostProtocol.ToElement(_desktop.GetWindow(Args<HandleArgs>().Handle));
            case HostOp.GetForegroundWindow:
                return HostProtocol.ToElement(_desktop.GetForegroundWindow());
            case HostOp.CaptureTree:
            {
                var a = Args<CaptureTreeArgs>();
                return HostProtocol.ToElement(_desktop.CaptureTree(a.Handle, a.Options, ct));
            }

            case HostOp.FindElements:
            {
                var a = Args<FindElementsArgs>();
                return HostProtocol.ToElement(_desktop.FindElements(a.Handle, a.Query, ct));
            }

            case HostOp.ElementFromPoint:
                return HostProtocol.ToElement(_desktop.ElementFromPoint(Args<PointArgs>().Point));
            case HostOp.FocusWindow:
            {
                var handle = Args<HandleArgs>().Handle;
                if (_guard.CheckTarget(_desktop.GetWindow(handle)) is { } refusal)
                {
                    throw new HostCallException(HostErrorKind.Refused, refusal);
                }

                return HostProtocol.ToElement(_desktop.FocusWindow(handle));
            }

            case HostOp.Perform:
            {
                var action = Args<PerformArgs>().Request;
                if (_guard.CheckTarget(_desktop.GetWindow(action.WindowHandle)) is { } refusal)
                {
                    _log.Warn("host", $"refused {action}: {refusal}");
                    return HostProtocol.ToElement(ActionOutcome.NotPerformed(_guard.IsLatched ? ExecutionErrorKind.Cancelled : ExecutionErrorKind.PolicyDenied, refusal));
                }

                return HostProtocol.ToElement(await _desktop.PerformAsync(action, ct).ConfigureAwait(false));
            }

            case HostOp.Launch:
            {
                var a = Args<LaunchArgs>();
                if (_guard.CheckLaunch(a.Executable) is { } refusal)
                {
                    _log.Warn("host", $"refused launch of {a.Executable}: {refusal}");
                    return HostProtocol.ToElement(new LaunchResult(false, 0, 0, refusal));
                }

                var timeout = TimeSpan.FromSeconds(Math.Clamp(a.TimeoutSeconds, 1, 120));
                return HostProtocol.ToElement(await _desktop.LaunchAsync(a.Executable, a.ExpectedProcessNames, timeout, ct).ConfigureAwait(false));
            }

            case HostOp.CaptureImage:
            {
                var a = Args<CaptureImageArgs>();
                return HostProtocol.ToElement(_desktop.CaptureWindowImage(a.Handle, Math.Clamp(a.MaxWidth, 16, 4096)));
            }

            default:
                throw new HostCallException(HostErrorKind.BadRequest, $"알 수 없는 요청: {request.Op}");
        }
    }

    private async Task WriteAsync(HostResponse response)
    {
        var bytes = Encoding.UTF8.GetBytes(HostProtocol.Serialize(response) + "\n");
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The shell is gone; RunAsync ends when the input closes.
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

internal sealed class NullOperatorLog : IOperatorLog
{
    public static NullOperatorLog Instance { get; } = new();

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
    }
}
