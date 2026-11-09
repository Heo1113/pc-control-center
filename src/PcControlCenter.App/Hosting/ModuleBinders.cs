using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LocalAiPerf.Core.Diagnostics;
using PcControlCenter.Core;
using PcControlCenter.Modules;
using PcControlCenter.Presentation;

namespace PcControlCenter.App.Hosting;

/// <summary>
/// Creates a module's own main view model (A, B or C) whenever its engine is ready, binds its pages into the shell's
/// menu and ticks it every second, exactly as the module's own App.xaml.cs and timer did. The module's own window is
/// not used. When the module stops or fails, the shell shows placeholders and the view model is dropped.
/// </summary>
internal abstract class ModuleBinder<TViewModel> : IDisposable
    where TViewModel : class, INotifyPropertyChanged
{
    private TViewModel? _viewModel;

    protected ModuleBinder(ShellViewModel shell, string moduleId, Dispatcher dispatcher, IAppLogger log)
    {
        Shell = shell;
        ModuleId = moduleId;
        Dispatcher = dispatcher;
        Log = log;
        shell.ModuleUnbound += OnUnbound;
    }

    protected ShellViewModel Shell { get; }
    protected string ModuleId { get; }
    protected Dispatcher Dispatcher { get; }
    protected IAppLogger Log { get; }

    public TViewModel? ViewModel => _viewModel;

    /// <summary>Raised on the UI thread after the module's view model was (re)created.</summary>
    public event Action<TViewModel>? Bound;

    /// <summary>The module's engine is ready (any thread): (re)build its view model on the UI thread.</summary>
    protected void RequestBind() => Dispatcher.BeginInvoke(BindNow);

    protected abstract TViewModel? Create();

    /// <summary>Shell page key → page view model of this module.</summary>
    protected abstract IEnumerable<(string TabKey, object Page)> Pages(TViewModel viewModel);

    protected abstract object CurrentPage(TViewModel viewModel);

    protected abstract void SetCurrentPage(TViewModel viewModel, object page);

    protected abstract void Tick(TViewModel viewModel);

    /// <summary>The header shown above this module's pages; by default the module's main view model (its status strip).</summary>
    protected virtual object Header(TViewModel viewModel) => viewModel;

    protected virtual void Dropped(TViewModel viewModel)
    {
    }

    public void Dispose()
    {
        Shell.ModuleUnbound -= OnUnbound;
        Drop();
    }

    private void BindNow()
    {
        Drop();
        TViewModel? vm;
        try
        {
            vm = Create();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("ui", $"{ModuleId}: 화면 준비 실패", ex);
            return;
        }

        if (vm is null)
        {
            return;
        }

        _viewModel = vm;
        foreach (var (key, page) in Pages(vm))
        {
            Shell.BindModulePage(key, page, Header(vm), () =>
            {
                if (!ReferenceEquals(CurrentPage(vm), page))
                {
                    SetCurrentPage(vm, page);
                }
            });
        }

        vm.PropertyChanged += OnViewModelChanged;
        Shell.AddTickHandler(TickCurrent);
        Bound?.Invoke(vm);
    }

    private void TickCurrent()
    {
        if (_viewModel is { } vm)
        {
            Tick(vm);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The module navigated by itself (for example C's "보기" on a notification): show that page in the shell.
        if (e.PropertyName == "CurrentPage" && _viewModel is { } vm)
        {
            Shell.FollowModulePage(CurrentPage(vm));
        }
    }

    private void OnUnbound(string moduleId)
    {
        if (moduleId == ModuleId)
        {
            Drop();
        }
    }

    private void Drop()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        _viewModel = null;
        vm.PropertyChanged -= OnViewModelChanged;
        Shell.RemoveTickHandler(TickCurrent);
        try
        {
            (vm as IDisposable)?.Dispose();
            Dropped(vm);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("ui", $"{ModuleId}: 화면 정리 중 오류", ex);
        }
    }
}

/// <summary>Project A's pages: hardware (live sensors and charts), Strata overview and runtime, sessions, comparison, settings.</summary>
internal sealed class LocalAiBinder : ModuleBinder<LocalAiPerf.Presentation.ViewModels.MainViewModel>
{
    private readonly LocalAiModule _module;

    public LocalAiBinder(ShellViewModel shell, LocalAiModule module, Dispatcher dispatcher, Func<Window?> owner, bool unattended, IAppLogger log)
        : base(shell, module.Id, dispatcher, log)
    {
        _module = module;
        Dialogs = new LocalAiPerf.App.Services.WpfDialogService(owner);
        if (unattended)
        {
            Dialogs.Unattended = true;
        }

        module.ServiceReady += RequestBind;
        if (module.Service is not null)
        {
            RequestBind();
        }
    }

    public LocalAiPerf.App.Services.WpfDialogService Dialogs { get; }

    protected override LocalAiPerf.Presentation.ViewModels.MainViewModel? Create()
    {
        if (_module.Service is not { } service || _module.SettingsStore is not { } store)
        {
            return null;
        }

        var vm = new LocalAiPerf.Presentation.ViewModels.MainViewModel(service, store, new LocalAiPerf.App.Services.WpfDispatcher(Dispatcher), Dialogs,
            new LocalAiPerf.App.Services.ShellService(), _module.LogSink);
        foreach (var note in _module.SettingsNotes)
        {
            vm.Notices.Add("설정: " + note);
        }

        return vm;
    }

    protected override IEnumerable<(string, object)> Pages(LocalAiPerf.Presentation.ViewModels.MainViewModel vm) =>
    [
        ("hardware/live", vm.Hardware),
        ("local-ai/overview", vm.Dashboard),
        ("local-ai/runtime", vm.Runtime),
        ("local-ai/history", vm.History),
        ("local-ai/compare", vm.Compare),
        ("settings/local-ai", vm.Settings),
    ];

    protected override object CurrentPage(LocalAiPerf.Presentation.ViewModels.MainViewModel vm) => vm.CurrentPage;

    protected override void SetCurrentPage(LocalAiPerf.Presentation.ViewModels.MainViewModel vm, object page)
        => vm.CurrentPage = (LocalAiPerf.Presentation.ViewModels.PageViewModel)page;

    protected override void Tick(LocalAiPerf.Presentation.ViewModels.MainViewModel vm) => vm.Tick();
}

/// <summary>Project B's pages: overview (power, cost, sessions), sessions, profitability, scenarios, history, comparison, devices, settings.</summary>
internal sealed class GpuEconomicsBinder : ModuleBinder<GpuProfit.Presentation.ViewModels.MainViewModel>, GpuProfit.Presentation.IAppHost
{
    private readonly GpuEconomicsModule _module;
    private readonly Action<double> _applyScale;

    public GpuEconomicsBinder(ShellViewModel shell, GpuEconomicsModule module, Dispatcher dispatcher, Func<Window?> owner, bool unattended,
        string unattendedFolder, Action<double> applyScale, IAppLogger log)
        : base(shell, module.Id, dispatcher, log)
    {
        _module = module;
        _applyScale = applyScale;
        Dialogs = new GpuProfit.App.Services.WpfDialogService(owner);
        if (unattended)
        {
            Dialogs.Unattended = true;
            Dialogs.UnattendedFolder = unattendedFolder;
        }

        module.EngineReady += RequestBind;
        if (module.Engine is not null)
        {
            RequestBind();
        }
    }

    public GpuProfit.App.Services.WpfDialogService Dialogs { get; }

    protected override GpuProfit.Presentation.ViewModels.MainViewModel? Create()
    {
        if (_module.Engine is not { } engine || _module.SettingsStore is not { } store)
        {
            return null;
        }

        var vm = new GpuProfit.Presentation.ViewModels.MainViewModel(new GpuProfit.Presentation.ViewModels.ViewContext
        {
            Engine = engine,
            SettingsStore = store,
            Ui = new GpuProfit.App.Services.WpfDispatcher(Dispatcher),
            Dialogs = Dialogs,
            Shell = new GpuProfit.App.Services.ShellService(),
            Host = this,
            Log = _module.LogSink ?? (IAppLogger)NullLogger.Instance,
        });
        foreach (var note in _module.SettingsNotes)
        {
            vm.Notices.Add("설정: " + note);
        }

        return vm;
    }

    protected override IEnumerable<(string, object)> Pages(GpuProfit.Presentation.ViewModels.MainViewModel vm) =>
    [
        ("hardware/gpu-power", vm.Hardware),
        ("economics/overview", vm.Dashboard),
        ("economics/sessions", vm.Sessions),
        ("economics/profitability", vm.Profitability),
        ("economics/scenarios", vm.Scenarios),
        ("economics/history", vm.History),
        ("economics/compare", vm.Compare),
        ("settings/economics", vm.Settings),
    ];

    protected override object CurrentPage(GpuProfit.Presentation.ViewModels.MainViewModel vm) => vm.CurrentPage;

    protected override void SetCurrentPage(GpuProfit.Presentation.ViewModels.MainViewModel vm, object page)
        => vm.CurrentPage = (GpuProfit.Presentation.ViewModels.PageViewModel)page;

    protected override void Tick(GpuProfit.Presentation.ViewModels.MainViewModel vm) => vm.Tick();

    /// <summary>
    /// B's restore inside the integrated app: only B stops; gpc.db is replaced (the current file kept aside) and B starts
    /// again with new view models. The original program restarted itself instead.
    /// </summary>
    public async Task RestoreAndRestartAsync(string backupPath)
    {
        Shell.UnbindModule(ModuleId);
        try
        {
            var aside = await _module.RestoreAsync(backupPath);
            Dialogs.Info("복원 완료", $"GPU 경제성 데이터를 복원했습니다. 이전 데이터베이스는 {(aside is null ? "없었습니다" : $"'{Path.GetFileName(aside)}'로 보관했습니다")}. 다른 모듈은 계속 동작했습니다.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Dialogs.Error("복원 실패", ex.Message);
        }
    }

    /// <summary>B's font-scale setting applies to the whole integrated window (as it did to B's window).</summary>
    public void ApplyFontScale(double scale) => _applyScale(scale);

    public void NotifySuspend() => _module.NotifySuspend();

    public void NotifyResume() => _module.NotifyResume();
}

/// <summary>Project C's pages: overview, events, timeline, diagnosis, baseline comparison, sensors around events, settings.</summary>
internal sealed class DiagnosticsBinder : ModuleBinder<WinDiag.Presentation.ViewModels.MainViewModel>, WinDiag.Presentation.IAppHost
{
    private readonly DiagnosticsModule _module;

    public DiagnosticsBinder(ShellViewModel shell, DiagnosticsModule module, Dispatcher dispatcher, Func<Window?> owner, bool unattended,
        string unattendedFolder, IAppLogger log)
        : base(shell, module.Id, dispatcher, log)
    {
        _module = module;
        Dialogs = new WinDiag.App.Services.WpfDialogService(owner);
        if (unattended)
        {
            Dialogs.Unattended = true;
            Dialogs.UnattendedFolder = unattendedFolder;
        }

        module.EngineReady += RequestBind;
        if (module.Engine is not null)
        {
            RequestBind();
        }
    }

    public WinDiag.App.Services.WpfDialogService Dialogs { get; }

    protected override WinDiag.Presentation.ViewModels.MainViewModel? Create()
    {
        if (_module.Engine is not { } engine || _module.SettingsStore is not { } store)
        {
            return null;
        }

        var vm = new WinDiag.Presentation.ViewModels.MainViewModel(new WinDiag.Presentation.ViewModels.ViewContext
        {
            Engine = engine,
            SettingsStore = store,
            Ui = new WinDiag.App.Services.WpfDispatcher(Dispatcher),
            Dialogs = Dialogs,
            Shell = new WinDiag.App.Services.ShellService(),
            Host = this,
            Log = _module.LogSink ?? (IAppLogger)NullLogger.Instance,
        });
        foreach (var note in _module.SettingsNotes)
        {
            vm.Notices.Add("설정: " + note);
        }

        return vm;
    }

    protected override IEnumerable<(string, object)> Pages(WinDiag.Presentation.ViewModels.MainViewModel vm) =>
    [
        ("hardware/around-events", vm.Hardware),
        ("diagnostics/overview", vm.Dashboard),
        ("diagnostics/events", vm.Events),
        ("diagnostics/timeline", vm.Timeline),
        ("diagnostics/analysis", vm.Diagnostics),
        ("diagnostics/compare", vm.Compare),
        ("settings/diagnostics", vm.Settings),
    ];

    protected override object CurrentPage(WinDiag.Presentation.ViewModels.MainViewModel vm) => vm.CurrentPage;

    protected override void SetCurrentPage(WinDiag.Presentation.ViewModels.MainViewModel vm, object page)
        => vm.CurrentPage = (WinDiag.Presentation.ViewModels.PageViewModel)page;

    protected override void Tick(WinDiag.Presentation.ViewModels.MainViewModel vm) => vm.Tick();

    /// <summary>C's restore inside the integrated app: only C stops and restarts.</summary>
    public async Task RestoreAndRestartAsync(string backupPath)
    {
        Shell.UnbindModule(ModuleId);
        try
        {
            var aside = await _module.RestoreAsync(backupPath);
            Dialogs.Info("복원 완료", $"시스템 진단 데이터를 복원했습니다. 이전 데이터베이스는 {(aside is null ? "없었습니다" : $"'{Path.GetFileName(aside)}'로 보관했습니다")}. 다른 모듈은 계속 동작했습니다.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Dialogs.Error("복원 실패", ex.Message);
        }
    }
}

/// <summary>E's status strip plus the shell's Operator controls (host process state, emergency stop, resume, host restart).</summary>
public sealed record OperatorHeader(WindowsAiOperator.Presentation.ViewModels.MainViewModel Main, OperatorControlViewModel Controls);

/// <summary>
/// Project E's pages: overview, task runner (request, plan review, live steps, approvals, pause/resume/stop), window
/// inspector, workflows, history, model connection, safety settings, settings. E's view models run unchanged on E's
/// OperatorContext; its desktop is the remote one, so every screen action happens in the Operator host process.
/// </summary>
internal sealed class OperatorBinder : ModuleBinder<WindowsAiOperator.Presentation.ViewModels.MainViewModel>
{
    private readonly OperatorModule _module;
    private readonly OperatorControlViewModel _controls;

    public OperatorBinder(ShellViewModel shell, OperatorModule module, OperatorControlViewModel controls, Dispatcher dispatcher, IAppLogger log)
        : base(shell, module.Id, dispatcher, log)
    {
        _module = module;
        _controls = controls;
        Presenter = new WindowsAiOperator.App.Services.ApprovalPresenter();
        module.ContextReady += RequestBind;
        if (module.Context is not null)
        {
            RequestBind();
        }
    }

    /// <summary>E's approval and plan-review windows (the smoke test answers through its Shown event).</summary>
    public WindowsAiOperator.App.Services.ApprovalPresenter Presenter { get; }

    protected override WindowsAiOperator.Presentation.ViewModels.MainViewModel? Create()
    {
        if (_module.Context is not { } context)
        {
            return null;
        }

        var vm = new WindowsAiOperator.Presentation.ViewModels.MainViewModel(context, new WindowsAiOperator.App.Services.WpfDispatcher(Dispatcher),
            new WindowsAiOperator.App.Services.WpfDialogService(), Presenter, new WindowsAiOperator.App.Services.OverlayHighlighter(),
            new WindowsAiOperator.App.Services.AppHost(context.Log));
        _controls.StopRunningTask = vm.EmergencyStop;
        _module.SummaryProvider = () => Summarize(vm, context);
        return vm;
    }

    protected override void Dropped(WindowsAiOperator.Presentation.ViewModels.MainViewModel viewModel)
    {
        viewModel.Runner.StopAll("Operator 모듈 종료");
        _controls.StopRunningTask = null;
        _module.SummaryProvider = null;
    }

    protected override IEnumerable<(string, object)> Pages(WindowsAiOperator.Presentation.ViewModels.MainViewModel vm) =>
    [
        ("operator/overview", vm.Dashboard),
        ("operator/run", vm.Runner),
        ("operator/inspector", vm.Inspector),
        ("operator/workflows", vm.Workflows),
        ("operator/history", vm.History),
        ("operator/models", vm.Models),
        ("settings/operator-safety", vm.Safety),
        ("settings/operator", vm.Settings),
    ];

    protected override object Header(WindowsAiOperator.Presentation.ViewModels.MainViewModel viewModel) => new OperatorHeader(viewModel, _controls);

    protected override object CurrentPage(WindowsAiOperator.Presentation.ViewModels.MainViewModel vm) => vm.CurrentPage;

    protected override void SetCurrentPage(WindowsAiOperator.Presentation.ViewModels.MainViewModel vm, object page)
        => vm.CurrentPage = (WindowsAiOperator.Presentation.ViewModels.PageViewModel)page;

    /// <summary>E's pages refresh themselves; the shell has nothing to tick.</summary>
    protected override void Tick(WindowsAiOperator.Presentation.ViewModels.MainViewModel vm)
    {
    }

    private OperatorSummary Summarize(WindowsAiOperator.Presentation.ViewModels.MainViewModel vm, WindowsAiOperator.Presentation.OperatorContext context)
    {
        var runner = vm.Runner;
        var host = _module.Host;
        var last = runner.LastRun;
        return new OperatorSummary
        {
            StateText = host is null ? _module.Status.Summary : OperatorModule.HostText(host),
            HostConnected = host?.State == PcControlCenter.Operator.HostState.Connected,
            IsMockDesktop = context.Desktop.IsMock,
            CurrentTask = runner.IsRunning ? (string.IsNullOrWhiteSpace(runner.RequestText) ? runner.StatusText : runner.RequestText) : null,
            CurrentStep = runner.IsRunning ? runner.Progress : null,
            AwaitingApproval = runner.PendingApproval is not null || runner.PendingPlanReview is not null,
            IsPaused = runner.IsPaused,
            LastResult = last is null ? null : $"최근 작업: {last.Status} — {last.StatusMessage}",
            Model = context.Settings.ActiveProfile is { } profile ? $"{profile.Name} ({profile.Model})" : null,
        };
    }
}
