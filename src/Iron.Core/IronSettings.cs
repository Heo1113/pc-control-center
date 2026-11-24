using System.Text.Json;
using System.Text.Json.Serialization;
using Suite.Core.Storage;

namespace Iron.Core;

/// <summary>
/// IRON's own settings (<c>data\iron\iron-settings.json</c>). Every section keeps fields it does not know (from a newer
/// version or added by hand) and writes them back unchanged.
/// </summary>
public sealed class IronSettings : IVersionedSettings<IronSettings>
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public static int CurrentSchemaVersion => 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public IronHardwareSettings Hardware { get; set; } = new();
    public IronRecordingSettings Recording { get; set; } = new();
    public IronWindowSettings Window { get; set; } = new();
    public IronElectricitySettings Electricity { get; set; } = new();
    public IronAlertSettings Alerts { get; set; } = new();

    public IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        Hardware ??= new IronHardwareSettings();
        Recording ??= new IronRecordingSettings();
        Window ??= new IronWindowSettings();
        Electricity ??= new IronElectricitySettings();
        Alerts ??= new IronAlertSettings();
        notes.AddRange(Hardware.Normalize());
        notes.AddRange(Recording.Normalize());
        notes.AddRange(Window.Normalize());
        notes.AddRange(Electricity.Normalize());
        notes.AddRange(Alerts.Normalize());
        return notes;
    }

    internal static int InRange(int value, int min, int max, int fallback, string label, string unit, List<string> notes)
    {
        if (value >= min && value <= max)
        {
            return value;
        }

        notes.Add($"{label} {value}{unit}은(는) 허용 범위({min}~{max}{unit})를 벗어나 {fallback}{unit}(으)로 바꿨습니다.");
        return fallback;
    }
}

/// <summary>The shared hub's sensors (NVML GPU, Windows CPU/RAM) as IRON reads them.</summary>
public sealed class IronHardwareSettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public const int MinIntervalMs = 500;
    public const int MaxIntervalMs = 5000;

    public bool GpuEnabled { get; set; } = true;
    public bool SystemEnabled { get; set; } = true;

    /// <summary>Reading interval of the shared hub (the overview, the tray and the external devices all use these readings).</summary>
    public int IntervalMs { get; set; } = 1000;

    internal IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        IntervalMs = IronSettings.InRange(IntervalMs, MinIntervalMs, MaxIntervalMs, 1000, "센서 간격", " ms", notes);
        return notes;
    }
}

/// <summary>What IRON writes into <c>iron-telemetry.db</c>.</summary>
public sealed class IronRecordingSettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>One stored reading every N seconds (the screen still shows every reading).</summary>
    public int IntervalSeconds { get; set; } = 5;

    /// <summary>Stored readings older than this are removed.</summary>
    public int RetentionDays { get; set; } = 30;

    internal IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        IntervalSeconds = IronSettings.InRange(IntervalSeconds, 1, 60, 5, "기록 간격", "초", notes);
        RetentionDays = IronSettings.InRange(RetentionDays, 1, 365, 30, "기록 보관 기간", "일", notes);
        return notes;
    }
}

public sealed class IronWindowSettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public const string Maximized = "Maximized";
    public const string HalfWidth = "HalfWidth";

    public static readonly IReadOnlySet<string> Layouts = new HashSet<string>(StringComparer.Ordinal) { Maximized, HalfWidth };

    /// <summary><see cref="Maximized"/> (4K 175%: 2194×1186 DIP) or <see cref="HalfWidth"/> (1097×1186 DIP).</summary>
    public string StartLayout { get; set; } = Maximized;

    /// <summary>Closing the window keeps IRON in the system tray (Exit in the tray menu ends it).</summary>
    public bool CloseToTray { get; set; } = true;

    public bool StartInTray { get; set; }

    internal IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        if (StartLayout is null || !Layouts.Contains(StartLayout))
        {
            notes.Add($"시작 창 배치 '{StartLayout}'을(를) 알 수 없어 {Maximized}로 바꿨습니다.");
            StartLayout = Maximized;
        }

        return notes;
    }
}

/// <summary>The rate for the estimated electricity cost per hour (GPU board power only; null = not set, shown as N/A).</summary>
public sealed class IronElectricitySettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public const decimal MaxRate = 10_000m;

    public decimal? RateKrwPerKwh { get; set; }

    internal IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        if (RateKrwPerKwh is decimal rate && (rate < 0 || rate > MaxRate))
        {
            notes.Add($"전기 요금 {rate} 원/kWh는 허용 범위(0~{MaxRate:0} 원/kWh)를 벗어나 지웠습니다 (요금 미설정).");
            RateKrwPerKwh = null;
        }

        return notes;
    }
}

/// <summary>
/// When a value turns red: only that value and the status LED change color (no banner, no red frame), and they return
/// to normal when the value does.
/// </summary>
public sealed class IronAlertSettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public int GpuTemperatureC { get; set; } = 85;
    public int VramPercent { get; set; } = 95;

    internal IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        GpuTemperatureC = IronSettings.InRange(GpuTemperatureC, 50, 110, 85, "GPU 온도 경고", " °C", notes);
        VramPercent = IronSettings.InRange(VramPercent, 50, 100, 95, "VRAM 경고", " %", notes);
        return notes;
    }
}
