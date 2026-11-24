using System.Reflection;

namespace PcControlCenter.Core;

public static class AppInfo
{
    public const string ProductName = "PC Control Center";

    /// <summary>Prefix of the shell's log files (pcc-yyyyMMdd.log).</summary>
    public const string ShortName = "pcc";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
