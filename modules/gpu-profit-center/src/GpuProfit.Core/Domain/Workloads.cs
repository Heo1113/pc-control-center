namespace GpuProfit.Core.Domain;

public enum WorkloadType
{
    Mining = 0,
    AiInference = 1,
    GpuCompute = 2,
    Other = 3,
}

public static class WorkloadTypeText
{
    public static string ToKorean(WorkloadType type) => type switch
    {
        WorkloadType.Mining => "암호화폐 GPU 채굴",
        WorkloadType.AiInference => "AI 추론·연산 서비스",
        WorkloadType.GpuCompute => "GPU 컴퓨팅 작업",
        WorkloadType.Other => "기타 사용자 정의",
        _ => type.ToString(),
    };

    public static IReadOnlyList<WorkloadType> All { get; } = Enum.GetValues<WorkloadType>();
}

/// <summary>A kind of work the user runs on the GPU (not tied to any coin, pool or service).</summary>
public sealed record Workload
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public WorkloadType Type { get; init; } = WorkloadType.Other;
    public string? Notes { get; init; }
    public bool Archived { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; init; }
}

public enum SessionState
{
    /// <summary>Recording now.</summary>
    Running = 0,

    /// <summary>Recording is paused (the pause is not counted as session time).</summary>
    Paused = 1,

    /// <summary>Ended by the user.</summary>
    Ended = 2,

    /// <summary>Ended early by the user (work failed or was cancelled). Kept and counted, but flagged.</summary>
    Aborted = 3,

    /// <summary>The app ended uncleanly while recording; the session was closed at its last recorded data and can be resumed.</summary>
    Interrupted = 4,
}

public static class SessionStateText
{
    public static string ToKorean(SessionState state) => state switch
    {
        SessionState.Running => "기록 중",
        SessionState.Paused => "일시정지",
        SessionState.Ended => "종료",
        SessionState.Aborted => "중단",
        SessionState.Interrupted => "비정상 종료 후 복구됨",
        _ => state.ToString(),
    };

    public static bool IsOpen(SessionState state) => state is SessionState.Running or SessionState.Paused;
}

public static class SegmentEndReasons
{
    public const string Paused = "paused";
    public const string Stopped = "stopped";
    public const string Aborted = "aborted";
    public const string AppExit = "app_exit";
    public const string RecoveredAfterCrash = "recovered_after_crash";
    public const string WindowsSessionEnd = "windows_session_end";
    public const string Manual = "manual";
}

/// <summary>A recorded interval of a session. Pauses are the time between segments.</summary>
public sealed record SessionSegment
{
    public long Id { get; init; }
    public long SessionId { get; init; }
    public required DateTimeOffset Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string? EndReason { get; init; }

    public TimeSpan Duration(DateTimeOffset now) => (End ?? now) - Start is { } d && d > TimeSpan.Zero ? d : TimeSpan.Zero;
}

public enum EnergyBasis
{
    /// <summary>Only the GPU board power measured by NVML (does not include CPU, board, fans or PSU losses).</summary>
    GpuMeasured = 0,

    /// <summary>Measured GPU board power plus a user-defined assumption for the rest of the system.</summary>
    GpuPlusSystemAssumption = 1,

    /// <summary>Whole-PC energy read from an external wall power meter and entered by the user.</summary>
    WallMeter = 2,

    /// <summary>Measured GPU power minus an assumed GPU idle power: the energy the work added.</summary>
    IncrementalGpu = 3,
}

public static class EnergyBasisText
{
    public static string ToKorean(EnergyBasis basis) => basis switch
    {
        EnergyBasis.GpuMeasured => "GPU 보드 전력 실측만",
        EnergyBasis.GpuPlusSystemAssumption => "GPU 실측 + 시스템 추가 전력 가정",
        EnergyBasis.WallMeter => "외부 전력계 PC 전체 전력 (사용자 입력)",
        EnergyBasis.IncrementalGpu => "증분 전력 (GPU 실측 − 유휴 전력 가정)",
        _ => basis.ToString(),
    };

    public static IReadOnlyList<EnergyBasis> All { get; } = Enum.GetValues<EnergyBasis>();
}

/// <summary>What to do with time inside a session where power could not be observed.</summary>
public enum UnobservedPolicy
{
    /// <summary>No energy is counted for unobserved time; the gap is reported (default).</summary>
    Exclude = 0,

    /// <summary>An estimate (average observed power × unobserved time) is added as a separate, estimated component.</summary>
    EstimateFromObservedAverage = 1,
}

public enum SessionSource
{
    /// <summary>Recorded live by this app.</summary>
    Live = 0,

    /// <summary>Entered afterwards by the user (no sensor data unless the time range overlaps recorded samples).</summary>
    Manual = 1,
}

/// <summary>
/// A work session. Its times are when the user recorded it, which is not proof that the work itself ran:
/// <see cref="ExecutionConfirmed"/> is the user's statement, and the GPU activity observed in the samples is reported
/// separately by the accounting engine.
/// </summary>
public sealed record WorkSession
{
    public long Id { get; init; }
    public long WorkloadId { get; init; }
    public string? Title { get; init; }
    public string? Notes { get; init; }
    public SessionState State { get; init; }
    public SessionSource Source { get; init; }
    public required DateTimeOffset Started { get; init; }
    public DateTimeOffset? Ended { get; init; }
    public string? EndReason { get; init; }
    public long? RunId { get; init; }
    public bool IsMock { get; init; }

    /// <summary>GPU indices this session accounts for; null means every GPU seen.</summary>
    public IReadOnlyList<int>? DeviceIndices { get; init; }

    /// <summary>The user states the work was actually running during the recorded segments.</summary>
    public bool ExecutionConfirmed { get; init; }

    /// <summary>The user states this session earned nothing (a cost-only run). Without it, missing revenue is "not entered".</summary>
    public bool NoRevenueConfirmed { get; init; }

    // Per-session overrides of the accounting profile (null = use the profile).
    public EnergyBasis? EnergyBasis { get; init; }
    public double? ExtraSystemPowerW { get; init; }
    public double? IdleGpuPowerW { get; init; }
    public UnobservedPolicy? UnobservedPolicy { get; init; }
    public decimal? ElectricityRateKrwPerKwh { get; init; }

    /// <summary>Whole-PC energy from an external meter for this session (user entered), in kWh.</summary>
    public double? WallMeterKwh { get; init; }

    /// <summary>Work hours entered by the user (for example from the miner's log), used instead of the recorded hours.</summary>
    public double? WorkHoursOverride { get; init; }

    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; init; }

    public bool IsOpen => SessionStateText.IsOpen(State);
}
