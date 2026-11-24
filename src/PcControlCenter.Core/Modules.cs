using LocalAiPerf.Core.Diagnostics;

namespace PcControlCenter.Core;

public enum ModuleState
{
    /// <summary>Turned off in the shell settings.</summary>
    Disabled,
    NotStarted,
    Starting,
    Running,

    /// <summary>Running with a limitation (for example no database, event log not readable, Strata not reachable).</summary>
    Degraded,

    /// <summary>Could not start or stopped working. The other modules keep running.</summary>
    Failed,
    Stopping,
    Stopped,
}

public static class ModuleStateText
{
    public static string ToKorean(ModuleState state) => state switch
    {
        ModuleState.Disabled => "꺼짐",
        ModuleState.NotStarted => "시작 전",
        ModuleState.Starting => "시작 중",
        ModuleState.Running => "정상",
        ModuleState.Degraded => "제한됨",
        ModuleState.Failed => "실패",
        ModuleState.Stopping => "종료 중",
        ModuleState.Stopped => "중지됨",
        _ => state.ToString(),
    };
}

public sealed record ModuleStatus(string ModuleId, ModuleState State, string Summary, DateTimeOffset Since, string? Detail = null)
{
    public bool IsUsable => State is ModuleState.Running or ModuleState.Degraded;
}

/// <summary>One integrated program (A, B, C or E) as the shell sees it.</summary>
public interface IShellModule
{
    string Id { get; }
    ModuleStatus Status { get; }

    /// <summary>Raised on any thread when <see cref="Status"/> changes.</summary>
    event Action<ModuleStatus>? StatusChanged;

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

/// <summary>
/// Start/stop with fault isolation: an exception or a hang in one module ends as that module's Failed state and is
/// logged; it never propagates to the shell or to another module.
/// </summary>
public abstract class ShellModuleBase(string id, IAppLogger log, TimeProvider? time = null) : IShellModule
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Task? _pendingStop;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private ModuleStatus _status = new(id, ModuleState.NotStarted, "시작 전", (time ?? TimeProvider.System).GetUtcNow());

    public string Id { get; } = id;
    protected IAppLogger Log { get; } = log;

    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Per module (not shared): a module that has not started after this long is marked failed and cleaned up.</summary>
    public TimeSpan StartTimeout { get; init; } = DefaultStartTimeout;

    public TimeSpan StopTimeout { get; init; } = DefaultStopTimeout;

    public ModuleStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public event Action<ModuleStatus>? StatusChanged;

    /// <summary>False when the module is turned off; it then stays <see cref="ModuleState.Disabled"/>.</summary>
    protected virtual bool Enabled => true;

    public Task StartAsync(CancellationToken cancellationToken)
        => WithLifecycleAsync(async (_, start) => { await start(cancellationToken).ConfigureAwait(false); return true; });

    public Task StopAsync() => WithLifecycleAsync((stop, _) => stop());

    public async Task<T> WithLifecycleAsync<T>(Func<Func<Task<bool>>, Func<CancellationToken, Task>, Task<T>> operation)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            return await operation(StopCoreAsync, StartCoreAsync).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_pendingStop is not null && !await SafeStopAsync().ConfigureAwait(false))
        {
            SetStatus(ModuleState.Failed, "이전 모듈 정리가 끝나지 않아 시작하지 못했습니다.");
            return;
        }

        if (!Enabled)
        {
            SetStatus(ModuleState.Disabled, "설정에서 꺼져 있습니다.");
            return;
        }

        if (Status.State is ModuleState.Starting or ModuleState.Running or ModuleState.Degraded)
        {
            return;
        }

        SetStatus(ModuleState.Starting, "시작 중");
        var start = Task.Run(() => OnStartAsync(cancellationToken), cancellationToken);
        try
        {
            await start.WaitAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
            RefreshStatus();
        }
        catch (TimeoutException)
        {
            Log.Error("module", $"{Id}: {StartTimeout.TotalSeconds:0}초 안에 시작하지 못했습니다.");
            SetStatus(ModuleState.Failed, $"{StartTimeout.TotalSeconds:0}초 안에 시작하지 못했습니다.", "다른 모듈은 계속 동작합니다.");

            // If the hung start finishes later, release what it opened instead of leaving a half-started module running.
            _pendingStop = StopAfterStartAsync(start);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("module", $"{Id}: 시작 실패", ex);
            SetStatus(ModuleState.Failed, "시작하지 못했습니다: " + SecretRedactor.Redact(ex.Message), ex.GetType().Name);
            if (!start.IsCompleted)
            {
                _pendingStop = StopAfterStartAsync(start);
            }
            await SafeStopAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> StopCoreAsync()
    {
        if (Status.State is ModuleState.Disabled or ModuleState.NotStarted or ModuleState.Stopped)
        {
            return true;
        }

        var failed = Status.State == ModuleState.Failed;
        SetStatus(ModuleState.Stopping, "종료 중");
        if (!await SafeStopAsync().ConfigureAwait(false))
        {
            SetStatus(ModuleState.Failed, "모듈 정지가 완료되지 않았습니다.");
            return false;
        }

        SetStatus(failed ? ModuleState.Failed : ModuleState.Stopped, failed ? "실패 후 정리됨" : "중지됨");
        return true;
    }

    public Task RestartAsync(CancellationToken cancellationToken = default)
        => WithLifecycleAsync(async (stop, start) =>
        {
            if (!await stop().ConfigureAwait(false))
            {
                return false;
            }

            SetStatus(ModuleState.NotStarted, "다시 시작 준비");
            await start(cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <summary>Re-evaluates Running/Degraded from the module's own state (call after its state changed).</summary>
    public void RefreshStatus()
    {
        if (Status.State is ModuleState.Starting or ModuleState.Running or ModuleState.Degraded)
        {
            var (degraded, summary, detail) = Evaluate();
            SetStatus(degraded ? ModuleState.Degraded : ModuleState.Running, summary, detail, onlyWhenActive: true);
        }
    }

    /// <summary>Marks the module failed while it runs (for example its engine reported a fatal storage error).</summary>
    protected void ReportFailure(string summary, string? detail = null) => SetStatus(ModuleState.Failed, summary, detail);

    protected abstract Task OnStartAsync(CancellationToken cancellationToken);

    protected abstract Task OnStopAsync();

    /// <summary>The module's own view of whether it runs with limitations.</summary>
    protected virtual (bool Degraded, string Summary, string? Detail) Evaluate() => (false, "정상", null);

    protected void SetStatus(ModuleState state, string summary, string? detail = null, bool onlyWhenActive = false)
    {
        ModuleStatus status;
        lock (_gate)
        {
            if (onlyWhenActive && _status.State is not (ModuleState.Starting or ModuleState.Running or ModuleState.Degraded)) return;
            if (_status.State == state && _status.Summary == summary && _status.Detail == detail)
            {
                return;
            }

            status = new ModuleStatus(Id, state, summary, _time.GetUtcNow(), detail);
            _status = status;
        }

        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("module", $"{Id}: 상태 처리기 예외", ex);
        }
    }

    private async Task StopAfterStartAsync(Task start)
    {
        try { await start.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warn("module", $"{Id}: 지연된 시작 실패", ex); }
        await OnStopAsync().ConfigureAwait(false);
    }

    private async Task<bool> SafeStopAsync()
    {
        try
        {
            _pendingStop ??= Task.Run(OnStopAsync);
            await _pendingStop.WaitAsync(StopTimeout).ConfigureAwait(false);
            _pendingStop = null;
            return true;
        }
        catch (TimeoutException)
        {
            Log.Error("module", $"{Id}: {StopTimeout.TotalSeconds:0}초 안에 종료하지 못했습니다 (다음 실행에서 복구됩니다).");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _pendingStop = null;
            Log.Error("module", $"{Id}: 종료 중 오류", ex);
        }

        return false;
    }
}

/// <summary>Starts and stops all modules independently and in parallel.</summary>
public sealed class ModuleSupervisor(IAppLogger log)
{
    private readonly List<IShellModule> _modules = [];

    public IReadOnlyList<IShellModule> Modules => _modules;

    public event Action<ModuleStatus>? StatusChanged;

    public T Add<T>(T module)
        where T : IShellModule
    {
        _modules.Add(module);
        module.StatusChanged += s => StatusChanged?.Invoke(s);
        return module;
    }

    public IShellModule? Find(string id) => _modules.FirstOrDefault(m => m.Id == id);

    /// <summary>Each module starts on its own; one failing or hanging does not delay or stop the others.</summary>
    public Task StartAllAsync(CancellationToken cancellationToken = default)
        => Task.WhenAll(_modules.Select(m => StartIsolatedAsync(m, cancellationToken)));

    public Task StopAllAsync() => Task.WhenAll(_modules.Select(StopIsolatedAsync));

    private async Task StartIsolatedAsync(IShellModule module, CancellationToken cancellationToken)
    {
        try
        {
            await module.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Error("supervisor", $"{module.Id} 시작 처리 중 예외", ex);
        }
    }

    private async Task StopIsolatedAsync(IShellModule module)
    {
        try
        {
            await module.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Error("supervisor", $"{module.Id} 종료 처리 중 예외", ex);
        }
    }
}
