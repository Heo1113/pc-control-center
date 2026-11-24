using System.Reflection;

namespace LocalAiPerf.Core;

public static class AppInfo
{
    public const string ProductName = "Local AI Performance Center";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
