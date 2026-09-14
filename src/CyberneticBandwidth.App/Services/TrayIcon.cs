using System.ComponentModel;
using System.Windows;
using CyberneticBandwidth.App.Views;
using CyberneticBandwidth.Core.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace CyberneticBandwidth.App.Services;

/// <summary>
/// The notification-area icon. Cybernetic Bandwidth Monitor is a collector first and a window second, so closing the
/// window hides it here and collection carries on; only Exit on the tray menu stops the app.
///
/// WinForms' NotifyIcon rather than a package: WPF has no equivalent of its own, and this one is
/// part of the Windows Desktop runtime, maintained with it, at about 11 MB of self-contained size.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // NOTIFYICONDATA.szTip is 128 UTF-16 units including the terminator; NotifyIcon throws past 127.
    private const int MaxTooltipLength = 127;

    private readonly ShellWindow _shell;
    private readonly LiveActivityBroker _broker;
    private readonly NotificationCenter _notifications;
    private readonly Forms.NotifyIcon _icon;
    private bool _exiting;
    private bool _toldAboutHiding;
    private string _tooltip = "Cybernetic Bandwidth Monitor";

    public TrayIcon(ShellWindow shell, LiveActivityBroker broker, NotificationCenter notifications)
    {
        _shell = shell;
        _broker = broker;
        _notifications = notifications;

        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Open Cybernetic Bandwidth Monitor", null, (_, _) => ShowShell());
        open.Font = new Drawing.Font(open.Font, Drawing.FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit Cybernetic Bandwidth Monitor", null, (_, _) => Exit()));

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = _tooltip,
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowShell();
        };
        _icon.BalloonTipClicked += (_, _) => ShowShell();

        _shell.Closing += OnShellClosing;
        Application.Current.SessionEnding += (_, _) => _exiting = true;
        _broker.ThroughputUpdated += OnThroughput;
        _notifications.Raised += OnNotification;
    }

    public void Show() => _icon.Visible = true;

    /// <summary>Brings the window back from the tray, from minimised, or from behind others.</summary>
    public void ShowShell()
    {
        if (!_shell.IsVisible) _shell.Show();
        if (_shell.WindowState == WindowState.Minimized) _shell.WindowState = WindowState.Normal;

        _shell.Activate();

        // Activate alone leaves the window behind the foreground app when the request came from
        // another process (a second launch) rather than a click on our own icon.
        _shell.Topmost = true;
        _shell.Topmost = false;
        _shell.Focus();
    }

    public void Exit()
    {
        _exiting = true;
        Application.Current.Shutdown();
    }

    private void OnShellClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;

        e.Cancel = true;
        _shell.Hide();

        if (_toldAboutHiding) return;
        _toldAboutHiding = true;
        _icon.ShowBalloonTip(
            4000,
            "Cybernetic Bandwidth Monitor is still running",
            "It keeps recording in the background. Right-click the tray icon and choose Exit to stop it.",
            Forms.ToolTipIcon.None);
    }

    // Raised from whichever thread noticed; the icon belongs to the UI thread.
    private void OnNotification(string title, string message) =>
        Application.Current.Dispatcher.BeginInvoke(() => _icon.ShowBalloonTip(8000, title, message, Forms.ToolTipIcon.Info));

    private void OnThroughput(double down, double up)
    {
        string text = $"Cybernetic Bandwidth Monitor\n↓ {ByteFormat.Rate(down)}   ↑ {ByteFormat.Rate(up)}";
        if (text.Length > MaxTooltipLength) text = text[..MaxTooltipLength];

        // Every modify is a shell round-trip; skip the ones that would change nothing.
        if (text == _tooltip) return;
        _tooltip = text;
        _icon.Text = text;
    }

    private static Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/CyberneticBandwidth.ico"));
        using var stream = resource.Stream;

        // Picks the entry closest to the tray's size at the current DPI (16, 20, 24 or 32 px)
        // instead of scaling one bitmap.
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _broker.ThroughputUpdated -= OnThroughput;
        _notifications.Raised -= OnNotification;
        _shell.Closing -= OnShellClosing;

        // Without hiding first, the icon lingers in the tray until the mouse passes over it.
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
