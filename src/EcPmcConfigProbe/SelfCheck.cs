namespace EcPmcConfigProbe;
internal static class SelfCheck
{
    internal static void Run()
    {
        BaselineSelfCheck.Run();
        RpmWriterSelfCheck.Run();
        var normal = new Fake(); var read = new ConfigurationReader(normal).Read();
        Require(read.Succeeded && normal.Ldn == 0x0F && read.Devices.Count == 2, "normal and restoration");
        Require(read.Devices[0].Descriptor0Base == 0x62 && read.Devices[0].Descriptor1Base == 0x66 &&
            read.Devices[1].Descriptor0Base == 0x68 && read.Devices[1].Descriptor1Base == 0x6C, "big endian configuration");
        var unknown = new Fake(chip: 0x1234); var rejected = new ConfigurationReader(unknown).Read();
        Require(!rejected.Succeeded && rejected.Devices.Count == 0 && !unknown.SelectedDevices.Contains((byte)0x11), "unknown ID gate");
        var changed = new Fake(changeData: true); var unstable = new ConfigurationReader(changed).Read();
        Require(!unstable.Succeeded && unstable.Errors.Count > 0 && changed.Ldn == 0x0F, "configuration mutation rejected");
        int calls = 0;
        var status = PmcStatusReader.Read(read, () => { calls++; return 0; });
        Require(status.ReadCompleted && status.Raw == 0 && calls == 1, "zero status retained");
        var refused = PmcStatusReader.Read(read, () => throw new System.ComponentModel.Win32Exception(5));
        Require(refused.Attempted && !refused.ReadCompleted && refused.Raw is null && refused.Win32Error == 5, "driver refusal retained");
        var mismatch = read with { Devices = [new(0x11, 1, 0x62, 0x66), new(0x12, 1, 0x70, 0x74)] };
        var skip = PmcStatusReader.Read(mismatch, () => { calls++; return 1; });
        Require(!skip.Attempted && calls == 1, "unknown configuration cannot trigger PIO");
        int failures = 0;
        for (int n = 1; n <= normal.Operations; n++)
            foreach (bool after in new[] { false, true })
            {
                var io = new Fake(failureAt: n, failAfter: after); var failure = new ConfigurationReader(io).Read();
                Require(!failure.Succeeded && failure.Errors.Count > 0, "failure cannot be success");
                Require(!failure.LogicalDeviceRestored || io.Ldn == 0x0F, "restore cannot be false positive");
                failures++;
            }
        Console.WriteLine($"PNP configuration mocks passed: identity gate, coherent bytes, LDN restore, {failures} before/after faults; no device access.");
    }
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private sealed class Fake(int failureAt = 0, bool failAfter = false, ushort chip = 0x5571, bool changeData = false) : IPnpConfigurationIo
    {
        public byte Ldn { get; private set; } = 0x0F;
        public int Operations { get; private set; }
        public HashSet<byte> SelectedDevices { get; } = [];
        private int _activeReads;
        private T Operation<T>(Func<T> action)
        {
            int operation = ++Operations;
            if (operation == failureAt && !failAfter) throw new IOException("injected before");
            T value = action();
            if (operation == failureAt && failAfter) throw new IOException("injected after");
            return value;
        }
        public byte ReadRegister(byte reg) => Operation(() => reg switch
        {
            0x07 => Ldn, 0x20 => (byte)(chip >> 8), 0x21 => (byte)chip,
            0x30 => changeData && ++_activeReads == 2 ? (byte)0 : (byte)1,
            0x60 or 0x62 => (byte)0,
            0x61 => Ldn == 0x11 ? (byte)0x62 : Ldn == 0x12 ? (byte)0x68 : throw new InvalidOperationException("unselected"),
            0x63 => Ldn == 0x11 ? (byte)0x66 : Ldn == 0x12 ? (byte)0x6C : throw new InvalidOperationException("unselected"),
            _ => throw new InvalidOperationException("forbidden register")
        });
        public void SelectLogicalDevice(byte value) => Operation(() =>
        {
            if (value is not (0x11 or 0x12 or 0x0F)) throw new InvalidOperationException("forbidden LDN write");
            Ldn = value; SelectedDevices.Add(value); return true;
        });
    }
}
