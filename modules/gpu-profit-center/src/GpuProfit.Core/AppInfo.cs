using System.Reflection;

namespace GpuProfit.Core;

public static class AppInfo
{
    public const string ProductName = "GPU Profit & Compute Center";
    public const string ShortName = "gpc";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
