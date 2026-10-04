using EcFeedbackProbe;
using EcIdentityProbe;

namespace myFanControl.Monitoring;

public sealed record EcFanTelemetrySnapshot(
    bool Available,
    ushort? RawChipId,
    ushort? RawChipIdAfter,
    byte? Mapping1060,
    byte? Mapping1060After,
    bool SelectorsRestored,
    string? FirstReadBytesHex,
    string? SecondReadBytesHex,
    int? Fan1Rpm,
    int? Fan2Rpm,
    byte? Target0804Byte,
    byte? Target0805Byte,
    int? Target0804Times100Rpm,
    int? Target0805Times100Rpm,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    double? StopwatchElapsedMilliseconds,
    bool MutexAbandoned,
    bool ReaderStopped,
    IReadOnlyList<string> Errors);

/// <summary>
/// Opens the existing PawnIO client only for the measured model/BIOS and an already elevated
/// process with an accessible device. It never requests elevation and owns only read-only EC reads.
/// </summary>
internal sealed class EcFanTelemetryReader : IDisposable
{
    private readonly List<string> _initializationErrors = [];
    private PawnSelectorIo? _io;
    private LowFeedbackReader? _reader;
    private bool _stopped;

    internal EcFanTelemetryReader(MachineIdentity machine, PawnIoAccessSnapshot access)
    {
        if (!string.Equals(machine.ProductName, "21CX", StringComparison.Ordinal))
            _initializationErrors.Add("EC 风扇读取仅适用于已验证的 21CX；当前型号未匹配。");
        if (!string.Equals(machine.BiosVersion, "HYCN42WW", StringComparison.Ordinal))
            _initializationErrors.Add("EC 风扇读取仅适用于已验证的 HYCN42WW BIOS；当前版本未匹配。");
        if (!access.IsAdministrator)
            _initializationErrors.Add("EC 风扇读数需要管理员权限；请以管理员身份运行程序。");
        else if (!access.OpenSucceeded)
            _initializationErrors.Add(access.Win32Error is int error
                ? $"PawnIO 设备当前不可用：Win32 {error} {access.ErrorMessage}" 
                : $"PawnIO 设备当前不可用：{access.ErrorMessage ?? "未能打开设备"}");

        if (_initializationErrors.Count > 0) return;

        try
        {
            _io = new PawnSelectorIo();
            _reader = new LowFeedbackReader(_io);
        }
        catch (Exception exception)
        {
            _io?.Dispose();
            _io = null;
            _initializationErrors.Add($"初始化只读 PawnIO 客户端失败：{exception.GetType().Name}: {exception.Message}");
        }
    }

    internal static bool IsSupportedProfile(MachineIdentity machine) =>
        string.Equals(machine.ProductName, "21CX", StringComparison.Ordinal) &&
        string.Equals(machine.BiosVersion, "HYCN42WW", StringComparison.Ordinal);

    internal EcFanTelemetrySnapshot Capture()
    {
        if (_reader is null || _stopped)
        {
            IReadOnlyList<string> errors = _stopped
                ? [.. _initializationErrors, "EC 读取已停止；不会继续访问选择器。"]
                : _initializationErrors;
            return Unavailable(errors, readerStopped: _stopped);
        }

        LowFeedbackReadResult result;
        try
        {
            result = _reader.Read();
        }
        catch (Exception exception)
        {
            _stopped = true;
            return Unavailable([.. _initializationErrors,
                $"EC 读取停止：{exception.GetType().Name}: {exception.Message}"], readerStopped: true);
        }
        if (result.ReaderStopped) _stopped = true;
        return new EcFanTelemetrySnapshot(
            result.Available,
            result.ChipIdBefore,
            result.ChipIdAfter,
            result.Mapping1060Before,
            result.Mapping1060After,
            result.SelectorsRestored,
            result.FirstBytes is null ? null : Convert.ToHexString(result.FirstBytes),
            result.SecondBytes is null ? null : Convert.ToHexString(result.SecondBytes),
            result.Fan1Rpm,
            result.Fan2Rpm,
            result.Target0804,
            result.Target0805,
            result.Target0804Times100Rpm,
            result.Target0805Times100Rpm,
            result.StartedAtUtc,
            result.FinishedAtUtc,
            result.StopwatchElapsedMilliseconds,
            result.MutexAbandoned,
            result.ReaderStopped,
            result.Errors);
    }

    public void Dispose()
    {
        _io?.Dispose();
        _io = null;
        _reader = null;
    }

    private static EcFanTelemetrySnapshot Unavailable(IReadOnlyList<string> errors, bool readerStopped = false)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new EcFanTelemetrySnapshot(false, null, null, null, null, false,
            null, null, null, null, null, null, null, null,
            now, now, null, false, readerStopped, errors);
    }
}
