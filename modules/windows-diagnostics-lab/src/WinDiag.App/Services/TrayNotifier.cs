using WinDiag.Collection.Notifications;
using WinDiag.Core.Events;
using Forms = System.Windows.Forms;

namespace WinDiag.App.Services;

/// <summary>
/// Notification-area icon with balloon notifications for important new events. Uses the standard NotifyIcon, which
/// needs no registration (no registry changes, no Start menu shortcut).
/// </summary>
public sealed class TrayNotifier : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayNotifier(Action open)
    {
        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Text = "Windows Diagnostics Lab",
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => open();
    }

    public void Show(DiagNotification notification)
    {
        var icon = notification.Severity >= DiagSeverity.Error ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Warning;
        var text = notification.Message.Length > 200 ? notification.Message[..200] + "…" : notification.Message;
        _icon.ShowBalloonTip(8000, notification.Title, text, icon);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
