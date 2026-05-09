using CleanDesk.App.UI;
using Forms = System.Windows.Forms;

namespace CleanDesk.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly CleanDeskController _controller;
    private readonly Forms.NotifyIcon _notifyIcon;

    public TrayService(CleanDeskController controller)
    {
        _controller = controller;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "CleanDesk - 桌面整理工具",
            Icon = AppIconService.CreateNotifyIcon(),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notifyIcon.DoubleClick += (_, _) => _controller.ToggleAllBoxes();
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏所有盒子", null, (_, _) => _controller.ToggleAllBoxes());
        menu.Items.Add("自动整理桌面", null, (_, _) => _controller.OrganizeDesktop("Tray"));
        menu.Items.Add("一键恢复桌面", null, (_, _) => _controller.RestoreDesktop());
        menu.Items.Add("设置中心", null, (_, _) => _controller.ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        var autoStart = new Forms.ToolStripMenuItem("开机自启动") { Checked = _controller.Settings.AutoStart, CheckOnClick = true };
        autoStart.CheckedChanged += (_, _) =>
        {
            _controller.Settings.AutoStart = autoStart.Checked;
            AutoStartService.SetEnabled(autoStart.Checked);
            _controller.SaveSettings();
        };
        menu.Items.Add(autoStart);
        menu.Items.Add("退出应用", null, (_, _) => _controller.RequestExit());
        return menu;
    }

    public void ShowBalloon(string title, string text)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(2400);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
