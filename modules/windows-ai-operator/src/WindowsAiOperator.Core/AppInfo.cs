namespace WindowsAiOperator.Core;

public static class AppInfo
{
    public const string ProductName = "Windows AI Operator";
    public const string ExecutableName = "WindowsAiOperator";

    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
