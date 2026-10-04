namespace EcIdentityProbe;

internal interface ISelectorIo
{
    byte Read(byte register);
    void Write(byte register, byte value);
}

// Only 10/11 address selectors are written. Data register 12 is read only.
internal sealed class IdentityReader(ISelectorIo io)
{
    private byte ReadSelector(byte selector)
    {
        io.Write(0x2E, selector);
        return io.Read(0x2F);
    }

    private void WriteSelector(byte selector, byte value)
    {
        if (selector is not (0x10 or 0x11)) throw new ArgumentOutOfRangeException(nameof(selector));
        io.Write(0x2E, selector);
        io.Write(0x2F, value);
    }

    private byte ReadAddress(ushort address)
    {
        WriteSelector(0x11, (byte)(address >> 8));
        WriteSelector(0x10, (byte)address);
        io.Write(0x2E, 0x12);
        return io.Read(0x2F);
    }

    public IdentityResult Read()
    {
        byte originalSelector = io.Read(0x2E);
        byte? originalHigh = null, originalLow = null;
        var errors = new List<string>();
        ushort? chip = null;
        byte? mapping = null;
        bool restored = false;
        try
        {
            originalHigh = ReadSelector(0x11);
            originalLow = ReadSelector(0x10);
            chip = (ushort)((ReadAddress(0x2000) << 8) | ReadAddress(0x2001));
            // No mapping-register read until the returned ID matches the known family.
            if (chip is 0x5570 or 0x5571) mapping = ReadAddress(0x1060);
            else errors.Add("芯片身份未匹配 5570/5571；停止进一步访问，返回值不视为有效芯片识别。");
        }
        catch (Exception e) { errors.Add($"读取失败：{e.Message}"); }
        finally
        {
            // Attempt each restoration independently, even when another one fails.
            bool highOk = originalHigh is null, lowOk = originalLow is null, selectorOk = false;
            if (originalHigh is byte hi)
                try { WriteSelector(0x11, hi); highOk = true; } catch (Exception e) { errors.Add($"高地址选择器恢复失败：{e.Message}"); }
            if (originalLow is byte lo)
                try { WriteSelector(0x10, lo); lowOk = true; } catch (Exception e) { errors.Add($"低地址选择器恢复失败：{e.Message}"); }
            if (originalHigh is byte verifyHigh && originalLow is byte verifyLow && highOk && lowOk)
                try
                {
                    if (ReadSelector(0x11) != verifyHigh || ReadSelector(0x10) != verifyLow)
                        throw new InvalidOperationException("地址选择器回读不一致");
                }
                catch (Exception e) { highOk = lowOk = false; errors.Add($"恢复回读失败：{e.Message}"); }
            try
            {
                io.Write(0x2E, originalSelector);
                selectorOk = io.Read(0x2E) == originalSelector;
                if (!selectorOk) errors.Add("间接选择器恢复回读不一致。");
            }
            catch (Exception e) { errors.Add($"间接选择器恢复失败：{e.Message}"); }
            restored = highOk && lowOk && selectorOk;
        }
        return new(chip, mapping, restored, errors);
    }
}
internal sealed record IdentityResult(ushort? RawChipId, byte? Mapping1060, bool SelectorsRestored, IReadOnlyList<string> Errors);
