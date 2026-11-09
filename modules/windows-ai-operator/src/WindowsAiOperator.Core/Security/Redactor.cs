using System.Text;
using System.Text.RegularExpressions;

namespace WindowsAiOperator.Core.Security;

/// <summary>
/// Masks secrets before text reaches a log, the database, the screen history or a model prompt.
/// Conservative on purpose: a false positive only hides a few characters, a false negative leaks a secret.
/// </summary>
public static partial class Redactor
{
    public const string Mask = "[가림]";

    /// <summary>Returns the text with secret-looking parts replaced by <see cref="Mask"/>.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = PemBlock().Replace(text, Mask);
        result = KeyValueSecret().Replace(result, m => m.Groups["key"].Value + m.Groups["sep"].Value + Mask);
        result = BearerToken().Replace(result, "Bearer " + Mask);
        result = KnownTokenPrefix().Replace(result, Mask);
        result = Jwt().Replace(result, Mask);
        result = ResidentNumber().Replace(result, Mask);
        result = CardCandidate().Replace(result, m => LuhnValid(m.Value) ? Mask : m.Value);
        result = LongToken().Replace(result, m => LooksRandom(m.Value) ? Mask : m.Value);
        return result;
    }

    /// <summary>True when the text contains something <see cref="Redact"/> would mask.</summary>
    public static bool ContainsSecret(string? text) =>
        !string.IsNullOrEmpty(text) && !string.Equals(Redact(text), text, StringComparison.Ordinal);

    /// <summary>Redacts, strips control characters and shortens text for display or storage.</summary>
    public static string Sanitize(string? text, int maxLength)
    {
        var redacted = Redact(text);
        var builder = new StringBuilder(Math.Min(redacted.Length, maxLength + 16));
        foreach (var ch in redacted)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            builder.Append(char.IsControl(ch) && ch is not '\n' and not '\t' ? ' ' : ch);
        }

        if (redacted.Length > maxLength)
        {
            builder.Append($"…({redacted.Length - maxLength}자 생략)");
        }

        return builder.ToString();
    }

    /// <summary>One-line form for lists and logs.</summary>
    public static string OneLine(string? text, int maxLength) =>
        Sanitize(text, maxLength).Replace('\n', ' ').Replace('\t', ' ');

    private static bool LuhnValid(string candidate)
    {
        var digits = candidate.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i];
            if (alternate)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            alternate = !alternate;
        }

        return sum % 10 == 0;
    }

    /// <summary>Long runs of mixed letters and digits (keys, tokens). Plain words or numbers are kept.</summary>
    private static bool LooksRandom(string value)
    {
        var letters = value.Count(char.IsAsciiLetter);
        var digits = value.Count(char.IsAsciiDigit);
        var upper = value.Count(char.IsAsciiLetterUpper);
        var lower = value.Count(char.IsAsciiLetterLower);
        return digits >= 4 && letters >= 4 && (upper >= 2 || value.Length >= 40) && (lower >= 2 || value.Length >= 40);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)")]
    private static partial Regex PemBlock();

    [GeneratedRegex(@"(?<key>\b(password|passwd|pwd|passcode|secret|api[_-]?key|access[_-]?token|token|client[_-]?secret|비밀번호|암호|패스워드|인증번호)\b)(?<sep>\s*[:=]\s*)(?<value>""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/]{8,}=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\b(sk-[A-Za-z0-9_\-]{16,}|sk-ant-[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{35}|glpat-[A-Za-z0-9_\-]{20,}|hf_[A-Za-z0-9]{30,})")]
    private static partial Regex KnownTokenPrefix();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b\d{6}-?[1-8]\d{6}\b")]
    private static partial Regex ResidentNumber();

    [GeneratedRegex(@"\b(?:\d[ \-]?){12,18}\d\b")]
    private static partial Regex CardCandidate();

    [GeneratedRegex(@"[A-Za-z0-9_\-+/=]{32,}")]
    private static partial Regex LongToken();
}
