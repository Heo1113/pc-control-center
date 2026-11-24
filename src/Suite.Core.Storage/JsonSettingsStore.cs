using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suite.Core.Storage;

/// <summary>
/// A product's settings object. Every settings class (the root and each nested section) should carry
/// <c>[JsonExtensionData] public Dictionary&lt;string, JsonElement&gt;? AdditionalSettings</c> so fields written by a newer
/// version, or added by hand, survive a load and save unchanged.
/// </summary>
public interface IVersionedSettings<TSelf>
    where TSelf : class, IVersionedSettings<TSelf>, new()
{
    /// <summary>The schema this build writes.</summary>
    static abstract int CurrentSchemaVersion { get; }

    /// <summary>The schema of the file it was read from (kept on save, so a newer file is not marked older).</summary>
    int SchemaVersion { get; set; }

    /// <summary>Fixes missing sections and out-of-range values. Returns a note for each change (shown, never silent).</summary>
    IReadOnlyList<string> Normalize();
}

/// <param name="ReadFailed">The file existed but could not be read; <paramref name="Settings"/> are defaults.</param>
/// <param name="KeptAsidePath">Where the unreadable file was moved. Null with <paramref name="ReadFailed"/> = it is still in
/// place, and saving would replace it (the caller should ask first).</param>
public sealed record LoadedSettings<T>(T Settings, IReadOnlyList<string> Notes, bool WasCreated, bool ReadFailed = false, string? KeptAsidePath = null);

/// <summary>
/// Loads and saves one product's settings file, with the guarantees of PC Control Center's ShellSettingsStore:
/// writes are atomic (temporary file, then replace); a file that cannot be read is moved aside (never deleted, never
/// overwritten) and defaults are used; unknown fields at any depth are written back as they were; a file from a newer
/// schema is read and its version kept.
/// </summary>
public sealed class JsonSettingsStore<T>(string path, TimeProvider? time = null)
    where T : class, IVersionedSettings<T>, new()
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public LoadedSettings<T> Load()
    {
        if (!File.Exists(Path))
        {
            var created = new T();
            return new LoadedSettings<T>(created, created.Normalize(), WasCreated: true);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<T>(File.ReadAllText(Path), JsonOptions) ?? new T();
            var notes = new List<string>(settings.Normalize());
            if (settings.SchemaVersion > T.CurrentSchemaVersion)
            {
                notes.Add($"설정 파일이 더 새 버전(스키마 {settings.SchemaVersion})에서 저장되었습니다. 알 수 없는 필드도 저장할 때 보존합니다.");
            }

            return new LoadedSettings<T>(settings, notes, WasCreated: false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            var aside = KeepAside();
            var defaults = new T();
            defaults.Normalize();
            var where = aside is null ? "그대로 두었습니다 (저장하지 않으면 바뀌지 않습니다)" : $"'{System.IO.Path.GetFileName(aside)}'로 보관했습니다";
            return new LoadedSettings<T>(defaults, [$"설정 파일을 읽지 못해 기본값을 사용합니다 ({ex.GetType().Name}). 원래 파일은 {where}."],
                WasCreated: false, ReadFailed: true, KeptAsidePath: aside);
        }
    }

    public void Save(T settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private string? KeepAside()
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(Path);
        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? "" : $"-{attempt}";
            var aside = System.IO.Path.Combine(directory, $"{name}.corrupt-{stamp}{suffix}.json");
            if (File.Exists(aside))
            {
                continue;
            }

            try
            {
                File.Move(Path, aside);
                return aside;
            }
            catch (IOException) when (File.Exists(aside))
            {
                // Taken between the check and the move: try the next name.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }
}
