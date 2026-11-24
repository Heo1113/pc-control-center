using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuProfit.Core.Settings;

/// <summary>
/// Machine-level settings (sampling, retention, display) kept in settings.json. Accounting conditions are not here:
/// they live in the database (<see cref="Domain.AccountingProfile"/>) so that a backup carries them with the data.
/// </summary>
public sealed class AppSettings
{
    public SamplingOptions Sampling { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public UiOptions Ui { get; set; } = new();

    /// <summary>Clamps out-of-range values and returns a note for each change.</summary>
    public IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        Sampling ??= new SamplingOptions();
        Storage ??= new StorageOptions();
        Ui ??= new UiOptions();
        if (Sampling.IntervalSeconds is < 1 or > 60)
        {
            notes.Add($"샘플 간격 {Sampling.IntervalSeconds}초는 허용 범위(1~60초)를 벗어나 1초로 바꿨습니다.");
            Sampling.IntervalSeconds = 1;
        }

        if (Storage.SampleRetentionDays is < 0 or > 3650)
        {
            notes.Add("센서 기록 보존 기간이 범위를 벗어나 0(무기한)으로 바꿨습니다.");
            Storage.SampleRetentionDays = 0;
        }

        if (Ui.FontScale is < 0.9 or > 1.6 || !double.IsFinite(Ui.FontScale))
        {
            notes.Add("글자 크기 배율이 범위를 벗어나 100%로 바꿨습니다.");
            Ui.FontScale = 1.0;
        }

        return notes;
    }
}

public sealed class SamplingOptions
{
    public int IntervalSeconds { get; set; } = 1;
    public bool GpuEnabled { get; set; } = true;
    public bool SystemEnabled { get; set; } = true;
}

public sealed class StorageOptions
{
    /// <summary>Sensor samples older than this many days are removed, except those inside a session. 0 keeps everything.</summary>
    public int SampleRetentionDays { get; set; } = 180;
}

public sealed class UiOptions
{
    /// <summary>Scales the whole window (accessibility). 1.0 = 100 %.</summary>
    public double FontScale { get; set; } = 1.0;
    public bool UseUtc { get; set; }
}

public sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Notes, bool WasCreated);

/// <summary>Loads and saves settings.json. Adapted from project C (WinDiag.Core/Settings/AppSettings.cs, commit 06cc9e5).</summary>
public sealed class SettingsStore(string path)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

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
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json) ?? throw new JsonException("빈 설정 파일");
            notes.AddRange(settings.Normalize());
            return new SettingsLoadResult(settings, notes, false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The unreadable file is kept next to the new one, never deleted.
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
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, Path, overwrite: true);
    }
}
