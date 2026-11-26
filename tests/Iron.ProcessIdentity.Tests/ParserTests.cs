using Iron.ProcessIdentity;

namespace Iron.ProcessIdentity.Tests;

public class CommandLineTests
{
    [Fact]
    public void SplitsLikeCommandLineToArgvW()
    {
        Assert.Equal(["C:\\Program Files\\App\\x.exe", "-a", "b c", "d"], CommandLineParser.Split("\"C:\\Program Files\\App\\x.exe\" -a \"b c\" d"));
        Assert.Equal(["x.exe", "a\"b", "c\\d e"], CommandLineParser.Split("x.exe \"a\\\"b\" c\\\\\"d e\""));
        Assert.Equal(["x.exe", "a\"b"], CommandLineParser.Split("x.exe \"a\"\"b\""));
        Assert.Equal(["x.exe", "C:\\dir\\"], CommandLineParser.Split("x.exe C:\\dir\\"));
        Assert.Empty(CommandLineParser.Split("   "));
    }

    [Fact]
    public void ReadsOptionsInEveryForm()
    {
        var a = CommandLineParser.Split("x.exe --type=renderer -k netsvcs /Processid:{AB8902B4-09CA-4BB6-B78D-A8F59079A8D5} --user-data-dir \"C:\\a b\"");
        Assert.Equal("renderer", CommandLineParser.Option(a, "--type"));
        Assert.Equal("netsvcs", CommandLineParser.Option(a, "-k"));
        Assert.Equal("{AB8902B4-09CA-4BB6-B78D-A8F59079A8D5}", CommandLineParser.Option(a, "/Processid"));
        Assert.Equal("C:\\a b", CommandLineParser.Option(a, "--user-data-dir"));
        Assert.Null(CommandLineParser.Option(a, "--missing"));
    }

    [Fact]
    public void RestAfterProgramKeepsQuotedProgram()
    {
        Assert.Equal("/c \"deploy.bat\" x", CommandLineParser.RestAfterProgram("\"C:\\Windows\\cmd.exe\" /c \"deploy.bat\" x"));
        Assert.Equal("", CommandLineParser.RestAfterProgram("cmd.exe"));
    }
}

public class RedactorTests
{
    [Theory]
    [InlineData("tool.exe --password=hunter2 --mode fast", "hunter2")]
    [InlineData("tool.exe --api-key abcdef123 run", "abcdef123")]
    [InlineData("tool.exe -Token s3cr3t", "s3cr3t")]
    [InlineData("tool.exe /pwd:topsecret", "topsecret")]
    [InlineData("curl https://admin:Passw0rd@example.com/x", "Passw0rd")]
    [InlineData("app.exe --url https://e.com/cb?token=QQQ111&x=1", "QQQ111")]
    [InlineData("app.exe --blob 0123456789abcdef0123456789abcdef0123", "0123456789abcdef0123456789abcdef0123")]
    [InlineData("app.exe eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.c2lnbmF0dXJl", "eyJhbGciOiJIUzI1NiJ9")]
    public void RemovesSecrets(string commandLine, string secret)
    {
        var r = Redactor.Redact(commandLine);
        Assert.DoesNotContain(secret, r);
        Assert.Contains("***", r);
    }

    [Fact]
    public void RemovesUserProfileNameAndKeepsNormalArguments()
    {
        var r = Redactor.Redact("\"C:\\Users\\kim.jh\\proj\\.venv\\Scripts\\python.exe\" train.py --epochs 3 -k netsvcs");
        Assert.DoesNotContain("kim.jh", r);
        Assert.Contains("%USERPROFILE%", r);
        Assert.Contains("--epochs 3", r);
        Assert.Contains("-k netsvcs", r);
        Assert.Equal(Redactor.MaxLength + 1, Redactor.Redact(string.Join(" ", Enumerable.Repeat("word", 400))).Length);
    }
}

public class ClassifierTests
{
    private static readonly RoleDictionary Dict = RoleDictionary.Default;

    private static ClassifyResult C(string image, string? cmd, IHostContext? ctx = null, IReadOnlyList<ServiceEntry>? services = null, string? parent = null,
        IReadOnlyList<ModuleInfo>? modules = null, string? imagePath = null)
        => SharedHostClassifier.Classify(new ClassifyInput(image, imagePath, cmd, services, parent, modules, modules is not null), ctx ?? new FakeHostContext(), Dict);

    [Fact]
    public void Svchost_UsesServiceManagerAndGroup()
    {
        var services = new[] { new ServiceEntry("Schedule", "작업 스케줄러"), new ServiceEntry("Winmgmt", "Windows Management Instrumentation"), new ServiceEntry("BITS", "Background Intelligent Transfer Service") };
        var r = C("svchost.exe", @"C:\Windows\system32\svchost.exe -k netsvcs -p -s Schedule", services: services);
        Assert.Equal("작업 스케줄러 외 2개", r.Title!.Text);
        Assert.Equal(IdentityConfidence.Confirmed, r.Title.Confidence);
        Assert.Equal("서비스 그룹 -k netsvcs (네트워크·시스템 서비스 그룹)", r.Executor!.Text);
        Assert.True(r.IsSharedHost);
        Assert.Equal(3, r.Hosted.Count(h => h.Kind == SharedHostClassifier.KindService));
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Svchost_FallsBackWhenServiceManagerOrCommandLineIsMissing()
    {
        var noScm = C("svchost.exe", "svchost.exe -k LocalServiceNetworkRestricted -p", services: null);
        Assert.Equal(IdentityConfidence.Inferred, noScm.Title!.Confidence);
        Assert.Contains(noScm.Notes, n => n.Contains("서비스 목록", StringComparison.Ordinal));
        var noCmd = C("svchost.exe", null, services: [new ServiceEntry("Dhcp", "DHCP Client")]);
        Assert.Equal("DHCP Client", noCmd.Title!.Text);
        Assert.Contains(noCmd.Notes, n => n.Contains("명령줄", StringComparison.Ordinal));
    }

    [Fact]
    public void Dllhost_SeparatesAppIdAndClsidAndListsThirdPartyExtensions()
    {
        var id = SharedHostClassifier.ThumbnailCacheId;
        var ctx = new FakeHostContext();
        ctx.AppIds[id] = new ComAppIdInfo(id, "Thumbnail Cache Out of Proc Server", true);
        ctx.Clsids[id] = new ComClsidInfo(id, "Thumbnail Cache Class Factory", @"C:\Windows\System32\thumbcache.dll", id);
        var modules = new[]
        {
            new ModuleInfo(@"C:\Windows\System32\thumbcache.dll", new FileIdentityInfo("", "Thumbnail Cache", "Windows", "Microsoft Corporation", null, null, null)),
            new ModuleInfo(@"C:\Program Files\Icaros\64-bit\IcarosThumbnailProvider.dll", new FileIdentityInfo("", "Icaros Thumbnail Provider", "Icaros", "Xanashi", null, null, null)),
            new ModuleInfo(@"C:\Program Files\PowerToys\PowerToys.SvgThumbnailProvider.dll", new FileIdentityInfo("", "SVG Thumbnail Provider", "PowerToys", "Microsoft Corporation", null, null, null)),
        };
        var r = C("dllhost.exe", @"C:\Windows\system32\DllHost.exe /Processid:{AB8902B4-09CA-4BB6-B78D-A8F59079A8D5}", ctx, modules: modules);
        Assert.Equal("COM 서러게이트: Thumbnail Cache Out of Proc Server", r.Title!.Text);
        Assert.Equal(IdentityConfidence.Confirmed, r.Title.Confidence);
        Assert.Contains(r.Hosted, h => h.Kind == SharedHostClassifier.KindAppId && h.Confidence == IdentityConfidence.Confirmed);
        Assert.Contains(r.Hosted, h => h.Kind == SharedHostClassifier.KindClsid && h.Detail!.Contains("AppID 연결 확인", StringComparison.Ordinal));
        var ext = r.Hosted.Where(h => h.Kind == SharedHostClassifier.KindExtension).ToList();
        Assert.Equal(["Icaros Thumbnail Provider", "SVG Thumbnail Provider"], ext.Select(e => e.Name)); // Windows-folder modules are not listed
        Assert.Contains("Xanashi", ext[0].Detail, StringComparison.Ordinal);
        Assert.True(r.WantsModuleCheck);
        Assert.True(r.IsSharedHost);
        Assert.Equal(IdentityConfidence.Inferred, r.Executor!.Confidence);
    }

    [Fact]
    public void Dllhost_ClsidWithoutAppIdLinkIsOnlyInferred()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var ctx = new FakeHostContext();
        ctx.Clsids[id] = new ComClsidInfo(id, "Some Class", null, null);
        var r = C("dllhost.exe", "dllhost.exe /Processid:{11111111-2222-3333-4444-555555555555}", ctx);
        var clsid = Assert.Single(r.Hosted, h => h.Kind == SharedHostClassifier.KindClsid);
        Assert.Equal(IdentityConfidence.Inferred, clsid.Confidence);
        Assert.Equal(IdentityConfidence.Inferred, r.Title!.Confidence); // no AppID name, CLSID not linked
        Assert.False(r.WantsModuleCheck);
    }

    [Fact]
    public void Cmd_FindsBatchOrCommandAndParent()
    {
        Assert.Equal("배치 파일 deploy.bat · 부모 explorer.exe", C("cmd.exe", "C:\\Windows\\system32\\cmd.exe /c \"C:\\work\\deploy.bat\" --password=x", parent: "explorer.exe").Executor!.Text);
        Assert.Equal("명령 ping", C("cmd.exe", "cmd.exe /C ping -n 30 127.0.0.1").Executor!.Text);
        Assert.Equal("프로그램 node.exe", C("cmd.exe", "cmd /k call node.exe app.js").Executor!.Text);
        Assert.Equal("대화형 명령 프롬프트", C("cmd.exe", "cmd.exe").Executor!.Text);
    }

    [Fact]
    public void PowerShell_FileCommandAndEncodedCommand()
    {
        Assert.Equal("스크립트 backup.ps1", C("powershell.exe", "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\scripts\\backup.ps1").Executor!.Text);
        Assert.Equal("인라인 명령 (Get-Process)", C("pwsh.exe", "pwsh -Command \"Get-Process | Sort CPU\"").Executor!.Text);
        var enc = C("powershell.exe", "powershell -EncodedCommand SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQA").Executor!.Text;
        Assert.Equal("인코딩된 명령(내용 숨김)", enc);
    }

    [Fact]
    public void Python_ScriptModuleVenvAndInlineCode()
    {
        var ctx = new FakeHostContext();
        ctx.Existing.Add(@"C:\proj\ml-lab\.venv\pyvenv.cfg");
        var r = C("python.exe", @"C:\proj\ml-lab\.venv\Scripts\python.exe C:\proj\ml-lab\src\train.py --epochs 3", ctx, imagePath: @"C:\proj\ml-lab\.venv\Scripts\python.exe");
        Assert.Equal("train.py · 프로젝트 ml-lab · 가상환경 .venv", r.Executor!.Text);
        Assert.Equal("모듈 uvicorn", C("python.exe", "python.exe -u -m uvicorn app:app --port 9000").Executor!.Text);
        Assert.Equal("인라인 코드(내용 숨김)", C("pythonw.exe", "pythonw.exe -c \"import secret\"").Executor!.Text);
        Assert.Contains("conda 환경 torch", C("python.exe", "python.exe x.py", imagePath: @"C:\miniconda3\envs\torch\python.exe").Executor!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Node_ScriptPackageAndNodeModules()
    {
        var ctx = new FakeHostContext();
        ctx.Packages[@"C:\web\shop\node_modules\vite\bin"] = "vite";
        Assert.Equal("vite (node_modules) · 패키지 vite", C("node.exe", @"node.exe C:\web\shop\node_modules\vite\bin\vite.js --port 5173", ctx).Executor!.Text);
        var ctx2 = new FakeHostContext();
        ctx2.Packages[@"C:\web\shop"] = "shop-server";
        Assert.Equal("server.js · 패키지 shop-server", C("node.exe", @"node --inspect C:\web\shop\server.js", ctx2).Executor!.Text);
        Assert.Equal("인라인 코드(내용 숨김)", C("node.exe", "node -e \"console.log(1)\"").Executor!.Text);
    }

    [Fact]
    public void WebView2_HostAppDataDirAndComponent()
    {
        var cmd = "\"C:\\Program Files (x86)\\Microsoft\\EdgeWebView\\Application\\131.0\\msedgewebview2.exe\" --type=renderer --webview-exe-name=ms-teams.exe "
                  + "--user-data-dir=\"C:\\Users\\kim\\AppData\\Local\\Packages\\MSTeams_8wekyb3d8bbwe\\LocalCache\\Microsoft\\MSTeams\\EBWebView\" --lang=ko";
        var r = C("msedgewebview2.exe", cmd);
        Assert.Equal("WebView2 · ms-teams.exe", r.Title!.Text);
        Assert.Equal(IdentityConfidence.Confirmed, r.Title.Confidence);
        Assert.Equal("ms-teams.exe · 렌더러(웹 페이지)", r.Executor!.Text);
        Assert.Contains(r.Hosted, h => h.Kind == SharedHostClassifier.KindHostApp && h.Name == "ms-teams.exe");
        Assert.Contains(r.Hosted, h => h.Name == "MSTeams_8wekyb3d8bbwe" && h.Confidence == IdentityConfidence.Inferred);
        Assert.True(r.IsSharedHost);
        Assert.DoesNotContain(r.Hosted, h => (h.Detail ?? "").Contains("kim", StringComparison.Ordinal) || h.Name.Contains("kim", StringComparison.Ordinal));
        var child = C("msedgewebview2.exe", "msedgewebview2.exe --type=gpu-process");
        Assert.Equal(IdentityConfidence.Inferred, child.Executor!.Confidence);
        Assert.Contains(child.Notes, n => n.Contains("호스트 앱", StringComparison.Ordinal));
    }

    [Fact]
    public void Rundll32_ResolvesTargetDllVersionInfo()
    {
        var ctx = new FakeHostContext();
        ctx.Files[@"C:\Windows\System32\shell32.dll"] = new FileIdentityInfo(@"C:\Windows\System32\shell32.dll", "Windows 셸 공용 DLL", "Microsoft® Windows® Operating System", "Microsoft Corporation", null, "ko-KR", null);
        var r = C("rundll32.exe", "rundll32.exe shell32.dll,Control_RunDLL desk.cpl", ctx);
        Assert.Equal("Windows 셸 공용 DLL · 진입점 Control_RunDLL", r.Executor!.Text);
        Assert.Equal(IdentityConfidence.Confirmed, r.Executor.Confidence);
        var unknown = C("rundll32.exe", "rundll32.exe \"C:\\Program Files\\Foo\\foo.dll\",#12");
        Assert.Equal("foo.dll · 진입점 #12", unknown.Executor!.Text);
        Assert.Equal(IdentityConfidence.Inferred, unknown.Executor.Confidence);
    }

    [Fact]
    public void OtherSharedHosts()
    {
        var t = C("taskhostw.exe", "taskhostw.exe KEYROAMING");
        Assert.True(t.IsSharedHost);
        Assert.Equal("작업 KEYROAMING", t.Executor!.Text);
        Assert.True(C("WmiPrvSE.exe", "wmiprvse.exe -Embedding").IsSharedHost);
        Assert.False(C("notepad.exe", "notepad.exe").IsSharedHost);
    }
}
