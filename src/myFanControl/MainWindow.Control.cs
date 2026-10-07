using System.Globalization;
using System.Text.Json;
using FanControl.Core;
using Microsoft.Win32;
using System.Windows;
using myFanControl.Monitoring;
using myFanControl.Editing;

namespace myFanControl;

public partial class MainWindow
{
    private enum ControlMode { Firmware, Manual, Curve }

    private static readonly TimeSpan CpuMaximumAge = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly string _controlSettingsPath;
    private readonly object _settingsFileGate = new();
    private List<CurvePreset> _curvePresets = [];
    private bool _firmwareRestoreConfirmed = true;
    private bool _uiControlBusy;
    private string? _activeFan1Curve;
    private string? _activeFan2Curve;
    private ControlClient? _controlClient;
    private ControlMode _controlMode;
    private FanControlPolicy? _fan1Policy;
    private FanControlPolicy? _fan2Policy;
    private AppliedControl? _fan1Applied;
    private AppliedControl? _fan2Applied;
    private TemperatureSnapshot? _latestControlSnapshot;
    private bool _controlUiInitialized;
    private bool _controlStopped;
    private int _settingsFan1Rpm = 3600;
    private int _settingsFan2Rpm = 3600;
    private FanResponseSettings[] _settingsResponses = [new(), new()];
    private FanResponseSettings[] _activeResponses = [new(), new()];
    private string _settingsFan1Curve = DefaultCurve;
    private string _settingsFan2Curve = DefaultCurve;

    private const string DefaultCurve = "40:2500;60:3500;75:5000;90:7500";

    internal void InitializeControlUi()
    {
        if (_controlUiInitialized) return;
        _controlUiInitialized = true;
        LoadControlSettings();
        ApplyThemeColor();
        InitializeEditorUi();
        SuspendResumeObserved += OnPowerModeChanged;
        SetControlStatus(string.Empty);
    }

    internal async Task ApplyManualAsync(int fan1, int fan2)
    {
        ValidateRpm(fan1, nameof(fan1));
        ValidateRpm(fan2, nameof(fan2));
        await _controlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfControlStopped();
            TemperatureSnapshot? snapshot = _latestControlSnapshot;
            if (!TryGetFreshCpuTemperature(snapshot, DateTimeOffset.UtcNow, out double temperature))
            {
                await SetControlStatusAsync("手动控制未启用：需要 5 秒内有效的 CPU 温度。固件仍在控制风扇。").ConfigureAwait(false);
                throw new InvalidOperationException("手动控制需要一条不超过 5 秒的有效 CPU 温度读数。");
            }

            if (temperature >= 90)
                fan1 = fan2 = 7500;

            _controlClient ??= new ControlClient();
            await _controlClient.SetAsync(fan1, fan2).ConfigureAwait(false);
            DateTimeOffset appliedAt = DateTimeOffset.UtcNow;
            _fan1Applied = new AppliedControl(fan1, temperature, appliedAt);
            _fan2Applied = new AppliedControl(fan2, temperature, appliedAt);
            _fan1Policy = _fan2Policy = null;
            _controlMode = ControlMode.Manual;
            _activeFan1Curve = _activeFan2Curve = null;
            _firmwareRestoreConfirmed = true;
            _settingsFan1Rpm = fan1;
            _settingsFan2Rpm = fan2;
            await SaveAppliedSettingsAsync().ConfigureAwait(false);
            await SetControlStatusAsync($"手动控制中 · 风扇 1：{fan1} RPM · 风扇 2：{fan2} RPM").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!_controlStopped)
            {
                Exception? cleanup = await RestoreAndDisposeClientLockedAsync().ConfigureAwait(false);
                ResetControlState(cleanup is null);
                string status = cleanup is null ? "手动控制未启用，已切回固件控制：" + exception.Message
                    : "手动控制失败，固件恢复未确认：" + exception.Message;
                if (cleanup is not null) status += "；清理错误：" + cleanup.Message;
                await SetControlStatusAsync(status).ConfigureAwait(false);
            }
            throw;
        }
        finally { _controlGate.Release(); }
    }

    internal async Task ApplyCurvesAsync(string fan1Curve, string fan2Curve, FanResponseSettings? fan1Response = null, FanResponseSettings? fan2Response = null)
    {
        FanResponseSettings[] responses = [fan1Response ?? _settingsResponses[0], fan2Response ?? _settingsResponses[1]];
        if(responses.Any(r => !r.IsValid)) throw new ArgumentException("响应参数需为 0～10 的整数。");
        FanCurve curve1 = ParseCurve(fan1Curve), curve2 = ParseCurve(fan2Curve);
        FanControlPolicy policy1 = CreatePolicy(curve1, responses[0]);
        FanControlPolicy policy2 = CreatePolicy(curve2, responses[1]);
        string canonical1 = FormatCurve(curve1), canonical2 = FormatCurve(curve2);
        await _controlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfControlStopped();
            _controlClient ??= new ControlClient();
            _fan1Policy = policy1;
            _fan2Policy = policy2;
            _fan1Applied = _fan2Applied = null;
            _controlMode = ControlMode.Curve;
            _activeFan1Curve = canonical1;
            _activeFan2Curve = canonical2;
            _activeResponses = responses;
            var oldResponses = _settingsResponses;
            string oldCurve1 = _settingsFan1Curve, oldCurve2 = _settingsFan2Curve;
            _settingsFan1Curve = canonical1;
            _settingsFan2Curve = canonical2;
            _settingsResponses = responses;
            if(!await SaveAppliedCurveSettingsAsync().ConfigureAwait(false))
            {
                _settingsResponses = oldResponses;
                _settingsFan1Curve = oldCurve1; _settingsFan2Curve = oldCurve2;
            }
            await SetControlStatusAsync("自动曲线已启用；等待有效 CPU 温度样本。").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_controlMode == ControlMode.Firmware && !_controlStopped)
            {
                Exception? cleanup = await RestoreAndDisposeClientLockedAsync().ConfigureAwait(false);
                ResetControlState(cleanup is null);
                string status = "自动曲线未启用：" + exception.Message;
                if (cleanup is not null) status += "；清理错误：" + cleanup.Message;
                await SetControlStatusAsync(status).ConfigureAwait(false);
            }
            throw;
        }
        finally { _controlGate.Release(); }
    }

    internal async Task RestoreFirmwareAsync()
    {
        await _controlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_controlStopped) return;
            Exception? failure = await RestoreAndDisposeClientLockedAsync().ConfigureAwait(false);
            ResetControlState(failure is null);
            await SetControlStatusAsync(failure is null
                ? "固件散热控制已恢复。"
                : "已请求恢复固件控制；后端清理报告错误：" + failure.Message).ConfigureAwait(false);
            if (failure is not null) throw failure;
        }
        finally { _controlGate.Release(); }
    }

    internal async Task ProcessControlSnapshotAsync(TemperatureSnapshot snapshot)
    {
        await _controlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _latestControlSnapshot = snapshot;
            if (_controlStopped || _controlMode == ControlMode.Firmware) return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!TryGetFreshCpuTemperature(snapshot, now, out double temperature))
            {
                await RestoreForSnapshotAsync("CPU 温度缺失或已过期，已恢复固件控制。", null).ConfigureAwait(false);
                return;
            }

            if (_controlMode == ControlMode.Manual)
            {
                if (temperature >= 90 && (_fan1Applied?.Rpm != 7500 || _fan2Applied?.Rpm != 7500))
                {
                    try
                    {
                        _controlClient ??= new ControlClient();
                        await _controlClient.SetAsync(7500, 7500).ConfigureAwait(false);
                        DateTimeOffset appliedAt = DateTimeOffset.UtcNow;
                        _fan1Applied = new AppliedControl(7500, temperature, appliedAt);
                        _fan2Applied = new AppliedControl(7500, temperature, appliedAt);
                        _settingsFan1Rpm = _settingsFan2Rpm = 7500;
                        await SaveAppliedSettingsAsync().ConfigureAwait(false);
                        await SetControlStatusAsync("CPU 达到 90 °C · 两把风扇已强制设为 7500 RPM。").ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        await HandleSnapshotBackendFailureAsync(exception).ConfigureAwait(false);
                        return;
                    }
                }
                await SendHeartbeatAsync().ConfigureAwait(false);
                return;
            }

            try
            {
                var frame = new TemperatureFrame(snapshot.CapturedAtUtc,
                    new Dictionary<string, double?> { ["cpu"] = temperature });
                ControlDecision decision1 = _fan1Policy!.Evaluate(frame, now, _fan1Applied);
                ControlDecision decision2 = _fan2Policy!.Evaluate(frame, now, _fan2Applied);
                if (decision1.Action == ControlAction.RestoreFirmwareAutomatic ||
                    decision2.Action == ControlAction.RestoreFirmwareAutomatic)
                {
                    await RestoreForSnapshotAsync("温度样本不适用于自动曲线，已恢复固件控制。", null).ConfigureAwait(false);
                    return;
                }

                int requested1 = decision1.Action == ControlAction.RequestRpm
                    ? QuantizeRpm(decision1.RequestedRpm!.Value) : _fan1Applied?.Rpm ??
                        throw new InvalidOperationException("风扇 1 缺少自动控制历史。");
                int requested2 = decision2.Action == ControlAction.RequestRpm
                    ? QuantizeRpm(decision2.RequestedRpm!.Value) : _fan2Applied?.Rpm ??
                        throw new InvalidOperationException("风扇 2 缺少自动控制历史。");
                bool write1 = decision1.Action == ControlAction.RequestRpm && requested1 != _fan1Applied?.Rpm;
                bool write2 = decision2.Action == ControlAction.RequestRpm && requested2 != _fan2Applied?.Rpm;
                int rpm1 = write1 ? requested1 : _fan1Applied!.Rpm;
                int rpm2 = write2 ? requested2 : _fan2Applied!.Rpm;
                if (write1 || write2)
                {
                    _controlClient ??= new ControlClient();
                    await _controlClient.SetAsync(rpm1, rpm2).ConfigureAwait(false);
                    DateTimeOffset appliedAt = DateTimeOffset.UtcNow;
                    if(write1) _fan1Applied = new AppliedControl(rpm1, temperature, appliedAt);
                    if(write2) _fan2Applied = new AppliedControl(rpm2, temperature, appliedAt);
                    _firmwareRestoreConfirmed = true;
                    await SetControlStatusAsync($"{(temperature >= 90 ? "高温保护已生效" : "自动曲线控制中")} · CPU {temperature:F1} °C · 风扇 1：{rpm1} RPM · 风扇 2：{rpm2} RPM").ConfigureAwait(false);
                }
                else
                    await SetControlStatusAsync($"自动曲线控制中 · 风扇 1：{ResponseReason(decision1.Reason)} · 风扇 2：{ResponseReason(decision2.Reason)} · CPU {temperature:F1} °C").ConfigureAwait(false);
                await SendHeartbeatAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await HandleSnapshotBackendFailureAsync(exception).ConfigureAwait(false);
            }
        }
        finally { _controlGate.Release(); }
    }

    internal async Task StopControlAsync()
    {
        if (!_controlUiInitialized && _controlStopped) return;
        if (_controlUiInitialized)
            SuspendResumeObserved -= OnPowerModeChanged;
        await _controlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_controlStopped) return;
            _controlStopped = true;
            Exception? failure = await RestoreAndDisposeClientLockedAsync().ConfigureAwait(false);
            ResetControlState(failure is null);
            await SetControlStatusAsync(failure is null
                ? "窗口关闭时已恢复固件控制。"
                : "窗口关闭时已请求恢复固件控制；后端清理报告错误：" + failure.Message).ConfigureAwait(false);
            if (failure is not null) throw failure;
        }
        finally { _controlGate.Release(); }
    }

    private async void OnApplyManualClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(Fan1RpmBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fan1) ||
            !int.TryParse(Fan2RpmBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fan2))
        {
            SetControlStatus("请输入两把风扇的有效整数 RPM。允许范围为 1500–7500，步长为 100。");
            return;
        }
        await RunUiControlActionAsync(() => ApplyManualAsync(fan1, fan2));
    }

    private async void OnApplyCurvesClick(object sender, RoutedEventArgs e)
    {
        string fan1 = Fan1CurveBox.Text, fan2 = Fan2CurveBox.Text;
        if (!TryReadResponses(out var responses)) return;
        await RunUiControlActionAsync(() => ApplyCurvesAsync(fan1, fan2, responses[0], responses[1]));
    }

    private async void OnRestoreFirmwareClick(object sender, RoutedEventArgs e)
    {
        await RunUiControlActionAsync(RestoreFirmwareAsync);
    }

    private async Task RunUiControlActionAsync(Func<Task> action)
    {
        if (_uiControlBusy || _closing) return;
        _uiControlBusy = true;
        SetUiBusy(true);
        try { await action(); }
        catch (Exception exception)
        {
            SetControlStatus((_firmwareRestoreConfirmed ? "操作失败：" : "操作失败，固件恢复未确认：") + exception.Message);
        }
        finally
        {
            _uiControlBusy = false;
            SetUiBusy(false);
            RefreshAppliedUi();
        }
    }

    private async void OnPowerModeChanged(PowerModes mode)
    {
        if (mode == PowerModes.Resume)
        {
            SetControlStatus(_firmwareRestoreConfirmed ? "已唤醒，当前由固件控制；需要手动重新应用。"
                : "已唤醒，固件恢复未确认；请检查后端错误。");
            return;
        }
        if (mode != PowerModes.Suspend) return;
        if(Dispatcher.CheckAccess()) ClearTemperatureDisplay();
        else await Dispatcher.InvokeAsync(ClearTemperatureDisplay);
        try { await RestoreFirmwareAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            await SetControlStatusAsync("系统挂起时恢复控制失败：" + exception.Message).ConfigureAwait(false);
        }
    }

    private async Task SendHeartbeatAsync()
    {
        if (_controlMode == ControlMode.Firmware) return;
        try
        {
            _controlClient ??= new ControlClient();
            await _controlClient.HeartbeatAsync().ConfigureAwait(false);
        }
        catch (Exception exception) { await HandleSnapshotBackendFailureAsync(exception).ConfigureAwait(false); }
    }

    private async Task HandleSnapshotBackendFailureAsync(Exception exception)
    {
        Exception? cleanup = await RestoreAndDisposeClientLockedAsync().ConfigureAwait(false);
        ResetControlState(cleanup is null);
        string message = cleanup is null ? "控制后端发生错误，已切回固件控制：" + exception.Message
            : "控制后端发生错误，固件恢复未确认：" + exception.Message;
        if (cleanup is not null) message += "；清理错误：" + cleanup.Message;
        await SetControlStatusAsync(message).ConfigureAwait(false);
    }

    private async Task RestoreForSnapshotAsync(string reason, Exception? cause)
    {
        Exception? cleanup = await RestoreAndDisposeClientLockedAsync().ConfigureAwait(false);
        ResetControlState(cleanup is null);
        string message = cleanup is null ? reason : "已请求恢复固件控制，但恢复未确认。";
        if (cause is not null) message += " " + cause.Message;
        if (cleanup is not null) message += " 后端清理错误：" + cleanup.Message;
        await SetControlStatusAsync(message).ConfigureAwait(false);
    }

    private async Task<Exception?> RestoreAndDisposeClientLockedAsync()
    {
        ControlClient? client = _controlClient;
        _controlClient = null;
        if (client is null) return _firmwareRestoreConfirmed ? null
            : new IOException("此前固件恢复未确认，当前没有可重试的后端连接。");
        Exception? failure = null;
        try { await client.RestoreAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure ??= exception; }
        return failure;
    }

    private void ResetControlState(bool firmwareConfirmed = true)
    {
        _firmwareRestoreConfirmed = firmwareConfirmed;
        _controlMode = ControlMode.Firmware;
        _activeFan1Curve = _activeFan2Curve = null;
        _activeResponses = [new(), new()];
        _fan1Policy = _fan2Policy = null;
        _fan1Applied = _fan2Applied = null;
    }

    private void ThrowIfControlStopped()
    {
        if (_controlStopped) throw new ObjectDisposedException(nameof(MainWindow), "风扇控制已随窗口关闭而停止。");
    }

    private static FanControlPolicy CreatePolicy(FanCurve curve, FanResponseSettings? response = null)
    {
        response ??= new();
        return new(curve, ["cpu"], CpuMaximumAge, TimeSpan.FromSeconds(1), response.CoolingHysteresis, 90, 300,
            TimeSpan.FromSeconds(response.HeatingDelaySeconds), response.HeatingHysteresis, TimeSpan.FromSeconds(response.CoolingDelaySeconds));
    }

    private static string ResponseReason(DecisionReason reason) => reason switch
    {
        DecisionReason.HeatingDelay => "升速计时中", DecisionReason.CoolingDelay => "降速计时中",
        DecisionReason.HeatingHysteresis => "等待升温阈值", DecisionReason.CoolingHysteresis => "等待降温阈值",
        _ => "保持目标"
    };

    private static bool TryParseIncreaseDelay(string text, out int seconds) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) && seconds is >= 0 and <= 10;

    private static FanCurve ParseCurve(string text)
    {
        var points = new List<CurvePoint>();
        foreach (string entry in text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = entry.Split(':', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 ||
                !double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double celsius) ||
                !int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rpm))
                throw new ArgumentException("曲线格式应为 温度:RPM;温度:RPM，例如 40:2500;60:3500;75:5000;90:7500。");
            if (rpm % 100 != 0)
                throw new ArgumentException("曲线 RPM 必须为 100 的倍数。");
            points.Add(new CurvePoint(celsius, rpm));
        }
        try { return new FanCurve(points, new RpmLimits(1500, 7500)); }
        catch (ArgumentException exception) { throw new ArgumentException("曲线无效：" + exception.Message, exception); }
    }

    private static string FormatCurve(FanCurve curve) => string.Join(';', curve.Points.Select(point =>
        point.Celsius.ToString("R", CultureInfo.InvariantCulture) + ":" + point.Rpm.ToString(CultureInfo.InvariantCulture)));

    private static void ValidateRpm(int rpm, string parameterName)
    {
        if (rpm is < 1500 or > 7500 || rpm % 100 != 0)
            throw new ArgumentOutOfRangeException(parameterName, "RPM 必须在 1500–7500 之间，且为 100 的倍数。");
    }

    private static int QuantizeRpm(int rpm) => Math.Clamp(
        (int)Math.Round(rpm / 100d, MidpointRounding.AwayFromZero) * 100, 1500, 7500);

    private static bool TryGetFreshCpuTemperature(TemperatureSnapshot? snapshot, DateTimeOffset now, out double temperature)
    {
        temperature = 0;
        if (snapshot is null || snapshot.CapturedAtUtc > now || now - snapshot.CapturedAtUtc > CpuMaximumAge)
            return false;
        float[] values = snapshot.Temperatures
            .Where(static reading => reading.HardwareType.Equals("Cpu", StringComparison.OrdinalIgnoreCase) &&
                                     !reading.SensorName.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
            .Select(static reading => reading.ValueCelsius)
            .Where(static value => value.HasValue && float.IsFinite(value.Value))
            .Select(static value => value!.Value)
            .ToArray();
        if (values.Length == 0) return false;
        temperature = values.Max();
        return double.IsFinite(temperature) && temperature is >= -100 and <= 150;
    }

    private void LoadControlSettings()
    {
        if (!File.Exists(_controlSettingsPath)) return;
        try
        {
            ControlSettings? saved = JsonSerializer.Deserialize<ControlSettings>(File.ReadAllText(_controlSettingsPath));
            if (saved is null) return;
            ValidateRpm(saved.Fan1Rpm, nameof(saved.Fan1Rpm));
            ValidateRpm(saved.Fan2Rpm, nameof(saved.Fan2Rpm));
            FanCurve savedCurve1 = ParseCurve(saved.Fan1Curve), savedCurve2 = ParseCurve(saved.Fan2Curve);
            _ = CreatePolicy(savedCurve1);
            _ = CreatePolicy(savedCurve2);
            _settingsFan1Rpm = saved.Fan1Rpm;
            _settingsFan2Rpm = saved.Fan2Rpm;
            _settingsFan1Curve = FormatCurve(savedCurve1);
            _settingsFan2Curve = FormatCurve(savedCurve2);
            Fan1RpmBox.Text = _settingsFan1Rpm.ToString(CultureInfo.InvariantCulture);
            Fan2RpmBox.Text = _settingsFan2Rpm.ToString(CultureInfo.InvariantCulture);
            Fan1CurveBox.Text = _settingsFan1Curve;
            Fan2CurveBox.Text = _settingsFan2Curve;
            var rejected = new List<string>();
            int legacyDelay = saved.IncreaseDelaySeconds is >= 0 and <= 10 ? saved.IncreaseDelaySeconds : 0;
            if(legacyDelay != saved.IncreaseDelaySeconds) rejected.Add("升速延迟无效，已使用 0 秒。");
            JsonElement?[] loaded = [saved.Fan1Response, saved.Fan2Response];
            for(int i=0;i<2;i++)
            {
                if(loaded[i] is null || loaded[i]!.Value.ValueKind==JsonValueKind.Null)
                    _settingsResponses[i]=new(HeatingDelaySeconds:legacyDelay);
                else
                {
                    FanResponseSettings? response=null;
                    try { response=loaded[i]!.Value.Deserialize<FanResponseSettings>(); } catch(JsonException) { }
                    if(response is { IsValid: true }) _settingsResponses[i]=response;
                    else { _settingsResponses[i]=new(); rejected.Add($"风扇 {i+1} 响应参数无效，已使用默认值。"); }
                }
            }
            foreach (CurvePreset preset in saved.CurvePresets ?? [])
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Trim() == "默认曲线" ||
                        _curvePresets.Any(p => p.Name.Equals(preset.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException("名称为空、重复或属于默认曲线。");
                    FanCurve preset1 = ParseCurve(preset.Fan1Curve), preset2 = ParseCurve(preset.Fan2Curve);
                    _ = CreatePolicy(preset1);
                    _ = CreatePolicy(preset2);
                    _curvePresets.Add(preset with { Name = preset.Name.Trim(),
                        Fan1Curve = FormatCurve(preset1), Fan2Curve = FormatCurve(preset2) });
                }
                catch (Exception exception) { rejected.Add($"预设 {preset?.Name}：{exception.Message}"); }
            }
            if (saved.ThemeColor is { } theme)
            {
                try { _ = ParseThemeColor(theme); _themeColor = theme.ToUpperInvariant(); }
                catch (ArgumentException) { rejected.Add("主题色无效，已使用默认色。"); }
            }
            SettingsStatusText.Text = string.Join(Environment.NewLine, rejected);
        }
        catch (Exception exception)
        {
            SettingsStatusText.Text = "已忽略无效的控制设置文件：" + exception.Message;
        }
    }

    private bool SaveControlSettings()
    {
        string? temporaryPath = null;
        try
        {
            CurvePreset[] presets = Dispatcher.CheckAccess() ? _curvePresets.ToArray()
                : Dispatcher.Invoke(() => _curvePresets.ToArray());
            lock (_settingsFileGate)
            {
                var settings = new ControlSettings(_settingsFan1Rpm, _settingsFan2Rpm,
                    _settingsFan1Curve, _settingsFan2Curve, presets.ToList(), _themeColor,
                    _settingsResponses[0].HeatingDelaySeconds, JsonSerializer.SerializeToElement(_settingsResponses[0]), JsonSerializer.SerializeToElement(_settingsResponses[1]));
                temporaryPath = _controlSettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SettingsJsonOptions));
                File.Move(temporaryPath, _controlSettingsPath, overwrite: true);
            }
            SetSettingsStatus(string.Empty);
            return true;
        }
        catch (Exception exception)
        {
            SetSettingsStatus("设置保存失败，当前编辑内容尚未保存：" + exception.Message);
            return false;
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private Task SaveAppliedSettingsAsync() => SaveAppliedSettingsCoreAsync(saveCurves: false);

    private Task<bool> SaveAppliedCurveSettingsAsync() => SaveAppliedSettingsCoreAsync(saveCurves: true);

    private async Task<bool> SaveAppliedSettingsCoreAsync(bool saveCurves)
    {
        if (!SaveControlSettings()) return false;
        void MarkSaved()
        {
            if (saveCurves) MarkEditorSaved();
            else
            {
                _savedManual1 = _settingsFan1Rpm.ToString(CultureInfo.InvariantCulture);
                _savedManual2 = _settingsFan2Rpm.ToString(CultureInfo.InvariantCulture);
                UpdateEditorState();
            }
        }
        if (Dispatcher.CheckAccess()) MarkSaved();
        else await Dispatcher.InvokeAsync(MarkSaved).Task.ConfigureAwait(false);
        return true;
    }

    private void SetSettingsStatus(string message)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) SettingsStatusText.Text = message;
        else _ = Dispatcher.InvokeAsync(() => SettingsStatusText.Text = message);
    }

    private void SetControlStatus(string message)
    {
        if (Dispatcher.CheckAccess())
        {
            ControlStatusText.Text = message;
            RefreshAppliedUi();
        }
        else _ = SetControlStatusAsync(message);
    }

    private Task SetControlStatusAsync(string message)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return Task.CompletedTask;
        if (Dispatcher.CheckAccess())
        {
            ControlStatusText.Text = message;
            RefreshAppliedUi();
            return Task.CompletedTask;
        }
        return Dispatcher.InvokeAsync(() =>
        {
            ControlStatusText.Text = message;
            RefreshAppliedUi();
        }).Task;
    }

    private void RefreshAppliedUi()
    {
        ControlModeText.Text = _controlMode switch
        {
            ControlMode.Manual => _fan1Applied?.TemperatureCelsius >= 90 ? "手动 · 高温保护" : "手动控制",
            ControlMode.Curve => _fan1Applied is null || _fan2Applied is null ? "自动曲线 · 等待确认"
                : _fan1Applied.TemperatureCelsius >= 90 ? "自动曲线 · 高温保护" : "自动曲线",
            _ => _firmwareRestoreConfirmed ? "固件控制" : "固件恢复未确认"
        };
        Fan1TargetText.Text = _fan1Applied is { } fan1 ? $"已应用目标：{fan1.Rpm} RPM" : "已应用目标：—";
        Fan2TargetText.Text = _fan2Applied is { } fan2 ? $"已应用目标：{fan2.Rpm} RPM" : "已应用目标：—";
        UpdateEditorState();
    }

    private sealed record ControlSettings(int Fan1Rpm, int Fan2Rpm, string Fan1Curve, string Fan2Curve,
        List<CurvePreset>? CurvePresets = null, string? ThemeColor = null, int IncreaseDelaySeconds = 0,
        JsonElement? Fan1Response = null, JsonElement? Fan2Response = null);
}
