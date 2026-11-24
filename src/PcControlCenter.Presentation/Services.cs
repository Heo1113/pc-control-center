namespace PcControlCenter.Presentation;

/// <summary>Runs work on the UI thread. WPF: the dispatcher; tests: inline.</summary>
public interface IShellDispatcher
{
    void Post(Action action);
}

public sealed class InlineShellDispatcher : IShellDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Message boxes and pickers of the shell's own pages. Destructive actions always go through <see cref="Confirm"/>.</summary>
public interface IShellDialogs
{
    bool Confirm(string title, string message);
    void Info(string title, string message);
    void Error(string title, string message);
    string? PickFolder(string title, string? initialDirectory = null);

    /// <summary>A file to open; <paramref name="filter"/> in the Windows dialog format ("설명|*.json").</summary>
    string? PickFile(string title, string filter, string? initialDirectory = null);

    void OpenFolder(string path);

    /// <summary>Opens an http(s) address in the default browser (anything else is ignored).</summary>
    void OpenUrl(string url);
}

/// <summary>Dialogs for tests and unattended runs: confirms nothing, records every message.</summary>
public sealed class RecordingShellDialogs(bool confirm = false) : IShellDialogs
{
    public List<string> Messages { get; } = [];
    public string? FolderToPick { get; set; }

    public bool Confirm(string title, string message)
    {
        Messages.Add($"confirm: {title}: {message}");
        return confirm;
    }

    public void Info(string title, string message) => Messages.Add($"info: {title}: {message}");

    public void Error(string title, string message) => Messages.Add($"error: {title}: {message}");

    public string? FileToPick { get; set; }

    public string? PickFolder(string title, string? initialDirectory = null) => FolderToPick;

    public string? PickFile(string title, string filter, string? initialDirectory = null) => FileToPick;

    public void OpenFolder(string path) => Messages.Add($"open: {path}");

    public void OpenUrl(string url) => Messages.Add($"url: {url}");
}

public enum Level
{
    Muted,
    Good,
    Warning,
    Error,
}
