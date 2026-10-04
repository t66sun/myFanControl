using LibreHardwareMonitor.Hardware;

namespace myFanControl.Monitoring;

/// <summary>
/// Read-only CPU/GPU temperature sampling. Calls to Open, Capture, and Dispose are serialized.
/// This class deliberately never accesses ISensor.Control or any fan-control interface.
/// </summary>
public sealed class SensorMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Computer _computer;
    private readonly MachineIdentity _machineIdentity;
    private EcFanTelemetryReader? _ecFanTelemetryReader;
    private bool _opened;
    private bool _disposed;

    public SensorMonitor(MachineIdentity machineIdentity)
    {
        _machineIdentity = machineIdentity;
        _computer = new Computer
        {
            // Keep the LHM device set narrow. In particular, do not enable motherboard,
            // controller, or memory groups for this temperature-only first stage.
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = false,
            IsControllerEnabled = false,
            IsMemoryEnabled = false
        };
    }

    public PawnIoAccessSnapshot? PawnIoAccess { get; private set; }

    public void Open()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_opened)
                return;

            try
            {
                bool ecProfileMatches = EcFanTelemetryReader.IsSupportedProfile(_machineIdentity);
                PawnIoAccess = PawnIoAccessReader.Read(deviceOpenPermitted: ecProfileMatches);
                _ecFanTelemetryReader = new EcFanTelemetryReader(_machineIdentity, PawnIoAccess);
                _computer.Open();
                _opened = true;
            }
            catch
            {
                _ecFanTelemetryReader?.Dispose();
                _ecFanTelemetryReader = null;
                _computer.Close();
                throw;
            }
        }
    }

    public TemperatureSnapshot Capture()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
            var errors = new List<string>();
            var readings = new List<TemperatureReading>();
            var fans = new List<FanSpeedReading>();

            if (!_opened)
            {
                errors.Add("LibreHardwareMonitor 尚未成功初始化。");
                return new TemperatureSnapshot(capturedAt, readings, errors) { Fans = fans };
            }

            IHardware[] hardwareItems = _computer.Hardware.ToArray();
            if (!hardwareItems.Any(static item => item.HardwareType == HardwareType.Cpu))
                errors.Add("LibreHardwareMonitor 未报告 CPU 硬件。");

            foreach (IHardware hardware in hardwareItems)
                UpdateAndCollect(hardware, readings, fans, errors);

            bool hasCpuTemperatureSensor = readings.Any(static item => item.HardwareType == "Cpu");
            if (!hardwareItems.Any(static item => item.HardwareType == HardwareType.Cpu))
            {
                // The more specific CPU hardware error above is enough for this case.
            }
            else if (!hasCpuTemperatureSensor)
            {
                errors.Add("CPU 硬件未提供温度传感器。");
            }
            else if (!new TemperatureSnapshot(capturedAt, readings, errors).HasCpuTemperature)
            {
                errors.Add(PawnIoAccess is { DeviceOpenAttempted: false, IsAdministrator: false } skipped
                    ? $"CPU 温度无有效读数；当前进程没有已确认的管理员权限，未尝试打开 PawnIO：{skipped.ErrorMessage}"
                    : PawnIoAccess is { DeviceOpenAttempted: false } notAttempted
                        ? $"CPU 温度无有效读数；PawnIO 未尝试打开：{notAttempted.ErrorMessage}"
                        : PawnIoAccess is { OpenSucceeded: false, Win32Error: 5 }
                            ? "CPU 温度驱动访问被拒绝（Win32 5）；需以管理员权限进一步验证。"
                            : PawnIoAccess is { OpenSucceeded: false } access
                                ? $"CPU 温度无有效读数；PawnIO 设备打开失败：{access.Win32Error} {access.ErrorMessage}"
                                : "CPU 温度传感器当前没有可用读数；PawnIO 设备可打开，但温度读取仍需检查。");
            }

            EcFanTelemetrySnapshot? ecFanTelemetry = null;
            try
            {
                ecFanTelemetry = _ecFanTelemetryReader?.Capture();
                if (ecFanTelemetry is { Available: true, Fan1Rpm: int fan1, Fan2Rpm: int fan2 })
                {
                    fans.Add(new FanSpeedReading("EmbeddedController", "Lenovo EC",
                        "风扇 1（EC 报告）", "EC:0x0800-0x0801", fan1));
                    fans.Add(new FanSpeedReading("EmbeddedController", "Lenovo EC",
                        "风扇 2（EC 报告）", "EC:0x0802-0x0803", fan2));
                }
            }
            catch (Exception exception)
            {
                errors.Add($"EC 风扇遥测失败：{exception.GetType().Name}: {exception.Message}");
            }

            return new TemperatureSnapshot(capturedAt, readings, errors)
            {
                Fans = fans,
                EcFanTelemetry = ecFanTelemetry
            };
        }
    }

    private static void UpdateAndCollect(
        IHardware hardware,
        ICollection<TemperatureReading> readings,
        ICollection<FanSpeedReading> fans,
        ICollection<string> errors)
    {
        bool hardwareUpdated = true;
        try
        {
            hardware.Update();
        }
        catch (Exception exception)
        {
            hardwareUpdated = false;
            errors.Add($"更新 {hardware.HardwareType}「{hardware.Name}」失败：{exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType is not (SensorType.Temperature or SensorType.Fan))
                    continue;

                float? value = null;
                try
                {
                    value = hardwareUpdated ? sensor.Value : null;
                    if (value.HasValue && !float.IsFinite(value.Value))
                        value = null;
                }
                catch (Exception exception)
                {
                    errors.Add($"读取传感器「{sensor.Name}」失败：{exception.GetType().Name}: {exception.Message}");
                }

                if (sensor.SensorType == SensorType.Fan)
                {
                    if (value < 0) value = null;
                    fans.Add(new FanSpeedReading(hardware.HardwareType.ToString(), hardware.Name,
                        sensor.Name, sensor.Identifier.ToString(), value));
                    continue;
                }
                readings.Add(new TemperatureReading(
                    hardware.HardwareType.ToString(),
                    hardware.Name,
                    sensor.Name,
                    sensor.Identifier.ToString(),
                    value));
            }
        }
        catch (Exception exception)
        {
            errors.Add($"读取 {hardware.HardwareType}「{hardware.Name}」温度传感器失败：{exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            foreach (IHardware subHardware in hardware.SubHardware)
                UpdateAndCollect(subHardware, readings, fans, errors);
        }
        catch (Exception exception)
        {
            errors.Add($"读取 {hardware.HardwareType}「{hardware.Name}」子硬件失败：{exception.GetType().Name}: {exception.Message}");
        }
    }

    public IReadOnlyList<GpuFanDiagnostic> GetGpuFanDiagnostics()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_opened) return [];
            var results = new List<GpuFanDiagnostic>();
            foreach (var hardware in _computer.Hardware.Where(h => h.HardwareType == HardwareType.GpuNvidia))
            {
                try
                {
                    // Use the existing read-only LHM report queries; retain only fan sections.
                    var selected = new List<string>();
                    bool keep = false;
                    foreach (var line in hardware.GetReport().Replace("\r", "").Split('\n'))
                    {
                        if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                            keep = line is "Tachometer" or "Cooler Settings" or "Fan Coolers Status";
                        if (keep) selected.Add(line);
                    }
                    results.Add(new(hardware.Name, string.Join(Environment.NewLine, selected).Trim(), []));
                }
                catch (Exception e)
                {
                    results.Add(new(hardware.Name, "", [$"{e.GetType().Name}: {e.Message}"]));
                }
            }
            return results;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _ecFanTelemetryReader?.Dispose();
            _ecFanTelemetryReader = null;
            if (_opened)
            {
                _computer.Close();
                _opened = false;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
