using System.Text.RegularExpressions;

namespace Iron.ProcessIdentity;

/// <summary>COM application registration (HKCR\AppID\{GUID}): the id dllhost's /Processid: names.</summary>
public sealed record ComAppIdInfo(Guid Id, string? Name, bool HasDllSurrogate);

/// <summary>COM class registration (HKCR\CLSID\{GUID}). A CLSID with the same GUID as an AppID is a different key.</summary>
public sealed record ComClsidInfo(Guid Id, string? Name, string? InprocServer, Guid? AppId);

public sealed record ModuleInfo(string Path, FileIdentityInfo? File);

/// <summary>What the classifier may look up. The native implementation reads the registry and files; tests use fakes; mock rows use <see cref="NullHostContext"/>.</summary>
public interface IHostContext
{
    ComAppIdInfo? LookupAppId(Guid id);
    ComClsidInfo? LookupClsid(Guid id);
    FileIdentityInfo? GetFile(string path);

    /// <summary>"shell32.dll" → full path in the system folder when it exists there.</summary>
    string? ResolveSystemPath(string fileName);

    /// <summary>"name" of the nearest package.json at or above the folder (at most three levels).</summary>
    string? ReadPackageName(string directory);

    bool FileExists(string path);

    /// <summary>The service's own description (what it does), localized; null when unknown. Read lazily and cached.</summary>
    string? ServiceDescription(string serviceName) => null;
}

/// <summary>Looks nothing up (Stage 1 mock rows: no registry, no file, no process access).</summary>
public sealed class NullHostContext : IHostContext
{
    public static NullHostContext Instance { get; } = new();
    public ComAppIdInfo? LookupAppId(Guid id) => null;
    public ComClsidInfo? LookupClsid(Guid id) => null;
    public FileIdentityInfo? GetFile(string path) => null;
    public string? ResolveSystemPath(string fileName) => null;
    public string? ReadPackageName(string directory) => null;
    public bool FileExists(string path) => false;
}

/// <param name="CommandLine">Raw command line, used during classification only and never stored.</param>
/// <param name="Services">Services the service manager maps to this PID; null when the query failed or was not made.</param>
/// <param name="Modules">Loaded modules (dllhost shell-extension surrogates only); null when not read.</param>
public sealed record ClassifyInput(
    string ImageName,
    string? ImagePath,
    string? CommandLine,
    IReadOnlyList<ServiceEntry>? Services,
    string? ParentImageName,
    IReadOnlyList<ModuleInfo>? Modules,
    bool ModulesAttempted = false,
    bool IsMock = false);

public sealed record ClassifyResult(
    IdentityText? Title,
    IdentityText? Executor,
    bool IsSharedHost,
    IReadOnlyList<HostedItem> Hosted,
    IReadOnlyList<string> Notes,
    bool WantsModuleCheck,
    RoleEntry? GuidRole,
    IdentityText? Role = null);

/// <summary>
/// Finds the real program behind generic launchers and shared hosts. Pure: everything it needs comes in through
/// <see cref="ClassifyInput"/> and <see cref="IHostContext"/>. Hosted services, COM components and loaded extensions are
/// returned as auxiliary items; nothing here assigns the host's resource use to them.
/// </summary>
public static partial class SharedHostClassifier
{
    public const string KindService = "호스팅 서비스";
    public const string KindExtension = "로드된 확장";
    public const string KindAppId = "COM AppID";
    public const string KindClsid = "COM CLSID(같은 GUID)";
    public const string KindHostApp = "호스트 앱";
    public const string KindTargetDll = "대상 DLL";

    public static readonly Guid ThumbnailCacheId = new("AB8902B4-09CA-4BB6-B78D-A8F59079A8D5");

    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    { "src", "scripts", "script", "app", "bin", "lib", "tools", "server", "dist", "build", "source", "code" };

    private static readonly HashSet<string> VenvNames = new(StringComparer.OrdinalIgnoreCase) { ".venv", "venv", "env", ".env", "virtualenv" };

    [GeneratedRegex(@"^[A-Za-z]+-[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex Cmdlet();

    public static ClassifyResult Classify(ClassifyInput input, IHostContext ctx, RoleDictionary dict)
    {
        var args = CommandLineParser.Split(input.CommandLine);
        var hasCmd = input.CommandLine is not null;
        var b = new Builder(input.IsMock);
        switch (input.ImageName.ToLowerInvariant())
        {
            case "svchost.exe":
                Svchost(input, args, hasCmd, ctx, dict, b);
                break;
            case "dllhost.exe":
                Dllhost(input, args, hasCmd, ctx, dict, b);
                break;
            case "cmd.exe":
                Cmd(input, hasCmd, b);
                break;
            case "powershell.exe":
            case "pwsh.exe":
                PowerShell(input, args, hasCmd, b);
                break;
            case "python.exe":
            case "pythonw.exe":
                Python(input, args, hasCmd, ctx, b);
                break;
            case "node.exe":
                Node(input, args, hasCmd, ctx, b);
                break;
            case "msedgewebview2.exe":
                WebView2(args, hasCmd, dict, b);
                break;
            case "rundll32.exe":
                Rundll32(input, hasCmd, ctx, b);
                break;
            case "taskhostw.exe":
                b.Shared = true;
                if (args.Count > 1 && !args[1].StartsWith('-') && !args[1].StartsWith('/'))
                {
                    b.Executor = b.Text("작업 " + Short(args[1], 60), IdentityConfidence.Inferred, "명령줄");
                }

                break;
            case "wmiprvse.exe":
            case "wudfhost.exe":
                b.Shared = true;
                break;
            case "java.exe":
            case "javaw.exe":
                Java(input, args, hasCmd, b);
                break;
            case "dotnet.exe":
                DotNet(input, args, hasCmd, b);
                break;
            case "wsl.exe":
                Wsl(args, hasCmd, b);
                break;
            case "conhost.exe":
                if (input.ParentImageName is { Length: > 0 } client)
                {
                    b.Executor = b.Text("콘솔 창: " + client, IdentityConfidence.Inferred, "프로세스 트리");
                }

                break;
        }

        return b.Build();
    }

    private static void Svchost(ClassifyInput input, IReadOnlyList<string> args, bool hasCmd, IHostContext ctx, RoleDictionary dict, Builder b)
    {
        b.Shared = true;
        var group = CommandLineParser.Option(args, "-k");
        var single = CommandLineParser.Option(args, "-s");
        if (input.Services is { Count: > 0 } services)
        {
            var ordered = services.OrderBy(s => s.DisplayName, StringComparer.Ordinal).ToList();
            var rep = single is null ? null : ordered.FirstOrDefault(s => s.Name.Equals(single, StringComparison.OrdinalIgnoreCase));
            rep ??= ordered[0];
            var title = rep.DisplayName + (ordered.Count > 1 ? $" 외 {ordered.Count - 1}개" : "");
            b.Title = b.Text(title, IdentityConfidence.Confirmed, "서비스 관리자");
            if (!input.IsMock && ctx.ServiceDescription(rep.Name) is { Length: > 0 } description)
            {
                // what the (first) hosted service says it does; the host's use is still not split among its services
                b.Role = b.Text((ordered.Count > 1 ? rep.DisplayName + ": " : "") + Short(description.Trim(), 160), IdentityConfidence.Confirmed, "서비스 설명");
            }
            foreach (var s in ordered)
            {
                b.Hosted.Add(new HostedItem(KindService, s.DisplayName, b.Cap(IdentityConfidence.Confirmed), s.Name));
            }
        }
        else if (!input.IsMock)
        {
            b.Notes.Add(input.Services is null ? "서비스 목록을 확인하지 못함(서비스 관리자 조회 실패)" : "이 PID에 연결된 서비스 없음(시작·종료 중일 수 있음)");
        }

        if (group is not null)
        {
            var desc = dict.SvchostGroup(group);
            b.Executor = b.Text("서비스 그룹 -k " + group + (desc is null ? "" : $" ({desc})"), IdentityConfidence.Confirmed, "명령줄");
            b.Title ??= b.Text("서비스 호스트 (-k " + group + ")", IdentityConfidence.Inferred, "명령줄");
        }
        else if (!hasCmd && !input.IsMock)
        {
            b.Notes.Add("서비스 그룹 미확인(명령줄 없음)");
        }
    }

    private static void Dllhost(ClassifyInput input, IReadOnlyList<string> args, bool hasCmd, IHostContext ctx, RoleDictionary dict, Builder b)
    {
        b.Shared = true;
        var raw = CommandLineParser.Option(args, "/Processid", "-Processid");
        if (raw is null || !Guid.TryParse(raw.Trim(), out var id))
        {
            if (!input.IsMock)
            {
                b.Notes.Add(hasCmd ? "COM 서러게이트 ID(/Processid)가 명령줄에 없음" : "COM 구성 요소 미확인(명령줄 없음)");
            }

            return;
        }

        var idText = id.ToString("B").ToUpperInvariant();
        b.GuidRole = dict.AppId(id) ?? dict.Clsid(id);

        // /Processid: names an AppID. A CLSID with the same GUID is a separate registration; it counts as the
        // component only when its AppID value points back to this AppID.
        var appId = ctx.LookupAppId(id);
        if (appId is not null)
        {
            b.Hosted.Add(new HostedItem(KindAppId, appId.Name ?? idText, b.Cap(IdentityConfidence.Confirmed), "HKCR\\AppID\\" + idText + (appId.HasDllSurrogate ? " · DllSurrogate" : "")));
        }

        var clsid = ctx.LookupClsid(id);
        var linked = clsid is not null && clsid.AppId == id;
        FileIdentityInfo? serverFile = null;
        if (clsid is not null)
        {
            var server = clsid.InprocServer is null ? null : CommandLineParser.FileName(clsid.InprocServer);
            serverFile = clsid.InprocServer is null ? null : ctx.GetFile(clsid.InprocServer);
            var serverText = server is null ? "" : "InprocServer32 " + server
                + (serverFile?.FileDescription is { } fd ? " (" + fd + (serverFile.CompanyName is { } co ? " · " + co : "") + ")" : "") + " · ";
            b.Hosted.Add(new HostedItem(KindClsid, clsid.Name ?? idText, b.Cap(linked ? IdentityConfidence.Confirmed : IdentityConfidence.Inferred),
                serverText + (linked ? "AppID 연결 확인" : "AppID 연결 미확인")));
            // a third-party COM server (outside Windows) is what this surrogate runs for: name it, as the registered server
            if (linked && clsid.InprocServer is { } serverPath && IsNonWindowsModule(serverPath))
            {
                b.Executor = b.Text("COM 서버 " + (serverFile?.FileDescription ?? server) + (serverFile?.CompanyName is { } c2 ? " · " + c2 : ""),
                    serverFile is null ? IdentityConfidence.Inferred : IdentityConfidence.Confirmed, "레지스트리·버전 정보");
            }
        }

        var name = appId?.Name ?? (linked ? clsid!.Name : null);
        if (name is not null)
        {
            b.Title = b.Text("COM 서러게이트: " + name, IdentityConfidence.Confirmed, "레지스트리");
        }
        else if (b.GuidRole is not null)
        {
            b.Title = b.Text("COM 서러게이트: " + b.GuidRole.ShortName, IdentityConfidence.Inferred, "역할 사전");
        }
        else
        {
            b.Title = b.Text("COM 서러게이트 " + idText, IdentityConfidence.Inferred, "명령줄");
            if (!input.IsMock && appId is null && clsid is null)
            {
                b.Notes.Add("COM 등록 정보를 찾지 못함(AppID·CLSID 모두 없음)");
            }
        }

        var shellSurrogate = b.GuidRole?.ShellExtensionSurrogate == true || id == ThumbnailCacheId;
        if (!shellSurrogate)
        {
            return;
        }

        b.WantsModuleCheck = true;
        if (input.Modules is { } modules)
        {
            foreach (var m in modules.Where(m => IsNonWindowsModule(m.Path)).DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
            {
                var label = m.File?.FileDescription ?? CommandLineParser.FileName(m.Path);
                var detail = CommandLineParser.FileName(m.Path) + (m.File?.CompanyName is { } c ? " · " + c : "");
                b.Hosted.Add(new HostedItem(KindExtension, label, b.Cap(IdentityConfidence.Confirmed), detail));
            }

            var ext = b.Hosted.Where(h => h.Kind == KindExtension).ToList();
            if (ext.Count > 0)
            {
                b.Executor = b.Text("셸 확장 " + string.Join(", ", ext.Take(2).Select(e => e.Name)) + (ext.Count > 2 ? $" 외 {ext.Count - 2}개" : ""),
                    IdentityConfidence.Inferred, "로드 모듈");
            }
            else if (!input.IsMock)
            {
                b.Notes.Add("Windows 폴더 밖의 셸 확장 없음(확인 시점 기준)");
            }
        }
        else if (input.ModulesAttempted && !input.IsMock)
        {
            b.Notes.Add("로드 모듈을 읽지 못함(권한 부족 또는 종료)");
        }
    }

    /// <summary>
    /// A module that is not part of Windows (outside the Windows folder): a shell extension such as Icaros or PowerToys.
    /// Its company (also "Microsoft Corporation" for PowerToys/OneDrive) is shown as detail; the extension is listed as
    /// loaded, never as the user of the host's resources.
    /// </summary>
    internal static bool IsNonWindowsModule(string path)
    {
        var p = path.Replace('/', '\\');
        var win = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        return !(p.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase)
                 || p.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase) || p.Contains(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase)
                 || p.Contains(@"\Windows\WinSxS\", StringComparison.OrdinalIgnoreCase));
    }

    private static void Cmd(ClassifyInput input, bool hasCmd, Builder b)
    {
        if (!hasCmd)
        {
            NoCmd(input, b);
            return;
        }

        var rest = CommandLineParser.RestAfterProgram(input.CommandLine!);
        var m = Regex.Match(rest, @"(?i)(?:^|\s)/([ck])\s*(.*)$", RegexOptions.Singleline);
        string text;
        if (!m.Success)
        {
            text = "대화형 명령 프롬프트";
        }
        else
        {
            var body = m.Groups[2].Value.Trim();
            if (body.StartsWith('"') && body.EndsWith('"') && body.Count(c => c == '"') == 2)
            {
                body = body[1..^1];
            }

            var tokens = CommandLineParser.Split("x " + body).Skip(1).ToList();
            while (tokens.Count > 0 && (tokens[0].Equals("call", StringComparison.OrdinalIgnoreCase) || tokens[0].Equals("start", StringComparison.OrdinalIgnoreCase)
                                        || tokens[0].Length == 0 || tokens[0].StartsWith('/')))
            {
                tokens.RemoveAt(0);
            }

            text = tokens.Count == 0 ? "명령 실행" : Describe(tokens[0]) + FolderOf(tokens[0]);
        }

        b.Executor = b.Text(text + Parent(input), IdentityConfidence.Inferred, "명령줄");
    }

    private static void PowerShell(ClassifyInput input, IReadOnlyList<string> args, bool hasCmd, Builder b)
    {
        if (!hasCmd)
        {
            NoCmd(input, b);
            return;
        }

        string? text = null;
        for (var i = 1; i < args.Count && text is null; i++)
        {
            var a = args[i];
            var name = a.TrimStart('-', '/').ToLowerInvariant();
            if (!a.StartsWith('-') && !a.StartsWith('/'))
            {
                text = a.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? "스크립트 " + CommandLineParser.FileName(a) + FolderOf(a) : null;
                break;
            }

            if (name is "file" or "f")
            {
                text = i + 1 < args.Count ? "스크립트 " + CommandLineParser.FileName(args[i + 1]) + FolderOf(args[i + 1]) : "스크립트";
            }
            else if (name is "encodedcommand" or "enc" or "ec" or "e" || name.StartsWith("encodedcommand", StringComparison.Ordinal))
            {
                text = "인코딩된 명령(내용 숨김)";
            }
            else if (name is "command" or "c")
            {
                var next = i + 1 < args.Count ? args[i + 1].Split(' ', ';')[0] : "";
                text = "인라인 명령" + (Cmdlet().IsMatch(next) ? $" ({next})" : "");
            }
            else if (name is "executionpolicy" or "ep" or "windowstyle" or "w" or "configurationname" or "workingdirectory" or "wd")
            {
                i++; // option with a value
            }
        }

        b.Executor = b.Text((text ?? "대화형 PowerShell") + Parent(input), IdentityConfidence.Inferred, "명령줄");
    }

    private static void Python(ClassifyInput input, IReadOnlyList<string> args, bool hasCmd, IHostContext ctx, Builder b)
    {
        var parts = new List<string>();
        var confidence = IdentityConfidence.Inferred;
        if (hasCmd)
        {
            for (var i = 1; i < args.Count; i++)
            {
                var a = args[i];
                if (a == "-m" && i + 1 < args.Count)
                {
                    parts.Add("모듈 " + args[i + 1]);
                    break;
                }

                if (a.StartsWith("-m", StringComparison.Ordinal) && a.Length > 2 && !a.StartsWith("--", StringComparison.Ordinal))
                {
                    parts.Add("모듈 " + a[2..]);
                    break;
                }

                if (a == "-c")
                {
                    parts.Add("인라인 코드(내용 숨김)");
                    break;
                }

                if (a is "-X" or "-W" or "--check-hash-based-pycs")
                {
                    i++;
                    continue;
                }

                if (a.StartsWith('-'))
                {
                    continue;
                }

                parts.Add(CommandLineParser.FileName(a));
                var project = ProjectFolder(a);
                if (project is not null)
                {
                    parts.Add("프로젝트 " + project);
                }

                break;
            }
        }
        else
        {
            NoCmdNote(input, b);
        }

        // Virtual environment: ...\<venv>\Scripts\python.exe with pyvenv.cfg next to Scripts (a file fact).
        if (input.ImagePath is { } image)
        {
            var folders = CommandLineParser.Folders(image);
            if (folders.Count >= 2 && folders[0].Equals("Scripts", StringComparison.OrdinalIgnoreCase))
            {
                var venvDir = DirOf(DirOf(image));
                var isVenv = ctx.FileExists(Path.Combine(venvDir, "pyvenv.cfg"));
                if (isVenv || VenvNames.Contains(folders[1]))
                {
                    parts.Add("가상환경 " + folders[1]);
                    if (isVenv)
                    {
                        confidence = parts.Count == 1 ? IdentityConfidence.Confirmed : confidence;
                    }

                    if (folders.Count >= 3 && VenvNames.Contains(folders[1]) && !parts.Any(p => p.StartsWith("프로젝트 ", StringComparison.Ordinal)))
                    {
                        parts.Add("프로젝트 " + folders[2]);
                    }
                }
            }
            else
            {
                var envs = folders.ToList();
                var k = envs.FindIndex(f => f.Equals("envs", StringComparison.OrdinalIgnoreCase));
                if (k > 0)
                {
                    parts.Add("conda 환경 " + envs[k - 1]);
                }
            }
        }

        if (parts.Count > 0)
        {
            b.Executor = b.Text(string.Join(" · ", parts), confidence, "명령줄·경로");
        }
    }

    private static void Node(ClassifyInput input, IReadOnlyList<string> args, bool hasCmd, IHostContext ctx, Builder b)
    {
        if (!hasCmd)
        {
            NoCmd(input, b);
            return;
        }

        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-r" or "--require" or "--import" or "--loader" or "--env-file" or "--input-type" or "--title" or "-C" or "--conditions")
            {
                i++;
                continue;
            }

            if (a is "-e" or "--eval" or "-p" or "--print")
            {
                b.Executor = b.Text("인라인 코드(내용 숨김)" + Parent(input), IdentityConfidence.Inferred, "명령줄");
                return;
            }

            if (a.StartsWith('-'))
            {
                continue;
            }

            var p = a.Replace('/', '\\');
            var nm = p.LastIndexOf(@"\node_modules\", StringComparison.OrdinalIgnoreCase);
            string text;
            if (nm >= 0)
            {
                var pkg = p[(nm + 14)..].Split('\\');
                var pkgName = pkg.Length > 1 && pkg[0].StartsWith('@') ? pkg[0] + "/" + pkg[1] : pkg[0];
                text = pkgName + " (node_modules)";
            }
            else
            {
                text = CommandLineParser.FileName(a);
            }

            var dir = DirOf(a);
            var pkgJson = dir.Length > 0 ? ctx.ReadPackageName(dir) : null;
            var conf = IdentityConfidence.Inferred;
            if (pkgJson is not null)
            {
                // the package.json name confirms the node_modules package; repeat it only when it says something new
                if (!text.StartsWith(pkgJson + " ", StringComparison.Ordinal))
                {
                    text += " · 패키지 " + pkgJson;
                }
            }
            else if (ProjectFolder(a) is { } proj)
            {
                text += " · 프로젝트 " + proj;
            }

            b.Executor = b.Text(text, conf, pkgJson is null ? "명령줄" : "명령줄·package.json");
            return;
        }

        b.Executor = b.Text("대화형 Node.js" + Parent(input), IdentityConfidence.Inferred, "명령줄");
    }

    private static void WebView2(IReadOnlyList<string> args, bool hasCmd, RoleDictionary dict, Builder b)
    {
        b.Shared = true;
        if (!hasCmd)
        {
            if (!b.IsMock)
            {
                b.Notes.Add("호스트 앱 미확인(명령줄 없음)");
            }

            return;
        }

        var exe = CommandLineParser.Option(args, "--webview-exe-name");
        var dataDir = CommandLineParser.Option(args, "--user-data-dir");
        var type = CommandLineParser.Option(args, "--type");
        var sub = CommandLineParser.Option(args, "--utility-sub-type");
        var component = type is null ? "브라우저(주 프로세스)" : (dict.WebViewType(type) ?? type) + (sub is null ? "" : " · " + sub.Split('.').Last());
        string? app = null;
        if (dataDir is not null)
        {
            var folders = CommandLineParser.Folders(dataDir.TrimEnd('\\', '/') + "\\x");
            var pk = folders.ToList().FindIndex(f => f.Equals("Packages", StringComparison.OrdinalIgnoreCase));
            app = pk > 0 ? folders[pk - 1] : folders.FirstOrDefault(f => !f.Equals("EBWebView", StringComparison.OrdinalIgnoreCase) && !f.Equals("User Data", StringComparison.OrdinalIgnoreCase));
            if (app is not null && app.Equals("Local", StringComparison.OrdinalIgnoreCase))
            {
                app = null;
            }
        }

        if (exe is not null)
        {
            b.Hosted.Add(new HostedItem(KindHostApp, exe, b.Cap(IdentityConfidence.Confirmed), "명령줄 --webview-exe-name"));
            b.Title = b.Text("WebView2 · " + exe, IdentityConfidence.Confirmed, "명령줄");
            b.Executor = b.Text(exe + " · " + component, IdentityConfidence.Confirmed, "명령줄");
        }
        else if (app is not null)
        {
            b.Executor = b.Text(app + " · " + component, IdentityConfidence.Inferred, "--user-data-dir 폴더");
        }
        else
        {
            b.Executor = b.Text(component, IdentityConfidence.Inferred, "명령줄");
            if (!b.IsMock)
            {
                b.Notes.Add("호스트 앱을 명령줄에서 찾지 못함");
            }
        }

        if (app is not null && exe is not null)
        {
            b.Hosted.Add(new HostedItem("사용자 데이터 폴더", app, b.Cap(IdentityConfidence.Inferred), null));
        }
    }

    private static void Rundll32(ClassifyInput input, bool hasCmd, IHostContext ctx, Builder b)
    {
        if (!hasCmd)
        {
            NoCmd(input, b);
            return;
        }

        var rest = CommandLineParser.RestAfterProgram(input.CommandLine!);
        var tokens = CommandLineParser.Split("x " + rest);
        if (tokens.Count < 2)
        {
            b.Executor = b.Text("대상 DLL 없음", IdentityConfidence.Inferred, "명령줄");
            return;
        }

        var target = tokens[1];
        var comma = target.IndexOf(',');
        var dll = (comma >= 0 ? target[..comma] : target).Trim().Trim('"');
        var entry = comma >= 0 ? target[(comma + 1)..].Trim() : (tokens.Count > 2 ? tokens[2] : "");
        var path = dll.Contains('\\') || dll.Contains('/') ? dll : ctx.ResolveSystemPath(dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? dll : dll + ".dll");
        var file = path is null ? null : ctx.GetFile(path);
        var name = file?.FileDescription ?? CommandLineParser.FileName(dll);
        b.Hosted.Add(new HostedItem(KindTargetDll, CommandLineParser.FileName(dll), b.Cap(file is null ? IdentityConfidence.Inferred : IdentityConfidence.Confirmed),
            file?.CompanyName));
        b.Executor = b.Text(name + (entry.Length > 0 ? " · 진입점 " + Short(entry, 40) : ""), file is null ? IdentityConfidence.Inferred : IdentityConfidence.Confirmed,
            file is null ? "명령줄" : "명령줄·버전 정보");
    }

    /// <summary>java / javaw: -jar target, -m module, or the main class (class path and VM options skipped).</summary>
    private static void Java(ClassifyInput input, IReadOnlyList<string> args, bool hasCmd, Builder b)
    {
        if (!hasCmd)
        {
            NoCmd(input, b);
            return;
        }

        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-cp" or "-classpath" or "--class-path" or "-p" or "--module-path" or "--add-opens" or "--add-exports" or "--add-modules"
                or "--add-reads" or "--patch-module" or "--upgrade-module-path" or "--limit-modules" or "-agentpath" or "-javaagent")
            {
                i++;
                continue;
            }

            if (a is "-jar" && i + 1 < args.Count)
            {
                b.Executor = b.Text("JAR " + CommandLineParser.FileName(args[i + 1]) + FolderOf(args[i + 1]) + Parent(input), IdentityConfidence.Inferred, "명령줄");
                return;
            }

            if (a is "-m" or "--module" && i + 1 < args.Count)
            {
                b.Executor = b.Text("모듈 " + Short(args[i + 1], 60), IdentityConfidence.Inferred, "명령줄");
                return;
            }

            if (a.StartsWith('-') || a.StartsWith('@'))
            {
                continue;
            }

            b.Executor = b.Text("클래스 " + Short(a, 60), IdentityConfidence.Inferred, "명령줄");
            return;
        }

        b.Executor = b.Text("대상 미확인" + Parent(input), IdentityConfidence.Inferred, "명령줄");
    }

    private static readonly HashSet<string> DotNetCommands = new(StringComparer.OrdinalIgnoreCase)
    { "run", "watch", "build", "test", "publish", "restore", "pack", "tool", "exec", "ef", "format", "new", "msbuild", "nuget", "dev-certs", "workload", "sln", "add", "remove", "list", "clean" };

    /// <summary>dotnet: "dotnet app.dll" (the app) or an SDK command ("dotnet run --project X").</summary>
    private static void DotNet(ClassifyInput input, IReadOnlyList<string> args, bool hasCmd, Builder b)
    {
        if (!hasCmd)
        {
            NoCmd(input, b);
            return;
        }

        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith('-'))
            {
                if (a is "--additionalprobingpath" or "--depsfile" or "--runtimeconfig" or "--fx-version" or "--roll-forward" or "--additional-deps")
                {
                    i++;
                }

                continue;
            }

            if (a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                b.Executor = b.Text("앱 " + CommandLineParser.FileName(a) + FolderOf(a), IdentityConfidence.Inferred, "명령줄");
                return;
            }

            if (DotNetCommands.Contains(a))
            {
                var project = CommandLineParser.Option(args, "--project");
                b.Executor = b.Text("SDK 명령 " + a.ToLowerInvariant() + (project is null ? "" : " · " + CommandLineParser.FileName(project)) + Parent(input),
                    IdentityConfidence.Inferred, "명령줄");
                return;
            }

            break;
        }

        b.Executor = b.Text("대상 미확인" + Parent(input), IdentityConfidence.Inferred, "명령줄");
    }

    /// <summary>wsl.exe: the distribution (-d) and, when given, the command it runs (-e / --exec / after "--").</summary>
    private static void Wsl(IReadOnlyList<string> args, bool hasCmd, Builder b)
    {
        if (!hasCmd)
        {
            return;
        }

        var distro = CommandLineParser.Option(args, "-d") ?? CommandLineParser.Option(args, "--distribution");
        string? command = CommandLineParser.Option(args, "-e") ?? CommandLineParser.Option(args, "--exec");
        var dashes = args.ToList().IndexOf("--");
        if (command is null && dashes > 0 && dashes + 1 < args.Count)
        {
            command = args[dashes + 1];
        }

        var parts = new List<string> { "배포판 " + (distro ?? "기본값") };
        if (command is not null)
        {
            parts.Add("명령 " + Short(CommandLineParser.FileName(command), 40));
        }

        b.Executor = b.Text(string.Join(" · ", parts), IdentityConfidence.Inferred, "명령줄");
    }

    /// <summary>" · 폴더 X" for a script or program given with a path, when its folder names something (not "src", "bin" ...).</summary>
    private static string FolderOf(string path) =>
        (path.Contains('\\') || path.Contains('/')) && ProjectFolder(path) is { } folder ? " · 폴더 " + folder : "";

    private static string Describe(string token)
    {
        var name = CommandLineParser.FileName(token);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".bat" or ".cmd" => "배치 파일 " + name,
            ".ps1" => "스크립트 " + name,
            ".py" => "Python 스크립트 " + name,
            ".js" or ".mjs" or ".cjs" => "JavaScript " + name,
            ".exe" or ".com" => "프로그램 " + name,
            _ => "명령 " + Short(name, 30),
        };
    }

    private static string Parent(ClassifyInput input) => input.ParentImageName is { Length: > 0 } p ? " · 부모 " + p : "";

    private static void NoCmd(ClassifyInput input, Builder b)
    {
        NoCmdNote(input, b);
        if (input.ParentImageName is { Length: > 0 } p)
        {
            b.Executor = b.Text("부모 " + p, IdentityConfidence.Inferred, "프로세스 트리");
        }
    }

    private static void NoCmdNote(ClassifyInput input, Builder b)
    {
        if (!input.IsMock)
        {
            b.Notes.Add("실행 대상 미확인(명령줄 없음)");
        }
    }

    /// <summary>Folder that names the project of a script: its folder, or the one above a generic name like "src".</summary>
    internal static string? ProjectFolder(string scriptPath)
    {
        var folders = CommandLineParser.Folders(scriptPath);
        foreach (var f in folders.Take(3))
        {
            if (!GenericFolders.Contains(f) && !VenvNames.Contains(f) && !f.Equals("Users", StringComparison.OrdinalIgnoreCase) && !f.StartsWith('%'))
            {
                return f;
            }
        }

        return null;
    }

    private static string DirOf(string path)
    {
        var p = path.Trim().Trim('"');
        var cut = p.LastIndexOfAny(['\\', '/']);
        return cut > 0 ? p[..cut] : "";
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class Builder(bool isMock)
    {
        public bool IsMock { get; } = isMock;
        public IdentityText? Title { get; set; }
        public IdentityText? Executor { get; set; }
        public bool Shared { get; set; }
        public bool WantsModuleCheck { get; set; }
        public RoleEntry? GuidRole { get; set; }
        public IdentityText? Role { get; set; }
        public List<HostedItem> Hosted { get; } = [];
        public List<string> Notes { get; } = [];

        /// <summary>Mock rows confirm nothing: nothing was read from the system.</summary>
        public IdentityConfidence Cap(IdentityConfidence c) => IsMock && c == IdentityConfidence.Confirmed ? IdentityConfidence.Inferred : c;

        public IdentityText Text(string text, IdentityConfidence c, string basis) => new(text, Cap(c), IsMock ? basis + "(모의)" : basis);

        public ClassifyResult Build() => new(Title, Executor, Shared, Hosted, Notes, WantsModuleCheck, GuidRole, Role);
    }
}
