using System.Text;

namespace PcControlCenter.Basalt;

/// <summary>The command line that starts Basalt: Windows PowerShell running Basalt's own start script.</summary>
public static class BasaltCommand
{
    /// <summary>Windows PowerShell from System32 (a full path, so nothing on PATH or in the working folder is run instead).</summary>
    public static string PowerShellPath()
        => OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
            : "powershell.exe";

    /// <summary>
    /// <c>-NoProfile -ExecutionPolicy Bypass -File "script" -Config "config"</c>. The execution policy applies to this one
    /// PowerShell process only (no setting is changed). The window closes when the script ends (no -NoExit), so Ctrl+C in
    /// it, or the one this app sends, ends the window too.
    /// </summary>
    public static string Arguments(string script, string config)
        => $"-NoProfile -ExecutionPolicy Bypass -File {Quote(script)} -Config {Quote(config)}";

    /// <summary>One argument quoted the way Windows' command line parser splits it back (backslashes before a quote doubled).</summary>
    public static string Quote(string argument)
    {
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            builder.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }
}
