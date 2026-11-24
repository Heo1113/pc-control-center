namespace PcControlCenter.Core;

/// <summary>The four integrated programs. The id is also the name of the module's data folder.</summary>
public static class ModuleIds
{
    /// <summary>Project A: Local AI Performance Center (Strata telemetry, inference sessions, comparison).</summary>
    public const string LocalAi = "local-ai";

    /// <summary>Project B: GPU Profit &amp; Compute Center (energy, cost, revenue, scenarios).</summary>
    public const string GpuEconomics = "gpu-economics";

    /// <summary>Project C: Windows Diagnostics Lab (event log, WHEA, changes, diagnosis).</summary>
    public const string Diagnostics = "diagnostics";

    /// <summary>Project E: Windows AI Operator (UI Automation tasks with policy and approval).</summary>
    public const string Operator = "operator";

    public static IReadOnlyList<string> All { get; } = [LocalAi, GpuEconomics, Diagnostics, Operator];

    public static string DisplayName(string id) => id switch
    {
        LocalAi => "로컬 AI",
        GpuEconomics => "GPU 경제성",
        Diagnostics => "시스템 진단",
        Operator => "AI Operator",
        _ => id,
    };

    /// <summary>The original program the module comes from (shown in settings and data screens).</summary>
    public static string Origin(string id) => id switch
    {
        LocalAi => "A · Local AI Performance Center",
        GpuEconomics => "B · GPU Profit & Compute Center",
        Diagnostics => "C · Windows Diagnostics Lab",
        Operator => "E · Windows AI Operator",
        _ => id,
    };

    /// <summary>The database file name the module keeps in its own folder (same name as in the original program).</summary>
    public static string DatabaseFile(string id) => id switch
    {
        LocalAi => "lapc.db",
        GpuEconomics => "gpc.db",
        Diagnostics => "wdl.db",
        Operator => "wao.db",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };
}
