namespace myFanControl.Monitoring;

public sealed record TemperatureReading(
    string HardwareType,
    string HardwareName,
    string SensorName,
    string Identifier,
    float? ValueCelsius)
{
    public string Category => HardwareType == "Cpu" ? "CPU" : "GPU";
}

public sealed record TemperatureSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<TemperatureReading> Temperatures,
    IReadOnlyList<string> Errors)
{
    public IReadOnlyList<FanSpeedReading> Fans { get; init; } = [];
    public EcFanTelemetrySnapshot? EcFanTelemetry { get; init; }
    public bool HasFanTelemetry => Fans.Any(static fan => fan.ValueRpm.HasValue);

    public bool HasCpuTemperature => Temperatures.Any(static item =>
        item.HardwareType == "Cpu" && item.ValueCelsius.HasValue &&
        !item.SensorName.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase));
}

public sealed record FanSpeedReading(string HardwareType, string HardwareName, string SensorName,
    string Identifier, float? ValueRpm);

public sealed record GpuFanDiagnostic(string HardwareName, string Sections, IReadOnlyList<string> Errors);
