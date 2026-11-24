namespace PcControlCenter.Basalt.Mock;

/// <summary>
/// A simulated Basalt for <c>--mock-sensors</c> runs and the smoke test: a made-up process tree (PowerShell window →
/// basalt.exe → python.exe listening → basalt-engine.exe) that appears after a start and leaves after Ctrl+C, and a
/// loopback page labelled MOCK as the dashboard. No real process is listed, started, signalled or ended.
/// </summary>
public sealed class MockBasaltSystem : IBasaltSystem, IDisposable
{
    public const int LauncherId = 41000;
    public const int WrapperId = 41010;
    public const int ServerId = 41020;
    public const int EngineId = 41030;

    private readonly TimeProvider _time;
    private readonly LoopbackHttpServer _server;
    private readonly object _gate = new();
    private readonly List<BasaltProcess> _processes = [];
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _endsAt;
    private string _mode = "";

    public MockBasaltSystem(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _server = new LoopbackHttpServer(Respond);
    }

    /// <summary>How long after the start the server listens and answers.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    public string Name => "MOCK";
    public bool IsMock => true;
    public bool IsElevated => false;
    public int? PortOverride => _server.Port;

    public IReadOnlyList<BasaltProcess>? Processes()
    {
        lock (_gate)
        {
            Advance();
            return [.. _processes];
        }
    }

    public IReadOnlyList<BasaltListener>? Listeners(int port)
    {
        lock (_gate)
        {
            Advance();
            return port == _server.Port && Listening ? [new BasaltListener(port, ServerId)] : [];
        }
    }

    public bool FileExists(string path) => true;
    public bool DirectoryExists(string path) => true;

    public BasaltLaunch Start(BasaltStartRequest request, string mode)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _startedAt = now;
            _endsAt = null;
            _mode = mode;
            _processes.Clear();
            _processes.Add(new BasaltProcess(LauncherId, 1, "powershell.exe", now));
            _processes.Add(new BasaltProcess(WrapperId, LauncherId, "basalt.exe", now.AddMilliseconds(200)));
            _processes.Add(new BasaltProcess(ServerId, WrapperId, "python.exe", now.AddMilliseconds(400)));
            _processes.Add(new BasaltProcess(EngineId, ServerId, "basalt-engine.exe", now.AddMilliseconds(600)));
            return new BasaltLaunch(LauncherId, now, mode, now);
        }
    }

    public ConsoleSignalResult SendCtrlC(int processId, IReadOnlyList<BasaltProcess> owned)
    {
        lock (_gate)
        {
            if (_processes.All(p => p.Id != processId))
            {
                return ConsoleSignalResult.NoConsole;
            }

            _endsAt = _time.GetUtcNow() + TimeSpan.FromSeconds(0.8);
            return ConsoleSignalResult.Sent;
        }
    }

    public TerminateResult Terminate(BasaltProcess process)
    {
        lock (_gate)
        {
            var index = _processes.FindIndex(p => p.Id == process.Id);
            if (index < 0)
            {
                return TerminateResult.AlreadyGone;
            }

            if (!_processes[index].SameAs(process))
            {
                return TerminateResult.IdentityChanged;
            }

            _processes.RemoveAt(index);
            return TerminateResult.Ended;
        }
    }

    public Task<BasaltHealth> HealthAsync(int port, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Advance();
            return Task.FromResult(Listening ? new BasaltHealth(200, "basalt (MOCK)", true, null) : BasaltHealth.NoAnswer("연결 거부"));
        }
    }

    public void Dispose() => _server.Dispose();

    private bool Listening => _startedAt is { } start && _time.GetUtcNow() - start >= StartDelay && _processes.Any(p => p.Id == ServerId);

    private void Advance()
    {
        if (_endsAt is { } end && _time.GetUtcNow() >= end)
        {
            _processes.Clear();
            _startedAt = null;
            _endsAt = null;
        }
    }

    private LoopbackResponse Respond(string path)
    {
        bool listening;
        string mode;
        lock (_gate)
        {
            Advance();
            listening = Listening;
            mode = _mode;
        }

        if (path == "/health")
        {
            return listening
                ? new LoopbackResponse(200, "application/json", "{\"service\":\"basalt-mock\",\"loaded\":true}")
                : new LoopbackResponse(503, "application/json", "{\"service\":\"basalt-mock\",\"loaded\":false}");
        }

        if (path == "/v1/status" && listening)
        {
            // Part of Strata's status shape only: the page shows the rest as "미지원", as with a Basalt that lacks them.
            return new LoopbackResponse(200, "application/json",
                "{\"service\":\"basalt-mock\",\"model\":\"MOCK-model\",\"loaded\":true,\"cache_max_tokens\":32768,\"concurrency\":{\"serving\":1}}");
        }

        if (path is "/" or "/index.html")
        {
            var modeText = BasaltController.ModeText(mode);
            return new LoopbackResponse(200, "text/html; charset=utf-8", $$"""
                <!doctype html><html lang="ko"><head><meta charset="utf-8"><title>Basalt (MOCK)</title>
                <style>body{background:#121211;color:#ecebe8;font:14px "Segoe UI",sans-serif;margin:24px}
                .tag{display:inline-block;border:1px solid #d95926;color:#d95926;padding:2px 8px;border-radius:4px;font-weight:600}
                table{margin-top:16px;border-collapse:collapse}td{padding:4px 16px 4px 0;color:#bdbbb6}td+td{color:#ecebe8}</style></head>
                <body><span class="tag">MOCK</span><h2>Basalt 대시보드 (모의)</h2>
                <p>실제 Basalt 서버가 아닙니다. --mock-sensors 실행에서 화면 구성을 확인하기 위한 페이지입니다.</p>
                <table><tr><td>모드</td><td>{{modeText}}</td></tr><tr><td>상태</td><td>{{(listening ? "응답함" : "꺼짐")}}</td></tr></table>
                </body></html>
                """);
        }

        return new LoopbackResponse(404, "text/plain; charset=utf-8", "not found");
    }
}
