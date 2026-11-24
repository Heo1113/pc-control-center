namespace LocalAiPerf.Core.Sessions;

public enum SessionKind
{
    /// <summary>Recorded automatically while the app runs.</summary>
    Auto = 0,

    /// <summary>Started and stopped by the user (for example "before update" / "after update").</summary>
    Manual = 1,
}

public static class SessionEndReasons
{
    public const string UserStopped = "user_stopped";
    public const string AppExit = "app_exit";
    public const string ConditionChanged = "condition_changed";
    public const string RecoveredAfterCrash = "recovered_after_crash";
    public const string SettingsChanged = "settings_changed";
    public const string Replaced = "replaced_by_new_session";

    public static string ToKorean(string? reason) => reason switch
    {
        null => "기록 중",
        UserStopped => "사용자 중지",
        AppExit => "앱 종료",
        ConditionChanged => "엔진/모델 조건 변경",
        RecoveredAfterCrash => "비정상 종료 후 복구",
        SettingsChanged => "설정 변경",
        Replaced => "새 세션 시작",
        _ => reason,
    };
}

public sealed record SessionInfo
{
    public required long Id { get; init; }
    public required string Uid { get; init; }
    public required string Name { get; init; }
    public string? Notes { get; init; }
    public string? Tags { get; init; }
    public SessionKind Kind { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? EndReason { get; init; }
    public DateTimeOffset? LastSampleAt { get; init; }
    public int SampleIntervalMs { get; init; }
    public string AppVersion { get; init; } = "";
    public string? HostJson { get; init; }
    public string? RuntimeKind { get; init; }
    public string? RuntimeEndpoint { get; init; }
    public long SampleCount { get; init; }
    public bool IsMock { get; init; }

    /// <summary>Short summary of the runtime conditions seen in the session (model / engine version), if any.</summary>
    public string? ModelSummary { get; init; }
    public string? EngineVersionSummary { get; init; }

    public bool IsActive => EndedAt is null;

    public TimeSpan Duration => ((EndedAt ?? LastSampleAt ?? StartedAt) - StartedAt) is var d && d > TimeSpan.Zero ? d : TimeSpan.Zero;
}

public sealed record SessionStartRequest(
    string Name,
    SessionKind Kind,
    DateTimeOffset StartedAt,
    int SampleIntervalMs,
    string AppVersion,
    string? HostJson,
    string? RuntimeKind,
    string? RuntimeEndpoint,
    bool IsMock,
    string? Notes = null,
    string? Tags = null);
