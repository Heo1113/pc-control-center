using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PcControlCenter.Presentation;

namespace PcControlCenter.App;

/// <summary>Message boxes and pickers for the shell's own pages. Unattended (smoke test): answers without showing anything.</summary>
internal sealed class ShellDialogs(Func<Window?> owner) : IShellDialogs
{
    public bool? Unattended { get; set; }
    public List<string> Log { get; } = [];

    public bool Confirm(string title, string message)
    {
        if (Unattended is bool answer)
        {
            Log.Add($"confirm({answer}): {title}");
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

    public void OpenFolder(string path)
    {
        Log.Add($"open: {path}");
        if (Unattended is not null || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Error("폴더 열기 실패", ex.Message);
        }
    }

    private MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        => owner() is { IsLoaded: true } window ? MessageBox.Show(window, message, title, buttons, image) : MessageBox.Show(message, title, buttons, image);
}
