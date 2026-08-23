using Microsoft.UI.Dispatching;
using System.Drawing;
using System.Windows.Forms;

namespace IDVBuff.Lifecycle;

public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public TrayIconController(DispatcherQueue dispatcher, Action showWindow, Action exitApplication)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => dispatcher.TryEnqueue(() => showWindow()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 IDVB", null, (_, _) => dispatcher.TryEnqueue(() => exitApplication()));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "IDVB_icon_multisize.ico");
        _notifyIcon = new NotifyIcon
        {
            Text = "Identity Vision Bridge",
            Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => dispatcher.TryEnqueue(() => showWindow());
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }
}
