using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcControlCenter.Core;

/// <summary>
/// Settings that belong to the integrated app itself. Each module keeps its own settings.json in its folder with the
/// same format as the original program; those are edited on the module's settings page.
/// </summary>
public sealed class ShellSettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public SharedHardwareSettings Hardware { get; set; } = new();
    public ModuleSwitches Modules { get; set; } = new();
    public ShellUiSettings Ui { get; set; } = new();
    public OperatorHostSettings Operator { get; set; } = new();

    /// <summary>How the hardware › GPU 메모리 page was left (sort, columns, panel size). Missing in older files = defaults.</summary>
    public GpuMemoryPageSettings GpuMemoryPage { get; set; } = new();

    /// <summary>The local Basalt server the 로컬 AI › Basalt page starts and stops. Missing in older files = defaults.</summary>
    public BasaltSettings Basalt { get; set; } = new();

    /// <summary>Fixes out-of-range values. Returns a note for each change (shown to the user, never silent).</summary>
    public IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        Hardware ??= new SharedHardwareSettings();
        Modules ??= new ModuleSwitches();
        Ui ??= new ShellUiSettings();
        Operator ??= new OperatorHostSettings();
        GpuMemoryPage ??= new GpuMemoryPageSettings();
        GpuMemoryPage.Normalize();
        Basalt ??= new BasaltSettings();
        notes.AddRange(Basalt.Normalize());
        if (Hardware.MaxIntervalMs is < SharedHardwareSettings.MinIntervalMs or > SharedHardwareSettings.MaxIntervalMsLimit)
        {
            notes.Add($"공통 센서 최대 간격 {Hardware.MaxIntervalMs} ms는 허용 범위({SharedHardwareSettings.MinIntervalMs}~{SharedHardwareSettings.MaxIntervalMsLimit} ms)를 벗어나 1000 ms로 바꿨습니다.");
            Hardware.MaxIntervalMs = 1000;
        }

        if (Ui.FontScale is < 0.8 or > 2.0 || double.IsNaN(Ui.FontScale))
        {
            notes.Add($"글자 크기 배율 {Ui.FontScale}은(는) 허용 범위(0.8~2.0)를 벗어나 1.0으로 바꿨습니다.");
            Ui.FontScale = 1.0;
        }

        if (Operator.MaxAutomaticRestarts is < 0 or > 10)
        {
            notes.Add($"Operator 자동 재시작 횟수 {Operator.MaxAutomaticRestarts}은(는) 허용 범위(0~10)를 벗어나 3으로 바꿨습니다.");
            Operator.MaxAutomaticRestarts = 3;
        }

        return notes;
    }

    public bool IsEnabled(string moduleId) => moduleId switch
    {
        ModuleIds.LocalAi => Modules.LocalAi,
        ModuleIds.GpuEconomics => Modules.GpuEconomics,
        ModuleIds.Diagnostics => Modules.Diagnostics,
        ModuleIds.Operator => Modules.Operator,
        _ => false,
    };
}

/// <summary>The one hardware reader shared by all modules.</summary>
public sealed class SharedHardwareSettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public const int MinIntervalMs = 500;
    public const int MaxIntervalMsLimit = 60_000;

    /// <summary>
    /// Upper bound for the reading interval. The reader runs at the fastest interval any running module asks for
    /// (A: Sampling.IntervalMs, B: Sampling.IntervalSeconds, C: Sensors.IntervalSeconds), but never slower than this.
    /// </summary>
    public int MaxIntervalMs { get; set; } = 1000;

    /// <summary>NVIDIA GPU sensors (NVML). Off = GPU values are N/A in every module.</summary>
    public bool GpuEnabled { get; set; } = true;

    /// <summary>Windows CPU and RAM counters.</summary>
    public bool SystemEnabled { get; set; } = true;

    /// <summary>
    /// GPU memory per process and per adapter from the Windows performance counters (hardware › GPU 메모리). Read on a
    /// thread of its own, not by the shared reader; off = that page shows N/A. Missing in older files = on.
    /// </summary>
    public bool ProcessMemoryEnabled { get; set; } = true;
}

/// <summary>Turns whole modules off (for example the Operator on a PC where no automation should run).</summary>
public sealed class ModuleSwitches
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public bool LocalAi { get; set; } = true;
    public bool GpuEconomics { get; set; } = true;
    public bool Diagnostics { get; set; } = true;
    public bool Operator { get; set; } = true;
}

public sealed class ShellUiSettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public double FontScale { get; set; } = 1.0;
}

/// <summary>
/// View state of the GPU memory page, remembered across visits and restarts. Column keys are the table's sort member
/// names; unknown keys (from a newer or edited file) are dropped silently since they only affect the layout.
/// </summary>
public sealed class GpuMemoryPageSettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public static readonly IReadOnlySet<string> ColumnKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "Name", "Pid", "KindLabel", "Dedicated", "Delta", "Shared", "Gpu", "Runtimes", "Inside", "Adapter",
    };

    public static readonly IReadOnlySet<string> ChartMetrics = new HashSet<string>(StringComparer.Ordinal) { "Dedicated", "Shared", "Gpu" };

    public static readonly IReadOnlyList<int> ChartWindows = [5, 15, 30];

    /// <summary>Where the detail panel goes: "Auto" (beside the table when the width allows it), "Below" or "Beside".</summary>
    public static readonly IReadOnlySet<string> Layouts = new HashSet<string>(StringComparer.Ordinal) { "Auto", "Below", "Beside" };

    /// <summary>The kinds a process can be given (the names of PcControlCenter.GpuMemory.ProcessKind).</summary>
    public static readonly IReadOnlySet<string> KindNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Llm", "Ai", "Compute", "Graphics3D", "Video", "Browser", "Compositor", "System", "Kernel", "App",
    };

    public const int MaxKindOverrides = 200;

    /// <summary>Sort column; null = dedicated memory, largest first.</summary>
    public string? SortColumn { get; set; }

    public bool SortDescending { get; set; } = true;

    /// <summary>Also list processes below the listing thresholds.</summary>
    public bool ShowAll { get; set; }

    /// <summary>Hidden table columns (never the process name).</summary>
    public List<string> HiddenColumns { get; set; } = [];

    /// <summary>Height of the detail panel in device-independent pixels; 0 = share the height with the table.</summary>
    public double DetailHeight { get; set; }

    /// <summary>Where the detail panel goes (see <see cref="Layouts"/>); the user's choice is kept.</summary>
    public string Layout { get; set; } = "Auto";

    /// <summary>Width of the detail panel beside the table, in device-independent pixels; 0 = the default share.</summary>
    public double DetailWidth { get; set; }

    /// <summary>What the detail chart shows: Dedicated, Shared or Gpu (the busiest engine, %).</summary>
    public string ChartMetric { get; set; } = "Dedicated";

    /// <summary>The detail chart's window in minutes (5, 15 or 30; the reader keeps 30).</summary>
    public int ChartMinutes { get; set; } = 15;

    /// <summary>
    /// Kinds the user gave by executable name (lower case, such as "myapp.exe"), used instead of the classification from
    /// loaded libraries, model files and names. Only the name and the kind are kept.
    /// </summary>
    public Dictionary<string, string> KindOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A bare executable name: no folder, no control characters, at most 260 characters.</summary>
    public static bool IsExecutableName(string? name)
        => name is { Length: > 0 and <= 260 } && name.Trim().Length > 0 && name.IndexOfAny(['\\', '/', ':']) < 0 && !name.Any(char.IsControl);

    public void Normalize()
    {
        if (SortColumn is not null && !ColumnKeys.Contains(SortColumn))
        {
            SortColumn = null;
            SortDescending = true;
        }

        HiddenColumns = [.. (HiddenColumns ?? []).Where(k => k is not null && ColumnKeys.Contains(k) && k != "Name").Distinct()];
        if (double.IsNaN(DetailHeight) || DetailHeight is < 0 or > 4000)
        {
            DetailHeight = 0;
        }

        if (Layout is null || !Layouts.Contains(Layout))
        {
            Layout = "Auto";
        }

        if (double.IsNaN(DetailWidth) || DetailWidth is < 0 or > 4000)
        {
            DetailWidth = 0;
        }

        if (ChartMetric is null || !ChartMetrics.Contains(ChartMetric))
        {
            ChartMetric = "Dedicated";
        }

        if (!ChartWindows.Contains(ChartMinutes))
        {
            ChartMinutes = 15;
        }

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, kind) in KindOverrides ?? [])
        {
            if (overrides.Count < MaxKindOverrides && IsExecutableName(name) && kind is not null && KindNames.Contains(kind))
            {
                overrides[name.Trim().ToLowerInvariant()] = kind;
            }
        }

        KindOverrides = overrides;
    }
}

/// <summary>
/// The local Basalt server (a Strata fork for Blackwell GPUs). The app runs Basalt's own start script with the chosen
/// configuration file and stops what it started; the files in the install folder are only read, never changed.
/// Relative paths are relative to <see cref="InstallDirectory"/>.
/// </summary>
public sealed class BasaltSettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public const string DefaultInstallDirectory = @"C:\AI\basalt";
    public const string DefaultStartScript = "start-basalt.ps1";
    public const string DefaultQualityConfig = "basalt_config.json";
    public const string DefaultFastConfig = "basalt_config.fast.json";
    public const int DefaultPort = 8080;
    public const int DefaultStopWaitSeconds = 30;
    public const int MinStopWaitSeconds = 5;
    public const int MaxStopWaitSeconds = 300;
    public const int DefaultStartWaitSeconds = 900;
    public const int MinStartWaitSeconds = 30;
    public const int MaxStartWaitSeconds = 3600;
    public const string QualityMode = "Quality";
    public const string DualMode = "DualQuality";
    public const string DefaultDualConfig = "basalt_config.dual.json";
    public const string FastMode = "Fast";

    public static readonly IReadOnlySet<string> Modes = new HashSet<string>(StringComparer.Ordinal) { QualityMode, FastMode, DualMode };

    public string InstallDirectory { get; set; } = DefaultInstallDirectory;

    /// <summary>Basalt's start script (run with <c>-Config &lt;file&gt;</c>).</summary>
    public string StartScript { get; set; } = DefaultStartScript;

    public string QualityConfig { get; set; } = DefaultQualityConfig;
    public bool EmbeddedDashboard { get; set; } = true;
    public string DualConfig { get; set; } = DefaultDualConfig;
    public string FastConfig { get; set; } = DefaultFastConfig;

    /// <summary>The port Basalt serves its API and dashboard on (127.0.0.1). Both configurations use the same one.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>How long a graceful stop (Ctrl+C to Basalt's console) may take before Basalt's processes are ended.</summary>
    public int StopWaitSeconds { get; set; } = DefaultStopWaitSeconds;

    /// <summary>How long a start may take until Basalt answers (loading the model included).</summary>
    public int StartWaitSeconds { get; set; } = DefaultStartWaitSeconds;

    /// <summary>The configuration chosen last on the page: <see cref="QualityMode"/> or <see cref="FastMode"/>.</summary>
    public string Mode { get; set; } = QualityMode;

    public string ConfigFor(string mode) => mode switch { FastMode => FastConfig, DualMode => DualConfig, _ => QualityConfig };

    /// <summary>A path from these settings as a full path: relative ones are taken from the install folder.</summary>
    public string Resolve(string path) => WindowsPath.Combine(InstallDirectory, path);

    public IReadOnlyList<string> Normalize()
    {
        var notes = new List<string>();
        if (!WindowsPath.IsUsable(InstallDirectory))
        {
            notes.Add($"Basalt 설치 폴더 값이 올바르지 않아 기본값({DefaultInstallDirectory})으로 바꿨습니다.");
            InstallDirectory = DefaultInstallDirectory;
        }

        InstallDirectory = InstallDirectory.Trim();
        StartScript = Checked(StartScript, DefaultStartScript, "시작 스크립트", notes);
        if (!StartScript.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"Basalt 시작 스크립트는 PowerShell 스크립트(.ps1)여야 해서 기본값({DefaultStartScript})으로 바꿨습니다.");
            StartScript = DefaultStartScript;
        }

        QualityConfig = Checked(QualityConfig, DefaultQualityConfig, "품질용 설정 파일", notes);
        DualConfig = Checked(DualConfig, DefaultDualConfig, "듀얼 품질 설정 파일", notes);
        FastConfig = Checked(FastConfig, DefaultFastConfig, "빠른 모드 설정 파일", notes);
        if (Port is < 1 or > 65535)
        {
            notes.Add($"Basalt 포트 {Port}은(는) 허용 범위(1~65535)를 벗어나 {DefaultPort}으로 바꿨습니다.");
            Port = DefaultPort;
        }

        if (StopWaitSeconds is < MinStopWaitSeconds or > MaxStopWaitSeconds)
        {
            notes.Add($"Basalt 정상 종료 대기 {StopWaitSeconds}초는 허용 범위({MinStopWaitSeconds}~{MaxStopWaitSeconds}초)를 벗어나 {DefaultStopWaitSeconds}초로 바꿨습니다.");
            StopWaitSeconds = DefaultStopWaitSeconds;
        }

        if (StartWaitSeconds is < MinStartWaitSeconds or > MaxStartWaitSeconds)
        {
            notes.Add($"Basalt 시작 대기 {StartWaitSeconds}초는 허용 범위({MinStartWaitSeconds}~{MaxStartWaitSeconds}초)를 벗어나 {DefaultStartWaitSeconds}초로 바꿨습니다.");
            StartWaitSeconds = DefaultStartWaitSeconds;
        }

        if (Mode is null || !Modes.Contains(Mode))
        {
            Mode = QualityMode;
        }

        return notes;
    }

    private static string Checked(string? value, string fallback, string label, List<string> notes)
    {
        if (WindowsPath.IsUsable(value))
        {
            return value!.Trim();
        }

        notes.Add($"Basalt {label} 값이 올바르지 않아 기본값({fallback})으로 바꿨습니다.");
        return fallback;
    }
}

/// <summary>Windows paths handled the same way on every OS (the settings are Windows paths; tests run on Linux too).</summary>
public static class WindowsPath
{
    private static readonly char[] Forbidden = ['"', '<', '>', '|', '?', '*'];

    /// <summary>Non-empty, at most 260 characters, no control characters, quotes or wildcards.</summary>
    public static bool IsUsable(string? path)
        => path is { Length: > 0 and <= 260 } && path.Trim().Length > 0 && path.IndexOfAny(Forbidden) < 0 && !path.Any(char.IsControl);

    /// <summary>A drive path (C:\ or C:/) or a UNC path (\\server\share), on any OS.</summary>
    public static bool IsFullyQualified(string path)
        => (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
           || path.StartsWith(@"\\", StringComparison.Ordinal);

    public static string Combine(string directory, string path)
    {
        path = path.Trim();
        if (IsFullyQualified(path) || (!OperatingSystem.IsWindows() && Path.IsPathFullyQualified(path)))
        {
            return path;
        }

        directory = directory.Trim();
        var separator = directory.Contains('\\') || !directory.Contains('/') ? '\\' : '/';
        if (!OperatingSystem.IsWindows() && Path.IsPathFullyQualified(directory))
        {
            separator = '/';
        }

        return directory.TrimEnd('\\', '/') + separator + path.TrimStart('\\', '/');
    }
}

public sealed class OperatorHostSettings
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    /// <summary>How many times a crashed Operator host process is restarted automatically before the user is asked.</summary>
    public int MaxAutomaticRestarts { get; set; } = 3;
}

public sealed record LoadedShellSettings(ShellSettings Settings, IReadOnlyList<string> Notes, bool WasCreated);

/// <summary>Loads and saves shell-settings.json. Writes atomically; a damaged file is kept aside, never deleted.</summary>
public sealed class ShellSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Path { get; } = path;

    public LoadedShellSettings Load()
    {
        if (!File.Exists(Path))
        {
            var created = new ShellSettings();
            return new LoadedShellSettings(created, created.Normalize(), WasCreated: true);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<ShellSettings>(File.ReadAllText(Path), Json) ?? new ShellSettings();
            var notes = new List<string>(settings.Normalize());
            if (settings.SchemaVersion > ShellSettings.CurrentSchemaVersion)
            {
                notes.Add($"설정 파일이 더 새 버전(스키마 {settings.SchemaVersion})에서 저장되었습니다. 알 수 없는 필드도 저장할 때 보존합니다.");
            }

            return new LoadedShellSettings(settings, notes, WasCreated: false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var aside = KeepAside();
            var defaults = new ShellSettings();
            return new LoadedShellSettings(defaults,
                [$"설정 파일을 읽지 못해 기본값을 사용합니다 ({ex.GetType().Name}). 원래 파일은 {(aside is null ? "그대로 두었습니다" : $"'{System.IO.Path.GetFileName(aside)}'로 보관했습니다")}."],
                WasCreated: false);
        }
    }

    public void Save(ShellSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private string? KeepAside()
    {
        try
        {
            var aside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!,
                $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(Path, aside);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
