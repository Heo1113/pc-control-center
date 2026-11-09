using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using GpuProfit.Presentation;
using Microsoft.Win32;

namespace GpuProfit.App.Services;

/// <summary>Adapted from project C (WinDiag.App/Services/WpfServices.cs, commit 06cc9e5), which follows project A.</summary>
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

    /// <summary>Smoke test: file dialogs answer with a path in this folder instead of opening.</summary>
    public string? UnattendedFolder { get; set; }

    /// <summary>Smoke test: the file an open dialog returns.</summary>
    public string? UnattendedOpen { get; set; }

    public List<string> Log { get; } = [];

    public string? SaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null)
    {
        if (Unattended is not null)
        {
            Log.Add($"save: {title}");
            return UnattendedFolder is null ? null : Path.Combine(UnattendedFolder, defaultFileName);
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
            Log.Add($"open: {title}");
            var answer = UnattendedOpen;
            UnattendedOpen = null;
            return answer;
        }

        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
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
