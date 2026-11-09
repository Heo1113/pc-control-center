using LocalAiPerf.Core.Diagnostics;
using PcControlCenter.Core;
using PcControlCenter.Operator;
using WindowsAiOperator.Core.Diagnostics;
using WindowsAiOperator.Core.Settings;
using WindowsAiOperator.Presentation;
using EAppPaths = WindowsAiOperator.Core.AppPaths;
using EDataFolderLock = WindowsAiOperator.Core.DataFolderLock;

namespace PcControlCenter.Modules;

public sealed record OperatorModuleOptions
{
    /// <summary>PcControlCenter.OperatorHost.exe next to PcControlCenter.exe (tests: the mock host .dll).</summary>
    public required string HostPath { get; init; }

    /// <summary>Simulated desktop in the host (tests, demos): nothing on the real screen is touched; shown as MOCK.</summary>
    public bool MockDesktop { get; init; }

    /// <summary>E's MOCK model profile is selected (tests, smoke runs without a model server).</summary>
    public bool MockModel { get; init; }

    /// <summary>E's secret store for API keys (DPAPI in the WPF app; in memory in tests).</summary>
    public required Func<EAppPaths, IOperatorLog, ISecretStore> SecretStore { get; init; }
}

/// <summary>
/// Project E in the shell. E's OperatorContext (settings, wao.db, policy engine, planner and model connection,
/// workflows, executor with approvals and journal) runs in the shell process exactly as in E. Everything that touches
/// the desktop (UI Automation, input, screen capture, starting programs) runs in the separate Operator host process
/// through <see cref="RemoteDesktop"/>. If the host is down, E's history, workflows, models and settings still work and
/// tasks fail visibly; the sensor, diagnostics and economics modules are not affected in any case.
/// </summary>
public sealed class OperatorModule(ModuleContext context, OperatorModuleOptions options) : ShellModuleBase(ModuleIds.Operator, context.Log, context.Time)
{
    private EDataFolderLock? _lock;
    private FileOperatorLog? _log;

    public EAppPaths? Paths { get; private set; }
    public OperatorHostSupervisor? Host { get; private set; }
    public RemoteDesktop? Desktop { get; private set; }
    public OperatorContext? Context { get; private set; }

    /// <summary>The live task state for the dashboard, provided by the UI layer that owns E's task runner.</summary>
    public Func<OperatorSummary>? SummaryProvider { get; set; }

    /// <summary>Raised after E's context was created, so the UI builds E's view models.</summary>
    public event Action? ContextReady;

    /// <summary>Raised when the host process state or the emergency latch changed.</summary>
    public event Action? HostChanged;

    protected override bool Enabled => context.Settings.IsEnabled(Id);

    protected override async Task OnStartAsync(CancellationToken cancellationToken)
    {
        var paths = EAppPaths.Resolve(context.Paths.ModuleDirectory(Id), context.ExecutableDirectory);
        paths.EnsureDirectories();
        _lock = EDataFolderLock.TryAcquire(paths.DataDirectory)
                ?? throw new InvalidOperationException("이 모듈의 데이터 폴더를 다른 프로세스(wao 명령 등)가 사용 중입니다.");
        _log = new FileOperatorLog(paths.LogDirectory);
        var host = new OperatorHostSupervisor(new OperatorHostOptions
        {
            HostPath = options.HostPath,
            LogDirectory = Path.Combine(paths.LogDirectory, "host"),
            MockDesktop = options.MockDesktop,
            MaxAutomaticRestarts = context.Settings.Operator.MaxAutomaticRestarts,
        }, _log);
        host.Changed += OnHostChanged;
        Host = host;
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        Paths = paths;
        Desktop = new RemoteDesktop(host, Environment.ProcessId, _log);
        Context = new OperatorContext(paths, Desktop, options.SecretStore(paths, _log), _log, options.MockModel);
        _log.Info("app", $"PC Control Center 모듈로 시작: 데스크톱 자동화는 별도 프로세스(PID {host.ProcessId?.ToString() ?? "-"})");
        ContextReady?.Invoke();
    }

    protected override async Task OnStopAsync()
    {
        Context?.Dispose();
        Context = null;
        if (Host is { } host)
        {
            host.Changed -= OnHostChanged;
            await host.DisposeAsync().ConfigureAwait(false);
        }

        Host = null;
        _log?.Dispose();
        _log = null;
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>
    /// Emergency stop of the desktop automation (the UI also stops E's running task and denies open approvals): the host
    /// cancels and latches, and is killed when input may be in flight. Nothing changes the screen until <see cref="ResumeAsync"/>.
    /// </summary>
    public Task EmergencyStopAsync() => Host?.EmergencyStopAsync() ?? Task.CompletedTask;

    public Task ResumeAsync() => Host?.ResumeAsync() ?? Task.CompletedTask;

    public Task RestartHostAsync() => Host?.RestartAsync() ?? Task.CompletedTask;

    public OperatorSummary Summary()
    {
        if (SummaryProvider is { } provider)
        {
            try
            {
                return provider();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLoggerExtensions.Warn(Log, "operator", "작업 상태 요약 실패", ex);
            }
        }

        return new OperatorSummary
        {
            StateText = Host is null ? Status.Summary : HostText(Host),
            HostConnected = Host?.State == HostState.Connected,
            IsMockDesktop = options.MockDesktop,
            Problem = Status.State == ModuleState.Failed ? Status.Summary : null,
        };
    }

    public static string HostText(OperatorHostSupervisor host) => host.State switch
    {
        HostState.Connected when host.EmergencyLatched => "긴급 중단 상태 — [재개] 전에는 화면을 조작하지 않음",
        HostState.Connected => $"실행 프로세스 연결됨 (PID {host.ProcessId?.ToString() ?? "-"})" +
                               (host.HostDeElevated ? " · 일반 사용자 권한으로 실행" : "") + (host.ExpectMock ? " · MOCK 데스크톱" : ""),
        HostState.Starting => "실행 프로세스 시작 중",
        HostState.Restarting => host.UnavailableReason,
        HostState.Failed => host.UnavailableReason,
        HostState.Stopped => "실행 프로세스 종료됨",
        _ => "실행 프로세스 시작 전",
    };

    protected override (bool Degraded, string Summary, string? Detail) Evaluate()
    {
        if (Host is not { } host)
        {
            return (true, "준비 중", null);
        }

        if (host.State != HostState.Connected || host.EmergencyLatched)
        {
            return (true, HostText(host), host.State == HostState.Connected ? null : "작업 기록·워크플로·모델·설정은 계속 쓸 수 있고, 화면 조작만 멈춥니다.");
        }

        if (Context?.Store.IsReadOnly == true)
        {
            return (true, "기록 데이터베이스가 읽기 전용입니다 (더 새 버전에서 만든 파일)", null);
        }

        return (false, HostText(host), null);
    }

    private void OnHostChanged()
    {
        RefreshStatus();
        HostChanged?.Invoke();
    }
}
