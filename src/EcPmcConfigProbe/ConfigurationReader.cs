namespace EcPmcConfigProbe;

internal interface IPnpConfigurationIo
{
    byte ReadRegister(byte register);
    void SelectLogicalDevice(byte value);
}
internal sealed record PmcConfiguration(byte LogicalDevice, byte ActivateRaw, ushort Descriptor0Base, ushort Descriptor1Base);
internal sealed record ConfigurationResult(ushort? ChipBefore, ushort? ChipAfter, byte? OriginalLogicalDevice,
    bool LogicalDeviceRestored, bool ReadCompleted, IReadOnlyList<PmcConfiguration> Devices, IReadOnlyList<string> Errors)
{
    public bool Succeeded => ReadCompleted && LogicalDeviceRestored && ChipBefore == 0x5571 && ChipAfter == 0x5571 && Errors.Count == 0;
}

// Only LDN 11/12 are selected for reads; the original LDN is restored in finally.
// No activation, base-address, indirect target data, or PMC command/data registers are written.
internal sealed class ConfigurationReader(IPnpConfigurationIo io)
{
    internal ConfigurationResult Read()
    {
        byte? original = null; ushort? before = null, after = null;
        bool restored = false, completed = false;
        var devices = new List<PmcConfiguration>(); var errors = new List<string>();
        try
        {
            original = io.ReadRegister(0x07);
            before = Chip();
            if (before != 0x5571) throw new NotSupportedException($"Direct PNP chip ID {before:X4} is not 5571; no LDN data read.");
            foreach (byte ldn in new byte[] { 0x11, 0x12 })
            {
                io.SelectLogicalDevice(ldn);
                if (io.ReadRegister(0x07) != ldn) throw new InvalidOperationException("LDN selection readback mismatch.");
                byte active = io.ReadRegister(0x30);
                ushort first = (ushort)((io.ReadRegister(0x60) << 8) | io.ReadRegister(0x61));
                ushort second = (ushort)((io.ReadRegister(0x62) << 8) | io.ReadRegister(0x63));
                // Repeat all fields, retaining only a coherent configuration observation.
                if (active != io.ReadRegister(0x30) || first != ((io.ReadRegister(0x60) << 8) | io.ReadRegister(0x61)) ||
                    second != ((io.ReadRegister(0x62) << 8) | io.ReadRegister(0x63)))
                    throw new InvalidOperationException("PNP configuration changed during the read.");
                devices.Add(new(ldn, active, first, second));
            }
            after = Chip();
            if (after != before) throw new InvalidOperationException("Direct PNP chip identity changed.");
            completed = true;
        }
        catch (Exception e) { errors.Add($"Configuration read failed: {e.GetType().Name}: {e.Message}"); }
        finally
        {
            if (original is byte ldn)
            {
                try
                {
                    io.SelectLogicalDevice(ldn);
                    restored = io.ReadRegister(0x07) == ldn;
                    if (!restored) errors.Add("Original LDN readback mismatch.");
                }
                catch (Exception e) { errors.Add($"LDN restoration failed: {e.GetType().Name}: {e.Message}"); }
            }
        }
        return new(before, after, original, restored, completed, devices, errors);
    }
    private ushort Chip() => (ushort)((io.ReadRegister(0x20) << 8) | io.ReadRegister(0x21));
}
