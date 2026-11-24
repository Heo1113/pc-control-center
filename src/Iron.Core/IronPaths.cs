using Suite.Core.Storage;

namespace Iron.Core;

/// <summary>
/// Where IRON keeps its data: <c>&lt;suite data&gt;\iron\</c> with <c>iron-settings.json</c>, <c>iron-telemetry.db</c>,
/// <c>logs\</c> and the single-instance lock. Nothing of IRON is written into PC Control Center's module folders
/// (lapc.db, gpc.db, wdl.db, wao.db) or the original programs' folders; those locations are refused.
/// </summary>
public sealed record IronPaths(string Directory, bool IsPortable, string? Note = null)
{
    public const string ProductId = "iron";
    public const string SettingsFileName = "iron-settings.json";
    public const string DatabaseFileName = "iron-telemetry.db";
    public const string LockFileName = "iron.lock";

    public string SettingsPath => Path.Combine(Directory, SettingsFileName);
    public string DatabasePath => Path.Combine(Directory, DatabaseFileName);
    public string LogDirectory => Path.Combine(Directory, "logs");

    /// <param name="overrideDirectory">--data-dir: the suite data folder (IRON uses its <c>iron</c> subfolder).</param>
    public static IronPaths Resolve(string? overrideDirectory, string executableDirectory, string? localAppData = null)
    {
        var root = ProductDataRoot.Resolve(overrideDirectory, executableDirectory, localAppData);
        var directory = root.ProductDirectory(ProductId, ProductDataRoot.KnownReserved(executableDirectory, localAppData));
        return new IronPaths(directory, root.IsPortable, root.Note);
    }

    public void EnsureDirectories()
    {
        System.IO.Directory.CreateDirectory(Directory);
        System.IO.Directory.CreateDirectory(LogDirectory);
    }
}
