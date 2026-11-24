using System.Text.RegularExpressions;
using LocalAiPerf.Core.Diagnostics;
using WinDiag.Core.Events;

namespace WinDiag.Core.Privacy;

/// <summary>
/// Removes personal identifiers from event text before it is stored or exported: user profile paths, the current user
/// and computer names, account SIDs, e-mail addresses and anything that looks like a credential (via project A's
/// <see cref="SecretRedactor"/>). Version numbers and hardware identifiers needed for diagnosis are kept.
/// </summary>
public sealed partial class PrivacyMasker
{
    public const string UserToken = "<사용자>";
    public const string ComputerToken = "<컴퓨터>";
    public const string SidToken = "<SID>";
    public const string EmailToken = "<이메일>";
    public const string InstanceToken = "<인스턴스>";

    /// <summary>Account names too generic to replace as a standalone word (they also appear in ordinary sentences).</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "users", "admin", "administrator", "owner", "guest", "test", "home", "default", "public", "system", "local",
        "service", "windows", "microsoft", "pc", "desktop", "computer", "server", "network", "device", "driver",
    };

    private readonly Regex? _userWord;
    private readonly Regex? _machineWord;
    private readonly string? _profilePath;

    public PrivacyMasker(string? userName, string? machineName, string? userProfilePath)
    {
        UserName = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();
        MachineName = string.IsNullOrWhiteSpace(machineName) ? null : machineName.Trim();
        _profilePath = string.IsNullOrWhiteSpace(userProfilePath) ? null : userProfilePath.TrimEnd('\\', '/');
        if (UserName is { Length: >= 3 } && !CommonWords.Contains(UserName))
        {
            _userWord = new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(UserName) + @"(?![\p{L}\p{N}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (MachineName is { Length: >= 2 })
        {
            _machineWord = new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(MachineName) + @"(?![\p{L}\p{N}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    public string? UserName { get; }
    public string? MachineName { get; }

    /// <summary>A masker for the machine this process runs on.</summary>
    public static PrivacyMasker ForCurrentMachine()
        => new(Environment.UserName, Environment.MachineName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>A masker that only applies the generic patterns (no known names).</summary>
    public static PrivacyMasker Generic { get; } = new(null, null, null);

    public string Mask(string? text, string? extraComputerName = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        var result = SecretRedactor.Redact(text);
        if (_profilePath is not null)
        {
            result = result.Replace(_profilePath, ProfileReplacement(_profilePath), StringComparison.OrdinalIgnoreCase);
        }

        result = UserPath().Replace(result, m => m.Groups[1].Value + UserToken);
        result = DomainSid().Replace(result, m => m.Groups[1].Value + "-" + SidToken);
        result = Email().Replace(result, EmailToken);
        if (_machineWord is not null)
        {
            result = _machineWord.Replace(result, ComputerToken);
        }

        if (!string.IsNullOrWhiteSpace(extraComputerName) && !string.Equals(extraComputerName, MachineName, StringComparison.OrdinalIgnoreCase))
        {
            // Computer names in events from another machine (imported files): mask that name too, plus its short form.
            foreach (var name in ComputerNameForms(extraComputerName))
            {
                result = Regex.Replace(result, @"(?<![\p{L}\p{N}_])" + Regex.Escape(name) + @"(?![\p{L}\p{N}_])", ComputerToken,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        result = DomainUser().Replace(result, m => m.Groups[1].Value + "\\" + UserToken);
        if (_userWord is not null)
        {
            result = _userWord.Replace(result, UserToken);
        }

        return result;
    }

    /// <summary>Masks every text field of an event (message and data values).</summary>
    public RawEvent Mask(RawEvent e) => e with
    {
        Message = e.Message is null ? null : Mask(e.Message, e.Computer),
        MessageError = e.MessageError is null ? null : Mask(e.MessageError, e.Computer),
        Data = e.Data.Select(d => new EventDataItem(d.Name, Mask(d.Value, e.Computer))).ToList(),
    };

    /// <summary>
    /// Extra masking for exports at the "minimal" privacy level: device instance paths keep the bus and hardware ID but
    /// lose the instance part, which can carry a device serial number.
    /// </summary>
    public static string MaskDeviceInstances(string? text)
        => string.IsNullOrEmpty(text) ? text ?? "" : DeviceInstance().Replace(text, m => m.Groups[1].Value + InstanceToken);

    private static IEnumerable<string> ComputerNameForms(string computer)
    {
        computer = computer.Trim();
        if (computer.Length >= 2)
        {
            yield return computer;
        }

        var dot = computer.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 2)
        {
            yield return computer[..dot];
        }
    }

    private static string ProfileReplacement(string profilePath)
    {
        var separator = profilePath.LastIndexOfAny(['\\', '/']);
        return separator < 0 ? UserToken : profilePath[..(separator + 1)] + UserToken;
    }

    // \Users\<name>\ in drive, UNC and device paths (C:\Users\x, \Device\HarddiskVolume3\Users\x, C:/Users/x).
    [GeneratedRegex(@"([\\/]Users[\\/])(?!(?:Public|Default|Default User|All Users|defaultuser0)(?:[\\/]|$))[^\\/:*?""<>|\r\n\t]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserPath();

    // Account SIDs of local, domain and Entra ID users. Well-known SIDs (S-1-5-18 and so on) are kept.
    [GeneratedRegex(@"\b(S-1-(?:5-21|12-1))(?:-\d+){3,5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DomainSid();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    // "<컴퓨터>\name" after the machine name has been masked, and "AzureAD\name".
    [GeneratedRegex(@"(<컴퓨터>|AzureAD)\\[^\s\\,;:""'()]+", RegexOptions.CultureInvariant)]
    private static partial Regex DomainUser();

    [GeneratedRegex(@"\b((?:USB|HID|PCI|SWD|BTHENUM|BTHLE|BTHLEDEVICE|SCSI|STORAGE|NVME|DISPLAY|HDAUDIO|ACPI|ROOT|USBSTOR|WPDBUSENUMROOT|SD|UEFI|MMDEVAPI)\\[^\\\s]+\\)[^\s\\,;""')]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceInstance();
}
