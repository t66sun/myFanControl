using EcIdentityProbe;

namespace EcFeedbackProbe;

// A separate fixed-address experiment. Does not alter the approved identity tool.
internal sealed class FeedbackReader(ISelectorIo io)
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
        if (address is not (0x2000 or 0x2001 or 0x1060) &&
            address is not (>= 0x0800 and <= 0x080F) && address is not (>= 0x8800 and <= 0x880F))
            throw new ArgumentOutOfRangeException(nameof(address));
        WriteSelector(0x11, (byte)(address >> 8));
        WriteSelector(0x10, (byte)address);
        io.Write(0x2E, 0x12);
        return io.Read(0x2F); // Never writes to the target data register.
    }
    private byte[] ReadBlock(ushort start)
    {
        var bytes = new byte[16];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = ReadAddress((ushort)(start + i));
        return bytes;
    }
    public FeedbackResult Read()
    {
        var started = DateTimeOffset.UtcNow;
        byte originalSelector = io.Read(0x2E);
        byte? originalHigh = null, originalLow = null, mapping = null, finalMapping = null;
        ushort? chip = null;
        var blocks = new List<FeedbackBlock>();
        var errors = new List<string>();
        bool restored = false;
        try
        {
            originalHigh = ReadSelector(0x11);
            originalLow = ReadSelector(0x10);
            chip = (ushort)((ReadAddress(0x2000) << 8) | ReadAddress(0x2001));
            if (chip != 0x5571) throw new NotSupportedException("Chip ID differs from the measured 0x5571; no SRAM read.");
            mapping = ReadAddress(0x1060);
            if (mapping != 0) throw new NotSupportedException("1060 differs from the measured 0x00; no SRAM read or mapping change.");
            foreach (ushort start in new ushort[] { 0x0800, 0x8800 })
            {
                var blockStarted = DateTimeOffset.UtcNow;
                byte[] first = ReadBlock(start), second = ReadBlock(start);
                blocks.Add(FeedbackBlock.Create(start, first, second, blockStarted, DateTimeOffset.UtcNow));
            }
            finalMapping = ReadAddress(0x1060);
            if (finalMapping != mapping) throw new InvalidOperationException("1060 changed during the read; sample is not valid.");
        }
        catch (Exception e) { errors.Add($"{e.GetType().Name}: {e.Message}"); }
        finally
        {
            bool highOk = originalHigh is null, lowOk = originalLow is null, selectorOk = false;
            if (originalHigh is byte hi)
                try { WriteSelector(0x11, hi); highOk = true; } catch (Exception e) { errors.Add($"High selector restoration: {e.Message}"); }
            if (originalLow is byte lo)
                try { WriteSelector(0x10, lo); lowOk = true; } catch (Exception e) { errors.Add($"Low selector restoration: {e.Message}"); }
            if (originalHigh is byte vh && originalLow is byte vl && highOk && lowOk)
                try
                {
                    if (ReadSelector(0x11) != vh || ReadSelector(0x10) != vl)
                        throw new InvalidOperationException("Address selector readback mismatch");
                }
                catch (Exception e) { highOk = lowOk = false; errors.Add($"Restoration readback: {e.Message}"); }
            try
            {
                io.Write(0x2E, originalSelector);
                selectorOk = io.Read(0x2E) == originalSelector;
                if (!selectorOk) errors.Add("Indirect selector readback mismatch.");
            }
            catch (Exception e) { errors.Add($"Indirect selector restoration: {e.Message}"); }
            restored = highOk && lowOk && selectorOk;
        }
        bool complete = chip == 0x5571 && mapping == 0 && finalMapping == 0 && blocks.Count == 2 && restored && errors.Count == 0;
        return new(started, DateTimeOffset.UtcNow, chip, mapping, finalMapping, restored, complete, blocks, errors);
    }
}
internal sealed record FeedbackResult(DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, ushort? ChipId,
    byte? Mapping1060, byte? FinalMapping1060, bool SelectorsRestored, bool ReadCompleted,
    IReadOnlyList<FeedbackBlock> Blocks, IReadOnlyList<string> Errors);
internal sealed record FeedbackBlock(string StartAddressHex, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
    string FirstHex, string SecondHex, bool RepeatedBytesAgree, ushort? Word01BigEndian, ushort? Word23BigEndian)
{
    internal static FeedbackBlock Create(ushort start, byte[] first, byte[] second, DateTimeOffset began, DateTimeOffset ended)
    {
        bool agree = first.SequenceEqual(second);
        return new($"0x{start:X4}", began, ended, Convert.ToHexString(first), Convert.ToHexString(second), agree,
            agree ? (ushort)(first[0] * 256 + first[1]) : null, agree ? (ushort)(first[2] * 256 + first[3]) : null);
    }
}
