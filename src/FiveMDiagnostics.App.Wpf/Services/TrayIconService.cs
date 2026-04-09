using System.Drawing;
using Forms = System.Windows.Forms;

namespace FiveMDiagnostics.App.Wpf.Services;

using FiveMDiagnostics.App.Wpf.Properties;


public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _showMenuItem;
    private readonly Forms.ToolStripMenuItem _startSessionMenuItem;
    private readonly Forms.ToolStripMenuItem _stopSessionMenuItem;
    private readonly Forms.ToolStripMenuItem _markStutterMenuItem;
    private readonly Forms.ToolStripMenuItem _markSevereMenuItem;
    private readonly Forms.ToolStripMenuItem _exportLatestMenuItem;
    private readonly Forms.ToolStripMenuItem _exitMenuItem;

    public TrayIconService()
    {
        _menu = new Forms.ContextMenuStrip();
        _showMenuItem = CreateMenuItem(Strings.TrayShowWindow, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty));
        _startSessionMenuItem = CreateMenuItem(Strings.StartSession, (_, _) => StartSessionRequested?.Invoke(this, EventArgs.Empty));
        _stopSessionMenuItem = CreateMenuItem(Strings.StopSession, (_, _) => StopSessionRequested?.Invoke(this, EventArgs.Empty));
        _markStutterMenuItem = CreateMenuItem(Strings.MarkStutter, (_, _) => MarkStutterRequested?.Invoke(this, EventArgs.Empty));
        _markSevereMenuItem = CreateMenuItem(Strings.MarkSevere, (_, _) => MarkSevereRequested?.Invoke(this, EventArgs.Empty));
        _exportLatestMenuItem = CreateMenuItem(Strings.ExportLatest, (_, _) => ExportLatestRequested?.Invoke(this, EventArgs.Empty));
        _exitMenuItem = CreateMenuItem(Strings.TrayExit, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _menu.Items.AddRange(
        [
            _showMenuItem,
            new Forms.ToolStripSeparator(),
            _startSessionMenuItem,
            _stopSessionMenuItem,
            _markStutterMenuItem,
            _markSevereMenuItem,
            _exportLatestMenuItem,
            new Forms.ToolStripSeparator(),
            _exitMenuItem,
        ]);

        UpdateDiagnosticsActions(canStartSession: true, canStopSession: false, canMarkStutter: false, canMarkSevere: false, canExportLatest: false);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadEmbeddedIcon(),
            Text = Strings.AppTitle,
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? StartSessionRequested;

    public event EventHandler? StopSessionRequested;

    public event EventHandler? MarkStutterRequested;

    public event EventHandler? MarkSevereRequested;

    public event EventHandler? ExportLatestRequested;

    public event EventHandler? ExitRequested;

    public void ShowBalloon(string title, string text)
    {
        _notifyIcon.ShowBalloonTip(2500, title, text, Forms.ToolTipIcon.Info);
    }

    public void UpdateDiagnosticsActions(bool canStartSession, bool canStopSession, bool canMarkStutter, bool canMarkSevere, bool canExportLatest)
    {
        _startSessionMenuItem.Enabled = canStartSession;
        _stopSessionMenuItem.Enabled = canStopSession;
        _markStutterMenuItem.Enabled = canMarkStutter;
        _markSevereMenuItem.Enabled = canMarkSevere;
        _exportLatestMenuItem.Enabled = canExportLatest;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }

    private static Forms.ToolStripMenuItem CreateMenuItem(string text, EventHandler onClick)
    {
        return new Forms.ToolStripMenuItem(text, image: null, onClick);
    }

    private static Icon LoadEmbeddedIcon()
    {
        var info = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/AppIcon.ico", UriKind.Absolute));

        return info?.Stream is { } stream ? new Icon(stream) : SystemIcons.Application;
    }
}