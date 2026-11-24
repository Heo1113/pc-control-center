using System.Text.Json;
using System.Text.Json.Serialization;
using WinDiag.Core.Events;

namespace WinDiag.Core.Settings;

/// <summary>An event log channel the collector reads.</summary>
public sealed class ChannelSetting
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class CollectionSettings
{
    public const string SecurityChannel = "Security";

    public static IReadOnlyList<ChannelSetting> DefaultChannels =>
    [
        new() { Name = "System", Enabled = true },
        new() { Name = "Application", Enabled = true },
        new() { Name = "Setup", Enabled = true },
        new() { Name = "Microsoft-Windows-Kernel-PnP/Configuration", Enabled = true },
        new() { Name = "Microsoft-Windows-WindowsUpdateClient/Operational", Enabled = false },
    ];

    public List<ChannelSetting> Channels { get; set; } = [.. DefaultChannels];

    /// <summary>How far back the first history import reads (days).</summary>
    public int HistoryDays { get; set; } = 30;

    /// <summary>How often new events are read while the app runs (seconds).</summary>
    public int PollSeconds { get; set; } = 10;

    /// <summary>Also collect warnings of providers outside the catalog (more noise).</summary>
    public bool CollectAllWarnings { get; set; }

    public List<string> ExcludedProviders { get; set; } = [.. EventCatalog.DefaultExcludedProviders];
}

public sealed class SensorSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 5;
    public bool GpuEnabled { get; set; } = true;
    public bool SystemEnabled { get; set; } = true;
}

public sealed class RetentionSettings
{
    /// <summary>Events older than this are removed by the retention task. 0 keeps events forever (default).</summary>
    public int EventDays { get; set; }

    /// <summary>Sensor samples older than this are removed by the retention task.</summary>
    public int SensorDays { get; set; } = 30;

    /// <summary>A warning is shown above this size. Nothing is deleted because of the size.</summary>
    public int WarnDatabaseMb { get; set; } = 2048;
}

public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Also show a Windows notification-area balloon (in addition to the in-app banner).</summary>
    public bool TrayBalloon { get; set; } = true;

    public DiagSeverity MinimumSeverity { get; set; } = DiagSeverity.Error;

    /// <summary>Repeats of the same problem within this window are merged into one notification.</summary>
    public int RepeatQuietMinutes { get; set; } = 10;
}

public sealed class PrivacySettings
{
    /// <summary>Mask user names, profile paths, SIDs, e-mail addresses and secrets before storing (default on).</summary>
    public bool MaskAtCollection { get; set; } = true;
}

public sealed class SnapshotSettings
{
    public int IntervalHours { get; set; } = 6;

    /// <summary>The driver inventory (WMI Win32_PnPSignedDriver) can take several seconds.</summary>
    public bool IncludeDrivers { get; set; } = true;
}

public sealed class AnalysisSettings
{
    /// <summary>Two events closer than this are treated as "at the same time" for co-occurrence.</summary>
    public int CorrelationWindowSeconds { get; set; } = 120;

    /// <summary>Sensor data shown before and after an event.</summary>
    public int SensorWindowMinutes { get; set; } = 10;

    /// <summary>A group with at least this many events in the analysed range counts as a repeat pattern.</summary>
    public int RepeatThreshold { get; set; } = 3;

    public int HighGpuTempC { get; set; } = 85;
}

public sealed class UiSettings
{
    public bool UseUtc { get; set; }
}

/// <summary>User settings (no secrets). Saved as settings.json in the data folder.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public CollectionSettings Collection { get; set; } = new();
    public SensorSettings Sensors { get; set; } = new();
    public RetentionSettings Retention { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public SnapshotSettings Snapshots { get; set; } = new();
    public AnalysisSettings Analysis { get; set; } = new();
    public UiSettings Ui { get; set; } = new();

    public AppSettings Clone() => SettingsSerializer.Deserialize(SettingsSerializer.Serialize(this));

    /// <summary>Clamps every value into its valid range. Returns notes about what was fixed.</summary>
    public IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        Collection ??= new();
        Sensors ??= new();
        Retention ??= new();
        Notifications ??= new();
        Privacy ??= new();
        Snapshots ??= new();
        Analysis ??= new();
        Ui ??= new();

        Collection.Channels ??= [.. CollectionSettings.DefaultChannels];
        Collection.Channels = Collection.Channels
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ChannelSetting { Name = g.Key, Enabled = g.First().Enabled })
            .ToList();
        if (Collection.Channels.RemoveAll(c => string.Equals(c.Name, CollectionSettings.SecurityChannel, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            notes.Add("보안(Security) 로그는 이 버전에서 수집하지 않으므로 목록에서 뺐습니다 (관리자 권한 필요, 민감 정보 포함).");
        }

        if (!Collection.Channels.Any(c => string.Equals(c.Name, "System", StringComparison.OrdinalIgnoreCase)))
        {
            Collection.Channels.Insert(0, new ChannelSetting { Name = "System", Enabled = true });
            notes.Add("System 로그는 진단에 필수라서 목록에 다시 추가했습니다.");
        }

        Collection.ExcludedProviders ??= [];
        Collection.HistoryDays = Clamp(Collection.HistoryDays, 1, 3650, "과거 로그 조회 기간", notes);
        Collection.PollSeconds = Clamp(Collection.PollSeconds, 2, 600, "새 이벤트 확인 간격", notes);
        Sensors.IntervalSeconds = Clamp(Sensors.IntervalSeconds, 1, 60, "센서 기록 간격", notes);
        Retention.EventDays = Retention.EventDays == 0 ? 0 : Clamp(Retention.EventDays, 7, 36500, "이벤트 보존 기간", notes);
        Retention.SensorDays = Clamp(Retention.SensorDays, 1, 3650, "센서 기록 보존 기간", notes);
        Retention.WarnDatabaseMb = Clamp(Retention.WarnDatabaseMb, 64, 1_048_576, "데이터베이스 크기 경고 기준", notes);
        Notifications.RepeatQuietMinutes = Clamp(Notifications.RepeatQuietMinutes, 0, 1440, "반복 알림 묶음 시간", notes);
        if (!Enum.IsDefined(Notifications.MinimumSeverity))
        {
            Notifications.MinimumSeverity = DiagSeverity.Error;
            notes.Add("알림 최소 심각도가 올바르지 않아 기본값(오류)으로 되돌렸습니다.");
        }

        Snapshots.IntervalHours = Clamp(Snapshots.IntervalHours, 1, 168, "시스템 스냅샷 간격", notes);
        Analysis.CorrelationWindowSeconds = Clamp(Analysis.CorrelationWindowSeconds, 5, 3600, "동시 발생 판정 구간", notes);
        Analysis.SensorWindowMinutes = Clamp(Analysis.SensorWindowMinutes, 1, 120, "이벤트 전후 센서 구간", notes);
        Analysis.RepeatThreshold = Clamp(Analysis.RepeatThreshold, 2, 1000, "반복 판정 기준", notes);
        Analysis.HighGpuTempC = Clamp(Analysis.HighGpuTempC, 50, 110, "GPU 고온 기준", notes);
        SchemaVersion = CurrentSchemaVersion;
        return notes;
    }

    private static int Clamp(int value, int min, int max, string name, List<string> notes)
    {
        if (value >= min && value <= max)
        {
            return value;
        }

        var fixedValue = Math.Clamp(value, min, max);
        notes.Add($"{name} 값 {value}이(가) 허용 범위({min}~{max})를 벗어나 {fixedValue}(으)로 조정했습니다.");
        return fixedValue;
    }
}

public static class SettingsSerializer
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static AppSettings Deserialize(string json)
        => JsonSerializer.Deserialize<AppSettings>(json, Options) ?? throw new JsonException("설정 파일이 비어 있습니다.");
}

public sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Notes, bool WasCreated);

/// <summary>
/// Loads and saves settings.json. A broken file is moved aside (never deleted) and defaults are used.
/// Adapted from project A (LocalAiPerf.Core/Settings/SettingsSerializer.cs, commit 5b6aae1).
/// </summary>
public sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    public SettingsLoadResult Load()
    {
        var notes = new List<string>();
        if (!File.Exists(Path))
        {
            var created = new AppSettings();
            created.Normalize();
            return new SettingsLoadResult(created, notes, true);
        }

        try
        {
            var settings = SettingsSerializer.Deserialize(File.ReadAllText(Path));
            notes.AddRange(settings.Normalize());
            return new SettingsLoadResult(settings, notes, false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var aside = $"{Path}.invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Move(Path, aside, overwrite: false);
                notes.Add($"설정 파일을 읽을 수 없어 기본값을 사용합니다. 기존 파일은 {System.IO.Path.GetFileName(aside)}(으)로 보관했습니다. ({ex.GetType().Name})");
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                notes.Add($"설정 파일을 읽을 수 없어 기본값을 사용합니다. ({ex.GetType().Name})");
            }

            var defaults = new AppSettings();
            defaults.Normalize();
            return new SettingsLoadResult(defaults, notes, true);
        }
    }

    /// <summary>Writes atomically: a temporary file is written and then moved over the old one.</summary>
    public void Save(AppSettings settings)
    {
        settings.Normalize();
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path + ".tmp";
        File.WriteAllText(temp, SettingsSerializer.Serialize(settings));
        File.Move(temp, Path, overwrite: true);
    }
}
