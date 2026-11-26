using System.Text;

namespace Iron.ProcessIdentity;

/// <summary>How sure an identity text is.</summary>
public enum IdentityConfidence
{
    /// <summary>Nothing found.</summary>
    Unknown,

    /// <summary>Guessed: command-line heuristics, folder names, the offline role dictionary, "Microsoft" by company name.</summary>
    Inferred,

    /// <summary>Read from the system: service manager PID mapping, file version resource, package identity, registry, loaded modules.</summary>
    Confirmed,
}

/// <summary>Where an identity came from. Stage 1 mock rows never touch the operating system.</summary>
public enum IdentitySource
{
    Native,
    Mock,
}

/// <summary>One shown text with how it was obtained ("서비스 관리자", "버전 정보", "명령줄", "역할 사전" ...).</summary>
public sealed record IdentityText(string Text, IdentityConfidence Confidence, string Basis);

/// <summary>A process instance: PID plus creation time, so a reused PID is never mistaken for the old process.</summary>
public readonly record struct ProcessKey(int Pid, long CreateTimeTicks);

/// <summary>An icon as 32-bit premultiplied BGRA pixels (the HICON is destroyed as soon as these are copied).</summary>
public sealed record IconPixels(int Width, int Height, byte[] Bgra);

/// <summary>What the file itself says (version resource, Korean MUI first) — cached per path, invalidated when the file changes.</summary>
public sealed record FileIdentityInfo(
    string Path,
    string? FileDescription,
    string? ProductName,
    string? CompanyName,
    string? FileVersion,
    string? Language,
    IconPixels? Icon)
{
    /// <summary>Company name says Microsoft. Not a signature check, so it only supports inferred statements.</summary>
    public bool CompanyIsMicrosoft => CompanyName?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Something running inside or tied to a host process: a hosted service, a loaded shell extension, a COM AppID/CLSID,
/// the host app of a WebView2. Auxiliary information only: the host's CPU, memory and GPU memory are never split among these.
/// </summary>
public sealed record HostedItem(string Kind, string Name, IdentityConfidence Confidence, string? Detail = null);

/// <summary>A service of the service control manager with its (localized) display name.</summary>
public sealed record ServiceEntry(string Name, string DisplayName);

/// <summary>A process from the system snapshot (no handle needed).</summary>
public sealed record ProcessEntry(int Pid, int ParentPid, string ImageName, long CreateTimeTicks)
{
    public ProcessKey Key => new(Pid, CreateTimeTicks);
}

/// <summary>The identity of one process instance, ready for a tooltip. Holds no raw command line.</summary>
public sealed record ProcessIdentityInfo
{
    public required ProcessKey Key { get; init; }
    public required string ImageName { get; init; }
    public required IdentitySource Source { get; init; }

    /// <summary>Main name, by priority: shared host result → Korean file description / service name → role dictionary → product / file name.</summary>
    public required IdentityText Title { get; init; }

    /// <summary>Short Korean role ("Windows 서비스 호스트 — ...").</summary>
    public IdentityText? Role { get; init; }

    /// <summary>The program really running inside a generic launcher ("train.py · 가상환경 .venv").</summary>
    public IdentityText? Executor { get; init; }

    public string? ProductName { get; init; }
    public string? CompanyName { get; init; }
    public string? FileDescription { get; init; }
    public string? PackageFullName { get; init; }
    public string? AppUserModelId { get; init; }

    /// <summary>Shared host: its totals belong to the whole host, never to one hosted service or extension.</summary>
    public bool IsSharedHost { get; init; }

    public IReadOnlyList<HostedItem> Hosted { get; init; } = [];

    /// <summary>Command line with secrets and the user profile name removed (never the original).</summary>
    public string? RedactedCommandLine { get; init; }

    public IconPixels? Icon { get; init; }

    /// <summary>What could not be read and why ("명령줄: 권한 부족" ...), so a missing value is explained, not hidden.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public const string SharedHostNotice = "공유 호스트: CPU·메모리·VRAM은 이 프로세스 전체 값이며, 아래 서비스·확장 각각의 사용량이 아닙니다.";

    /// <summary>Multi-line Korean text for the existing name tooltip.</summary>
    public string ToToolTipText()
    {
        var sb = new StringBuilder();
        sb.Append(Title.Text).Append(Mark(Title.Confidence)).Append('\n');
        if (Role is not null)
        {
            sb.Append("역할: ").Append(Role.Text).Append(Mark(Role.Confidence)).Append('\n');
        }

        if (Executor is not null)
        {
            sb.Append("실행 주체: ").Append(Executor.Text).Append(Mark(Executor.Confidence)).Append('\n');
        }

        if (ProductName is not null && ProductName != Title.Text)
        {
            sb.Append("제품: ").Append(ProductName);
            if (CompanyName is not null)
            {
                sb.Append(" · ").Append(CompanyName);
            }

            sb.Append('\n');
        }

        foreach (var group in Hosted.GroupBy(h => h.Kind))
        {
            var items = group.ToList();
            sb.Append(group.Key).Append(": ").Append(string.Join(", ", items.Take(4).Select(h => h.Name + Mark(h.Confidence))));
            if (items.Count > 4)
            {
                sb.Append(" 외 ").Append(items.Count - 4).Append("개");
            }

            sb.Append('\n');
        }

        if (IsSharedHost)
        {
            sb.Append(SharedHostNotice).Append('\n');
        }

        sb.Append(ImageName).Append(" · PID ").Append(Key.Pid);
        if (Source == IdentitySource.Mock)
        {
            sb.Append(" · Stage 1 모의 데이터(실제 프로세스를 조회하지 않음)");
        }

        foreach (var note in Notes)
        {
            sb.Append('\n').Append(note);
        }

        return sb.ToString();
    }

    /// <summary>Confirmed text has no mark; inferred text is marked "(추정)".</summary>
    public static string Mark(IdentityConfidence c) => c == IdentityConfidence.Confirmed ? "" : c == IdentityConfidence.Inferred ? " (추정)" : " (확인 불가)";
}
