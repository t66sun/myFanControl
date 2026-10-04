using EcFeedbackProbe;
using EcIdentityProbe;

namespace EcPmcConfigProbe;
internal static class BaselineSelfCheck
{
    internal static void Run()
    {
        var io = new FakeIo(); var gate = new Gate();
        var normal = new FanOverrideBaselineReader(io, gate).Read();
        Require(normal.Available && normal.Fan1Rpm == 3400 && normal.RawCandidateOverride0C == 0, "baseline data");
        Require(normal.SelectorsRestored && io.Restored && gate.Releases == 1, "baseline cleanup");
        var unknown = new FakeIo { Chip = 0x1234 };
        Require(!new FanOverrideBaselineReader(unknown, new Gate()).Read().Available && unknown.WindowReads == 0, "identity gate");
        var changed = new FakeIo { ChangeSecondSnapshot = true };
        Require(!new FanOverrideBaselineReader(changed, new Gate()).Read().Available && changed.Restored, "changing baseline rejected");
        var changingReader = new FanOverrideBaselineReader(new FakeIo { ChangeSecondSnapshot = true }, new Gate());
        int reads = 0, delays = 0;
        var recovered = ControlHost.ReadAutomaticBaseline(() => { reads++; return changingReader.Read(); }, () => delays++);
        Require(recovered.Available && reads == 2 && delays == 1, "startup retries changing feedback then accepts coherent zero");
        var unstable = normal with { Available = false, SecondBytes = [.. normal.SecondBytes!], Errors = ["The two complete 0800-080F snapshots differ."] };
        unstable.SecondBytes![0] ^= 1;
        reads = 0; delays = 0;
        try { ControlHost.ReadAutomaticBaseline(() => { reads++; return unstable; }, () => delays++); throw new InvalidOperationException("unbounded startup retry accepted"); }
        catch (IOException) { Require(reads == 8 && delays == 7, "startup retry bounded"); }
        foreach (var invalid in new[] { unstable with { SelectorsRestored = false }, unstable with { ChipIdAfter = 0x1234 },
            unstable with { Errors = ["Injected I/O fault", "The two complete 0800-080F snapshots differ."] },
            normal with { RawCandidateOverride0C = 36 } })
        {
            reads = 0;
            try { ControlHost.ReadAutomaticBaseline(() => { reads++; return invalid; }, () => throw new InvalidOperationException("invalid baseline retried")); throw new InvalidOperationException("invalid startup accepted"); }
            catch (IOException) { Require(reads == 1, "startup fault or override fails immediately"); }
        }
        int cases = io.Operations;
        for (int fault = 1; fault <= cases; fault++) {
            var failing = new FakeIo { FaultAt = fault }; var faultGate = new Gate();
            var result = new FanOverrideBaselineReader(failing, faultGate).Read();
            Require(!result.Available, "fault accepted as baseline");
            Require(!result.SelectorsRestored || failing.Restored, "false selector restore");
            Require(faultGate.Releases == 1, "fault leaked lease");
        }
        Console.WriteLine($"Fixed baseline mocks passed: coherent data, identity, changing snapshot, {cases} I/O fault boundaries; no hardware/mutex access.");
    }
    private static void Require(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private sealed class Gate : IEcMutexGate
    {
        public int Releases;
        public EcMutexWaitResult Acquire(TimeSpan timeout) => new(EcMutexWaitStatus.Acquired, new Lease(this));
        private sealed class Lease(Gate gate) : IDisposable { public void Dispose() { gate.Releases++; } }
    }
    private sealed class FakeIo : ISelectorIo
    {
        byte index = 0x33, high = 0xA3, low = 0xB2;
        public ushort Chip { get; init; } = 0x5571;
        public int FaultAt { get; init; }
        public bool ChangeSecondSnapshot { get; init; }
        public int Operations { get; private set; }
        public int WindowReads { get; private set; }
        public bool Restored => index == 0x33 && high == 0xA3 && low == 0xB2;
        void Step() { if (++Operations == FaultAt) throw new IOException("Injected baseline fault"); }
        public byte Read(byte register) {
            Step();
            if (register == 0x2E) return index;
            if (register != 0x2F) throw new InvalidOperationException("Wrong port");
            if (index == 0x11) return high;
            if (index == 0x10) return low;
            if (index != 0x12) throw new InvalidOperationException("Wrong selector");
            int address = (high << 8) | low;
            if (address == 0x2000) return (byte)(Chip >> 8);
            if (address == 0x2001) return (byte)Chip;
            if (address == 0x1060) return 0;
            if (address is < 0x0800 or > 0x080F) throw new InvalidOperationException("Wrong window");
            WindowReads++;
            if (ChangeSecondSnapshot && WindowReads == 17) return 0x0C;
            return low switch { 0 or 2 => 0x0D, 1 or 3 => 0x48, 4 or 5 => 34, _ => 0 };
        }
        public void Write(byte register, byte value) {
            Step();
            if (register == 0x2E) { index = value; return; }
            if (register != 0x2F) throw new InvalidOperationException("Wrong port");
            if (index == 0x11) high = value;
            else if (index == 0x10) low = value;
            else throw new InvalidOperationException("Attempted target/data write");
        }
    }
}
