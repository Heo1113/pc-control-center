using GpuProfit.Accounting.Analysis;
using GpuProfit.Accounting.Costs;
using GpuProfit.Core.Domain;
using GpuProfit.Storage;
using LocalAiPerf.Core.Hardware;
using LocalAiPerf.Core.Runtime;
using WinDiag.Core.Events;
using WinDiag.Storage;

namespace PcControlCenter.Modules;

/// <summary>Strata state for the integrated dashboard, as A reported it (values come from the engine, never estimated).</summary>
public sealed record LocalAiSummary
{
    public required string StateText { get; init; }
    public RuntimeConnectionState? State { get; init; }
    public string? RuntimeName { get; init; }
    public string? Model { get; init; }
    public string? EngineVersion { get; init; }
    public double? AnswerTokPerSec { get; init; }
    public double? PrefillTokPerSec { get; init; }
    public int? ActiveRequests { get; init; }
    public int? QueuedRequests { get; init; }
    public double? ContextFillPercent { get; init; }
    public required string RecordingText { get; init; }
    public DateTimeOffset? MeasuredAt { get; init; }
    public string? Problem { get; init; }
}

/// <summary>Current power and electricity cost for the dashboard. Measured, estimated and session-accounted values stay apart.</summary>
public sealed record EconomicsSummary
{
    /// <summary>Sum of the GPUs' board power in the newest shared reading (NVML measured); null when no GPU reported power.</summary>
    public double? GpuBoardPowerW { get; init; }

    public bool PowerIsMock { get; init; }
    public DateTimeOffset? PowerMeasuredAt { get; init; }

    /// <summary>B's effective electricity rate for the accounting profile (B's ElectricityCostCalculator).</summary>
    public Maybe<decimal> RateKrwPerKwh { get; init; } = Maybe<decimal>.None("요금 설정 없음");

    public bool RateIsEstimate { get; init; }
    public string RateBasis { get; init; } = "";

    /// <summary>Board power × rate for one hour: an estimate, never shown as a measured cost.</summary>
    public decimal? EstimatedCostPerHourKrw { get; init; }

    public int RunningSessions { get; init; }
    public int PausedSessions { get; init; }

    /// <summary>B's own period aggregation of today's sessions (realized basis), or null when nothing was recorded today.</summary>
    public PeriodBucket? Today { get; init; }

    public string? Problem { get; init; }
}

/// <summary>Recent Windows problems as C classified and stored them.</summary>
public sealed record DiagnosticsSummary
{
    public long Errors24h { get; init; }
    public long Critical24h { get; init; }
    public long HardwareErrors7d { get; init; }
    public long Crashes7d { get; init; }
    public IReadOnlyList<StoredEvent> Recent { get; init; } = [];
    public string? CollectionProblem { get; init; }
    public DateTimeOffset? LastPoll { get; init; }
}

/// <summary>Builds the dashboard summaries from the running engines, using the engines' own calculations.</summary>
public static class ModuleSummaries
{
    /// <summary>Categories C treats as hardware errors (WHEA, CPU, memory, PCIe, other hardware, GPU driver resets).</summary>
    public static readonly IReadOnlyList<string> HardwareCategories =
    [
        EventCategories.HardwareCpu, EventCategories.HardwareMemory, EventCategories.HardwarePcie, EventCategories.HardwareOther, EventCategories.GpuDriver,
    ];

    public static readonly IReadOnlyList<string> CrashCategories =
    [
        EventCategories.BugCheck, EventCategories.CrashDump, EventCategories.UnexpectedShutdown,
    ];

    public static LocalAiSummary LocalAi(LocalAiModule module)
    {
        if (module.Service is not { } service)
        {
            return new LocalAiSummary { StateText = module.Status.Summary, RecordingText = "-", Problem = module.Status.Detail };
        }

        var recording = service.Recorder?.ActiveSession is { } session ? $"기록 중: {session.Name}" : service.StorageProblem is null ? "기록 안 함" : "기록 불가";
        if (service.Runtime is null)
        {
            return new LocalAiSummary { StateText = "Strata 연결 꺼짐 (로컬 AI 설정)", State = RuntimeConnectionState.Disabled, RecordingText = recording };
        }

        if (service.LatestRuntime is not { } rt)
        {
            return new LocalAiSummary { StateText = "연결 중", State = RuntimeConnectionState.Connecting, RuntimeName = service.Runtime.DisplayName, RecordingText = recording };
        }

        return new LocalAiSummary
        {
            StateText = RuntimeStateText.ToKorean(rt.State),
            State = rt.State,
            RuntimeName = service.Runtime.DisplayName,
            Model = rt.Model?.Id,
            EngineVersion = rt.Server?.EngineVersion,
            AnswerTokPerSec = rt.Rates?.AnswerTokPerSec,
            PrefillTokPerSec = rt.Rates?.PrefillTokPerSec,
            ActiveRequests = rt.Work?.Active,
            QueuedRequests = rt.Work?.Queued,
            ContextFillPercent = rt.ContextFillPercent,
            RecordingText = recording,
            MeasuredAt = rt.Timestamp,
            Problem = rt.Error?.Message,
        };
    }

    /// <summary>
    /// Mirrors B's dashboard (DashboardViewModel.RefreshTotalsAsync, "오늘" row): B's SessionService.Compute per session,
    /// B's PeriodAggregator for the day bucket. Only the orchestration is repeated here; every number is B's.
    /// </summary>
    public static EconomicsSummary Economics(GpuEconomicsModule module, HardwareSnapshot? latest, DateTimeOffset now, TimeZoneInfo zone)
    {
        var power = latest?.Gpus.Where(g => g.PowerW is double p && double.IsFinite(p)).Select(g => g.PowerW!.Value).ToList();
        var boardPower = power is { Count: > 0 } ? power.Sum() : (double?)null;
        if (module.Engine is not { } engine)
        {
            return new EconomicsSummary { GpuBoardPowerW = boardPower, PowerIsMock = latest?.IsMock ?? false, PowerMeasuredAt = latest?.Timestamp, Problem = module.Status.Summary };
        }

        var profile = engine.Profile;
        var rate = ElectricityCostCalculator.EffectiveRate(profile.Tariff, null, null, out var isEstimate, out var basis);
        decimal? perHour = boardPower is double w && rate.HasValue ? Math.Round((decimal)(w / 1000.0) * rate.Value!.Value, 0) : null;
        var open = engine.Sessions.OpenSessions();
        PeriodBucket? today = null;
        string? problem = null;
        try
        {
            var dayStart = PeriodAggregator.PeriodStart(now, PeriodGranularity.Day, zone);
            var sessions = engine.Store.ListSessions(new SessionQuery { From = dayStart, Limit = 5000 });
            var segments = engine.Store.ListAllSegments();
            var items = sessions.Select(s => new PeriodItem(s, segments.GetValueOrDefault(s.Id, []), engine.Sessions.Compute(s.Id, profile))).ToList();
            var days = PeriodAggregator.Aggregate(items, PeriodGranularity.Day, zone, now, engine.Store.ListCosts(), engine.Store.ListRates());
            today = days.FirstOrDefault(b => b.Start == dayStart);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
        {
            problem = "오늘 합계를 계산하지 못했습니다: " + ex.Message;
        }

        return new EconomicsSummary
        {
            GpuBoardPowerW = boardPower,
            PowerIsMock = latest?.IsMock ?? false,
            PowerMeasuredAt = latest?.Timestamp,
            RateKrwPerKwh = rate,
            RateIsEstimate = isEstimate,
            RateBasis = basis,
            EstimatedCostPerHourKrw = perHour,
            RunningSessions = open.Count(s => s.State == SessionState.Running),
            PausedSessions = open.Count(s => s.State == SessionState.Paused),
            Today = today is { Sessions: > 0 } ? today : null,
            Problem = problem,
        };
    }

    public static DiagnosticsSummary Diagnostics(DiagnosticsModule module, DateTimeOffset now, int recent = 6)
    {
        if (module.Engine is not { } engine)
        {
            return new DiagnosticsSummary { CollectionProblem = module.Status.Summary };
        }

        var store = engine.Store;
        var day = now.AddHours(-24);
        var week = now.AddDays(-7);
        return new DiagnosticsSummary
        {
            Errors24h = store.CountEvents(new EventQuery { From = day, MinSeverity = DiagSeverity.Error }),
            Critical24h = store.CountEvents(new EventQuery { From = day, MinSeverity = DiagSeverity.Critical }),
            HardwareErrors7d = store.CountEvents(new EventQuery { From = week, MinSeverity = DiagSeverity.Warning, Categories = HardwareCategories }),
            Crashes7d = store.CountEvents(new EventQuery { From = week, Categories = CrashCategories }),
            Recent = store.QueryEvents(new EventQuery { From = week, MinSeverity = DiagSeverity.Error, Limit = recent }),
            CollectionProblem = engine.EventCollectionUnavailableReason ?? engine.LastPollError,
            LastPoll = engine.LastPoll,
        };
    }
}

/// <summary>Operator (E) state for the dashboard, as reported by the isolated Operator host.</summary>
public sealed record OperatorSummary
{
    public required string StateText { get; init; }
    public bool HostConnected { get; init; }
    public bool IsMockDesktop { get; init; }
    public string? CurrentTask { get; init; }
    public string? CurrentStep { get; init; }
    public bool AwaitingApproval { get; init; }
    public bool IsPaused { get; init; }
    public string? LastResult { get; init; }
    public string? Model { get; init; }
    public string? Problem { get; init; }
}

/// <summary>Stops and starts one module of the shell (used when its data is replaced by an import).</summary>
public sealed class SupervisorModuleRestarter(PcControlCenter.Core.ModuleSupervisor supervisor) : PcControlCenter.Migration.IModuleRestarter
{
    public Task StopAsync(string moduleId) => supervisor.Find(moduleId)?.StopAsync() ?? Task.CompletedTask;

    public Task StartAsync(string moduleId) => supervisor.Find(moduleId)?.StartAsync(CancellationToken.None) ?? Task.CompletedTask;
}
