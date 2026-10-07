namespace myFanControl;

public partial class MainWindow
{
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.NotifyIcon? _gpuTrayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private System.Windows.Threading.DispatcherTimer? _temperatureDisplayTimer;
    private readonly string[] _trayValues = ["", ""];
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
            Icon = CreateTemperatureIcon("--", false),
            Text = "CPU 温度不可用",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _gpuTrayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = CreateTemperatureIcon("--", true), Text = "GPU 温度不可用",
            ContextMenuStrip = _trayMenu, Visible = true
        };
        _trayIcon.DoubleClick += OnTrayIconDoubleClick;
        _gpuTrayIcon.DoubleClick += OnTrayIconDoubleClick;
        StateChanged += OnTrayWindowStateChanged;
        _temperatureDisplayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _temperatureDisplayTimer.Tick += OnTemperatureDisplayTick;
        _temperatureDisplayTimer.Start();
        RefreshLiveTemperatureUi();
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
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    private void DisposeTrayUi()
    {
        if (!_trayInitialized) return;
        _trayInitialized = false;
        StateChanged -= OnTrayWindowStateChanged;
        if(_temperatureDisplayTimer is not null)
        {
            _temperatureDisplayTimer.Stop(); _temperatureDisplayTimer.Tick -= OnTemperatureDisplayTick;
            _temperatureDisplayTimer=null;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.DoubleClick -= OnTrayIconDoubleClick;
            _trayIcon.Visible = false;
            _trayIcon.Icon?.Dispose();
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        if(_gpuTrayIcon is not null)
        {
            _gpuTrayIcon.DoubleClick -= OnTrayIconDoubleClick;
            _gpuTrayIcon.Visible=false; _gpuTrayIcon.Icon?.Dispose(); _gpuTrayIcon.Dispose(); _gpuTrayIcon=null;
        }
        _trayValues[0]=_trayValues[1]="";

        _trayMenu?.Dispose();
        _trayMenu = null;
    }

    private void OnTemperatureDisplayTick(object? sender, EventArgs e) => RefreshLiveTemperatureUi();

    private void UpdateTrayTemperatures(double? cpu, double? gpu)
    {
        System.Windows.Forms.NotifyIcon?[] icons=[_trayIcon,_gpuTrayIcon];
        double?[] values=[cpu,gpu];
        for(int i=0;i<2;i++)
        {
            if(icons[i] is not { } icon) continue;
            double? value=values[i]; string name=i==0?"CPU":"GPU";
            icon.Text=value is { } t ? $"{name} · {t:F1} °C" : $"{name} 温度不可用";
            string text=value is { } temperature ? Math.Round(temperature,MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture) : "--";
            if(_trayValues[i]==text) continue;
            var old=icon.Icon; icon.Icon=CreateTemperatureIcon(text,i==1); old?.Dispose(); _trayValues[i]=text;
        }
    }

    private static System.Drawing.Icon CreateTemperatureIcon(string text, bool gpu)
    {
        // Include the native notification-area sizes for 100%, 150%, and 200% DPI.
        int[] sizes=[16,24,32,48]; var images=new List<byte[]>();
        foreach(int size in sizes)
        {
            using var bitmap=new System.Drawing.Bitmap(size,size);
            using var graphics=System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(gpu ? System.Drawing.Color.FromArgb(245,158,11) : System.Drawing.Color.FromArgb(15,118,110));
            graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font=new System.Drawing.Font("Segoe UI",size*(text.Length>2 ? .43f : .64f),System.Drawing.FontStyle.Bold,System.Drawing.GraphicsUnit.Pixel);
            using var brush=new System.Drawing.SolidBrush(gpu ? System.Drawing.Color.Black : System.Drawing.Color.White);
            using var format=new System.Drawing.StringFormat { Alignment=System.Drawing.StringAlignment.Center, LineAlignment=System.Drawing.StringAlignment.Center };
            graphics.DrawString(text,font,brush,new System.Drawing.RectangleF(-1,-1,size+2,size+2),format);
            using var png=new MemoryStream(); bitmap.Save(png,System.Drawing.Imaging.ImageFormat.Png); images.Add(png.ToArray());
        }
        using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        int offset=6+16*sizes.Length;
        for(int i=0;i<sizes.Length;i++)
        {
            writer.Write((byte)sizes[i]); writer.Write((byte)sizes[i]); writer.Write((ushort)0);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(images[i].Length); writer.Write(offset); offset+=images[i].Length;
        }
        foreach(var image in images) writer.Write(image);
        stream.Position=0;
        using var source=new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)source.Clone();
    }
}
