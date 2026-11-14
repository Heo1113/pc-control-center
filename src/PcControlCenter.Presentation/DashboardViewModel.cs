using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Runtime;
using PcControlCenter.Core;
using PcControlCenter.GpuMemory;
using PcControlCenter.Hardware;
using PcControlCenter.Modules;
using WinDiag.Core.Events;

namespace PcControlCenter.Presentation;

public sealed record EventLine(string Time, string Title, string Category, string Severity, Level Level, bool IsVirtual);

/// <summary>A process in the dashboard's GPU memory card: its name, measured dedicated memory and (several GPUs) which one.</summary>
public sealed record GpuMemoryTopRow(string Name, string Value, string? Adapter)
{
    public string AdapterSuffix => Adapter is { Length: > 0 } a ? $" · {a}" : "";
}

/// <summary>
/// The integrated dashboard: hardware (shared reader), Strata (A), power and electricity cost (B), recent Windows
/// errors (C) and the Operator (E). Each card names its source; measured, engine-reported, estimated and MOCK values
/// are labelled as such and never mixed into one number.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private static readonly TimeSpan EconomicsRefresh = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DiagnosticsRefresh = TimeSpan.FromSeconds(30);

    private readonly SharedHardwareHub _hub;
    private readonly LocalAiModule? _localAi;
    private readonly GpuEconomicsModule? _economics;
    private readonly DiagnosticsModule? _diagnostics;
    private readonly Func<OperatorSummary?> _operator;
    private readonly IShellDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Action<string> _navigate;
    private readonly GpuMemoryMonitor? _gpuMemory;
    private DateTimeOffset _lastEconomics = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDiagnostics = DateTimeOffset.MinValue;
    private int _economicsBusy;
    private int _diagnosticsBusy;

    /// <param name="gpuMemory">The per-process GPU memory reader, for the card with the largest processes; null leaves the card empty.</param>
    public DashboardViewModel(SharedHardwareHub hub, LocalAiModule? localAi, GpuEconomicsModule? economics, DiagnosticsModule? diagnostics,
        Func<OperatorSummary?> operatorSummary, IShellDispatcher ui, Action<string> navigate, TimeProvider? time = null, GpuMemoryMonitor? gpuMemory = null)
    {
        _hub = hub;
        _localAi = localAi;
        _economics = economics;
        _diagnostics = diagnostics;
        _operator = operatorSummary;
        _ui = ui;
        _navigate = navigate;
        _time = time ?? TimeProvider.System;
        _gpuMemory = gpuMemory;
        IsMock = hub.IsMock;
    }

    public bool IsMock { get; }

    /// <summary>True while the dashboard is visible (only then are module databases queried).</summary>
    public bool IsActive { get; set; }

    // Hardware (shared reader)
    [ObservableProperty] public partial string GpuName { get; set; } = "GPU";
    [ObservableProperty] public partial string GpuUtil { get; set; } = Text.NA;
    [ObservableProperty] public partial string GpuTemp { get; set; } = Text.NA;
    [ObservableProperty] public partial string GpuPower { get; set; } = Text.NA;
    [ObservableProperty] public partial string GpuVram { get; set; } = Text.NA;
    [ObservableProperty] public partial string GpuClock { get; set; } = Text.NA;
    [ObservableProperty] public partial string CpuUtil { get; set; } = Text.NA;
    [ObservableProperty] public partial string RamUsed { get; set; } = Text.NA;
    [ObservableProperty] public partial string SensorSource { get; set; } = "";
    [ObservableProperty] public partial string SensorMeasured { get; set; } = "측정값 기다리는 중";
    [ObservableProperty] public partial Level SensorLevel { get; set; } = Level.Muted;
    public ObservableCollection<string> SensorIssues { get; } = [];

    // Local AI (A)
    [ObservableProperty] public partial string StrataState { get; set; } = Text.NA;
    [ObservableProperty] public partial Level StrataLevel { get; set; } = Level.Muted;
    [ObservableProperty] public partial string StrataModel { get; set; } = Text.NA;
    [ObservableProperty] public partial string StrataEngine { get; set; } = Text.NA;
    [ObservableProperty] public partial string AnswerSpeed { get; set; } = Text.NA;
    [ObservableProperty] public partial string PrefillSpeed { get; set; } = Text.NA;
    [ObservableProperty] public partial string StrataRequests { get; set; } = Text.NA;
    [ObservableProperty] public partial string StrataContext { get; set; } = Text.NA;
    [ObservableProperty] public partial string LocalAiRecording { get; set; } = Text.NA;
    [ObservableProperty] public partial string StrataNote { get; set; } = "";

    // Power and cost (B)
    [ObservableProperty] public partial string BoardPower { get; set; } = Text.NA;
    [ObservableProperty] public partial string BoardPowerSource { get; set; } = "";
    [ObservableProperty] public partial string CostPerHour { get; set; } = Text.NA;
    [ObservableProperty] public partial string RateText { get; set; } = Text.NA;
    [ObservableProperty] public partial string TodayEnergy { get; set; } = Text.NA;
    [ObservableProperty] public partial string TodayCost { get; set; } = Text.NA;
    [ObservableProperty] public partial string TodayNote { get; set; } = "";
    [ObservableProperty] public partial string EconomicsSessions { get; set; } = Text.NA;
    [ObservableProperty] public partial string EconomicsNote { get; set; } = "";

    public string BoardPowerNote => GpuProfit.Presentation.ViewModels.DashboardViewModel.BoardPowerNote;

    // Diagnostics (C)
    [ObservableProperty] public partial string Errors24h { get; set; } = Text.NA;
    [ObservableProperty] public partial string Critical24h { get; set; } = Text.NA;
    [ObservableProperty] public partial string HardwareErrors7d { get; set; } = Text.NA;
    [ObservableProperty] public partial string Crashes7d { get; set; } = Text.NA;
    [ObservableProperty] public partial Level DiagnosticsLevel { get; set; } = Level.Muted;
    [ObservableProperty] public partial string DiagnosticsNote { get; set; } = "";
    public ObservableCollection<EventLine> RecentErrors { get; } = [];

    // Operator (E)
    [ObservableProperty] public partial string OperatorState { get; set; } = Text.NA;
    [ObservableProperty] public partial Level OperatorLevel { get; set; } = Level.Muted;
    [ObservableProperty] public partial string OperatorTask { get; set; } = "-";
    [ObservableProperty] public partial string OperatorNote { get; set; } = "";

    public ObservableCollection<ModuleStatusRow> Modules { get; } = [];

    // GPU memory (the per-process reader): the processes holding the most dedicated memory, measured; never summed into a total.
    [ObservableProperty] public partial Level GpuMemoryLevel { get; set; } = Level.Muted;

    /// <summary>The largest process's name, or the reader's state (waiting, switched off, unavailable, no new reading).</summary>
    [ObservableProperty] public partial string GpuMemoryState { get; set; } = "측정 대기";
    [ObservableProperty] public partial string GpuMemoryLargest { get; set; } = Text.NA;

    /// <summary>The next largest processes (up to three).</summary>
    public ObservableCollection<GpuMemoryTopRow> GpuMemoryTop { get; } = [];
    [ObservableProperty] public partial string GpuMemoryNote { get; set; } = "";

    [ObservableProperty] public partial string UpdatedAt { get; set; } = Text.NA;

    /// <summary>Called every second on the UI thread.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        UpdateHardware(now);
        UpdateGpuMemory(now);
        UpdateLocalAi();
        UpdateOperator();
        UpdatedAt = Text.Clock(now);
        if (!IsActive)
        {
            return;
        }

        if (now - _lastEconomics >= EconomicsRefresh)
        {
            _lastEconomics = now;
            RefreshEconomics(now);
        }

        if (now - _lastDiagnostics >= DiagnosticsRefresh)
        {
            _lastDiagnostics = now;
            RefreshDiagnostics(now);
        }
    }

    /// <summary>Forces the database-backed cards to reload at the next tick (dashboard shown, data changed).</summary>
    public void Invalidate()
    {
        _lastEconomics = DateTimeOffset.MinValue;
        _lastDiagnostics = DateTimeOffset.MinValue;
    }

    [RelayCommand]
    private void Open(string? sectionKey)
    {
        if (sectionKey is not null)
        {
            _navigate(sectionKey);
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        Invalidate();
        Tick();
    }

    /// <summary>The largest processes by measured dedicated memory, from the reader's latest reading (it reads every 10 s off the page).</summary>
    private void UpdateGpuMemory(DateTimeOffset now)
    {
        var health = _gpuMemory?.Health;
        var latest = _gpuMemory?.Latest;
        var state = health?.State switch
        {
            null => "사용하지 않음",
            GpuMemoryMonitorState.Disabled => "꺼져 있음",
            GpuMemoryMonitorState.Unavailable => "사용 불가",
            _ when latest is null => "측정 대기",
            _ when now - latest.Timestamp > GpuMemoryViewModel.StaleAfter(latest) => $"새 측정 없음 · 마지막 {Text.Clock(latest.Timestamp)}",
            _ => null,
        };
        if (state is not null || latest is null || health is null)
        {
            // No reading to show as current: nothing old is shown in its place.
            GpuMemoryState = state ?? "측정 대기";
            GpuMemoryLargest = Text.NA;
            SyncTop([]);
            GpuMemoryLevel = health?.State switch
            {
                GpuMemoryMonitorState.Unavailable => Level.Warning,
                _ when latest is not null && health?.State is not GpuMemoryMonitorState.Disabled => Level.Error,
                _ => Level.Muted,
            };
            GpuMemoryNote = health?.State switch
            {
                GpuMemoryMonitorState.Disabled => "설정 › 공통 › 공통 센서에서 켤 수 있습니다.",
                GpuMemoryMonitorState.Unavailable => health.UnavailableReason ?? "",
                _ => "",
            };
            return;
        }

        var listed = latest.Processes.Where(p => p.DedicatedBytes >= 1L << 20).OrderByDescending(p => p.DedicatedBytes).ThenBy(p => p.ProcessId).ToList();
        var severalGpus = listed.Select(p => p.Luid).Distinct().Count() > 1;
        var top = listed.Take(4).Select(p => new GpuMemoryTopRow(latest.Process(p.ProcessId)?.Name ?? $"PID {p.ProcessId}", Text.Bytes(p.DedicatedBytes),
            severalGpus && latest.Adapter(p.Luid)?.Info?.Name is { Length: > 0 } adapter ? GpuMemoryViewModel.ShortAdapterName(adapter) : null)).ToList();
        GpuMemoryState = top.Count > 0 ? top[0].Name + top[0].AdapterSuffix : "전용 메모리를 1 MiB 이상 쓰는 프로세스 없음";
        GpuMemoryLargest = top.Count > 0 ? top[0].Value : Text.NA;
        SyncTop(top.Skip(1).ToList());
        GpuMemoryLevel = latest.IsMock || latest.Variant != ProcessCounterVariant.LocalNonLocal || latest.Issues.Count > 0 ? Level.Warning : Level.Good;
        GpuMemoryNote = (latest.IsMock ? "MOCK 가상 값 — 실제 측정이 아닙니다. " : "")
                        + $"프로세스별 전용 GPU 메모리 (Windows 카운터) · 측정 {Text.Clock(latest.Timestamp)} · {health.Interval.TotalSeconds:0.#}초마다";
    }

    private void SyncTop(IReadOnlyList<GpuMemoryTopRow> rows)
    {
        if (rows.SequenceEqual(GpuMemoryTop))
        {
            return;
        }

        GpuMemoryTop.Clear();
        foreach (var row in rows)
        {
            GpuMemoryTop.Add(row);
        }
    }

    private void UpdateHardware(DateTimeOffset now)
    {
        var latest = _hub.Latest?.Snapshot;
        if (latest is null)
        {
            SensorMeasured = "측정값 기다리는 중";
            SensorLevel = Level.Muted;
            return;
        }

        var gpu = latest.Gpus.FirstOrDefault();
        GpuName = gpu?.Name ?? _hub.Info.Gpus.FirstOrDefault()?.Name ?? "GPU";
        GpuUtil = Text.Percent(gpu?.UtilizationPercent);
        GpuTemp = Text.Celsius(gpu?.TemperatureC);
        GpuPower = gpu?.PowerW is double p ? $"{Text.Num(p, "0", "W")} / {Text.Num(gpu.PowerLimitW, "0", "W")}" : Text.NA;
        GpuVram = Text.GibPair(gpu?.MemoryUsedBytes, gpu?.MemoryTotalBytes);
        GpuClock = gpu is null ? Text.NA : $"{Text.Mhz(gpu.GraphicsClockMhz)} / {Text.Mhz(gpu.MemoryClockMhz)}";
        CpuUtil = Text.Percent(latest.System.CpuUtilizationPercent);
        RamUsed = Text.GibPair(latest.System.MemoryUsedBytes, latest.System.MemoryTotalBytes);
        SensorSource = latest.IsMock
            ? "MOCK 가상 값 — 실제 하드웨어 측정이 아닙니다"
            : gpu is null
                ? $"GPU N/A: {_hub.GpuUnavailableReason ?? "GPU 값을 읽지 못했습니다"} · CPU·RAM: {_hub.SystemProviderName}"
                : $"실측: {_hub.GpuProviderName} (GPU), {_hub.SystemProviderName} (CPU·RAM)";
        var age = now - latest.Timestamp;
        var interval = _hub.Interval;
        SensorLevel = latest.IsMock ? Level.Warning
            : age <= interval * 2 + TimeSpan.FromSeconds(1) ? (latest.Issues.Count == 0 ? Level.Good : Level.Warning)
            : Level.Error;
        SensorMeasured = $"측정 {Text.Clock(latest.Timestamp)} · {Text.Age(age)}" + (age > interval * 10 + TimeSpan.FromSeconds(2) ? " · 갱신 중단" : "");
        var issues = latest.Issues.Select(i => $"{i.Source}: {SensorStatusText.ToKorean(i.Status)} — {i.Message}").ToList();
        if (!issues.SequenceEqual(SensorIssues))
        {
            SensorIssues.Clear();
            foreach (var issue in issues)
            {
                SensorIssues.Add(issue);
            }
        }
    }

    private void UpdateLocalAi()
    {
        if (_localAi is null)
        {
            StrataState = "모듈 없음";
            return;
        }

        if (!_localAi.Status.IsUsable)
        {
            StrataState = $"로컬 AI 모듈 {ModuleStateText.ToKorean(_localAi.Status.State)}";
            StrataLevel = _localAi.Status.State == ModuleState.Failed ? Level.Error : Level.Muted;
            StrataNote = _localAi.Status.Summary;
            return;
        }

        var s = ModuleSummaries.LocalAi(_localAi);
        StrataState = s.RuntimeName is null ? s.StateText : $"{s.RuntimeName}: {s.StateText}";
        StrataLevel = s.State switch
        {
            RuntimeConnectionState.Connected => Level.Good,
            RuntimeConnectionState.Limited or RuntimeConnectionState.Connecting => Level.Warning,
            RuntimeConnectionState.Disabled or null => Level.Muted,
            _ => Level.Error,
        };
        StrataModel = s.Model ?? Text.NA;
        StrataEngine = s.EngineVersion ?? Text.NA;
        AnswerSpeed = Text.Num(s.AnswerTokPerSec, "0.0", "tok/s");
        PrefillSpeed = Text.Num(s.PrefillTokPerSec, "0", "tok/s");
        StrataRequests = s.ActiveRequests is null && s.QueuedRequests is null ? Text.NA : $"처리 {s.ActiveRequests?.ToString() ?? Text.NA} · 대기 {s.QueuedRequests?.ToString() ?? Text.NA}";
        StrataContext = Text.Percent(s.ContextFillPercent);
        LocalAiRecording = s.RecordingText;
        StrataNote = s.Problem is { } problem ? problem
            : s.MeasuredAt is { } at ? $"엔진 보고값 · 조회 {Text.Clock(at)} (N/A = 엔진이 주지 않은 값)" : "";
    }

    private void UpdateOperator()
    {
        var s = _operator();
        if (s is null)
        {
            OperatorState = "Operator 모듈 없음";
            OperatorLevel = Level.Muted;
            return;
        }

        OperatorState = s.StateText;
        OperatorLevel = !s.HostConnected ? Level.Muted : s.AwaitingApproval ? Level.Warning : s.Problem is null ? Level.Good : Level.Error;
        OperatorTask = s.CurrentTask is null ? (s.LastResult ?? "진행 중인 작업 없음") : $"{s.CurrentTask}{(s.CurrentStep is null ? "" : $" — {s.CurrentStep}")}";
        OperatorNote = string.Join(" · ", new[]
        {
            s.AwaitingApproval ? "승인 대기 중" : null,
            s.IsPaused ? "일시정지됨" : null,
            s.IsMockDesktop ? "MOCK 데스크톱" : null,
            s.Model is null ? null : $"모델: {s.Model}",
            s.Problem,
        }.Where(x => x is not null));
    }

    private void RefreshEconomics(DateTimeOffset now)
    {
        if (_economics is null || Interlocked.Exchange(ref _economicsBusy, 1) == 1)
        {
            return;
        }

        var latest = _hub.Latest?.Snapshot;
        _ = Task.Run(() =>
        {
            try
            {
                var summary = ModuleSummaries.Economics(_economics, latest, now, TimeZoneInfo.Local);
                _ui.Post(() => ApplyEconomics(summary));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _ui.Post(() => EconomicsNote = "GPU 경제성 정보를 읽지 못했습니다: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _economicsBusy, 0);
            }
        });
    }

    private void ApplyEconomics(EconomicsSummary s)
    {
        BoardPower = Text.Watts(s.GpuBoardPowerW);
        BoardPowerSource = s.GpuBoardPowerW is null ? "GPU 보드 전력 값 없음 (N/A, 0 W로 간주하지 않음)"
            : s.PowerIsMock ? "MOCK 가상 값" : $"NVML 실측 GPU 보드 전력 · {Text.Clock(s.PowerMeasuredAt)}";
        RateText = s.RateKrwPerKwh.HasValue ? $"{s.RateKrwPerKwh.Value!.Value:N1} 원/kWh ({s.RateBasis})" : $"{Text.NA} — {s.RateKrwPerKwh.Reason}";
        CostPerHour = s.EstimatedCostPerHourKrw is decimal c ? $"약 {Text.Krw(c)}/시간 (추정)" : Text.NA;
        EconomicsSessions = $"기록 중 {s.RunningSessions} · 일시정지 {s.PausedSessions}";
        if (s.Today is { } today)
        {
            TodayEnergy = Text.Kwh(today.TotalKwh);
            TodayCost = Text.Krw(today.ElectricityCost);
            TodayNote = $"오늘 세션 {today.Sessions}개, B의 세션 회계 기준" + (today.Coverage is < 0.9 ? $" · 전력 관측률 {today.Coverage:P0}" : "")
                        + (today.SessionsWithoutEnergy > 0 ? $" · 에너지 계산 불가 세션 {today.SessionsWithoutEnergy}개 제외" : "");
        }
        else
        {
            TodayEnergy = Text.NA;
            TodayCost = Text.NA;
            TodayNote = "오늘 기록한 세션이 없습니다 (세션을 기록해야 비용을 계산합니다).";
        }

        EconomicsNote = s.Problem ?? "시간당 비용은 현재 GPU 보드 전력 × 요금으로 낸 추정치입니다. PC 전체 전력이 아닙니다.";
    }

    private void RefreshDiagnostics(DateTimeOffset now)
    {
        if (_diagnostics is null || Interlocked.Exchange(ref _diagnosticsBusy, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var summary = ModuleSummaries.Diagnostics(_diagnostics, now);
                _ui.Post(() => ApplyDiagnostics(summary));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _ui.Post(() => DiagnosticsNote = "시스템 진단 정보를 읽지 못했습니다: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _diagnosticsBusy, 0);
            }
        });
    }

    private void ApplyDiagnostics(DiagnosticsSummary s)
    {
        Errors24h = Text.Count(s.Errors24h);
        Critical24h = Text.Count(s.Critical24h);
        HardwareErrors7d = Text.Count(s.HardwareErrors7d);
        Crashes7d = Text.Count(s.Crashes7d);
        DiagnosticsLevel = s.Critical24h > 0 || s.Crashes7d > 0 ? Level.Error : s.Errors24h > 0 || s.HardwareErrors7d > 0 ? Level.Warning : Level.Good;
        if (s.CollectionProblem is { } problem)
        {
            DiagnosticsLevel = DiagnosticsLevel == Level.Good ? Level.Warning : DiagnosticsLevel;
            DiagnosticsNote = "이벤트 수집 제한: " + problem;
        }
        else
        {
            DiagnosticsNote = s.LastPoll is { } at ? $"C가 분류한 Windows 이벤트 · 마지막 수집 {Text.Clock(at)}" : "이벤트 수집 대기 중";
        }

        RecentErrors.Clear();
        foreach (var e in s.Recent)
        {
            RecentErrors.Add(new EventLine(Text.DateTime(e.TimeCreated), e.Title, e.Category, LevelText(e.Severity),
                e.Severity >= DiagSeverity.Critical ? Level.Error : Level.Warning, e.IsVirtual));
        }
    }

    private static string LevelText(DiagSeverity severity) => severity switch
    {
        DiagSeverity.Critical => "심각",
        DiagSeverity.Error => "오류",
        DiagSeverity.Warning => "경고",
        _ => "정보",
    };
}
