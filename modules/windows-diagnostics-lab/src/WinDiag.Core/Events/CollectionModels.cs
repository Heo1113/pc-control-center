namespace WinDiag.Core.Events;

/// <summary>Whether a channel could be read the last time it was scanned.</summary>
public enum ChannelStatus
{
    NeverScanned = 0,
    Ok = 1,
    NotFound = 2,
    AccessDenied = 3,
    Disabled = 4,
    Error = 5,
}

public static class ChannelStatusText
{
    public static string Name(ChannelStatus status) => status switch
    {
        ChannelStatus.NeverScanned => "아직 확인 안 함",
        ChannelStatus.Ok => "정상 수집",
        ChannelStatus.NotFound => "이 PC에 없는 로그",
        ChannelStatus.AccessDenied => "권한 부족 (수집 불가)",
        ChannelStatus.Disabled => "설정에서 꺼짐",
        ChannelStatus.Error => "읽기 오류",
        _ => status.ToString(),
    };

    public static string Explain(ChannelStatus status, string channel) => status switch
    {
        ChannelStatus.AccessDenied =>
            $"'{channel}' 로그를 읽을 권한이 없습니다. 이 로그를 읽으려면 관리자 권한으로 실행하거나 사용자 계정을 'Event Log Readers' 그룹에 추가해야 합니다. " +
            "이 프로그램은 스스로 권한을 올리지 않으며, 이 로그 없이도 다른 로그는 계속 수집합니다.",
        ChannelStatus.NotFound => $"'{channel}' 로그가 이 Windows에 없거나 사용하지 않도록 설정되어 있습니다.",
        ChannelStatus.Error => $"'{channel}' 로그를 읽는 중 오류가 발생했습니다. 다음 확인 때 다시 시도합니다.",
        ChannelStatus.Disabled => "설정에서 이 로그 수집을 껐습니다.",
        _ => "",
    };
}

/// <summary>Collection progress of one event log channel, kept in the database so collection resumes after a restart.</summary>
public sealed record ChannelState
{
    public required string Channel { get; init; }
    public ChannelStatus Status { get; init; }
    public string? StatusMessage { get; init; }

    /// <summary>Highest record ID already scanned (not only stored): the next scan reads records after it.</summary>
    public long? LastRecordId { get; init; }

    public DateTimeOffset? LastScan { get; init; }
    public DateTimeOffset? LastEventTime { get; init; }
    public long? OldestRecordId { get; init; }
    public DateTimeOffset? OldestEventTime { get; init; }
    public long? RecordCount { get; init; }

    /// <summary>Earliest time covered by a history import of this channel.</summary>
    public DateTimeOffset? HistoryFrom { get; init; }
}

/// <summary>Kinds of periods for which data is missing.</summary>
public static class GapKinds
{
    /// <summary>The log wrapped before the app could read some records.</summary>
    public const string LogOverwritten = "log_overwritten";

    /// <summary>The log was cleared (records before that are gone).</summary>
    public const string LogCleared = "log_cleared";

    /// <summary>Record IDs went backwards: the log was recreated.</summary>
    public const string LogReset = "log_reset";

    /// <summary>The requested history goes back further than the oldest record Windows still has.</summary>
    public const string LogRetention = "log_retention";

    public const string AccessDenied = "access_denied";
    public const string ReadFailed = "read_failed";

    /// <summary>No sensor data: the app was not running.</summary>
    public const string AppNotRunning = "app_not_running";

    /// <summary>The app ended without closing its run (crash, power loss, kill).</summary>
    public const string AppEndedUncleanly = "app_unclean_end";

    public const string SensorUnavailable = "sensor_unavailable";

    /// <summary>The wall clock moved differently from the monotonic clock between two samples.</summary>
    public const string ClockJump = "clock_jump";

    public static string Name(string kind) => kind switch
    {
        LogOverwritten => "로그 덮어씀 (읽기 전에 Windows가 지움)",
        LogCleared => "이벤트 로그 지워짐",
        LogReset => "로그 재생성 (레코드 번호 역행)",
        LogRetention => "Windows에 기록이 남아 있지 않은 기간",
        AccessDenied => "권한 부족",
        ReadFailed => "읽기 실패",
        AppNotRunning => "센서 기록 없음 (앱 미실행)",
        AppEndedUncleanly => "앱 비정상 종료",
        SensorUnavailable => "센서 사용 불가",
        ClockJump => "시스템 시각 변경 감지",
        _ => kind,
    };
}

/// <summary>A period with missing or unreliable data. Scope is a channel name or "sensors".</summary>
public sealed record CollectionGap
{
    public long Id { get; init; }
    public required string Scope { get; init; }
    public required string Kind { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public required DateTimeOffset Detected { get; init; }
    public string? Detail { get; init; }
}

/// <summary>One run of the app (start to exit). Sensor data exists only inside runs.</summary>
public sealed record AppRun
{
    public long Id { get; init; }
    public required DateTimeOffset Started { get; init; }
    public DateTimeOffset? Ended { get; init; }
    public string? EndReason { get; init; }
    public DateTimeOffset? LastHeartbeat { get; init; }
    public string AppVersion { get; init; } = "";
    public bool IsMock { get; init; }
    public int SensorIntervalMs { get; init; }
    public bool Elevated { get; init; }

    public DateTimeOffset EffectiveEnd(DateTimeOffset now) => Ended ?? LastHeartbeat ?? now;
}

public static class RunEndReasons
{
    public const string Normal = "normal";
    public const string WindowsSessionEnd = "windows_session_end";
    public const string RecoveredAfterCrash = "recovered_after_crash";
}
