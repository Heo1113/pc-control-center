using LocalAiPerf.Core.Diagnostics;
using PcControlCenter.Core;

namespace PcControlCenter.Basalt;

/// <summary>Intervals of the controller. Tests shorten them.</summary>
public sealed record BasaltTimings
{
    /// <summary>How often the state is checked (the TCP table every time; the process list when something may run).</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The check interval while starting or stopping, and while waiting for processes to end.</summary>
    public TimeSpan FastPoll { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>With nothing listening and nothing started here, the process list is read only this often (for leftovers).</summary>
    public TimeSpan IdleScan { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>After ending the processes, children started again (a restarted engine) are looked for this long.</summary>
    public TimeSpan RespawnWatch { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>...and the watch ends early once nothing new appeared for this long and the port is free.</summary>
    public TimeSpan RespawnQuiet { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A start fails when this app's window has closed and nothing of Basalt appeared for this long.</summary>
    public TimeSpan LauncherGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Tests: replaces the settings' graceful stop wait (at least 5 seconds there).</summary>
    public TimeSpan? StopWait { get; init; }

    /// <summary>Tests: replaces the settings' start wait.</summary>
    public TimeSpan? StartWait { get; init; }
}

/// <summary>
/// Starts, watches and stops the local Basalt server. Starting runs Basalt's own start script with the chosen configuration
/// in a console window of its own, and only when nothing listens on the port. Stopping sends Ctrl+C to Basalt's console
/// (what pressing Ctrl+C in its window does, which leaves nothing behind) and waits; only if processes remain after the
/// wait are exactly the processes of the server ended - the listener first, so it cannot start the engine again - and the
/// result is checked: no process left, port free. A program on the port that is not Basalt is never ended. Closing the app
/// leaves Basalt running.
/// </summary>
public sealed class BasaltController : IAsyncDisposable
{
    private readonly IBasaltSystem _system;
    private readonly BasaltSettings _settings;
    private readonly TimeProvider _time;
    private readonly IAppLogger _log;
    private readonly BasaltTimings _timings;
    private readonly int _selfId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private BasaltStatus _status;
    private readonly BasaltOwnership _ownership;
    private int _oursNow;
    private bool _stopLeft;

    /// <summary>The window this app opened for the current Basalt (kept across restarts by <see cref="BasaltOwnership"/>).</summary>
    private BasaltLaunch? _launch => _ownership.Launch;
    private DateTimeOffset? _launcherGoneAt;
    private bool _launchAnswered;
    private BasaltStopReport? _lastStop;
    private string? _notice;
    private DateTimeOffset _lastFullScan = DateTimeOffset.MinValue;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private volatile bool _detailed;

    /// <param name="settings">The live settings object (the settings page edits and saves it); read at every action.</param>
    /// <param name="selfId">This app's PID (never part of a tree); default: the current process.</param>
    /// <param name="ownershipFile">Where the processes this app started are remembered across restarts; null = this run only.</param>
    public BasaltController(IBasaltSystem system, BasaltSettings settings, TimeProvider? time = null, IAppLogger? log = null, BasaltTimings? timings = null,
        int? selfId = null, string? ownershipFile = null)
    {
        _ownership = new BasaltOwnership(ownershipFile);
        _system = system;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _timings = timings ?? new BasaltTimings();
        _selfId = selfId ?? Environment.ProcessId;
        _status = new BasaltStatus { State = BasaltState.Unknown, Port = Port, IsMock = system.IsMock, Detail = "상태를 확인하는 중", CheckedAt = _time.GetUtcNow() };
    }

    /// <summary>Raised on a thread-pool thread after every check and at every step of a start or stop.</summary>
    public event Action<BasaltStatus>? Changed;

    public BasaltStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
    }

    public int Port => _system.PortOverride ?? _settings.Port;
    public bool IsMock => _system.IsMock;
    public string SystemName => _system.Name;
    public BasaltSettings Settings => _settings;

    /// <summary>The page is on screen: the process tree is read at every check (its details are shown).</summary>
    public bool Detailed
    {
        get => _detailed;
        set => _detailed = value;
    }

    /// <summary>Starts the periodic check (it never starts or stops the server by itself).</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loop = Task.Run(() => LoopAsync(token));
    }

    /// <summary>Stops the periodic check. Basalt keeps running.</summary>
    public async Task StopWatchingAsync()
    {
        if (_loopCts is null || _loop is null)
        {
            return;
        }

        await _loopCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _loopCts.Dispose();
        _loopCts = null;
        _loop = null;
    }

    /// <summary>Checks now (the process list included).</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RefreshCoreAsync(true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken);

    /// <summary>
    /// Starts Basalt with the configuration of <paramref name="mode"/> (<see cref="BasaltSettings.QualityMode"/> or
    /// <see cref="BasaltSettings.FastMode"/>). Returns why it was not started, or null when the window was started (the
    /// periodic check then follows it until it answers).
    /// </summary>
    public Task<BasaltRefusal?> StartServerAsync(string mode, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var refusal = StartCore(mode);
                _notice = refusal?.Message;
                if (refusal is not null)
                {
                    _log.Info("basalt", "켜지 않음: " + refusal.Message);
                }

                await RefreshCoreAsync(true, cancellationToken).ConfigureAwait(false);
                return refusal;
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken);

    /// <summary>
    /// Stops Basalt: Ctrl+C, the wait from the settings, then (if needed) the server's processes are ended and the result is
    /// checked. Returns the report, or null when nothing was done (the reason is in <see cref="BasaltStatus.Notice"/>).
    /// </summary>
    public Task<BasaltStopReport?> StopServerAsync(CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var report = await StopCoreAsync(cancellationToken).ConfigureAwait(false);
                if (report is not null)
                {
                    _lastStop = report;
                    _notice = null;
                    _log.Info("basalt", $"끄기: {report.Summary}; {report.Signal}; 종료 {report.Ended.Count}개; 남음 {string.Join(", ", report.Left)}");
                }

                await RefreshCoreAsync(true, cancellationToken).ConfigureAwait(false);
                return report;
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await StopWatchingAsync().ConfigureAwait(false);
        if (_system is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _gate.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await RefreshCoreAsync(_detailed, ct).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("basalt", "Basalt 상태 확인 실패", ex);
            }

            var interval = Status.State is BasaltState.Starting or BasaltState.Stopping ? _timings.FastPoll : _timings.Poll;
            try
            {
                await Task.Delay(interval, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private TimeSpan StopWait => _timings.StopWait ?? TimeSpan.FromSeconds(_settings.StopWaitSeconds);
    private TimeSpan StartWait => _timings.StartWait ?? TimeSpan.FromSeconds(_settings.StartWaitSeconds);

    private async Task RefreshCoreAsync(bool full, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var port = Port;
        if (_system.UnavailableReason is { } unavailable)
        {
            Publish(BasaltState.Unavailable, unavailable, null, null);
            return;
        }

        var listeners = _system.Listeners(port);
        if (listeners is null)
        {
            Publish(BasaltState.Unavailable, "TCP 수신 대기 목록을 읽지 못해 Basalt 상태를 알 수 없습니다.", null, null);
            return;
        }

        var previous = Status.State;
        var scan = full || listeners.Count > 0 || _launch is not null || now - _lastFullScan >= _timings.IdleScan
                   || previous is BasaltState.Unknown or BasaltState.Starting or BasaltState.Leftover or BasaltState.Failed or BasaltState.Running;
        IReadOnlyList<BasaltProcess>? processes = null;
        if (scan)
        {
            processes = _system.Processes();
            _lastFullScan = now;
            if (processes is null)
            {
                Publish(BasaltState.Unavailable, "프로세스 목록을 읽지 못해 Basalt 상태를 알 수 없습니다.", null, null);
                return;
            }
        }

        var tree = processes is null ? null : BasaltProcessTree.Identify(port, processes, listeners, _launch, _selfId);
        if (processes is not null)
        {
            var ours = tree is { HasMembers: true } ? _ownership.Ours(tree.Members.Select(m => m.Process), processes) : [];
            _ownership.Adopt(ours);
            _ownership.Prune(processes);
            _oursNow = ours.Count;
            if (_ownership.Restored && !_ownership.AnyAlive(processes))
            {
                _ownership.Clear(); // what an earlier run started has ended meanwhile
            }
        }

        var launcherAlive = _launch is not null && processes is not null && processes.Any(p => p.Id == _launch.ProcessId && p.StartTime == _launch.StartTime);
        if (_launch is not null && processes is not null)
        {
            _launcherGoneAt = launcherAlive ? null : _launcherGoneAt ?? now;
        }

        if (listeners.Count > 0)
        {
            if (tree is { IsBasalt: false })
            {
                Publish(BasaltState.PortBusy, tree.NotBasaltReason ?? $"포트 {port}를 다른 프로그램이 쓰고 있습니다.", tree, null);
                return;
            }

            var health = await _system.HealthAsync(port, ct).ConfigureAwait(false);
            if (health.IsUp)
            {
                _launchAnswered |= _launch is not null;
                Publish(BasaltState.Running, (health.Loaded == false
                    ? $"응답함 · 모델을 올리는 중 (/health: loaded=false)"
                    : $"응답함 (/health HTTP {health.StatusCode})") + (_oursNow == 0 ? " · 이 프로그램이 켜지 않아 여기서는 끄지 않음" : ""), tree, health);
                return;
            }

            var waited = _launch is null ? (TimeSpan?)null : now - _launch.StartedAt;
            if (waited > StartWait)
            {
                Publish(BasaltState.Failed, $"시작 대기 {BasaltProcessTree.Seconds(StartWait)}초가 지나도 /health가 응답하지 않습니다 ({HealthText(health)}). "
                                            + "Basalt 창의 메시지를 확인하고, 끄기를 누르면 정리합니다.", tree, health);
                return;
            }

            Publish(BasaltState.Starting, $"포트 {port}는 열렸지만 /health가 아직 응답하지 않습니다 ({HealthText(health)})", tree, health);
            return;
        }

        if (_stopLeft && tree is { HasMembers: true } afterStop)
        {
            Publish(BasaltState.Leftover, $"끄기 뒤 Basalt 프로세스 {afterStop.Members.Count}개가 남아 있습니다 (마지막 끄기 결과 참고).", tree, null);
            return;
        }

        if (tree is { HasMembers: false })
        {
            _stopLeft = false;
        }

        if (_launch is not null)
        {
            var waited = now - _launch.StartedAt;
            if (tree is { HasMembers: false } && _launcherGoneAt is { } gone && now - gone >= _timings.LauncherGrace)
            {
                _notice = _launchAnswered
                    ? "Basalt가 꺼졌습니다 (시작 스크립트 창이 닫혔거나 서버가 스스로 끝남)."
                    : "Basalt를 켜지 못했습니다: 시작 스크립트 창이 닫혔고 Basalt 프로세스가 없습니다. 스크립트나 설정 파일의 오류일 수 있으니 "
                      + "start-basalt.ps1을 직접 실행해 메시지를 확인하세요.";
                _log.Warn("basalt", _launchAnswered ? "Basalt 창이 닫힘" : "시작 실패: 창이 닫혔고 프로세스 없음");
                _ownership.Clear();
                Publish(BasaltState.Off, "꺼짐", tree, null);
                return;
            }

            if (waited > StartWait)
            {
                Publish(BasaltState.Failed, $"시작 대기 {BasaltProcessTree.Seconds(StartWait)}초가 지나도 포트 {port}에서 수신하지 않습니다. "
                                            + "Basalt 창의 메시지를 확인하고, 끄기를 누르면 남은 프로세스를 정리합니다.", tree, null);
                return;
            }

            Publish(BasaltState.Starting, $"켜는 중 · {BasaltProcessTree.Seconds(waited)}초 경과 (모델을 올리는 동안 오래 걸릴 수 있습니다)", tree, null);
            return;
        }

        if (tree is { HasMembers: true } leftovers)
        {
            var youngest = leftovers.Members.Max(m => m.Process.StartTime ?? DateTimeOffset.MinValue);
            if (now - youngest < StartWait)
            {
                Publish(BasaltState.Starting, $"Basalt 프로세스가 있지만 아직 포트 {port}에서 수신하지 않습니다 (이 프로그램 밖에서 켜는 중일 수 있음).", tree, null);
                return;
            }

            Publish(BasaltState.Leftover, _oursNow > 0
                ? $"Basalt 프로세스 {leftovers.Members.Count}개가 포트를 쓰지 않은 채 남아 있습니다. 끄기를 누르면 이 프로그램이 켠 {_oursNow}개를 정리합니다."
                : $"이 프로그램이 켜지 않은 Basalt 프로세스 {leftovers.Members.Count}개가 포트를 쓰지 않은 채 있습니다. 이 화면에서는 끄지 않습니다.", tree, null);
            return;
        }

        if (tree is null && previous is BasaltState.Leftover or BasaltState.Starting)
        {
            return; // no fresh process list this time: keep what was seen
        }

        Publish(BasaltState.Off, "꺼짐", tree, null);
    }

    private BasaltRefusal? StartCore(string mode)
    {
        if (_system.UnavailableReason is { } unavailable)
        {
            return new BasaltRefusal(unavailable);
        }

        if (!BasaltSettings.Modes.Contains(mode))
        {
            return new BasaltRefusal($"알 수 없는 모드입니다: {mode}");
        }

        if (Status.State is BasaltState.Stopping)
        {
            return new BasaltRefusal("끄는 중입니다. 끝난 뒤에 켜세요.");
        }

        if (_system.IsElevated && !_system.IsMock)
        {
            return new BasaltRefusal("PC Control Center가 관리자 권한으로 실행 중이라 Basalt를 켜지 않았습니다 (Basalt가 관리자 권한을 물려받지 않도록). "
                                     + "PC Control Center를 일반 권한으로 실행하거나 Basalt를 직접 켜세요.");
        }

        var directory = _settings.InstallDirectory;
        var script = _settings.Resolve(_settings.StartScript);
        var config = _settings.Resolve(_settings.ConfigFor(mode));
        if (!_system.DirectoryExists(directory))
        {
            return new BasaltRefusal($"Basalt 설치 폴더가 없습니다: {directory} (설정 › 공통 › Basalt에서 바꿀 수 있습니다)");
        }

        if (!_system.FileExists(script))
        {
            return new BasaltRefusal($"Basalt 시작 스크립트가 없습니다: {script}");
        }

        if (!_system.FileExists(config))
        {
            return new BasaltRefusal($"{ModeText(mode)} 설정 파일이 없습니다: {config}");
        }

        var port = Port;
        var listeners = _system.Listeners(port);
        if (listeners is null)
        {
            return new BasaltRefusal($"TCP 수신 대기 목록을 읽지 못해 포트 {port}가 비어 있는지 확인할 수 없어 켜지 않았습니다.");
        }

        var processes = _system.Processes();
        if (processes is null)
        {
            return new BasaltRefusal("프로세스 목록을 읽지 못해 켜지 않았습니다.");
        }

        var tree = BasaltProcessTree.Identify(port, processes, listeners, _launch, _selfId);
        if (listeners.Count > 0)
        {
            return tree.IsBasalt
                ? new BasaltRefusal($"Basalt가 이미 포트 {port}에서 실행 중입니다{(tree.Owner is { } o ? $" ({o.Label})" : "")}. 모드를 바꾸려면 먼저 끄세요.")
                : new BasaltRefusal($"포트 {port}를 다른 프로그램이 쓰고 있어 Basalt를 켜지 않았습니다: {tree.Owner?.Label ?? "알 수 없음"}. "
                                    + "그 프로그램을 끄거나 Basalt 포트를 바꾸세요.");
        }

        if (tree.HasMembers)
        {
            return new BasaltRefusal($"Basalt 프로세스가 이미 있어 켜지 않았습니다: {BasaltProcessTree.Describe(tree.Members.Select(m => m.Process))}. "
                                     + "켜는 중이면 기다리고, 남은 것이면 끄기를 눌러 정리한 뒤 켜세요.");
        }

        var request = new BasaltStartRequest(BasaltCommand.PowerShellPath(), BasaltCommand.Arguments(script, config), directory);
        try
        {
            _ownership.Started(_system.Start(request, mode));
            _stopLeft = false;
        }
        catch (BasaltStartException ex)
        {
            return new BasaltRefusal($"Basalt를 켜지 못했습니다: {ex.Message}");
        }

        _launcherGoneAt = null;
        _launchAnswered = false;
        _log.Info("basalt", $"Basalt 시작 ({ModeText(mode)}, 창 PID {_launch!.ProcessId}, 포트 {port})");
        return null;
    }

    private async Task<BasaltStopReport?> StopCoreAsync(CancellationToken ct)
    {
        if (_system.UnavailableReason is { } unavailable)
        {
            _notice = unavailable;
            return null;
        }

        var started = _time.GetUtcNow();
        var port = Port;
        var processes = _system.Processes();
        var listeners = _system.Listeners(port);
        if (processes is null || listeners is null)
        {
            _notice = "프로세스 목록이나 TCP 수신 대기 목록을 읽지 못해 끄지 않았습니다.";
            return null;
        }

        var tree = BasaltProcessTree.Identify(port, processes, listeners, _launch, _selfId);
        if (listeners.Count > 0 && !tree.IsBasalt)
        {
            _notice = $"포트 {port}를 쓰는 프로세스가 Basalt로 확인되지 않아 아무것도 끄지 않았습니다. {tree.NotBasaltReason}";
            return null;
        }

        if (!tree.HasMembers)
        {
            _ownership.Clear();
            _notice = "Basalt가 이미 꺼져 있습니다.";
            return null;
        }

        // Only what this app started (its window and what runs under it) is ever stopped.
        var ours = _ownership.Ours(tree.Members.Select(m => m.Process), processes).Select(p => p.Id).ToHashSet();
        if (ours.Count == 0)
        {
            _notice = "이 Basalt는 이 프로그램이 켜지 않아 끄지 않았습니다. Basalt를 켠 창에서 Ctrl+C로 끄세요.";
            return null;
        }

        var members = tree.Members.Where(m => ours.Contains(m.Process.Id)).Select(m => m.Process).ToList();
        var roles = tree.Members.ToDictionary(m => m.Process.Id, m => m.Role);
        PublishStopping("Ctrl+C를 보내는 중", started, tree);

        // 1. Ctrl+C to Basalt's console: every process attached to it receives it, as when it is pressed in the window.
        string? signal = null;
        var sent = false;
        var attempts = new List<string>();
        foreach (var candidate in tree.ConsoleCandidates.Where(c => ours.Contains(c.Id)))
        {
            var result = _system.SendCtrlC(candidate.Id);
            if (result == ConsoleSignalResult.Sent)
            {
                signal = $"{candidate.Label}의 콘솔에 Ctrl+C를 보냄";
                sent = true;
                break;
            }

            if (result == ConsoleSignalResult.HelperMissing)
            {
                signal = "Ctrl+C 도우미(PcControlCenter.ConsoleSignal.exe)를 찾지 못해 보내지 못함";
                break;
            }

            attempts.Add($"{candidate.Label}: {(result == ConsoleSignalResult.NoConsole ? "콘솔 없음" : "실패")}");
        }

        signal ??= attempts.Count == 0 ? "Ctrl+C를 보낼 프로세스가 없음" : $"Ctrl+C를 보낼 콘솔이 없음 ({string.Join(", ", attempts)})";
        var graceful = false;
        if (sent)
        {
            PublishStopping($"정상 종료를 기다리는 중 (최대 {BasaltProcessTree.Seconds(StopWait)}초)", started, tree);
            graceful = await WaitGoneAsync(members, port, StopWait, ct).ConfigureAwait(false);
        }

        // 2. Not gone (or no console): end exactly the server's processes, listener first, then look for restarted children.
        var ended = new List<string>();
        var reasons = new Dictionary<int, string>();
        var everything = new List<BasaltProcess>(members);
        if (!graceful)
        {
            PublishStopping(sent ? $"{BasaltProcessTree.Seconds(StopWait)}초 안에 끝나지 않아 프로세스를 정리하는 중" : "Ctrl+C를 보낼 수 없어 프로세스를 정리하는 중",
                started, tree);
            var nowProcesses = _system.Processes() ?? processes;
            var nowListeners = _system.Listeners(port) ?? [];
            var fresh = BasaltProcessTree.Identify(port, nowProcesses, nowListeners, _launch, _selfId);
            var targets = new List<(BasaltProcess Process, TreeRole Role)>();
            if (fresh is { IsBasalt: true, Owner: not null })
            {
                // What listens now (a restarted server included), as far as it is this app's. Without a listener only the
                // processes seen above are ended, not every basalt* process on the PC.
                var freshOurs = _ownership.Ours(fresh.Members.Select(m => m.Process), nowProcesses).Select(p => p.Id).ToHashSet();
                targets.AddRange(fresh.Members.Where(m => freshOurs.Contains(m.Process.Id)).Select(m => (m.Process, m.Role)));
            }

            foreach (var member in tree.Members.Where(m => ours.Contains(m.Process.Id)))
            {
                if (targets.All(t => t.Process.Id != member.Process.Id)
                    && BasaltProcessTree.StillRunning([member.Process], nowProcesses).Count > 0)
                {
                    targets.Add((member.Process, member.Role));
                }
            }

            foreach (var (process, role) in targets)
            {
                roles.TryAdd(process.Id, role);
                if (everything.All(e => e.Id != process.Id))
                {
                    everything.Add(process);
                }

                End(process, RoleText(role), ended, reasons);
            }

            await WatchRespawnsAsync(everything, port, ended, reasons, ct).ConfigureAwait(false);
        }

        // 3. Check: nothing of the server runs, nothing listens on the port.
        var final = _system.Processes();
        var finalListeners = _system.Listeners(port);
        var left = final is null ? everything : BasaltProcessTree.StillRunning(everything, final).ToList();
        if (final is not null)
        {
            left.AddRange(BasaltProcessTree.NewChildren(everything, final, everything.Select(e => e.Id).ToHashSet(), _selfId));
        }

        string? portOwner = null;
        if (finalListeners is { Count: > 0 })
        {
            var owner = finalListeners[0].ProcessId;
            portOwner = final?.FirstOrDefault(p => p.Id == owner)?.Label ?? $"PID {owner}";
        }

        if (left.Count == 0 && finalListeners is { Count: 0 })
        {
            _ownership.Clear();
        }

        _stopLeft = left.Count > 0;

        return new BasaltStopReport
        {
            At = _time.GetUtcNow(),
            Graceful = graceful,
            Signal = signal,
            Ended = ended,
            Left = [.. left.Select(p => reasons.TryGetValue(p.Id, out var why) ? $"{p.Label} — {why}" : $"{p.Label} — 아직 실행 중")],
            PortFree = finalListeners is { Count: 0 },
            PortOwner = finalListeners is null ? "TCP 목록을 읽지 못함" : portOwner,
            Took = _time.GetUtcNow() - started,
        };
    }

    /// <summary>Waits until no member (nor a new child of one) runs and the port is free, at most <paramref name="wait"/>.</summary>
    private async Task<bool> WaitGoneAsync(IReadOnlyCollection<BasaltProcess> members, int port, TimeSpan wait, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + wait;
        var known = members.Select(m => m.Id).ToHashSet();
        while (true)
        {
            var now = _system.Processes();
            var listeners = _system.Listeners(port);
            if (now is not null && listeners is { Count: 0 } && BasaltProcessTree.StillRunning(members, now).Count == 0
                && BasaltProcessTree.NewChildren(members, now, known, _selfId).Count == 0)
            {
                return true;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(_timings.FastPoll, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After the processes were ended: children started again by one of them (the Python server restarting the engine)
    /// are ended too, for <see cref="BasaltTimings.RespawnWatch"/> or until nothing new appeared for a while.
    /// </summary>
    private async Task WatchRespawnsAsync(List<BasaltProcess> everything, int port, List<string> ended, Dictionary<int, string> reasons, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + _timings.RespawnWatch;
        var quietSince = _time.GetUtcNow();
        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(_timings.FastPoll, _time, ct).ConfigureAwait(false);
            var now = _system.Processes();
            if (now is null)
            {
                return;
            }

            var born = BasaltProcessTree.NewChildren(everything, now, everything.Select(e => e.Id).ToHashSet(), _selfId);
            foreach (var process in born)
            {
                everything.Add(process);
                End(process, "다시 생긴 하위 프로세스", ended, reasons);
            }

            var listening = _system.Listeners(port) is { Count: > 0 };
            if (born.Count > 0 || listening)
            {
                quietSince = _time.GetUtcNow();
            }
            else if (_time.GetUtcNow() - quietSince >= _timings.RespawnQuiet)
            {
                return;
            }
        }
    }

    private void End(BasaltProcess process, string role, List<string> ended, Dictionary<int, string> reasons)
    {
        var result = _system.Terminate(process);
        switch (result)
        {
            case TerminateResult.Ended:
                ended.Add($"{process.Label} ({role})");
                break;
            case TerminateResult.AlreadyGone:
            case TerminateResult.IdentityChanged:
                break; // ended on its own, or the PID is another process now: nothing to do
            case TerminateResult.Unverified:
                reasons[process.Id] = "시작 시각을 확인할 수 없어 끝내지 않음";
                break;
            case TerminateResult.Denied:
                reasons[process.Id] = "Windows가 거부함 (권한 없음)";
                break;
            default:
                reasons[process.Id] = "끝내지 못함";
                break;
        }

        _log.Info("basalt", $"종료 {process.Label} ({role}): {result}");
    }

    private void PublishStopping(string detail, DateTimeOffset since, BasaltTree tree)
        => Publish(BasaltState.Stopping, detail, tree, null, since);

    private void Publish(BasaltState state, string detail, BasaltTree? tree, BasaltHealth? health, DateTimeOffset? since = null)
    {
        var startedHere = state is not BasaltState.Off and not BasaltState.PortBusy and not BasaltState.Unavailable
                          && (_oursNow > 0 || (state == BasaltState.Starting && _launch is not null));
        var status = new BasaltStatus
        {
            State = state,
            Port = Port,
            IsMock = _system.IsMock,
            Detail = detail,
            Mode = startedHere ? _launch!.Mode : null,
            StartedHere = startedHere,
            Since = since ?? (state == BasaltState.Starting ? _launch?.StartedAt : null),
            Tree = tree,
            Health = health,
            LastStop = _lastStop,
            Notice = _notice,
            CheckedAt = _time.GetUtcNow(),
        };
        lock (_statusGate)
        {
            _status = status;
        }

        try
        {
            Changed?.Invoke(status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("basalt", "상태 알림 처리 실패", ex);
        }
    }

    public static string ModeText(string? mode) => mode switch
    {
        BasaltSettings.QualityMode => "품질용",
        BasaltSettings.FastMode => "빠른 모드",
        _ => "알 수 없음",
    };

    public static string RoleText(TreeRole role) => role switch
    {
        TreeRole.Listener => "포트 소유",
        TreeRole.Wrapper => "basalt 래퍼",
        TreeRole.Descendant or TreeRole.WrapperDescendant or TreeRole.LauncherDescendant => "하위 프로세스",
        TreeRole.Launcher => "시작 스크립트 창",
        TreeRole.Named => "이름이 basalt",
        _ => role.ToString(),
    };

    private static string HealthText(BasaltHealth health)
        => health.StatusCode is int code ? $"HTTP {code}" : health.Error ?? "응답 없음";
}
