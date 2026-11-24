using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAiPerf.Core.Settings;

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
    };

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static AppSettings Deserialize(string json)
        => JsonSerializer.Deserialize<AppSettings>(json, Options) ?? throw new JsonException("Settings file is empty.");
}

public sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Notes, bool WasCreated);

/// <summary>Loads and saves settings.json. A broken file is moved aside and defaults are used.</summary>
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
                File.Move(Path, aside, overwrite: true);
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
