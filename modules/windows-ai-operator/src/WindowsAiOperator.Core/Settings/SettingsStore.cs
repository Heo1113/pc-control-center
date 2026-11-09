using System.Text;
using System.Text.Json;

namespace WindowsAiOperator.Core.Settings;

/// <summary>Reads and writes settings.json. A broken file is kept aside (renamed, not deleted) and defaults are used.</summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public SettingsStore(string path) => _path = path;

    public string Path => _path;

    public (AppSettings Settings, IReadOnlyList<string> Notes) Load()
    {
        var notes = new List<string>();
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                var fresh = AppSettings.CreateDefault();
                SaveUnlocked(fresh);
                notes.Add("설정 파일이 없어 기본 설정을 만들었습니다.");
                return (fresh, notes);
            }

            try
            {
                var text = File.ReadAllText(_path, Encoding.UTF8);
                var settings = JsonSerializer.Deserialize<AppSettings>(text, JsonOptions) ?? throw new JsonException("빈 설정");
                if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
                {
                    notes.Add($"이 프로그램보다 새로운 설정 형식(v{settings.SchemaVersion})이라 읽기만 하고 저장하지 않습니다.");
                }

                notes.AddRange(settings.Normalize());
                return (settings, notes);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException)
            {
                var aside = _path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
                try
                {
                    File.Move(_path, aside);
                    notes.Add($"설정 파일을 읽지 못해 '{System.IO.Path.GetFileName(aside)}'로 옮겨 두고 기본 설정을 사용합니다.");
                }
                catch (IOException)
                {
                    notes.Add("설정 파일을 읽지 못해 기본 설정을 사용합니다(원본은 그대로 둡니다).");
                    return (AppSettings.CreateDefault(), notes);
                }

                var fresh = AppSettings.CreateDefault();
                SaveUnlocked(fresh);
                return (fresh, notes);
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                throw new InvalidOperationException("새로운 형식의 설정 파일은 덮어쓰지 않습니다.");
            }

            SaveUnlocked(settings);
        }
    }

    private void SaveUnlocked(AppSettings settings)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, _path, overwrite: true);
    }
}
