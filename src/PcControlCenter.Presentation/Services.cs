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
    void OpenFolder(string path);
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

    public string? PickFolder(string title, string? initialDirectory = null) => FolderToPick;

    public void OpenFolder(string path) => Messages.Add($"open: {path}");
}

public enum Level
{
    Muted,
    Good,
    Warning,
    Error,
}
