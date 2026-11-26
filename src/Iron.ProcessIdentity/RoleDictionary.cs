using System.Text.Json;

namespace Iron.ProcessIdentity;

public sealed record RoleEntry(string Role, bool Shared, bool Launcher, bool ShellExtensionSurrogate)
{
    /// <summary>The part before " — " ("Windows 서비스 호스트"), used as a name when nothing better is known.</summary>
    public string ShortName => Role.Split(" — ")[0];
}

/// <summary>The offline Korean role dictionary (embedded resource). Everything it says is shown as inferred.</summary>
public sealed class RoleDictionary
{
    private readonly Dictionary<string, RoleEntry> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, RoleEntry> _clsid = [];
    private readonly Dictionary<Guid, RoleEntry> _appid = [];
    private readonly Dictionary<string, string> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _webviewTypes = new(StringComparer.OrdinalIgnoreCase);

    public static RoleDictionary Default { get; } = LoadEmbedded();

    public int ImageCount => _images.Count;

    public static RoleDictionary LoadEmbedded()
    {
        using var s = typeof(RoleDictionary).Assembly.GetManifestResourceStream("Iron.ProcessIdentity.ProcessRoleDictionary.ko.json")
                      ?? throw new InvalidOperationException("역할 사전 리소스가 없습니다.");
        return Parse(s);
    }

    public static RoleDictionary Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var d = new RoleDictionary();
        var root = doc.RootElement;
        static RoleEntry Entry(JsonElement e) => new(
            e.GetProperty("role").GetString()!,
            e.TryGetProperty("shared", out var s) && s.GetBoolean(),
            e.TryGetProperty("launcher", out var l) && l.GetBoolean(),
            e.TryGetProperty("shellExtensionSurrogate", out var x) && x.GetBoolean());
        foreach (var p in root.GetProperty("images").EnumerateObject())
        {
            d._images[p.Name] = Entry(p.Value);
        }

        foreach (var p in root.GetProperty("clsid").EnumerateObject())
        {
            d._clsid[Guid.Parse(p.Name)] = Entry(p.Value);
        }

        foreach (var p in root.GetProperty("appid").EnumerateObject())
        {
            d._appid[Guid.Parse(p.Name)] = Entry(p.Value);
        }

        foreach (var p in root.GetProperty("svchostGroups").EnumerateObject())
        {
            d._groups[p.Name] = p.Value.GetString()!;
        }

        foreach (var p in root.GetProperty("webviewTypes").EnumerateObject())
        {
            d._webviewTypes[p.Name] = p.Value.GetString()!;
        }

        return d;
    }

    public RoleEntry? Image(string imageName) => _images.GetValueOrDefault(imageName);
    public RoleEntry? Clsid(Guid id) => _clsid.GetValueOrDefault(id);
    public RoleEntry? AppId(Guid id) => _appid.GetValueOrDefault(id);
    public string? SvchostGroup(string group) => _groups.GetValueOrDefault(group);
    public string? WebViewType(string type) => _webviewTypes.GetValueOrDefault(type);
}
