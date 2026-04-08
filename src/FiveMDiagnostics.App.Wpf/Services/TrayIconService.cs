using System.Drawing;
using Forms = System.Windows.Forms;

namespace FiveMDiagnostics.App.Wpf.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;

    public TrayIconService()
    {
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Show", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "FiveM Diagnostics",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? ExitRequested;

    public void ShowBalloon(string title, string text)
    {
        _notifyIcon.ShowBalloonTip(2500, title, text, Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}