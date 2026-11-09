namespace LocalAiPerf.Core.Runtime;

/// <summary>Connection state of an AI runtime adapter. Stored as an integer; do not renumber.</summary>
public enum RuntimeConnectionState
{
    Disabled = 0,
    Connecting = 1,
    Connected = 2,

    /// <summary>The server answers, but only part of the telemetry is available (for example status only).</summary>
    Limited = 3,

    /// <summary>Nothing is listening (runtime not running, wrong port) or the host cannot be reached.</summary>
    Unreachable = 4,
    Timeout = 5,
    AuthRequired = 6,

    /// <summary>The server answered with a format this app does not understand (likely an API change after an update).</summary>
    Incompatible = 7,
    ServerError = 8,
    ConfigurationError = 9,
}

/// <summary>Error class. Connection problems and API compatibility problems are kept apart on purpose.</summary>
public enum RuntimeErrorCategory
{
    None = 0,
    Connection = 1,
    Timeout = 2,
    Authentication = 3,
    Compatibility = 4,
    Server = 5,
    Configuration = 6,
}

public sealed record RuntimeError(
    RuntimeErrorCategory Category,
    string Message,
    string? Hint = null,
    string? Endpoint = null,
    int? HttpStatus = null);

public static class RuntimeStateText
{
    public static string ToKorean(RuntimeConnectionState state) => state switch
    {
        RuntimeConnectionState.Disabled => "꺼짐",
        RuntimeConnectionState.Connecting => "연결 중",
        RuntimeConnectionState.Connected => "연결됨",
        RuntimeConnectionState.Limited => "제한적 연결",
        RuntimeConnectionState.Unreachable => "연결 안 됨",
        RuntimeConnectionState.Timeout => "응답 시간 초과",
        RuntimeConnectionState.AuthRequired => "인증 필요",
        RuntimeConnectionState.Incompatible => "API 호환성 오류",
        RuntimeConnectionState.ServerError => "서버 오류",
        RuntimeConnectionState.ConfigurationError => "설정 오류",
        _ => state.ToString(),
    };

    public static string ToKorean(RuntimeErrorCategory category) => category switch
    {
        RuntimeErrorCategory.None => "없음",
        RuntimeErrorCategory.Connection => "연결 오류",
        RuntimeErrorCategory.Timeout => "시간 초과",
        RuntimeErrorCategory.Authentication => "인증 오류",
        RuntimeErrorCategory.Compatibility => "API 호환성 오류",
        RuntimeErrorCategory.Server => "서버 오류",
        RuntimeErrorCategory.Configuration => "설정 오류",
        _ => category.ToString(),
    };
}

public sealed record RuntimeServerInfo
{
    public string? Service { get; init; }
    public string? InstanceId { get; init; }
    public string? InstanceState { get; init; }
    public double? BootTimeUnix { get; init; }
    public long? UptimeSeconds { get; init; }
    public string? EngineVersion { get; init; }

    /// <summary>Why the engine version is unknown, when it is.</summary>
    public string? EngineVersionNote { get; init; }

    /// <summary>
    /// True when the key in use is a read-only telemetry key: Strata then refuses /v1/status, /props and /v1/models,
    /// so the engine version, model file, default sampling settings and aliases are not available (not an error).
    /// </summary>
    public bool MetadataRestricted { get; init; }

    /// <summary>server-v1, legacy-adapter or status-only.</summary>
    public string? TelemetrySource { get; init; }
    public int? SchemaVersion { get; init; }
    public bool? ApiKeyConfigured { get; init; }
}

public sealed record RuntimeModelInfo
{
    public string? Id { get; init; }
    public bool? Loaded { get; init; }
    public long? ContextTokens { get; init; }

    /// <summary>File name only (never the full path, which can contain the user name).</summary>
    public string? ModelFileName { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public bool? Vision { get; init; }
    public bool? AutoLoad { get; init; }
}

public sealed record RuntimeWorkInfo
{
    public int? SlotsEffective { get; init; }
    public int? SlotsRequested { get; init; }
    public int? Active { get; init; }
    public int? Queued { get; init; }
    public int? Waiting { get; init; }
    public int? OccupiedSlots { get; init; }
    public int? MaxQueue { get; init; }
    public long? AdmissionRejected { get; init; }
    public long? QueueTimeouts { get; init; }
    public long? ClientDisconnectCancels { get; init; }
    public double? OldestQueueSeconds { get; init; }
    public double? QueueTimeoutSeconds { get; init; }
}

public sealed record RuntimeSlot(
    int Slot,
    string? State,
    long? HeldTokens,
    long? PromptTokens,
    long? PromptRead,
    long? Generated,
    double? TokPerSec);

public sealed record RuntimeRates
{
    public double? AnswerTokPerSec { get; init; }
    public double? AnswerWindowSeconds { get; init; }
    public double? PrefillTokPerSec { get; init; }
    public long? OutputTokensCounter { get; init; }

    /// <summary>Derived by this app from two counter readings of the same server instance.</summary>
    public double? AggregateOutputTokPerSec { get; init; }
}

/// <summary>Summary of one finished request as reported by the runtime. Never contains prompt or answer text.</summary>
public sealed record CompletedRequest
{
    public required string Id { get; init; }
    public double? StartedUnix { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Finish { get; init; }
    public long? PromptTokens { get; init; }
    public long? ReusedTokens { get; init; }
    public long? OutputTokens { get; init; }
    public double? PromptMs { get; init; }
    public double? DecodeMs { get; init; }
    public double? FirstEngineTokenSeconds { get; init; }

    /// <summary>Derived: output tokens per second of decode time.</summary>
    public double? DecodeTokPerSec => OutputTokens is long n && n >= 2 && DecodeMs is double ms && ms > 0
        ? n / (ms / 1000.0)
        : null;

    /// <summary>Derived: prompt tokens actually read (not reused from cache) per second of prompt time.</summary>
    public double? PrefillTokPerSec
    {
        get
        {
            if (PromptTokens is not long prompt || PromptMs is not double ms || ms <= 0)
            {
                return null;
            }

            var read = prompt - Math.Max(0, ReusedTokens ?? 0);
            return read > 0 ? read / (ms / 1000.0) : null;
        }
    }

    public bool IsError => Finish is "error" or "timeout";
}

public sealed record RuntimeTotals
{
    public double? SinceUnix { get; init; }
    public long? Requests { get; init; }
    public long? PromptTokens { get; init; }
    public long? OutputTokens { get; init; }
}

public sealed record RuntimeActivityEvent(long Sequence, double AtUnix, string RequestId, string Event, string? Outcome);

public sealed record RuntimeSetting(string Key, string Value);

/// <summary>Everything one poll of an AI runtime produced. Unknown values stay null; nothing is filled in.</summary>
public sealed record RuntimeSnapshot
{
    public required string RuntimeKind { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required RuntimeConnectionState State { get; init; }
    public string? Endpoint { get; init; }
    public RuntimeError? Error { get; init; }
    public DateTimeOffset? LastSuccess { get; init; }

    /// <summary>When the adapter will try again after a failure (back-off), if it is waiting.</summary>
    public DateTimeOffset? NextRetryAt { get; init; }
    public double? ServerSampledAtUnix { get; init; }
    public RuntimeServerInfo? Server { get; init; }
    public RuntimeModelInfo? Model { get; init; }
    public RuntimeWorkInfo? Work { get; init; }
    public RuntimeRates? Rates { get; init; }
    public IReadOnlyList<RuntimeSlot> Slots { get; init; } = [];
    public IReadOnlyList<CompletedRequest> RecentRequests { get; init; } = [];
    public RuntimeTotals? Totals { get; init; }
    public IReadOnlyList<RuntimeActivityEvent> Activity { get; init; } = [];
    public IReadOnlyList<RuntimeSetting> Settings { get; init; } = [];

    public bool IsReachable => State is RuntimeConnectionState.Connected or RuntimeConnectionState.Limited;

    /// <summary>Largest held token count over the reported slots (current context use of the fullest slot).</summary>
    public long? MaxSlotContextUsed => Slots.Count == 0 ? null : Slots.Max(s => s.HeldTokens);

    /// <summary>The newest completed request in the engine's recent list (numbers only), if any.</summary>
    public CompletedRequest? LastCompletedRequest => RecentRequests.Where(r => !r.IsError).MaxBy(r => r.StartedUnix ?? double.MinValue);

    public double? ContextFillPercent => MaxSlotContextUsed is long used && Model?.ContextTokens is long total && total > 0
        ? 100.0 * used / total
        : null;
}

/// <summary>An AI runtime this app can observe. Implementations are read-only and must not change the runtime.</summary>
public interface IAiRuntimeAdapter : IDisposable
{
    string Kind { get; }
    string DisplayName { get; }
    string EndpointDisplay { get; }

    /// <summary>Polls the runtime. Must not throw for connection or format problems; those are returned in the snapshot.</summary>
    Task<RuntimeSnapshot> PollAsync(CancellationToken cancellationToken);

    /// <summary>Clears any retry back-off so that the next poll connects immediately.</summary>
    void RequestReconnect();
}
