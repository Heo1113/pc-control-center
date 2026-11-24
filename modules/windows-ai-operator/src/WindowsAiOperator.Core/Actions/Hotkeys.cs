namespace WindowsAiOperator.Core.Actions;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

public sealed record Hotkey(KeyModifiers Modifiers, string Key)
{
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Ctrl))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }
}

public enum HotkeySafety
{
    Safe,
    NeedsApproval,
    Blocked,
}

/// <summary>
/// The keys the operator may press. Only listed keys parse at all; a short list of combinations is safe without asking,
/// system-level combinations are blocked, and everything else needs the user's approval.
/// </summary>
public static class HotkeyCatalog
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = "Escape", ["escape"] = "Escape", ["enter"] = "Enter", ["return"] = "Enter", ["tab"] = "Tab",
        ["space"] = "Space", ["spacebar"] = "Space", ["backspace"] = "Backspace", ["delete"] = "Delete", ["del"] = "Delete",
        ["home"] = "Home", ["end"] = "End", ["pageup"] = "PageUp", ["pgup"] = "PageUp", ["pagedown"] = "PageDown",
        ["pgdn"] = "PageDown", ["up"] = "Up", ["arrowup"] = "Up", ["down"] = "Down", ["arrowdown"] = "Down",
        ["left"] = "Left", ["arrowleft"] = "Left", ["right"] = "Right", ["arrowright"] = "Right", ["insert"] = "Insert", ["ins"] = "Insert",
    };

    private static readonly HashSet<string> SafeCombos = new(StringComparer.Ordinal)
    {
        "Escape", "Tab", "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right", "F3", "F5", "F6", "F10", "Backspace",
        "Shift+Tab", "Shift+Up", "Shift+Down", "Shift+Left", "Shift+Right", "Shift+Home", "Shift+End", "Shift+PageUp", "Shift+PageDown",
        "Ctrl+A", "Ctrl+C", "Ctrl+F", "Ctrl+Z", "Ctrl+Y", "Ctrl+Home", "Ctrl+End", "Ctrl+Left", "Ctrl+Right", "Ctrl+Up", "Ctrl+Down",
        "Ctrl+Tab", "Ctrl+Shift+Tab", "Ctrl+Shift+Left", "Ctrl+Shift+Right", "Ctrl+Shift+Home", "Ctrl+Shift+End", "Ctrl+N", "Ctrl+T", "Ctrl+O", "Ctrl+G",
    };

    private static readonly HashSet<string> BlockedCombos = new(StringComparer.Ordinal)
    {
        "Ctrl+Alt+Delete", "Ctrl+Shift+Escape", "Alt+Tab", "Shift+Alt+Tab", "Ctrl+Alt+Tab", "Alt+Escape", "Ctrl+Escape", "Ctrl+Alt+End",
    };

    private static readonly Dictionary<string, string> KnownRisks = new(StringComparer.Ordinal)
    {
        ["Enter"] = "입력 확정·제출이나 대화상자 기본 버튼이 실행될 수 있습니다.",
        ["Ctrl+Enter"] = "메시지 보내기·제출이 실행될 수 있습니다.",
        ["Space"] = "포커스된 버튼이나 확인란이 실행될 수 있습니다.",
        ["Delete"] = "선택한 내용이나 파일이 삭제될 수 있습니다.",
        ["Shift+Delete"] = "휴지통을 거치지 않고 영구 삭제될 수 있습니다.",
        ["Ctrl+S"] = "파일이 저장되거나 덮어써질 수 있습니다.",
        ["Ctrl+Shift+S"] = "다른 이름으로 저장(파일 쓰기)이 실행될 수 있습니다.",
        ["Alt+F4"] = "창이 닫히고 저장하지 않은 내용이 사라질 수 있습니다.",
        ["Ctrl+W"] = "창이나 탭이 닫힐 수 있습니다.",
        ["Ctrl+F4"] = "문서 창이 닫힐 수 있습니다.",
        ["Ctrl+P"] = "인쇄가 시작될 수 있습니다.",
        ["Ctrl+V"] = "클립보드 내용(민감정보 포함 가능)이 붙여 넣어집니다.",
        ["Ctrl+X"] = "선택한 내용이나 파일이 잘라내집니다(이동 준비).",
    };

    public static IReadOnlyCollection<string> SafeList => SafeCombos;

    public static bool TryParse(string? text, out Hotkey? hotkey, out string? error)
    {
        hotkey = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40)
        {
            error = "단축키가 비어 있거나 너무 깁니다.";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(string.IsNullOrEmpty))
        {
            error = $"단축키 형식이 잘못되었습니다: '{text}'";
            return false;
        }

        var modifiers = KeyModifiers.None;
        string? key = null;
        foreach (var raw in parts)
        {
            var part = raw.ToLowerInvariant();
            switch (part)
            {
                case "ctrl" or "control":
                    modifiers |= KeyModifiers.Ctrl;
                    continue;
                case "shift":
                    modifiers |= KeyModifiers.Shift;
                    continue;
                case "alt":
                    modifiers |= KeyModifiers.Alt;
                    continue;
                case "win" or "windows" or "meta" or "cmd" or "super" or "lwin" or "rwin":
                    error = "Windows 키 조합은 시스템 전체에 작용하므로 허용하지 않습니다.";
                    return false;
            }

            if (key is not null)
            {
                error = $"키는 하나만 지정할 수 있습니다: '{text}'";
                return false;
            }

            key = NormalizeKey(raw);
            if (key is null)
            {
                error = $"지원하지 않는 키입니다: '{raw}'";
                return false;
            }
        }

        if (key is null)
        {
            error = "수정 키만 있고 실제 키가 없습니다.";
            return false;
        }

        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    public static (HotkeySafety Safety, string Reason) Classify(Hotkey hotkey)
    {
        var text = hotkey.ToString();
        if (BlockedCombos.Contains(text))
        {
            return (HotkeySafety.Blocked, $"{text}: 시스템 전체 단축키는 사용하지 않습니다(창 전환은 focus_window 사용).");
        }

        if (SafeCombos.Contains(text))
        {
            return (HotkeySafety.Safe, $"{text}: 안전한 단축키 목록에 있습니다.");
        }

        if (KnownRisks.TryGetValue(text, out var risk))
        {
            return (HotkeySafety.NeedsApproval, $"{text}: {risk}");
        }

        return (HotkeySafety.NeedsApproval, $"{text}: 안전 목록에 없는 단축키라 실행 전에 확인합니다.");
    }

    private static string? NormalizeKey(string raw)
    {
        if (Aliases.TryGetValue(raw, out var alias))
        {
            return alias;
        }

        if (raw.Length == 1 && (char.IsAsciiLetter(raw[0]) || char.IsAsciiDigit(raw[0])))
        {
            return raw.ToUpperInvariant();
        }

        if (raw.Length is 2 or 3 && (raw[0] is 'F' or 'f') && int.TryParse(raw.AsSpan(1), out var f) && f is >= 1 and <= 12)
        {
            return "F" + f;
        }

        return null;
    }
}
