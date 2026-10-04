using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ThinkBookControl.Monitoring;

namespace ThinkBookControl;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _monitorCancellation = new();
    private Task? _monitorTask;
    private bool _closeAfterMonitor;
    private bool _closing;
    private IReadOnlyList<string> _identityErrors = Array.Empty<string>();

    public MainWindow(string? settingsPath = null)
    {
        _controlSettingsPath = settingsPath ?? Path.Combine(AppContext.BaseDirectory, "control-settings.json");
        InitializeComponent();
        SourceInitialized += OnPowerSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeControlUi();
        InitializeTrayUi();
        if (_monitorTask is null)
            _monitorTask = RunMonitorLoopAsync(_monitorCancellation.Token);
    }

    private async Task RunMonitorLoopAsync(CancellationToken cancellationToken)
    {
        SensorMonitor? monitor = null;
        try
        {
            MachineIdentity identity = await Task.Run(MachineIdentityReader.Read, cancellationToken)
                .ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => ShowIdentity(identity), DispatcherPriority.Background);
            var thermal = await Task.Run(LenovoThermalReader.Read, cancellationToken).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => ShowThermalSnapshot(thermal), DispatcherPriority.Background);

            var vpc = await Task.Run(LenovoVpcReader.Read, cancellationToken).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => ShowVpcSnapshot(vpc), DispatcherPriority.Background);

            monitor = new SensorMonitor(identity);
            await Task.Run(monitor.Open, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TemperatureSnapshot snapshot = await Task.Run(monitor.Capture, cancellationToken)
                    .ConfigureAwait(false);
                await ProcessControlSnapshotAsync(snapshot).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() => ShowSnapshot(snapshot), DispatcherPriority.Background);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing cancels sampling; disposal below runs before the window closes.
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                StatusText.Text = "温度监控初始化或采样失败。";
                ErrorsText.Text = string.Join(Environment.NewLine,
                    _identityErrors.Append($"{exception.GetType().Name}: {exception.Message}"));
                TemperatureText.Text = "CPU 温度：读取失败\nGPU 温度：读取失败";
                CpuTemperatureText.Text = GpuTemperatureText.Text = "不可用";
                Fan1ActualText.Text = Fan2ActualText.Text = "不可用";
            }, DispatcherPriority.Background);
        }
        finally
        {
            try { await StopControlAsync().ConfigureAwait(false); }
            catch (Exception exception) {
                await Dispatcher.InvokeAsync(() => ErrorsText.Text = "控制恢复失败：" + exception.Message);
            }
            if (monitor is not null)
            {
                try
                {
                    await Task.Run(monitor.Dispose).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusText.Text = "传感器监控已停止，但清理时发生错误。";
                        ErrorsText.Text = $"{exception.GetType().Name}: {exception.Message}";
                    }, DispatcherPriority.Background);
                }
            }
        }
    }

    private void ShowVpcSnapshot(VpcFanSnapshot vpc)
    {
        VpcText.Text = "双路 RPM 控制 · 正式签名驱动后端";
    }

    private void ShowThermalSnapshot(LenovoThermalSnapshot thermal)
    {
        ThermalText.Text = thermal.Available
            ? $"固件散热模式：{thermal.ModeName}（启动时查询）"
            : string.Join(Environment.NewLine, thermal.Errors);
    }
    private void ShowIdentity(MachineIdentity identity)
    {
        _identityErrors = identity.Errors;
        ModelText.Text = JoinAvailable(identity.Manufacturer, identity.Model);
        BiosText.Text = JoinAvailable(identity.BiosVendor, identity.BiosVersion);
        BiosDateText.Text = identity.BiosReleaseDate ?? "未读取到";

        ErrorsText.Text = string.Join(Environment.NewLine, _identityErrors);
    }

    private void ShowSnapshot(TemperatureSnapshot snapshot)
    {
        bool fresh = snapshot.CapturedAtUtc <= DateTimeOffset.UtcNow &&
            DateTimeOffset.UtcNow - snapshot.CapturedAtUtc <= CpuMaximumAge;
        CpuTemperatureText.Text = TryGetFreshCpuTemperature(snapshot, DateTimeOffset.UtcNow, out double cpu)
            ? $"{cpu:F1} °C" : "不可用";
        float[] gpu = snapshot.Temperatures.Where(reading => reading.Category == "GPU")
            .Select(reading => reading.ValueCelsius)
            .Where(value => value.HasValue && float.IsFinite(value.Value) && value.Value is >= -100 and <= 150)
            .Select(value => value!.Value).ToArray();
        GpuTemperatureText.Text = fresh && gpu.Length > 0 ? $"{gpu.Max():F1} °C" : "不可用";
        Fan1ActualText.Text = fresh && snapshot.EcFanTelemetry is { Available: true, Fan1Rpm: { } rpm1 }
            ? $"{rpm1} RPM" : "不可用";
        Fan2ActualText.Text = fresh && snapshot.EcFanTelemetry is { Available: true, Fan2Rpm: { } rpm2 }
            ? $"{rpm2} RPM" : "不可用";
        var lines = new List<string>();
        if (snapshot.Fans.Count == 0) lines.Add("风扇 RPM：硬件监控未提供有效转速");
        if (snapshot.EcFanTelemetry is { Available: true })
            lines.Add("RPM 来源：EC 测速反馈；风扇 1/2 为两路独立通道");
        else if (snapshot.EcFanTelemetry is { } ecTelemetry)
            lines.Add($"EC 报告 RPM：不可用（{string.Join("；", ecTelemetry.Errors)}）");
        foreach (var fan in snapshot.Fans)
            lines.Add($"风扇 · {fan.HardwareName} · {fan.SensorName}：" +
                (fan.ValueRpm is float rpm ? $"{rpm:F0} RPM" : "当前无有效读数"));
        foreach (string category in new[] { "CPU", "GPU" })
        {
            TemperatureReading[] categoryReadings = snapshot.Temperatures
                .Where(item => item.Category == category)
                .ToArray();

            if (categoryReadings.Length == 0)
            {
                lines.Add($"{category}：未提供温度传感器");
                continue;
            }

            foreach (TemperatureReading reading in categoryReadings)
            {
                string valueText = reading.ValueCelsius is float value
                    ? $"{value.ToString("F1", CultureInfo.CurrentCulture)} °C"
                    : "当前无有效读数";
                string readingType = reading.SensorName.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase)
                    ? "（距离温度上限）" : "";
                lines.Add($"{category} · {reading.HardwareName} · {reading.SensorName}{readingType}：{valueText}");
            }
        }

        TemperatureText.Text = string.Join(Environment.NewLine, lines);
        StatusText.Text = $"{(fresh ? "传感器更新" : "温度样本已过期或时间无效")} {snapshot.CapturedAtUtc.ToLocalTime():HH:mm:ss} · 控制状态见上方";
        string[] allErrors = _identityErrors
            .Concat(snapshot.Errors)
            .Concat(snapshot.EcFanTelemetry?.Errors ?? Array.Empty<string>())
            .ToArray();
        ErrorsText.Text = allErrors.Length > 0
            ? string.Join(Environment.NewLine, allErrors)
            : string.Empty;
    }

    private static string JoinAvailable(params string?[] values)
    {
        string[] available = values.OfType<string>()
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return available.Length == 0 ? "未读取到" : string.Join(" / ", available);
    }

    protected override void OnClosed(EventArgs e)
    {
        BeginSessionEnding();
        DisposePowerNotifications();
        _monitorCancellation.Dispose();
        base.OnClosed(e);
    }
}
