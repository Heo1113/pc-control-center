namespace GpuProfit.Presentation;

/// <summary>Runs work on the UI thread. Implemented by the WPF dispatcher; tests run inline. (Copied from project C, commit 06cc9e5.)</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>File pickers and message boxes. Every destructive action goes through <see cref="Confirm"/>.</summary>
public interface IDialogService
{
    string? SaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null);
    string? OpenFile(string title, string filter, string? initialDirectory = null);
    bool Confirm(string title, string message);
    void Info(string title, string message);
    void Error(string title, string message);
}

public interface IShellService
{
    void OpenFolder(string path);
}

/// <summary>Operations that need the application itself.</summary>
public interface IAppHost
{
    /// <summary>Stops recording, replaces the database with the backup (the current one is kept aside) and restarts the app.</summary>
    Task RestoreAndRestartAsync(string backupPath);

    /// <summary>Applies the window-wide font scale.</summary>
    void ApplyFontScale(double scale);
}

/// <summary>A choice in a drop-down list.</summary>
public sealed record Option<T>(string Label, T Value)
{
    public override string ToString() => Label;
}
