using System.Reflection;

namespace WinDiag.Core;

public static class AppInfo
{
    public const string ProductName = "Windows Diagnostics Lab";

    /// <summary>Prefix of this app's log files and other short identifiers.</summary>
    public const string ShortName = "wdl";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
