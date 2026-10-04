using EcFeedbackProbe;
using EcIdentityProbe;

namespace EcPmcConfigProbe;

internal static class RpmWriterSelfCheck
{
    internal static void Run()
    {
        var io = new FakeIo(); var gate = new FakeGate();
        var writer = new FixedRpmOverrideWriter(io, gate);
        writer.Set(1, 0); writer.Set(1, 15); writer.Set(2, 75);
        Require(io.DataWrites.SequenceEqual(new[] { (0x080C, (byte)0), (0x080C, (byte)15), (0x080D, (byte)75) }), "fixed-address writes");
        Require(io.SelectorsRestored && gate.Releases == 3, "success cleanup");

        var countIo = new FakeIo();
        new FixedRpmOverrideWriter(countIo, new FakeGate()).Set(1, 20);
        int operations = countIo.Operations;
        var invalidIo = new FakeIo(); var invalidGate = new FakeGate();
        var invalidWriter = new FixedRpmOverrideWriter(invalidIo, invalidGate);
        Throws(() => invalidWriter.Set(0, 15)); Throws(() => invalidWriter.Set(3, 15));
        Throws(() => invalidWriter.Set(1, 14)); Throws(() => invalidWriter.Set(2, 76));
        Require(invalidIo.Operations == 0 && invalidGate.Acquires == 0, "invalid arguments perform no I/O");

        var unknown = new FakeIo { Chip = 0x1234 }; var unknownGate = new FakeGate();
        Throws(() => new FixedRpmOverrideWriter(unknown, unknownGate).Set(1, 20));
        Require(unknown.DataWrites.Count == 0 && unknownGate.Releases == 1, "identity gate blocks target write");

        // A fault in a restoration write can prevent selector restoration.
        // Every such fault must throw, release the lease, and permit a zero write.
        for (int fault = 1; fault <= operations; fault++)
        {
            var failing = new FakeIo { FaultAt = fault }; var faultGate = new FakeGate();
            var faultWriter = new FixedRpmOverrideWriter(failing, faultGate);
            Throws(() => faultWriter.Set(1, 20));
            Require(faultGate.Releases == 1, $"lease released at fault {fault}");
            faultWriter.Set(1, 0);
            Require(faultGate.Releases == 2 && failing.DataWrites[^1] == (0x080C, (byte)0), $"zero retry after fault {fault}");
        }

        var failedGate = new FakeGate(EcMutexWaitStatus.TimedOut);
        var unopenedIo = new FakeIo();
        Throws(() => new FixedRpmOverrideWriter(unopenedIo, failedGate).Set(1, 0));
        Require(unopenedIo.Operations == 0 && failedGate.Releases == 0, "mutex failure blocks I/O");
        Console.WriteLine($"RPM writer mocks passed: bounds, fixed addresses, identity gate, cleanup and {operations} injected I/O faults; no hardware/mutex access.");
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception) { return; }
        throw new InvalidOperationException("Expected writer operation to fail.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeGate(EcMutexWaitStatus status = EcMutexWaitStatus.Acquired) : IEcMutexGate
    {
        internal int Acquires, Releases;
        public EcMutexWaitResult Acquire(TimeSpan timeout)
        {
            Acquires++;
            return status == EcMutexWaitStatus.Acquired
                ? new(EcMutexWaitStatus.Acquired, new Lease(this))
                : new(status);
        }
        private sealed class Lease(FakeGate gate) : IDisposable
        {
            public void Dispose() => gate.Releases++;
        }
    }

    private sealed class FakeIo : ISelectorIo
    {
        private byte index = 0x33, high = 0xA3, low = 0xB2, target;
        internal ushort Chip { get; init; } = 0x5571;
        internal int FaultAt { get; init; }
        internal int Operations { get; private set; }
        internal List<(int Address, byte Value)> DataWrites { get; } = [];
        internal bool SelectorsRestored => index == 0x33 && high == 0xA3 && low == 0xB2;

        private void Step()
        {
            Operations++;
            if (Operations == FaultAt) throw new IOException("Injected selector I/O failure.");
        }
        public byte Read(byte register)
        {
            Step();
            if (register == 0x2E) return index;
            if (register != 0x2F) throw new InvalidOperationException("Unexpected register read.");
            if (index == 0x11) return high;
            if (index == 0x10) return low;
            if (index != 0x12) throw new InvalidOperationException("Unexpected indirect selector.");
            int address = (high << 8) | low;
            if (address == 0x2000) return (byte)(Chip >> 8);
            if (address == 0x2001) return (byte)Chip;
            if (address == 0x1060) return 0;
            if (address is 0x080C or 0x080D) return target;
            throw new InvalidOperationException($"Unexpected indirect address {address:X4}.");
        }
        public void Write(byte register, byte value)
        {
            Step();
            if (register == 0x2E) { index = value; return; }
            if (register != 0x2F) throw new InvalidOperationException("Unexpected register write.");
            if (index == 0x11) { high = value; return; }
            if (index == 0x10) { low = value; return; }
            if (index == 0x12)
            {
                int address = (high << 8) | low;
                if (address is not (0x080C or 0x080D)) throw new InvalidOperationException($"Unexpected target address {address:X4}.");
                target = value; DataWrites.Add((address, value)); return;
            }
            throw new InvalidOperationException("Unexpected indirect selector.");
        }
    }
}
