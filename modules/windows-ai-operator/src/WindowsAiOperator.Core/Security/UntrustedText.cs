using System.Text.RegularExpressions;

namespace WindowsAiOperator.Core.Security;

/// <summary>
/// Text read from other programs (window titles, element names and values, file names) is data, never an instruction.
/// It is wrapped before it goes into a prompt, and text that tries to look like an instruction is flagged so the executor
/// asks the user before every action that changes something (prompt-injection defense in depth).
/// </summary>
public static partial class UntrustedText
{
    public const string OpenTag = "<untrusted_ui_data>";
    public const string CloseTag = "</untrusted_ui_data>";

    /// <summary>Wraps data for a prompt; tag look-alikes inside the data are neutralised so it cannot close the block.</summary>
    public static string Wrap(string data)
    {
        var safe = data.Replace("<untrusted_ui_data", "<untrusted-ui-data", StringComparison.OrdinalIgnoreCase)
                       .Replace("</untrusted_ui_data", "</untrusted-ui-data", StringComparison.OrdinalIgnoreCase);
        return OpenTag + "\n" + safe + "\n" + CloseTag;
    }

    /// <summary>True when the text reads like an instruction aimed at an AI agent rather than normal UI text.</summary>
    public static bool LooksLikeInstruction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 12)
        {
            return false;
        }

        return InstructionPattern().IsMatch(text);
    }

    [GeneratedRegex(
        @"(ignore|disregard|forget|override)\s+(all\s+|any\s+|the\s+|your\s+)?(previous|prior|above|earlier|system)\s+(instructions?|prompts?|rules?|messages?)" +
        @"|\b(system|developer)\s+prompt\b" +
        @"|\byou\s+are\s+now\b|\bnew\s+instructions?\s*:|\bact\s+as\s+(an?\s+)?(ai|assistant|agent)\b" +
        @"|<\|im_start\|>|<\|system\|>|\[INST\]|^\s*(system|assistant)\s*:" +
        @"|\b(run|execute)\s+(this|the\s+following)\s+(command|code|script)\b" +
        @"|이전\s*(의\s*)?(지시|명령|지침|규칙)(을|를)?\s*(모두\s*)?(무시|잊)" +
        @"|(지시|명령|지침)(을|를)?\s*무시" +
        @"|시스템\s*프롬프트|새로운\s*지시\s*:|다음\s*명령을\s*(즉시\s*)?실행",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex InstructionPattern();
}
