using LocalAiPerf.Core.Runtime;
using LocalAiPerf.Core.Sessions;

namespace LocalAiPerf.Storage;

public enum EventSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

public static class EventCategories
{
    public const string Session = "session";
    public const string RuntimeConnection = "runtime.connection";
    public const string RuntimeCompatibility = "runtime.compat";
    public const string RuntimeChange = "runtime.change";
    public const string RuntimeRequestError = "runtime.request_error";
    public const string Sensor = "sensor";
    public const string Gap = "gap";
    public const string Storage = "storage";
    public const string Retention = "retention";
}

public sealed record EventRecord(long? SessionId, DateTimeOffset Time, EventSeverity Severity, string Category, string Message);

/// <summary>Flags stored with each system sample row.</summary>
[Flags]
public enum SampleFlags
{
    None = 0,
    Mock = 1,
    CpuError = 2,
    MemoryError = 4,
    GpuUnavailable = 8,
    TickOverrun = 16,
}

/// <summary>Everything written for one collection tick, in one transaction.</summary>
public sealed record FrameWrite
{
    public required long SessionId { get; init; }
    public required Core.TelemetryFrame Frame { get; init; }
    public SampleFlags Flags { get; init; }
    public IReadOnlyList<CompletedRequest> NewRequests { get; init; } = [];
    public string? InstanceId { get; init; }
    public RuntimeConditions? Conditions { get; init; }
    public IReadOnlyList<EventRecord> Events { get; init; } = [];
}

public sealed record SystemPoint(long TimeMs, double? CpuUtil, long? RamUsed, long? RamTotal, SampleFlags Flags);

public sealed record GpuPoint(
    long TimeMs, int GpuIndex, double? Util, double? MemUtil, double? TempC, double? PowerW, double? PowerLimitW,
    long? VramUsed, long? VramTotal, double? ClockGfxMhz, double? ClockMemMhz, double? FanPct,
    double? PcieTxKBps, double? PcieRxKBps, long? EnergyMj, long? Throttle);

public sealed record RuntimePoint(
    long TimeMs, RuntimeConnectionState State, RuntimeErrorCategory? ErrorCategory, bool? ModelLoaded,
    double? AnswerTokS, double? PrefillTokS, double? AggregateTokS, int? Active, int? Queued, int? Waiting,
    int? OccupiedSlots, int? SlotsEffective, long? ContextMax, long? ContextUsed, long? OutputCounter,
    long? AdmissionRejected, long? QueueTimeouts, long? DisconnectCancels);

public sealed record RequestRow(
    string InstanceId, string RequestId, long ObservedMs, long? StartedMs, double? DurationS, string? Finish,
    long? PromptTokens, long? ReusedTokens, long? OutputTokens, double? PromptMs, double? DecodeMs, double? FirstTokenS)
{
    public CompletedRequest ToCompletedRequest() => new()
    {
        Id = RequestId,
        StartedUnix = StartedMs / 1000.0,
        DurationSeconds = DurationS,
        Finish = Finish,
        PromptTokens = PromptTokens,
        ReusedTokens = ReusedTokens,
        OutputTokens = OutputTokens,
        PromptMs = PromptMs,
        DecodeMs = DecodeMs,
        FirstEngineTokenSeconds = FirstTokenS,
    };
}

public sealed record RuntimeInfoRow(long TimeMs, RuntimeConditions Conditions, string Fingerprint);

/// <summary>All stored data of one session, for analysis, charts and export.</summary>
public sealed record SessionData(
    SessionInfo Session,
    IReadOnlyList<SystemPoint> System,
    IReadOnlyList<GpuPoint> Gpu,
    IReadOnlyList<RuntimePoint> Runtime,
    IReadOnlyList<RequestRow> Requests,
    IReadOnlyList<RuntimeInfoRow> RuntimeInfo,
    IReadOnlyList<EventRecord> Events);

public sealed record SessionFilter(string? Text = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int Limit = 500);

public sealed record DatabaseStats(
    string Path,
    long FileBytes,
    long WalBytes,
    long UsedBytes,
    long FreeBytes,
    long Sessions,
    long SystemSamples,
    long GpuSamples,
    long RuntimeSamples,
    long Requests,
    long Events,
    DateTimeOffset? Oldest,
    DateTimeOffset? Newest);

public sealed record RetentionResult(int SessionsDeleted, long RowsDeleted, long BytesBefore, long BytesAfter, IReadOnlyList<string> Notes);

public sealed record BackupValidation(bool IsValid, string Message, int SchemaVersion, long Sessions, DateTimeOffset? Oldest, DateTimeOffset? Newest);
