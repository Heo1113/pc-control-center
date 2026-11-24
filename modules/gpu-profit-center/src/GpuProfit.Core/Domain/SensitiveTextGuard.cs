using System.Text.RegularExpressions;

namespace GpuProfit.Core.Domain;

/// <summary>
/// This app never stores wallet keys or credentials. Free-text fields (notes, titles, provider names) are checked
/// before saving; text that looks like a private key, seed phrase or secret is refused.
/// Transaction ids and wallet addresses (public data) are allowed.
/// </summary>
public static partial class SensitiveTextGuard
{
    /// <summary>Returns a reason when the text must not be stored, otherwise null.</summary>
    public static string? Check(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (PemKey().IsMatch(text))
        {
            return "개인키(PEM) 형식의 텍스트는 저장하지 않습니다.";
        }

        if (ExtendedPrivateKey().IsMatch(text))
        {
            return "확장 개인키(xprv/tprv) 형식의 텍스트는 저장하지 않습니다.";
        }

        if (WifKey().IsMatch(text))
        {
            return "지갑 개인키(WIF)로 보이는 텍스트는 저장하지 않습니다.";
        }

        if (SecretWord().IsMatch(text) && (Hex64().IsMatch(text) || ManyWords(text)))
        {
            return "개인키·시드 문구·비밀값으로 보이는 텍스트는 저장하지 않습니다. 이 앱은 지갑 키나 인증정보를 보관하지 않습니다.";
        }

        if (ApiSecret().IsMatch(text))
        {
            return "API 키·토큰·비밀번호로 보이는 텍스트는 저장하지 않습니다.";
        }

        return null;
    }

    private static bool ManyWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.All(char.IsAsciiLetterLower)) >= 12;

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PemKey();

    [GeneratedRegex(@"\b[xtyz]prv[1-9A-HJ-NP-Za-km-z]{100,}\b")]
    private static partial Regex ExtendedPrivateKey();

    [GeneratedRegex(@"\b[5KLc9][1-9A-HJ-NP-Za-km-z]{50,51}\b")]
    private static partial Regex WifKey();

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex Hex64();

    [GeneratedRegex(@"(private|priv\s*key|secret|seed|mnemonic|개인\s*키|비밀\s*키|시드|니모닉|복구\s*문구)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretWord();

    [GeneratedRegex(@"(api[_-]?key|api[_-]?secret|access[_-]?token|password|passwd|비밀번호)\s*[:=]\s*\S{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex ApiSecret();
}
