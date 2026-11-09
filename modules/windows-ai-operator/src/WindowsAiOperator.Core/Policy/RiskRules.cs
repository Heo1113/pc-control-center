using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsAiOperator.Core.Policy;

public enum RiskCategory
{
    ConfirmButton,
    CloseWindow,
    FileDeleteOrOverwrite,
    BulkFileMove,
    InstallUninstall,
    ServiceOrScheduledTask,
    SecuritySettings,
    Elevation,
    ExternalSend,
    Payment,
    Credentials,
    SystemSettingChange,
    IrreversibleChange,
    CoordinateClick,
    UnlistedHotkey,
    SecretInText,
    NewlineMaySubmit,
    ReplaceExistingContent,
    InjectionSuspected,
    LaunchApp,
    UserRequiresApproval,
}

public sealed record RiskInfo(string Label, string Impact, string Recovery, bool Mandatory, bool Denied);

public static class RiskCatalog
{
    private static readonly Dictionary<RiskCategory, RiskInfo> Infos = new()
    {
        [RiskCategory.ConfirmButton] = new("확인·적용 버튼", "대화상자의 선택이 확정되거나 설정이 적용됩니다.", "대화상자 내용에 따라 되돌리기 어려울 수 있습니다.", false, false),
        [RiskCategory.CloseWindow] = new("창 닫기·종료", "창이나 프로그램이 닫히고 저장하지 않은 내용이 사라질 수 있습니다.", "닫힌 뒤에는 되돌릴 수 없습니다. 저장 여부를 먼저 확인하세요.", false, false),
        [RiskCategory.FileDeleteOrOverwrite] = new("파일 삭제·덮어쓰기·저장", "파일이 삭제되거나 기존 파일이 덮어써질 수 있습니다.", "휴지통·백업·이전 버전에서만 복구할 수 있습니다.", true, false),
        [RiskCategory.BulkFileMove] = new("파일 이동", "파일이나 폴더의 위치가 바뀝니다.", "원래 위치로 다시 옮겨야 합니다(실행 취소 Ctrl+Z가 되는 경우도 있음).", true, false),
        [RiskCategory.InstallUninstall] = new("프로그램 설치·제거·업데이트", "프로그램이 설치·제거되거나 시스템 구성이 바뀝니다.", "다시 설치하거나 제거해야 하며 완전히 되돌리지 못할 수 있습니다.", true, false),
        [RiskCategory.ServiceOrScheduledTask] = new("서비스·예약 작업·시작 프로그램 변경", "Windows 서비스나 예약 작업의 동작이 바뀝니다.", "이전 설정을 기억해 두었다가 직접 되돌려야 합니다.", true, false),
        [RiskCategory.SecuritySettings] = new("보안 설정 변경", "방화벽·백신·암호화 등 보안 수준이 바뀔 수 있습니다.", "Windows 보안에서 직접 되돌려야 합니다.", true, false),
        [RiskCategory.Elevation] = new("관리자 권한 작업", "관리자 권한으로 실행하거나 권한을 높이는 동작입니다.", "이 프로그램은 관리자 권한 작업을 하지 않습니다. 직접 수행하세요.", true, true),
        [RiskCategory.ExternalSend] = new("외부 전송", "파일·메시지·양식이 다른 사람이나 서버로 전송됩니다.", "전송한 뒤에는 회수할 수 없습니다.", true, false),
        [RiskCategory.Payment] = new("결제·구매·송금", "금전 거래가 일어날 수 있습니다.", "이 프로그램은 금융 거래를 자동으로 수행하지 않습니다. 직접 진행하세요.", true, true),
        [RiskCategory.Credentials] = new("인증정보 입력·제출", "로그인·인증이 진행되거나 인증정보가 입력됩니다.", "계정 보안에 영향을 줄 수 있습니다. 비밀번호는 직접 입력하세요.", true, false),
        [RiskCategory.SystemSettingChange] = new("Windows 설정 변경", "시스템 설정이 바뀝니다.", "설정 화면에서 이전 값으로 직접 되돌려야 합니다.", true, false),
        [RiskCategory.IrreversibleChange] = new("되돌리기 어려운 변경", "초기화·재설정처럼 복구가 어려운 변경입니다.", "백업이 없으면 되돌릴 수 없습니다.", true, false),
        [RiskCategory.CoordinateClick] = new("좌표 기반 클릭", "UI 요소 정보 없이 화면 좌표를 누릅니다.", "위치가 어긋나면 다른 항목이 눌릴 수 있습니다. 미리보기를 확인하세요.", false, false),
        [RiskCategory.UnlistedHotkey] = new("확인이 필요한 단축키", "단축키가 앱에서 예상하지 못한 명령을 실행할 수 있습니다.", "명령에 따라 Ctrl+Z로 되돌릴 수 있습니다.", false, false),
        [RiskCategory.SecretInText] = new("민감정보로 보이는 입력", "입력 내용에 비밀번호·키·카드번호처럼 보이는 값이 있습니다.", "입력된 값은 기록에 남지 않지만 대상 앱에는 입력됩니다.", true, false),
        [RiskCategory.NewlineMaySubmit] = new("줄바꿈 입력", "줄바꿈이 Enter 키로 전송되어 대화상자가 제출될 수 있습니다.", "제출된 경우 되돌리기 어려울 수 있습니다.", false, false),
        [RiskCategory.ReplaceExistingContent] = new("기존 내용 대체", "입력란의 기존 내용이 지워지고 새 내용으로 바뀝니다.", "대개 Ctrl+Z로 되돌릴 수 있습니다.", false, false),
        [RiskCategory.InjectionSuspected] = new("의심스러운 화면 텍스트", "화면 텍스트에 AI에게 지시하려는 듯한 문구가 있어, 이 작업의 모든 변경 동작을 확인합니다.", "요청하지 않은 동작이면 거부하세요.", false, false),
        [RiskCategory.LaunchApp] = new("앱 실행", "허용 목록의 프로그램을 시작합니다.", "실행된 프로그램을 닫으면 됩니다.", false, false),
        [RiskCategory.UserRequiresApproval] = new("사용자 설정에 따른 확인", "안전 설정에서 이 조작을 항상 확인하도록 했습니다.", "-", false, false),
    };

    public static RiskInfo Get(RiskCategory category) => Infos[category];
}

/// <summary>
/// Keyword rules that classify what a click or menu choice would do, from the element's name, automation id and help
/// text (and the dialog title for generic confirm buttons). Korean words match as substrings (particles attach to them);
/// English words match whole words.
/// </summary>
public static partial class RiskKeywords
{
    private static readonly (RiskCategory Category, string[] Korean, string[] English)[] Rules =
    [
        (RiskCategory.Payment,
            ["결제", "구매", "주문하기", "구독", "송금", "이체", "후원", "기부", "카드 등록", "충전하기", "결재"],
            ["pay", "payment", "purchase", "buy", "checkout", "place order", "order now", "subscribe", "donate", "transfer money", "wire transfer", "add card"]),
        (RiskCategory.Elevation,
            ["관리자 권한", "관리자로 실행", "관리자 모드"],
            ["run as administrator", "run as admin", "elevate", "elevated"]),
        (RiskCategory.FileDeleteOrOverwrite,
            ["삭제", "제거", "지우기", "휴지통", "영구", "비우기", "덮어쓰기", "덮어 쓰기", "저장", "포맷"],
            ["delete", "remove", "erase", "discard", "trash", "purge", "wipe", "overwrite", "save", "save as", "replace", "format", "empty recycle bin"]),
        (RiskCategory.BulkFileMove,
            ["잘라내기", "폴더로 이동", "위치로 이동", "이동 대상"],
            ["cut", "move to", "move here"]),
        (RiskCategory.InstallUninstall,
            ["설치", "업데이트 설치", "지금 업데이트", "다시 시작하여 업데이트"],
            ["install", "uninstall", "setup", "update now", "update and restart", "repair"]),
        (RiskCategory.ServiceOrScheduledTask,
            ["서비스", "작업 스케줄러", "예약 작업", "시작 프로그램"],
            ["service", "services", "scheduled task", "task scheduler", "startup apps"]),
        (RiskCategory.SecuritySettings,
            ["방화벽", "보안", "바이러스", "위협 방지", "실시간 보호", "암호화", "사용자 계정 컨트롤"],
            ["firewall", "defender", "antivirus", "security", "bitlocker", "real-time protection", "uac", "encryption"]),
        (RiskCategory.ExternalSend,
            ["보내기", "전송", "제출", "게시", "공유", "업로드", "답장", "전달", "발송", "회신"],
            ["send", "submit", "post", "publish", "share", "upload", "reply", "forward", "tweet", "sign up"]),
        (RiskCategory.Credentials,
            ["로그인", "로그 인", "비밀번호", "암호", "인증", "OTP", "보안 코드"],
            ["sign in", "log in", "login", "password", "passcode", "credential", "credentials", "2fa", "otp", "verification code", "pin"]),
        (RiskCategory.IrreversibleChange,
            ["초기화", "재설정", "복원", "되돌리기 불가"],
            ["reset", "factory reset", "restore", "wipe data"]),
    ];

    private static readonly string[] ConfirmWords = ["확인", "예", "적용", "동의", "수락", "계속", "ok", "yes", "apply", "accept", "agree", "confirm", "continue", "allow", "허용"];
    private static readonly string[] CloseWords = ["닫기", "종료", "끝내기", "close", "exit", "quit"];

    /// <summary>Categories that the text implies. <paramref name="dialogTitle"/> is used only for generic confirm buttons.</summary>
    public static IReadOnlyList<RiskCategory> Classify(string? name, string? automationId, string? helpText, string? dialogTitle = null)
    {
        var text = Normalize(string.Join(' ', new[] { name, SplitCamel(automationId), helpText }.Where(s => !string.IsNullOrWhiteSpace(s))));
        var found = new List<RiskCategory>();
        AddMatches(text, found);

        var normalizedName = Normalize(name);
        if (IsWord(normalizedName, ConfirmWords))
        {
            found.Add(RiskCategory.ConfirmButton);
            if (!string.IsNullOrWhiteSpace(dialogTitle))
            {
                AddMatches(Normalize(dialogTitle), found);
            }
        }

        if (IsWord(normalizedName, CloseWords) || string.Equals(automationId, "Close", StringComparison.OrdinalIgnoreCase))
        {
            found.Add(RiskCategory.CloseWindow);
        }

        return found.Distinct().ToList();
    }

    /// <summary>Categories for free text such as a menu path.</summary>
    public static IReadOnlyList<RiskCategory> ClassifyText(string? text)
    {
        var found = new List<RiskCategory>();
        AddMatches(Normalize(text), found);
        var normalized = Normalize(text);
        if (IsWord(normalized, CloseWords))
        {
            found.Add(RiskCategory.CloseWindow);
        }

        return found.Distinct().ToList();
    }

    public static bool LooksLikeCredentialField(string? name, string? automationId) =>
        Classify(name, automationId, null).Contains(RiskCategory.Credentials);

    /// <summary>Lower case, accelerator markers removed ("&amp;File", "파일(F)", "Save...").</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var value = text.Replace("&", string.Empty, StringComparison.Ordinal);
        value = Mnemonic().Replace(value, string.Empty);
        value = value.Replace("…", " ", StringComparison.Ordinal).Replace("...", " ", StringComparison.Ordinal);
        value = value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        return Spaces().Replace(value, " ").Trim();
    }

    private static void AddMatches(string text, List<RiskCategory> found)
    {
        if (text.Length == 0)
        {
            return;
        }

        foreach (var (category, korean, english) in Rules)
        {
            if (korean.Any(k => text.Contains(k.ToLowerInvariant(), StringComparison.Ordinal))
                || english.Any(e => ContainsWord(text, e)))
            {
                found.Add(category);
            }
        }
    }

    private static bool ContainsWord(string text, string phrase)
    {
        var index = 0;
        while ((index = text.IndexOf(phrase, index, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var end = index + phrase.Length;
            var afterOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            index = end;
        }

        return false;
    }

    /// <summary>The whole label is the word, or the word followed by a short suffix such as "(Enter)".</summary>
    private static bool IsWord(string normalizedName, string[] words)
    {
        if (normalizedName.Length == 0)
        {
            return false;
        }

        foreach (var word in words)
        {
            if (normalizedName == word)
            {
                return true;
            }

            if (normalizedName.StartsWith(word, StringComparison.Ordinal) && normalizedName.Length <= word.Length + 10)
            {
                var next = normalizedName[word.Length];
                if (next is ' ' or '(' or '[' or ':')
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? SplitCamel(string? id) =>
        string.IsNullOrEmpty(id) ? id : CamelBoundary().Replace(id, " ").Replace('_', ' ').Replace('-', ' ');

    [GeneratedRegex(@"\(\s*[A-Za-z0-9]\s*\)")]
    private static partial Regex Mnemonic();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelBoundary();
}

/// <summary>Processes the operator never automates, whatever the allow list says.</summary>
public static class BuiltInBlockList
{
    public static readonly IReadOnlySet<string> Processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Shells, consoles and script hosts: arbitrary command execution.
        "cmd", "powershell", "pwsh", "powershell_ise", "WindowsTerminal", "OpenConsole", "conhost", "wt", "wsl", "wslhost", "bash",
        "wscript", "cscript", "mshta", "rundll32", "regsvr32", "python", "pythonw", "node",
        // System administration and security.
        "regedit", "regedt32", "mmc", "taskmgr", "msiexec", "msconfig", "schtasks", "sc", "netsh", "bcdedit", "diskpart",
        "consent", "LogonUI", "CredentialUIBroker", "LockApp", "SecHealthUI", "SecurityHealthSystray", "SecurityHealthHost",
        "SystemPropertiesAdvanced", "SystemPropertiesProtection", "OptionalFeatures", "UserAccountControlSettings", "certmgr",
        "lsass", "winlogon", "csrss", "smss", "wininit", "services", "svchost", "dwm", "taskhostw", "sihost", "ShellExperienceHost",
        // Password managers and remote control.
        "KeePass", "KeePassXC", "1Password", "Bitwarden", "LastPass", "Dashlane", "Enpass", "NordPass",
        "mstsc", "AnyDesk", "TeamViewer", "vncviewer", "msra", "quickassist",
        // This program itself (its own approval buttons must never be pressed by automation).
        AppInfo.ExecutableName, "wao",
    };

    public static bool IsBlocked(string processName, IEnumerable<string>? extra = null)
    {
        var name = Settings.AppSettings.NormalizeProcessName(processName);
        return Processes.Contains(name) || (extra?.Any(e => string.Equals(Settings.AppSettings.NormalizeProcessName(e), name, StringComparison.OrdinalIgnoreCase)) ?? false);
    }
}
