using EcIdentityProbe;

namespace EcFeedbackProbe;

internal static class LowFeedbackSelfCheck
{
    internal static void Run()
    {
        var normalIo = new FakeIo();
        var normal = new LowFeedbackReader(normalIo, new FakeMutex(), new FakeClock()).Read();
        Require(normal.Available && normal.ReadCompleted && normal.SelectorsRestored && normalIo.OriginalState,
            "Normal read and selector restoration");
        Require(normal.Fan1Rpm == 4329 && normal.Fan2Rpm == 4303 &&
            normal.Target0804 == 43 && normal.Target0805 == 43 &&
            normal.Target0804Times100Rpm == 4300 && normal.Target0805Times100Rpm == 4300,
            "Big-endian RPM words and target bytes");
        Require(normalIo.Addresses.SetEquals(AllowedAddresses()), "Exact identity, mapping, and six-register address set");
        Require(normalIo.NoDataWrites && normalIo.OriginalState, "No writes to selector 12 data port");

        foreach (ushort chip in new ushort[] { 0x5570, 0xFFFF })
        {
            var io = new FakeIo(chip: chip);
            var result = new LowFeedbackReader(io, new FakeMutex(), new FakeClock()).Read();
            Require(!result.Available && result.Errors.Count > 0 && result.FirstBytes is null &&
                !io.Addresses.Contains(0x1060) && !io.Addresses.Any(IsFeedbackAddress), "Unknown chip gate");
        }

        var unknownMappingIo = new FakeIo(mapping: 0x80);
        var unknownMapping = new LowFeedbackReader(unknownMappingIo, new FakeMutex(), new FakeClock()).Read();
        Require(!unknownMapping.Available && !unknownMappingIo.Addresses.Any(IsFeedbackAddress) &&
            unknownMappingIo.Addresses.SetEquals(new ushort[] { 0x2000, 0x2001, 0x1060 }), "Initial mapping gate");

        var changedMappingIo = new FakeIo(changeFinalMapping: true);
        var changedMapping = new LowFeedbackReader(changedMappingIo, new FakeMutex(), new FakeClock()).Read();
        Require(!changedMapping.Available && changedMapping.Mapping1060After == 0x80 &&
            changedMapping.SelectorsRestored && changedMappingIo.OriginalState &&
            changedMapping.FirstBytes is not null && changedMapping.Fan1Rpm is null &&
            changedMapping.Target0804Times100Rpm is null, "Final mapping gate");

        var changingIo = new FakeIo(changeSecondPass: true);
        var changing = new LowFeedbackReader(changingIo, new FakeMutex(), new FakeClock()).Read();
        Require(!changing.Available && changing.ReadCompleted && changing.FirstBytes is not null &&
            changing.SecondBytes is not null && !changing.FirstBytes.SequenceEqual(changing.SecondBytes) &&
            changing.Fan1Rpm is null && changing.Fan2Rpm is null &&
            changing.Target0804Times100Rpm is null && changing.Target0805Times100Rpm is null,
            "Changing bytes rejected");

        var tooFastIo = new FakeIo(fan1Rpm: 7501);
        var tooFast = new LowFeedbackReader(tooFastIo, new FakeMutex(), new FakeClock()).Read();
        Require(!tooFast.Available && tooFast.Fan1Rpm is null && tooFast.Fan2Rpm is null &&
            tooFast.Target0804Times100Rpm is null && tooFast.Target0805Times100Rpm is null &&
            tooFast.Target0804 == 43 && tooFast.Errors.Any(static e => e.Contains("7500")),
            "RPM upper bound");

        var zeroIo = new FakeIo(fan1Rpm: 0, fan2Rpm: 0);
        var zeros = new LowFeedbackReader(zeroIo, new FakeMutex(), new FakeClock()).Read();
        Require(zeros.Available && zeros.Fan1Rpm == 0 && zeros.Fan2Rpm == 0, "Zero RPM remains valid");

        var timeoutIo = new FakeIo();
        var timeout = new LowFeedbackReader(timeoutIo, new FakeMutex(EcMutexWaitStatus.TimedOut), new FakeClock()).Read();
        Require(!timeout.Available && timeout.ReaderStopped && timeout.Errors.Count > 0 && timeoutIo.Operations == 0,
            "Mutex timeout stops reader before port access");

        var midReadIo = new FakeIo(failAtAddress: 0x0802);
        var midReadMutex = new FakeMutex();
        var midReadReader = new LowFeedbackReader(midReadIo, midReadMutex, new FakeClock());
        var midReadFailure = midReadReader.Read();
        int operationsAfterFailure = midReadIo.Operations;
        var afterMidReadFailure = midReadReader.Read();
        Require(!midReadFailure.Available && !midReadFailure.ReadCompleted && midReadFailure.ReaderStopped &&
            midReadFailure.Fan1Rpm is null && midReadFailure.Target0804Times100Rpm is null &&
            !afterMidReadFailure.Available && midReadIo.Operations == operationsAfterFailure &&
            midReadMutex.AcquireCount == 1, "Mid-read I/O failure stops repeated access and decoding");

        var abandonedIo = new FakeIo();
        var abandonedMutex = new FakeMutex(EcMutexWaitStatus.Abandoned);
        var abandonedReader = new LowFeedbackReader(abandonedIo, abandonedMutex, new FakeClock());
        var abandoned = abandonedReader.Read();
        var afterAbandoned = abandonedReader.Read();
        Require(!abandoned.Available && abandoned.MutexAbandoned && abandoned.ReaderStopped &&
            !afterAbandoned.Available && abandonedMutex.AcquireCount == 1 && abandonedIo.Operations == 0,
            "Abandoned mutex stops all subsequent EC access");

        var restoreFaultIo = new FakeIo(failHighRestore: true);
        var restoreFault = new LowFeedbackReader(restoreFaultIo, new FakeMutex(), new FakeClock()).Read();
        Require(!restoreFault.Available && !restoreFault.SelectorsRestored && restoreFault.ReaderStopped &&
            restoreFault.Errors.Any(static e => e.Contains("恢复")) && restoreFault.Fan1Rpm is null &&
            restoreFault.Fan2Rpm is null && restoreFault.Target0804Times100Rpm is null,
            "Selector restoration failure stops reader and rejects decoded values");

        var elapsedOverLimit = new LowFeedbackReader(new FakeIo(), new FakeMutex(),
            new FakeClock(stopwatchElapsed: TimeSpan.FromMilliseconds(2501), utcElapsed: TimeSpan.FromMilliseconds(100))).Read();
        Require(!elapsedOverLimit.Available && elapsedOverLimit.Fan1Rpm is null &&
            elapsedOverLimit.Target0804Times100Rpm is null && elapsedOverLimit.Errors.Any(static e => e.Contains("Stopwatch")),
            "Injected Stopwatch timeout rejects data");

        var utcOverLimit = new LowFeedbackReader(new FakeIo(), new FakeMutex(),
            new FakeClock(stopwatchElapsed: TimeSpan.FromMilliseconds(100), utcElapsed: TimeSpan.FromMilliseconds(2501))).Read();
        Require(!utcOverLimit.Available && utcOverLimit.Fan2Rpm is null &&
            utcOverLimit.Target0805Times100Rpm is null && utcOverLimit.Errors.Any(static e => e.Contains("UTC 间隔")),
            "Injected UTC timeout rejects data");

        var utcBackwards = new LowFeedbackReader(new FakeIo(), new FakeMutex(),
            new FakeClock(stopwatchElapsed: TimeSpan.FromMilliseconds(100), utcElapsed: TimeSpan.FromMilliseconds(-1))).Read();
        Require(!utcBackwards.Available && utcBackwards.Errors.Any(static e => e.Contains("UTC 结束时间")),
            "UTC clock reversal rejects data");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Low feedback self-check failed: {message}");
    }

    private static HashSet<ushort> AllowedAddresses() => new(
        Enumerable.Range(0x0800, 6).Concat(new[] { 0x2000, 0x2001, 0x1060 }).Select(static value => (ushort)value));

    private static bool IsFeedbackAddress(ushort address) => address is >= 0x0800 and <= 0x0805;

    private sealed class FakeMutex(EcMutexWaitStatus status = EcMutexWaitStatus.Acquired) : IEcMutexGate
    {
        public int AcquireCount { get; private set; }

        public EcMutexWaitResult Acquire(TimeSpan timeout)
        {
            AcquireCount++;
            return status == EcMutexWaitStatus.Acquired
                ? new(EcMutexWaitStatus.Acquired, new FakeLease())
                : new(status);
        }
    }

    private sealed class FakeLease : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class FakeClock(TimeSpan? stopwatchElapsed = null, TimeSpan? utcElapsed = null) : ILowFeedbackClock
    {
        private int _utcReads;
        private int _timestampReads;
        private readonly DateTimeOffset _start = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        private readonly TimeSpan _elapsed = stopwatchElapsed ?? TimeSpan.FromMilliseconds(50);
        private readonly TimeSpan _utcDelta = utcElapsed ?? TimeSpan.FromMilliseconds(50);

        public DateTimeOffset UtcNow => _utcReads++ == 0 ? _start : _start + _utcDelta;
        public long GetTimestamp() => _timestampReads++ == 0 ? 1000 : 1000 + (long)(_elapsed.TotalSeconds * Frequency);
        public long Frequency => 1_000_000;
    }

    private sealed class FakeIo(
        ushort chip = 0x5571,
        byte mapping = 0,
        bool changeFinalMapping = false,
        bool changeSecondPass = false,
        int fan1Rpm = 4329,
        int fan2Rpm = 4303,
        byte target0804 = 43,
        byte target0805 = 43,
        bool failHighRestore = false,
        ushort? failAtAddress = null) : ISelectorIo
    {
        private byte _selector = 7;
        private byte _high = 0xAA;
        private byte _low = 0xBB;
        private int _mappingReads;
        private readonly Dictionary<ushort, int> _readsPerAddress = [];

        public int Operations { get; private set; }
        public HashSet<ushort> Addresses { get; } = [];
        public bool NoDataWrites { get; private set; } = true;
        public bool OriginalState => _selector == 7 && _high == 0xAA && _low == 0xBB;

        public byte Read(byte register)
        {
            Operations++;
            if (register == 0x2E) return _selector;
            if (register != 0x2F) throw new InvalidOperationException("Unexpected port read");
            if (_selector == 0x11) return _high;
            if (_selector == 0x10) return _low;
            if (_selector != 0x12) throw new InvalidOperationException("Unexpected indirect selector");

            ushort address = (ushort)((_high << 8) | _low);
            Addresses.Add(address);
            if (!AllowedAddresses().Contains(address)) throw new InvalidOperationException($"Forbidden address 0x{address:X4}");
            if (failAtAddress == address) throw new IOException($"Injected read failure at 0x{address:X4}");
            int count = _readsPerAddress.GetValueOrDefault(address) + 1;
            _readsPerAddress[address] = count;
            return address switch
            {
                0x2000 => (byte)(chip >> 8),
                0x2001 => (byte)chip,
                0x1060 => changeFinalMapping && ++_mappingReads > 1 ? (byte)0x80 : mapping,
                0x0800 => (byte)((fan1Rpm >> 8) ^ (changeSecondPass && count > 1 ? 1 : 0)),
                0x0801 => (byte)fan1Rpm,
                0x0802 => (byte)(fan2Rpm >> 8),
                0x0803 => (byte)fan2Rpm,
                0x0804 => target0804,
                0x0805 => target0805,
                _ => throw new InvalidOperationException("Unexpected address")
            };
        }

        public void Write(byte register, byte value)
        {
            Operations++;
            if (register == 0x2E)
            {
                _selector = value;
                return;
            }
            if (register != 0x2F || _selector is not (0x10 or 0x11))
            {
                NoDataWrites = false;
                throw new InvalidOperationException("Forbidden data write");
            }
            if (failHighRestore && _selector == 0x11 && value == 0xAA)
                throw new IOException("Injected high selector restoration failure");
            if (_selector == 0x11) _high = value;
            else _low = value;
        }
    }
}
