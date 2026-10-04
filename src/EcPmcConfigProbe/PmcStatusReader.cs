namespace EcPmcConfigProbe;
internal sealed record PmcStatusResult(bool Attempted, bool ReadCompleted, byte? Raw, int? Win32Error, string? Error);
internal static class PmcStatusReader
{
    internal static PmcStatusResult Read(ConfigurationResult configuration, Func<byte> peek)
    {
        if (!configuration.Succeeded || configuration.Devices.Count != 2 ||
            !configuration.Devices.Any(x => x.LogicalDevice == 0x11 && x.ActivateRaw == 1 && x.Descriptor0Base == 0x62 && x.Descriptor1Base == 0x66) ||
            !configuration.Devices.Any(x => x.LogicalDevice == 0x12 && x.ActivateRaw == 1 && x.Descriptor0Base == 0x68 && x.Descriptor1Base == 0x6C))
            return new(false, false, null, null, "Exact observed PMC1/2 configuration mismatch; no PIO attempted.");
        try { return new(true, true, peek(), null, null); }
        catch (System.ComponentModel.Win32Exception e) { return new(true, false, null, e.NativeErrorCode, e.Message); }
        catch (Exception e) { return new(true, false, null, null, $"{e.GetType().Name}: {e.Message}"); }
    }
}
