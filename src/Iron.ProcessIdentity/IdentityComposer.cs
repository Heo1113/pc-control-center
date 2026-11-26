namespace Iron.ProcessIdentity;

/// <summary>What the native layer read about one process (any part may be missing when access is denied).</summary>
public sealed record NativeFacts(
    ProcessEntry Entry,
    string? ImagePath,
    string? CommandLine,
    FileIdentityInfo? File,
    string? PackageFullName,
    string? AppUserModelId,
    IReadOnlyList<ServiceEntry>? Services,
    string? ParentImageName,
    IReadOnlyList<ModuleInfo>? Modules,
    bool ModulesAttempted,
    IReadOnlyList<string> Notes);

/// <summary>
/// Builds the identity from what was read, by the agreed priority: shared-host result → Korean file description or
/// service display name → role dictionary → product name / file name. The raw command line is used here and dropped:
/// only its redacted form is kept.
/// </summary>
public static class IdentityComposer
{
    public static ProcessIdentityInfo Compose(NativeFacts f, IHostContext ctx, RoleDictionary dict, out bool wantsModuleCheck)
    {
        var classified = SharedHostClassifier.Classify(
            new ClassifyInput(f.Entry.ImageName, f.ImagePath, f.CommandLine, f.Services, f.ParentImageName, f.Modules, f.ModulesAttempted), ctx, dict);
        wantsModuleCheck = classified.WantsModuleCheck;
        return Build(f.Entry.Key, f.Entry.ImageName, IdentitySource.Native, f.File, classified, dict, f.CommandLine, f.PackageFullName, f.AppUserModelId,
            [.. f.Notes, .. classified.Notes]);
    }

    /// <summary>
    /// Stage 1 mock row: identity from the mock name and mock command line through the same rules, with no system call
    /// (no PID lookup, no file, no registry). Everything is at most inferred and marked as mock.
    /// </summary>
    public static ProcessIdentityInfo ComposeMock(int pid, string imageName, string? mockCommandLine, RoleDictionary? dict = null)
    {
        dict ??= RoleDictionary.Default;
        var args = CommandLineParser.Split(mockCommandLine);
        var imagePath = args.Count > 0 ? args[0] : null;
        var classified = SharedHostClassifier.Classify(
            new ClassifyInput(imageName, imagePath, mockCommandLine, null, null, null, false, IsMock: true), NullHostContext.Instance, dict);
        return Build(new ProcessKey(pid, 0), imageName, IdentitySource.Mock, null, classified, dict, mockCommandLine, null, null, []);
    }

    private static ProcessIdentityInfo Build(ProcessKey key, string imageName, IdentitySource source, FileIdentityInfo? file, ClassifyResult c, RoleDictionary dict,
        string? rawCommandLine, string? package, string? aumid, IReadOnlyList<string> notes)
    {
        var mock = source == IdentitySource.Mock;
        var imageRole = dict.Image(imageName);
        var roleEntry = c.GuidRole ?? imageRole;
        var title = c.Title
                    ?? (file?.FileDescription is { Length: > 0 } d ? new IdentityText(d, IdentityConfidence.Confirmed, "버전 정보") : null)
                    ?? (roleEntry is not null ? new IdentityText(roleEntry.ShortName, IdentityConfidence.Inferred, mock ? "역할 사전(모의)" : "역할 사전") : null)
                    ?? (file?.ProductName is { Length: > 0 } p ? new IdentityText(p, IdentityConfidence.Confirmed, "버전 정보") : null)
                    ?? new IdentityText(imageName, mock ? IdentityConfidence.Inferred : IdentityConfidence.Confirmed, mock ? "실행 파일 이름(모의)" : "실행 파일 이름");
        var role = c.Role ?? (roleEntry is null ? null : new IdentityText(roleEntry.Role, IdentityConfidence.Inferred, mock ? "역할 사전(모의)" : "역할 사전"));
        return new ProcessIdentityInfo
        {
            Key = key,
            ImageName = imageName,
            Source = source,
            Title = title,
            Role = role,
            Executor = c.Executor,
            ProductName = file?.ProductName,
            CompanyName = file?.CompanyName,
            FileDescription = file?.FileDescription,
            PackageFullName = package,
            AppUserModelId = aumid,
            IsSharedHost = c.IsSharedHost || imageRole?.Shared == true,
            Hosted = c.Hosted,
            RedactedCommandLine = rawCommandLine is null ? null : Redactor.Redact(rawCommandLine),
            Icon = file?.Icon,
            Notes = notes,
        };
    }
}
