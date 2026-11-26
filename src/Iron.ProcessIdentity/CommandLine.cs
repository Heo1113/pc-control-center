using System.Text;
using System.Text.RegularExpressions;

namespace Iron.ProcessIdentity;

/// <summary>
/// Splits a Windows command line like CommandLineToArgvW (and the MSVC runtime): spaces separate arguments outside quotes,
/// 2n backslashes + quote → n backslashes and a quote toggle, 2n+1 backslashes + quote → n backslashes and a literal
/// quote, "" inside quotes → a literal quote. The program name (first token) ends at the first unquoted space.
/// </summary>
public static class CommandLineParser
{
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return result;
        }

        var s = commandLine;
        var i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        // Program name: no backslash rules, quotes only group.
        var first = new StringBuilder();
        var quoted = false;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && (c == ' ' || c == '\t'))
            {
                break;
            }

            first.Append(c);
        }

        result.Add(first.ToString());

        while (true)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t'))
            {
                i++;
            }

            if (i >= s.Length)
            {
                break;
            }

            var arg = new StringBuilder();
            var inQuotes = false;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\\')
                {
                    var n = 0;
                    while (i < s.Length && s[i] == '\\')
                    {
                        n++;
                        i++;
                    }

                    if (i < s.Length && s[i] == '"')
                    {
                        arg.Append('\\', n / 2);
                        if (n % 2 == 1)
                        {
                            arg.Append('"');
                            i++;
                        }
                    }
                    else
                    {
                        arg.Append('\\', n);
                    }

                    continue;
                }

                if (c == '"')
                {
                    if (inQuotes && i + 1 < s.Length && s[i + 1] == '"')
                    {
                        arg.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    i++;
                    continue;
                }

                if (!inQuotes && (c == ' ' || c == '\t'))
                {
                    break;
                }

                arg.Append(c);
                i++;
            }

            result.Add(arg.ToString());
        }

        return result;
    }

    /// <summary>The raw text after the program name (cmd.exe /c takes the rest of the line as one command).</summary>
    public static string RestAfterProgram(string commandLine)
    {
        var s = commandLine.TrimStart();
        var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && (s[i] == ' ' || s[i] == '\t'))
            {
                return s[(i + 1)..].TrimStart();
            }
        }

        return "";
    }

    /// <summary>Value of an option written as "--name=value", "-name value", "/name:value" or "/name value" (case-insensitive).</summary>
    public static string? Option(IReadOnlyList<string> args, params string[] names)
    {
        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            foreach (var name in names)
            {
                if (a.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return i + 1 < args.Count ? args[i + 1] : "";
                }

                foreach (var sep in new[] { '=', ':' })
                {
                    if (a.Length > name.Length && a.StartsWith(name, StringComparison.OrdinalIgnoreCase) && a[name.Length] == sep)
                    {
                        return a[(name.Length + 1)..];
                    }
                }
            }
        }

        return null;
    }

    public static string FileName(string path)
    {
        var p = path.Trim().Trim('"');
        var cut = p.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? p[(cut + 1)..] : p;
    }

    /// <summary>Folder names of a path from the file upward (excluding drive and the file itself).</summary>
    public static IReadOnlyList<string> Folders(string path)
    {
        var parts = path.Trim().Trim('"').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var folders = new List<string>();
        for (var i = parts.Length - 2; i >= 0; i--)
        {
            if (parts[i].EndsWith(':'))
            {
                break;
            }

            folders.Add(parts[i]);
        }

        return folders;
    }
}

/// <summary>
/// Removes what must not leave the machine or be kept: secrets in options (password, token, key ...), credentials in
/// URLs, long opaque strings and the user profile folder name. Used for every command line that is stored or shown.
/// </summary>
public static partial class Redactor
{
    public const int MaxLength = 512;

    [GeneratedRegex(@"(?i)((?:--?|/)(?:[a-z0-9_.-]*?)(?:password|passwd|pwd|passphrase|token|secret|api[-_]?key|apikey|access[-_]?key|auth|authorization|credential|cookie|session|private[-_]?key|key)(?:[a-z0-9_.-]*)\s*[=: ]\s*)(""[^""]*""|'[^']*'|[^\s""']+)")]
    private static partial Regex SecretOption();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|secret|api[-_]?key|apikey|access[-_]?token|sig|signature|auth|code)=([^&\s""']+)")]
    private static partial Regex SecretQuery();

    [GeneratedRegex(@"(?i)\b([a-z][a-z0-9+.-]*://)[^/\s:@""']+:[^/\s@""']+@")]
    private static partial Regex UrlCredential();

    [GeneratedRegex(@"\b(?:[A-Za-z0-9+/=_-]{40,}|[A-Fa-f0-9]{32,}|eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)\b")]
    private static partial Regex OpaqueBlob();

    [GeneratedRegex(@"(?i)\b([a-z]:\\(?:users|documents and settings)\\)([^\\""\s]+)")]
    private static partial Regex UserProfile();

    public static string Redact(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return "";
        }

        var s = UrlCredential().Replace(commandLine, m => m.Groups[1].Value + "***@");
        s = SecretOption().Replace(s, m => m.Groups[1].Value + "***");
        s = SecretQuery().Replace(s, m => m.Groups[1].Value + "=***");
        s = OpaqueBlob().Replace(s, "***");
        s = UserProfile().Replace(s, m => m.Groups[2].Value.Equals("Public", StringComparison.OrdinalIgnoreCase) ? m.Value : "%USERPROFILE%");
        return s.Length > MaxLength ? s[..MaxLength] + "…" : s;
    }

    /// <summary>A path reduced to what identifies the program without personal data: profile name removed.</summary>
    public static string RedactPath(string path) => UserProfile().Replace(path, m => m.Groups[2].Value.Equals("Public", StringComparison.OrdinalIgnoreCase) ? m.Value : "%USERPROFILE%");
}
