namespace myFanControl.Monitoring;

public sealed record VpcFanSnapshot(bool Available, uint? RawMode, string? DriverSha256,
    DateTimeOffset CapturedAtUtc, IReadOnlyList<string> Errors);

/// <summary>
/// Legacy VPC fan queries are suspended: mailbox echo is not fan-state evidence.
/// No device is opened and no VPC command is submitted.
/// </summary>
public static class LenovoVpcReader
{
    public static VpcFanSnapshot Read() => new(false, null, null, DateTimeOffset.UtcNow,
        ["VPC 风扇接口已停用：固件分发表未列出 0x22/0x2B；旧回读可能仅为邮箱回显，不能确认风扇状态。"]);
}
