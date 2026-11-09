namespace WinDiag.Presentation;

/// <summary>Runs work on the UI thread. Implemented by the WPF dispatcher; tests run inline. (Adapted from project A.)</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>File pickers and message boxes. Every destructive or privacy-relevant action goes through <see cref="Confirm"/>.</summary>
public interface IDialogService
{
    string? SaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null);
    string? OpenFile(string title, string filter, string? initialDirectory = null);
    string? PickFolder(string title, string? initialDirectory = null);
    bool Confirm(string title, string message);
    void Info(string title, string message);
    void Error(string title, string message);

    /// <summary>Asks for a short text. Returns null when cancelled.</summary>
    string? Prompt(string title, string message, string defaultValue);
}

public interface IShellService
{
    void OpenFolder(string path);
    void OpenFile(string path);
}

/// <summary>Operations that need the application itself (restart after a restore).</summary>
public interface IAppHost
{
    /// <summary>Stops collection, replaces the database with the backup (the current one is kept aside) and restarts the app.</summary>
    Task RestoreAndRestartAsync(string backupPath);
}
