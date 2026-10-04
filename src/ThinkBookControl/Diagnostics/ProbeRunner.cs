using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThinkBookControl.Monitoring;

namespace ThinkBookControl.Diagnostics;

internal sealed record ProbeRunResult(int ExitCode);

internal sealed record ProbeRound(
    int Sequence,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<TemperatureReading> Temperatures,
    IReadOnlyList<string> Errors,
    bool HasCpuTemperature,
    IReadOnlyList<FanSpeedReading> Fans,
    bool FanTelemetryAvailable,
    EcFanTelemetrySnapshot? EcFanTelemetry = null);

internal sealed record ProbeDocument(
    int SchemaVersion,
    string Mode,
    bool ReadOnly,
    string FanControlStatus,
    MachineIdentity Machine,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    IReadOnlyList<ProbeRound> Samples,
    IReadOnlyList<string> Errors,
    bool CpuTemperatureAvailable,
    LenovoThermalSnapshot? ThermalState = null,
    PawnIoAccessSnapshot? PawnIoAccess = null,
    IReadOnlyList<GpuFanDiagnostic>? GpuFanDiagnostics = null,
    VpcFanSnapshot? VpcFanState = null);

internal static class ProbeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static async Task<ProbeRunResult> RunAsync(string[] args)
    {
        ProbeParseResult parsed = ProbeCommand.Parse(args);
        if (!parsed.Succeeded)
        {
            if (parsed.OutputPath is not null)
            {
                var emptyDocument = new ProbeDocument(
                    1,
                    "read-only-diagnostic",
                    true,
                    "尚未确认；未执行风扇写入",
                    MachineIdentityReader.Read(),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    Array.Empty<ProbeRound>(),
                    [parsed.Error ?? "诊断参数无效。"],
                    false);
                return await WriteDocumentAsync(parsed.OutputPath, emptyDocument, 64).ConfigureAwait(false);
            }

            return new ProbeRunResult(64);
        }

        ProbeOptions options = parsed.Options!;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        MachineIdentity machine = await Task.Run(MachineIdentityReader.Read).ConfigureAwait(false);
        var errors = new List<string>(machine.Errors);
        var rounds = new List<ProbeRound>(options.Samples);
        SensorMonitor? monitor = null;
        bool opened = false;
        IReadOnlyList<GpuFanDiagnostic> gpuFanDiagnostics = [];

        try
        {
            monitor = new SensorMonitor(machine);
            try
            {
                await Task.Run(monitor.Open).ConfigureAwait(false);
                opened = true;
            }
            catch (Exception exception)
            {
                errors.Add($"初始化 LibreHardwareMonitor 失败：{exception.GetType().Name}: {exception.Message}");
            }

            for (int index = 0; index < options.Samples; index++)
            {
                TemperatureSnapshot snapshot;
                if (opened)
                {
                    try
                    {
                        snapshot = await Task.Run(monitor!.Capture).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        snapshot = new TemperatureSnapshot(
                            DateTimeOffset.UtcNow,
                            Array.Empty<TemperatureReading>(),
                            [$"采集温度失败：{exception.GetType().Name}: {exception.Message}"]);
                    }
                }
                else
                {
                    snapshot = new TemperatureSnapshot(
                        DateTimeOffset.UtcNow,
                        Array.Empty<TemperatureReading>(),
                        ["传感器监控尚未初始化。"]);
                }

                rounds.Add(new ProbeRound(
                    index + 1,
                    snapshot.CapturedAtUtc,
                    snapshot.Temperatures,
                    snapshot.Errors,
                    snapshot.HasCpuTemperature,
                    snapshot.Fans,
                    snapshot.HasFanTelemetry,
                    snapshot.EcFanTelemetry));

                if (index + 1 < options.Samples)
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            if (opened)
                gpuFanDiagnostics = await Task.Run(monitor!.GetGpuFanDiagnostics).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add($"诊断运行失败：{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            if (monitor is not null)
            {
                try
                {
                    await Task.Run(monitor.Dispose).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    errors.Add($"关闭传感器监控时失败：{exception.GetType().Name}: {exception.Message}");
                }
            }
        }

        ProbeDocument document = new(
            1,
            "read-only-diagnostic",
            true,
            "尚未确认；未执行风扇写入",
            machine,
            startedAt,
            DateTimeOffset.UtcNow,
            rounds,
            errors,
            rounds.Count == options.Samples && rounds.All(static round => round.HasCpuTemperature),
            await Task.Run(LenovoThermalReader.Read).ConfigureAwait(false),
            monitor?.PawnIoAccess,
            gpuFanDiagnostics,
            await Task.Run(LenovoVpcReader.Read).ConfigureAwait(false));

        int exitCode = document.CpuTemperatureAvailable ? 0 : 2;
        return await WriteDocumentAsync(options.OutputPath, document, exitCode).ConfigureAwait(false);
    }

    private static async Task<ProbeRunResult> WriteDocumentAsync(
        string outputPath,
        ProbeDocument document,
        int successExitCode)
    {
        try
        {
            string fullPath = Path.GetFullPath(outputPath);
            string? parentDirectory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentDirectory))
                Directory.CreateDirectory(parentDirectory);

            string json = JsonSerializer.Serialize(document, JsonOptions);
            await File.WriteAllTextAsync(fullPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                .ConfigureAwait(false);
            return new ProbeRunResult(successExitCode);
        }
        catch
        {
            return new ProbeRunResult(3);
        }
    }
}
