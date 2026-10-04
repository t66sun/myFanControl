using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EcFeedbackProbe;
using EcIdentityProbe;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using FanControl.Core;

namespace EcPmcConfigProbe;

/// <summary>
/// Developer-only adapter for the fixed PMC2 query protocol. Native
/// DeviceIoControl calls are synchronous and cannot be interrupted by a managed
/// timeout; callers must account for that when setting transaction deadlines.
/// </summary>
internal sealed class DeveloperQueryTransport : IPmc2QueryTransport
{
    internal const string ExpectedModuleHash = "9386E0242C9412809F29F3962814163D4941696765A63D4284AB58EE85AEA56B";

    internal const string ControlModuleHash = "13750587876C85C678237AAE4B956476CFE8716003F96DBEC7A627C269E14223";
    private readonly bool controlEnabled;
    private readonly PnpSelectorIo selectorIo;
    private readonly object sync = new();
    private QueryModuleIo? moduleIo;
    private IDisposable? mutexLease;
    private bool transactionStarting;
    private bool transactionActive;
    private Stopwatch? transactionClock;
    internal List<object> Trace { get; } = [];

    internal IdentityResult? BeforeIdentity { get; private set; }
    internal IdentityResult? AfterIdentity { get; private set; }
    internal ConfigurationResult? Configuration { get; private set; }

    // The caller owns selectorIo; this transport only borrows it.
    internal DeveloperQueryTransport(PnpSelectorIo selectorIo, bool controlEnabled = false) {
        this.selectorIo = selectorIo ?? throw new ArgumentNullException(nameof(selectorIo));
        this.controlEnabled = controlEnabled;
    }

    public IDisposable BeginExclusiveTransaction()
    {
        lock (sync)
        {
            if (transactionActive || transactionStarting) throw new InvalidOperationException("A PMC2 transaction is already active or starting.");
            transactionStarting = true;
            Trace.Clear();
            transactionClock = Stopwatch.StartNew();
            BeforeIdentity = null;
            AfterIdentity = null;
            Configuration = null;
        }

        IDisposable? acquiredLease = null;
        QueryModuleIo? openedModule = null;
        try
        {
            using (var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", false))
            {
                if (bios?.GetValue("SystemProductName") as string != "21CX" ||
                    bios.GetValue("BIOSVersion") as string != "HYCN42WW")
                    throw new NotSupportedException("Machine/BIOS profile mismatch; no query device opened.");
            }

            EcMutexWaitResult wait = new GlobalIsaMutexGate().Acquire(TimeSpan.FromSeconds(2));
            if (wait.Status != EcMutexWaitStatus.Acquired || wait.Lease is null)
                throw new InvalidOperationException($"ISA mutex {wait.Status}; no query device opened.");
            acquiredLease = wait.Lease;
            Trace.Add(new { Stage = "MutexAcquired", ElapsedMs = transactionClock!.Elapsed.TotalMilliseconds });

            var before = new IdentityReader(selectorIo).Read();
            BeforeIdentity = before;
            ValidateIdentity(before, "Initial");
            Trace.Add(new { Stage = "IdentityValidated", ElapsedMs = transactionClock!.Elapsed.TotalMilliseconds });

            var config = new ConfigurationReader(selectorIo).Read();
            Configuration = config;
            ValidateConfiguration(config);
            Trace.Add(new { Stage = "ConfigurationValidated", ElapsedMs = transactionClock!.Elapsed.TotalMilliseconds });

            openedModule = new QueryModuleIo(controlEnabled);
            Trace.Add(new { Stage = "ModuleLoaded", ElapsedMs = transactionClock!.Elapsed.TotalMilliseconds });
            lock (sync)
            {
                moduleIo = openedModule;
                mutexLease = acquiredLease;
                transactionActive = true;
                transactionStarting = false;
                openedModule = null;
                acquiredLease = null;
                return new TransactionLease(this);
            }
        }
        catch
        {
            try { openedModule?.Dispose(); }
            finally
            {
                try { acquiredLease?.Dispose(); }
                finally { lock (sync) transactionStarting = false; }
            }
            throw;
        }
    }

    public byte ReadStatus() => WithActiveModule("ReadStatus", io => io.ReadStatus());
    public byte ReadData() => WithActiveModule("ReadData", io => io.ReadData());

    public void WriteQueryCommand(byte command)
    {
        if (command != 0xD5) throw new ArgumentOutOfRangeException(nameof(command), "Only fixed query command D5 is permitted.");
        WithActiveModule("WriteCommandD5", io => { io.WriteQueryCommand(command); return 0; });
    }

    public void WriteQueryParameter(byte parameter)
    {
        if (parameter is not (0x12 or 0x18 or 0x19))
            throw new ArgumentOutOfRangeException(nameof(parameter), "Only fixed query parameters 12, 18, and 19 are permitted.");
        WithActiveModule($"WriteParameter{parameter:X2}", io => { io.WriteQueryParameter(parameter); return 0; });
    }

    internal void SetRpmOverride(int fan, byte value)
    {
        if (!controlEnabled || fan is not (1 or 2) || (value != 0 && value is < 15 or > 75))
            throw new ArgumentOutOfRangeException(nameof(value));
        using var lease = BeginExclusiveTransaction();
        var clock = Stopwatch.StartNew();
        WaitIdle();
        WithActiveModule("RpmCommand", io => { io.WriteRpmCommand(fan == 1 ? (byte)0xD7 : (byte)0xC7); return 0; });
        WaitIdle();
        WithActiveModule("RpmValue", io => { io.WriteRpmValue(value); return 0; });
        WaitIdle();
        void WaitIdle() {
            for (int i = 0; i < 100; i++) {
                byte status = ReadStatus();
                if ((status & 1) != 0) throw new InvalidDataException("Unexpected PMC output during RPM command.");
                if (clock.ElapsedMilliseconds >= 100) throw new TimeoutException("RPM transaction exceeds 100 ms.");
                if ((status & 2) == 0) return;
                Thread.Sleep(1);
            }
            throw new TimeoutException("RPM input remained busy.");
        }
    }

    private T WithActiveModule<T>(string operation, Func<QueryModuleIo, T> action)
    {
        lock (sync)
        {
            if (!transactionActive || moduleIo is null)
                throw new InvalidOperationException("PMC2 port access requires an active exclusive transaction.");
            double started = transactionClock!.Elapsed.TotalMilliseconds;
            try {
                T result = action(moduleIo);
                Trace.Add(new { Stage = operation, StartedMs = started, CompletedMs = transactionClock.Elapsed.TotalMilliseconds, Result = result });
                return result;
            } catch (Exception e) {
                Trace.Add(new { Stage = operation, StartedMs = started, CompletedMs = transactionClock.Elapsed.TotalMilliseconds, Error = e.Message });
                throw;
            }
        }
    }

    private static void ValidateIdentity(IdentityResult identity, string stage)
    {
        if (identity.RawChipId != 0x5571 || identity.Mapping1060 != 0 ||
            !identity.SelectorsRestored || identity.Errors.Count != 0)
            throw new InvalidOperationException($"{stage} indirect identity/mapping/selector restoration gate failed.");
    }

    private static void ValidateConfiguration(ConfigurationResult config)
    {
        if (!config.Succeeded || config.Devices.Count != 2 ||
            !config.Devices.Any(x => x.LogicalDevice == 0x11 && x.ActivateRaw == 1 && x.Descriptor0Base == 0x62 && x.Descriptor1Base == 0x66) ||
            !config.Devices.Any(x => x.LogicalDevice == 0x12 && x.ActivateRaw == 1 && x.Descriptor0Base == 0x68 && x.Descriptor1Base == 0x6C))
            throw new InvalidOperationException("Exact PMC1/PMC2 configuration gate failed.");
    }

    private void EndTransaction()
    {
        lock (sync)
        {
            if (!transactionActive) return;
            transactionActive = false;
            Exception? cleanupError = null;
            try
            {
                try { moduleIo?.Dispose(); }
                catch (Exception e) { cleanupError = e; }
                finally { moduleIo = null; }
                try
                {
                    var after = new IdentityReader(selectorIo).Read();
                    AfterIdentity = after;
                    ValidateIdentity(after, "Final");
                    Trace.Add(new { Stage = "FinalIdentityValidated", ElapsedMs = transactionClock!.Elapsed.TotalMilliseconds });
                }
                catch (Exception e) { cleanupError ??= e; }
            }
            finally
            {
                try { mutexLease?.Dispose(); }
                catch (Exception e) { cleanupError ??= e; }
                finally { mutexLease = null; }
            }
            if (cleanupError is not null) throw cleanupError;
        }
    }

    private sealed class TransactionLease(DeveloperQueryTransport owner) : IDisposable
    {
        private DeveloperQueryTransport? transport = owner;
        public void Dispose() => Interlocked.Exchange(ref transport, null)?.EndTransaction();
    }

    private sealed class QueryModuleIo : IDisposable
    {
        private const uint IoctlLoadModule = 0xA1B22084;
        private const uint IoctlCallExport = 0xA1B22104;
        private readonly SafeFileHandle handle;

        internal QueryModuleIo(bool controlEnabled)
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(controlEnabled ? "Pmc2Control.bin" : "Pmc2Query.bin")
                ?? throw new InvalidOperationException("Missing PMC2 query module.");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            byte[] module = buffer.ToArray();
            if (Convert.ToHexString(SHA256.HashData(module)) != (controlEnabled ? ControlModuleHash : ExpectedModuleHash))
                throw new InvalidOperationException("PMC2 query module hash mismatch.");

            handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try
            {
                if (!DeviceIoControl(handle, IoctlLoadModule, module, (uint)module.Length, [], 0, out uint loaded, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (loaded != 0) throw new InvalidDataException("Unexpected query module load output.");
            }
            catch { handle.Dispose(); throw; }
        }

        internal byte ReadStatus() => (byte)Call("ioctl_read_status", [], 1)[0];
        internal byte ReadData() => (byte)Call("ioctl_read_data", [], 1)[0];
        internal void WriteQueryCommand(byte command) => Call("ioctl_query_command", [command], 0);
        internal void WriteQueryParameter(byte parameter) => Call("ioctl_query_parameter", [parameter], 0);

        internal void WriteRpmCommand(byte command) => Call("ioctl_rpm_command", [command], 0);
        internal void WriteRpmValue(byte value) => Call("ioctl_rpm_value", [value], 0);

        private long[] Call(string name, long[] arguments, int outputs)
        {
            byte[] input = new byte[32 + arguments.Length * sizeof(long)];
            Encoding.ASCII.GetBytes(name).CopyTo(input, 0);
            Buffer.BlockCopy(arguments, 0, input, 32, arguments.Length * sizeof(long));
            byte[] output = new byte[outputs * sizeof(long)];
            if (!DeviceIoControl(handle, IoctlCallExport, input, (uint)input.Length, output, (uint)output.Length,
                    out uint returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (returned != output.Length) throw new InvalidDataException($"Unexpected {name} response length: {returned}.");
            var values = new long[outputs];
            Buffer.BlockCopy(output, 0, values, 0, output.Length);
            if (outputs == 1 && values[0] is < 0 or > 255) throw new InvalidDataException($"Invalid {name} byte result.");
            return values;
        }

        public void Dispose() => handle.Dispose();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputBytes,
            byte[] output, uint outputBytes, out uint returned, IntPtr overlapped);
    }
}
