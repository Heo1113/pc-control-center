using System.Text.Json.Serialization;
using WindowsAiOperator.Core.Diagnostics;

namespace WindowsAiOperator.Core.Settings;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GeneralSettings General { get; set; } = new();
    public SafetySettings Safety { get; set; } = new();
    public List<ModelProfile> ModelProfiles { get; set; } = [];
    public string? ActiveModelProfileId { get; set; }

    public ModelProfile? ActiveProfile =>
        ModelProfiles.FirstOrDefault(p => string.Equals(p.Id, ActiveModelProfileId, StringComparison.Ordinal));

    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings();
        settings.Safety.AllowedApps.AddRange(AllowedApp.Defaults());
        settings.ModelProfiles.AddRange(ModelProfile.Defaults());
        settings.ActiveModelProfileId = settings.ModelProfiles[0].Id;
        return settings;
    }

    /// <summary>Clamps every number into its safe range and fills missing parts. Returns what was corrected.</summary>
    public IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        General ??= new GeneralSettings();
        Safety ??= new SafetySettings();
        ModelProfiles ??= [];
        Safety.AllowedApps ??= [];
        Safety.ExtraBlockedProcesses ??= [];
        Safety.GrantedFolders ??= [];

        Safety.MaxStepsPerTask = Clamp(Safety.MaxStepsPerTask, 1, 100, "최대 실행 단계", notes);
        Safety.TaskTimeoutSeconds = Clamp(Safety.TaskTimeoutSeconds, 10, 3600, "작업 제한 시간", notes);
        Safety.StepTimeoutSeconds = Clamp(Safety.StepTimeoutSeconds, 2, 120, "단계 제한 시간", notes);
        Safety.MaxConsecutiveFailures = Clamp(Safety.MaxConsecutiveFailures, 1, 10, "연속 실패 한도", notes);
        Safety.MaxReplans = Clamp(Safety.MaxReplans, 0, 10, "재계획 한도", notes);
        Safety.MaxModelCallsPerTask = Clamp(Safety.MaxModelCallsPerTask, 1, 50, "모델 호출 한도", notes);
        Safety.ApprovalTimeoutSeconds = Clamp(Safety.ApprovalTimeoutSeconds, 10, 900, "승인 대기 시간", notes);
        Safety.MaxTextLength = Clamp(Safety.MaxTextLength, 10, 20000, "입력 텍스트 상한", notes);
        Safety.MinActionIntervalMs = Clamp(Safety.MinActionIntervalMs, 0, 5000, "동작 간 최소 간격", notes);
        General.HistoryRetentionDays = Clamp(General.HistoryRetentionDays, 1, 3650, "기록 보존 기간", notes);
        General.ScreenshotRetentionDays = Clamp(General.ScreenshotRetentionDays, 1, 365, "스크린샷 보존 기간", notes);
        if (General.FontScale is < 0.8 or > 2.0 || double.IsNaN(General.FontScale))
        {
            General.FontScale = 1.0;
            notes.Add("글자 크기 배율을 기본값으로 되돌렸습니다.");
        }

        foreach (var app in Safety.AllowedApps)
        {
            app.ProcessNames ??= [];
            app.ProcessNames = app.ProcessNames
                .Select(n => NormalizeProcessName(n))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        foreach (var profile in ModelProfiles)
        {
            profile.TimeoutSeconds = Clamp(profile.TimeoutSeconds, 5, 900, "모델 응답 제한 시간", notes);
            profile.MaxTokens = Clamp(profile.MaxTokens, 128, 32768, "최대 출력 토큰", notes);
            if (profile.Temperature is < 0 or > 2 || double.IsNaN(profile.Temperature))
            {
                profile.Temperature = 0;
            }
        }

        if (ModelProfiles.Count == 0)
        {
            ModelProfiles.AddRange(ModelProfile.Defaults());
            notes.Add("모델 연결 프로필이 없어 기본 프로필을 추가했습니다.");
        }

        if (ActiveProfile is null)
        {
            ActiveModelProfileId = ModelProfiles[0].Id;
        }

        return notes;
    }

    public static string NormalizeProcessName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed;
    }

    private static int Clamp(int value, int min, int max, string label, List<string> notes)
    {
        if (value < min || value > max)
        {
            notes.Add($"{label} 값({value})을 허용 범위 {min}~{max}로 조정했습니다.");
            return Math.Clamp(value, min, max);
        }

        return value;
    }
}

public sealed class GeneralSettings
{
    /// <summary>Screenshots of other programs are only kept when the user turns this on.</summary>
    public bool SaveScreenshots { get; set; }
    public int ScreenshotRetentionDays { get; set; } = 7;
    public int HistoryRetentionDays { get; set; } = 180;

    [JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))]
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    public string Theme { get; set; } = "Dark";
    public double FontScale { get; set; } = 1.0;

    /// <summary>A database backup is made at start when the newest one is older than this many days (0 = off).</summary>
    public int AutoBackupDays { get; set; } = 7;
}

public enum AppPermission
{
    /// <summary>Only reading: observe, read_element, wait_for.</summary>
    ReadOnly,

    /// <summary>Reading and the limited UI actions (click, type, select, ...), subject to approval rules.</summary>
    Interact,
}

public sealed class AllowedApp
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Process names without ".exe", compared case-insensitively.</summary>
    public List<string> ProcessNames { get; set; } = [];

    /// <summary>What <c>launch_app</c> starts (an executable name, full path or a shell URI); null = the operator may not start it.</summary>
    public string? LaunchPath { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<AppPermission>))]
    public AppPermission Permission { get; set; } = AppPermission.Interact;

    public bool Enabled { get; set; } = true;
    public bool RequireApprovalForLaunch { get; set; }
    public bool RequireApprovalForAllActions { get; set; }

    /// <summary>Any change in this app is a Windows setting change (always needs approval).</summary>
    public bool IsSystemSettings { get; set; }

    public bool MatchesProcess(string processName) =>
        ProcessNames.Any(p => string.Equals(p, AppSettings.NormalizeProcessName(processName), StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<AllowedApp> Defaults() =>
    [
        new()
        {
            Id = "test-target", DisplayName = "Operator 테스트 앱", ProcessNames = ["OperatorTestTarget"],
            LaunchPath = "OperatorTestTarget.exe", Permission = AppPermission.Interact,
        },
        new()
        {
            Id = "notepad", DisplayName = "메모장", ProcessNames = ["notepad"], LaunchPath = "notepad.exe",
            Permission = AppPermission.Interact,
        },
        new()
        {
            Id = "calculator", DisplayName = "계산기", ProcessNames = ["CalculatorApp", "calc"], LaunchPath = "calc.exe",
            Permission = AppPermission.Interact,
        },
        new()
        {
            Id = "settings", DisplayName = "Windows 설정(읽기 전용)", ProcessNames = ["SystemSettings"], LaunchPath = "ms-settings:",
            Permission = AppPermission.ReadOnly, IsSystemSettings = true,
        },
        new()
        {
            Id = "explorer", DisplayName = "파일 탐색기(읽기 전용, 기본 꺼짐)", ProcessNames = ["explorer"], LaunchPath = null,
            Permission = AppPermission.ReadOnly, Enabled = false,
        },
    ];
}

public sealed class SafetySettings
{
    public List<AllowedApp> AllowedApps { get; set; } = [];

    /// <summary>Additional process names that can never be automated (added to the built-in block list).</summary>
    public List<string> ExtraBlockedProcesses { get; set; } = [];

    /// <summary>Folders <c>list_directory</c> may read (names, sizes and dates only).</summary>
    public List<string> GrantedFolders { get; set; } = [];

    public int MaxStepsPerTask { get; set; } = 30;
    public int TaskTimeoutSeconds { get; set; } = 300;
    public int StepTimeoutSeconds { get; set; } = 15;
    public int MaxConsecutiveFailures { get; set; } = 3;
    public int MaxReplans { get; set; } = 3;
    public int MaxModelCallsPerTask { get; set; } = 12;
    public int ApprovalTimeoutSeconds { get; set; } = 120;
    public int MaxTextLength { get; set; } = 2000;
    public int MinActionIntervalMs { get; set; } = 150;

    /// <summary>Show the whole plan and wait for "실행" before the first action.</summary>
    public bool RequirePlanReview { get; set; } = true;

    public bool RequireApprovalForAllInteractions { get; set; }
    public bool RequireApprovalForConfirmButtons { get; set; } = true;
    public bool RequireApprovalForCoordinateClicks { get; set; } = true;
    public bool RequireApprovalForCloseButtons { get; set; } = true;

    /// <summary>Ask before every state-changing step once UI text in the task looked like an injected instruction.</summary>
    public bool InjectionGuard { get; set; } = true;

    /// <summary>Allows screenshots to be sent to a model that really accepts images (vision fallback).</summary>
    public bool AllowVision { get; set; } = true;

    public AllowedApp? FindApp(string? appId) =>
        appId is null ? null : AllowedApps.FirstOrDefault(a => string.Equals(a.Id, appId, StringComparison.OrdinalIgnoreCase));

    public AllowedApp? FindAppForProcess(string processName) =>
        AllowedApps.FirstOrDefault(a => a.Enabled && a.MatchesProcess(processName));
}

public enum ProviderKind
{
    OpenAiCompatible,
    Strata,
    Mock,
}

public enum ApiKeySource
{
    None,
    EnvironmentVariable,

    /// <summary>Encrypted with Windows DPAPI (current user) in secrets.dat; never in settings.json.</summary>
    StoredEncrypted,
}

public enum StructuredOutputMode
{
    Auto,
    JsonSchema,
    JsonObject,
    PromptOnly,
}

public enum VisionPreference
{
    /// <summary>Use images only when the server reports image input or the image test passed.</summary>
    UseWhenSupported,
    Disabled,
}

public sealed class ModelProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "새 연결";

    [JsonConverter(typeof(JsonStringEnumConverter<ProviderKind>))]
    public ProviderKind Kind { get; set; } = ProviderKind.OpenAiCompatible;

    /// <summary>OpenAI-compatible base URL including /v1, e.g. http://127.0.0.1:8080/v1.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8080/v1";

    /// <summary>Model id chosen from the server's /v1/models list. Never hard-coded.</summary>
    public string? Model { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ApiKeySource>))]
    public ApiKeySource KeySource { get; set; } = ApiKeySource.None;

    public string? KeyEnvironmentVariable { get; set; }
    public int TimeoutSeconds { get; set; } = 180;
    public int MaxTokens { get; set; } = 2048;
    public double Temperature { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<StructuredOutputMode>))]
    public StructuredOutputMode StructuredOutput { get; set; } = StructuredOutputMode.Auto;

    [JsonConverter(typeof(JsonStringEnumConverter<VisionPreference>))]
    public VisionPreference Vision { get; set; } = VisionPreference.UseWhenSupported;

    /// <summary>The user explicitly chose to send requests (UI text) to a non-local server.</summary>
    public bool AllowRemote { get; set; }

    /// <summary>The user explicitly chose to send screenshots to a non-local server.</summary>
    public bool AllowRemoteScreenshots { get; set; }

    /// <summary>The image test passed for <see cref="VisionVerifiedModel"/> (cleared when the model changes).</summary>
    public bool VisionVerified { get; set; }
    public string? VisionVerifiedModel { get; set; }

    public static IEnumerable<ModelProfile> Defaults() =>
    [
        new() { Id = "strata-local", Name = "Strata (로컬 서버 8080)", Kind = ProviderKind.Strata, BaseUrl = "http://127.0.0.1:8080/v1" },
        new() { Id = "strata-desktop", Name = "Strata 데스크톱 앱 (47831)", Kind = ProviderKind.Strata, BaseUrl = "http://127.0.0.1:47831/v1" },
        new() { Id = "openai-compatible", Name = "OpenAI 호환 로컬 서버 (예: LM Studio 1234)", Kind = ProviderKind.OpenAiCompatible, BaseUrl = "http://127.0.0.1:1234/v1" },
    ];
}
