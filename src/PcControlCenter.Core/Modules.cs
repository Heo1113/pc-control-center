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
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private ModuleStatus _status = new(id, ModuleState.NotStarted, "시작 전", (time ?? TimeProvider.System).GetUtcNow());

    public string Id { get; } = id;
    protected IAppLogger Log { get; } = log;

    public static TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public static TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(15);

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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
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
            _ = start.ContinueWith(_ => SafeStopAsync(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("module", $"{Id}: 시작 실패", ex);
            SetStatus(ModuleState.Failed, "시작하지 못했습니다: " + SecretRedactor.Redact(ex.Message), ex.GetType().Name);
            await SafeStopAsync().ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        if (Status.State is ModuleState.Disabled or ModuleState.NotStarted or ModuleState.Stopped)
        {
            return;
        }

        var failed = Status.State == ModuleState.Failed;
        SetStatus(ModuleState.Stopping, "종료 중");
        await SafeStopAsync().ConfigureAwait(false);
        SetStatus(failed ? ModuleState.Failed : ModuleState.Stopped, failed ? "실패 후 정리됨" : "중지됨");
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        SetStatus(ModuleState.NotStarted, "다시 시작 준비");
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-evaluates Running/Degraded from the module's own state (call after its state changed).</summary>
    public void RefreshStatus()
    {
        if (Status.State is ModuleState.Starting or ModuleState.Running or ModuleState.Degraded)
        {
            var (degraded, summary, detail) = Evaluate();
            SetStatus(degraded ? ModuleState.Degraded : ModuleState.Running, summary, detail);
        }
    }

    /// <summary>Marks the module failed while it runs (for example its engine reported a fatal storage error).</summary>
    protected void ReportFailure(string summary, string? detail = null) => SetStatus(ModuleState.Failed, summary, detail);

    protected abstract Task OnStartAsync(CancellationToken cancellationToken);

    protected abstract Task OnStopAsync();

    /// <summary>The module's own view of whether it runs with limitations.</summary>
    protected virtual (bool Degraded, string Summary, string? Detail) Evaluate() => (false, "정상", null);

    protected void SetStatus(ModuleState state, string summary, string? detail = null)
    {
        ModuleStatus status;
        lock (_gate)
        {
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

    private async Task SafeStopAsync()
    {
        try
        {
            await OnStopAsync().WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Error("module", $"{Id}: {StopTimeout.TotalSeconds:0}초 안에 종료하지 못했습니다 (다음 실행에서 복구됩니다).");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("module", $"{Id}: 종료 중 오류 (다음 실행에서 복구됩니다)", ex);
        }
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
