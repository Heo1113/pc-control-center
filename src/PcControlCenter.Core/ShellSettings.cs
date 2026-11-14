using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcControlCenter.Core;

/// <summary>
/// Settings that belong to the integrated app itself. Each module keeps its own settings.json in its folder with the
/// same format as the original program; those are edited on the module's settings page.
/// </summary>
public sealed class ShellSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public SharedHardwareSettings Hardware { get; set; } = new();
    public ModuleSwitches Modules { get; set; } = new();
    public ShellUiSettings Ui { get; set; } = new();
    public OperatorHostSettings Operator { get; set; } = new();

    /// <summary>How the hardware › GPU 메모리 page was left (sort, columns, panel size). Missing in older files = defaults.</summary>
    public GpuMemoryPageSettings GpuMemoryPage { get; set; } = new();

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
    public bool LocalAi { get; set; } = true;
    public bool GpuEconomics { get; set; } = true;
    public bool Diagnostics { get; set; } = true;
    public bool Operator { get; set; } = true;
}

public sealed class ShellUiSettings
{
    public double FontScale { get; set; } = 1.0;
}

/// <summary>
/// View state of the GPU memory page, remembered across visits and restarts. Column keys are the table's sort member
/// names; unknown keys (from a newer or edited file) are dropped silently since they only affect the layout.
/// </summary>
public sealed class GpuMemoryPageSettings
{
    public static readonly IReadOnlySet<string> ColumnKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "Name", "Pid", "KindLabel", "Dedicated", "Delta", "Shared", "Gpu", "Runtimes", "Inside", "Adapter",
    };

    public static readonly IReadOnlySet<string> ChartMetrics = new HashSet<string>(StringComparer.Ordinal) { "Dedicated", "Shared", "Gpu" };

    public static readonly IReadOnlyList<int> ChartWindows = [5, 15, 30];

    /// <summary>Sort column; null = dedicated memory, largest first.</summary>
    public string? SortColumn { get; set; }

    public bool SortDescending { get; set; } = true;

    /// <summary>Also list processes below the listing thresholds.</summary>
    public bool ShowAll { get; set; }

    /// <summary>Hidden table columns (never the process name).</summary>
    public List<string> HiddenColumns { get; set; } = [];

    /// <summary>Height of the detail panel in device-independent pixels; 0 = share the height with the table.</summary>
    public double DetailHeight { get; set; }

    /// <summary>What the detail chart shows: Dedicated, Shared or Gpu (the busiest engine, %).</summary>
    public string ChartMetric { get; set; } = "Dedicated";

    /// <summary>The detail chart's window in minutes (5, 15 or 30; the reader keeps 30).</summary>
    public int ChartMinutes { get; set; } = 15;

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

        if (ChartMetric is null || !ChartMetrics.Contains(ChartMetric))
        {
            ChartMetric = "Dedicated";
        }

        if (!ChartWindows.Contains(ChartMinutes))
        {
            ChartMinutes = 15;
        }
    }
}

public sealed class OperatorHostSettings
{
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
                notes.Add($"설정 파일이 더 새 버전(스키마 {settings.SchemaVersion})에서 저장되었습니다. 알 수 없는 항목은 무시합니다.");
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
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, Path, overwrite: true);
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
