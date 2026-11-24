using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LocalAiPerf.Presentation.Services;
using Microsoft.Win32;

namespace LocalAiPerf.App.Services;

public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
    }
}

public sealed class WpfDialogService(Func<Window?> owner) : IDialogService
{
    /// <summary>When set (smoke test), dialogs never block: confirmations are answered with this value.</summary>
    public bool? Unattended { get; set; }

    public List<string> Log { get; } = [];

    public string? SaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null)
    {
        if (Unattended is not null)
        {
            return null;
        }

        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, OverwritePrompt = true, AddExtension = true };
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? OpenFile(string title, string filter, string? initialDirectory = null)
    {
        if (Unattended is not null)
        {
            return null;
        }

        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string? initialDirectory = null)
    {
        if (Unattended is not null)
        {
            return null;
        }

        var dialog = new OpenFolderDialog { Title = title };
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(owner()) == true ? dialog.FolderName : null;
    }

    public bool Confirm(string title, string message)
    {
        if (Unattended is bool answer)
        {
            Log.Add($"confirm: {title}");
            return answer;
        }

        return Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    public void Info(string title, string message)
    {
        Log.Add($"info: {title}: {message}");
        if (Unattended is null)
        {
            Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public void Error(string title, string message)
    {
        Log.Add($"error: {title}: {message}");
        if (Unattended is null)
        {
            Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public string? Prompt(string title, string message, string defaultValue)
    {
        if (Unattended is not null)
        {
            return defaultValue;
        }

        var window = new PromptWindow(title, message, defaultValue) { Owner = owner() };
        return window.ShowDialog() == true ? window.Value : null;
    }

    private MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        => owner() is { } window
            ? MessageBox.Show(window, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
}

public sealed class ShellService : IShellService
{
    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // Explorer is started with the folder as a single argument; no shell string is built.
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add(path);
        Process.Start(info)?.Dispose();
    }
}
