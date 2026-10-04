using EcIdentityProbe;

namespace EcFeedbackProbe;
internal static class SelfCheck
{
    internal static void Run()
    {
        LowFeedbackSelfCheck.Run();
        var normal = new FakeIo(); var result = new FeedbackReader(normal).Read();
        Require(result.ReadCompleted && result.ChipId == 0x5571 && result.Mapping1060 == 0 && result.FinalMapping1060 == 0 &&
            result.SelectorsRestored && normal.OriginalState && result.Errors.Count == 0 && result.Blocks.Count == 2, "Normal transaction");
        Require(result.Blocks[1].Word01BigEndian == 4500 && result.Blocks[1].Word23BigEndian == 4600, "Big-endian raw words");
        Require(normal.Addresses.SetEquals(AllowedAddresses()), "Exact fixed address set");
        foreach (ushort chip in new ushort[] { 0x5570, 0xFFFF }) {
            var io = new FakeIo(chip: chip); var rejected = new FeedbackReader(io).Read();
            Require(!rejected.ReadCompleted && rejected.Errors.Count > 0 && rejected.Blocks.Count == 0 &&
                !io.Addresses.Contains(0x1060) && rejected.SelectorsRestored && io.OriginalState, "Chip gate");
        }
        var changedMode = new FakeIo(mapping: 0x80); var changedModeRead = new FeedbackReader(changedMode).Read();
        Require(!changedModeRead.ReadCompleted && changedModeRead.Blocks.Count == 0 && changedModeRead.Errors.Count > 0 &&
            changedMode.Addresses.SetEquals(new ushort[] { 0x2000, 0x2001, 0x1060 }) && changedMode.OriginalState, "Initial mapping gate");
        var finalChanged = new FakeIo(changeFinalMapping: true); var finalChangedRead = new FeedbackReader(finalChanged).Read();
        Require(!finalChangedRead.ReadCompleted && finalChangedRead.FinalMapping1060 == 0x80 && finalChangedRead.Errors.Count > 0 &&
            finalChangedRead.SelectorsRestored && finalChanged.OriginalState, "Final mapping gate");
        var changing = new FakeIo(changeBytes: true); var changingRead = new FeedbackReader(changing).Read();
        Require(changingRead.ReadCompleted && !changingRead.Blocks[1].RepeatedBytesAgree &&
            changingRead.Blocks[1].Word01BigEndian is null && changingRead.Blocks[1].Word23BigEndian is null &&
            changingRead.Blocks[1].FirstHex != changingRead.Blocks[1].SecondHex, "Preserve changing bytes without decoded words");
        var zeros = new FakeIo(zeroBlocks: true); var zeroRead = new FeedbackReader(zeros).Read();
        Require(zeroRead.ReadCompleted && zeroRead.Blocks.All(b => b.Word01BigEndian == 0 && b.Word23BigEndian == 0), "Zero raw words preserved");
        int failures = 0;
        for (int fail = 2; fail <= normal.Operations; fail++)
            foreach (bool after in new[] { false, true }) {
                var io = new FakeIo(failureAt: fail, failAfter: after); var failure = new FeedbackReader(io).Read();
                Require(!failure.ReadCompleted && failure.Errors.Count > 0, "Failure cannot be success");
                Require(!failure.SelectorsRestored || io.OriginalState, "Restoration cannot be false-positive");
                failures++;
            }
        foreach (bool after in new[] { false, true }) {
            var io = new FakeIo(failureAt: 1, failAfter: after);
            bool threw = false; try { _ = new FeedbackReader(io).Read(); } catch (IOException) { threw = true; }
            Require(threw && io.OriginalState, "Initial read failure leaves original state"); failures++;
        }
        Console.WriteLine($"Fixed SRAM mock checks passed: exact addresses, chip/mapping gates, changing/zero data and {failures} injected failures. No device access.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static HashSet<ushort> AllowedAddresses() => new(Enumerable.Range(0x0800, 16).Concat(Enumerable.Range(0x8800, 16))
        .Concat(new[] { 0x2000, 0x2001, 0x1060 }).Select(i => (ushort)i));
    private sealed class FakeIo(ushort chip = 0x5571, byte mapping = 0, bool changeFinalMapping = false,
        bool changeBytes = false, bool zeroBlocks = false, int failureAt = 0, bool failAfter = false) : ISelectorIo
    {
        private byte selector = 7, high = 0xAA, low = 0xBB;
        private int mappingReads, changingReads;
        public int Operations { get; private set; }
        public HashSet<ushort> Addresses { get; } = [];
        public bool OriginalState => selector == 7 && high == 0xAA && low == 0xBB;
        private void Fail(bool after) { if (after == failAfter && Operations == failureAt) throw new IOException("Injected failure"); }
        public byte Read(byte register)
        {
            Operations++; Fail(false);
            byte value;
            if (register == 0x2E) value = selector;
            else if (register == 0x2F && selector == 0x11) value = high;
            else if (register == 0x2F && selector == 0x10) value = low;
            else if (register == 0x2F && selector == 0x12) {
                ushort address = (ushort)(high * 256 + low); Addresses.Add(address);
                if (!AllowedAddresses().Contains(address)) throw new InvalidOperationException("Forbidden address read");
                value = address switch {
                    0x2000 => (byte)(chip >> 8), 0x2001 => (byte)chip,
                    0x1060 => changeFinalMapping && ++mappingReads > 1 ? (byte)0x80 : mapping,
                    >= 0x0800 and <= 0x080F => zeroBlocks ? (byte)0 : (byte)(address & 0xFF),
                    >= 0x8800 and <= 0x880F => zeroBlocks ? (byte)0 : (address & 0xFF) switch {
                        0 => (byte)0x11, 1 => changeBytes && ++changingReads > 1 ? (byte)0x95 : (byte)0x94,
                        2 => (byte)0x11, 3 => (byte)0xF8, _ => (byte)(address & 0xFF)
                    }, _ => throw new InvalidOperationException("Unexpected address")
                };
            } else throw new InvalidOperationException("Unexpected read");
            Fail(true); return value;
        }
        public void Write(byte register, byte value)
        {
            Operations++; Fail(false);
            if (register == 0x2E && value is 0x10 or 0x11 or 0x12 or 7) selector = value;
            else if (register == 0x2F && selector == 0x11) high = value;
            else if (register == 0x2F && selector == 0x10) low = value;
            else throw new InvalidOperationException("Forbidden data/config write");
            Fail(true);
        }
    }
}
