namespace myFanControl;

public partial class MainWindow
{
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private bool _trayInitialized;
    private bool _trayTipShown;

    private void InitializeTrayUi()
    {
        if (_trayInitialized) return;
        _trayInitialized = true;

        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        var showItem = new System.Windows.Forms.ToolStripMenuItem("显示窗口");
        showItem.Click += (_, _) => ShowFromTray();
        var restoreItem = new System.Windows.Forms.ToolStripMenuItem("恢复固件控制");
        restoreItem.Click += async (_, _) =>
        {
            try { await RestoreFirmwareAsync(); }
            catch (Exception exception) { SetControlStatus("恢复固件控制时发生错误：" + exception.Message); }
        };
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("恢复固件并退出");
        exitItem.Click += async (_, _) =>
        {
            try { await RequestExitAsync(); }
            catch (Exception exception) { SetControlStatus("退出时发生错误：" + exception.Message); }
        };

        _trayMenu.Items.Add(showItem);
        _trayMenu.Items.Add(restoreItem);
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "ThinkBook 温度与双风扇控制",
            ContextMenuStrip = _trayMenu,
            Visible = false
        };
        _trayIcon.DoubleClick += OnTrayIconDoubleClick;
        StateChanged += OnTrayWindowStateChanged;
    }

    private void OnTrayWindowStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => OnTrayWindowStateChanged(sender, e)));
            return;
        }

        if (WindowState == System.Windows.WindowState.Minimized)
            HideToTray();
    }

    private void HideToTray()
    {
        if (_closing || _sessionEnding || _closeAfterMonitor) return;
        InitializeTrayUi();
        if (_trayIcon is not null) _trayIcon.Visible = true;
        Hide();
        if (!_trayTipShown && _trayIcon is not null)
        {
            _trayTipShown = true;
            _trayIcon.ShowBalloonTip(6000, "仍在后台运行",
                "关闭窗口或最小化会保留编辑内容并继续监控。双击此图标显示窗口；右键选择“恢复固件并退出”结束程序。",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void OnTrayIconDoubleClick(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) ShowFromTray();
        else _ = Dispatcher.BeginInvoke(new Action(ShowFromTray));
    }

    internal void ShowFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ShowFromTray));
            return;
        }

        if (_sessionEnding || _closeAfterMonitor) return;
        if (_trayIcon is not null) _trayIcon.Visible = false;
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    private void DisposeTrayUi()
    {
        if (!_trayInitialized) return;
        _trayInitialized = false;
        StateChanged -= OnTrayWindowStateChanged;

        if (_trayIcon is not null)
        {
            _trayIcon.DoubleClick -= OnTrayIconDoubleClick;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
    }
}
