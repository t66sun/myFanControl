using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

using EcIdentityProbe;

namespace EcPmcConfigProbe;

internal sealed class PnpSelectorIo : ISelectorIo, IPnpConfigurationIo, IDisposable
{
    internal const string ExpectedModuleHash = "b3896a1cab0d808fca31fe2ebcae045d59dac690da87b17c858bb8da357eb45e";
    private readonly SafeFileHandle handle;
    public PnpSelectorIo()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("LpcIO.bin") ?? throw new InvalidOperationException("Missing module");
        using var buffer = new MemoryStream(); resource.CopyTo(buffer);
        byte[] module = buffer.ToArray();
        if (Convert.ToHexString(SHA256.HashData(module)).ToLowerInvariant() != ExpectedModuleHash)
            throw new InvalidOperationException("Unexpected signed-module hash");
        handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try
        {
            if (!DeviceIoControl(handle, 0xA1B22084, module, (uint)module.Length, [], 0, out uint loadedBytes, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (loadedBytes != 0) throw new InvalidOperationException("Unexpected module load output");
            Execute("ioctl_select_slot", [1], 0); // 4E/4F only; never scan BARs.
        }
        catch { handle.Dispose(); throw; }
    }
    private long[] Execute(string name, long[] arguments, int outputs)
    {
        byte[] input = new byte[32 + arguments.Length * 8];
        Encoding.ASCII.GetBytes(name).CopyTo(input, 0);
        Buffer.BlockCopy(arguments, 0, input, 32, arguments.Length * 8);
        byte[] output = new byte[outputs * 8];
        if (!DeviceIoControl(handle, 0xA1B22104, input, (uint)input.Length, output, (uint)output.Length, out uint returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (returned != output.Length) throw new InvalidOperationException($"Invalid {name} output length: {returned}");
        long[] values = new long[outputs]; Buffer.BlockCopy(output, 0, values, 0, output.Length); return values;
    }
    public byte Read(byte register)
    {
        if (register is not (0x2E or 0x2F)) throw new ArgumentOutOfRangeException(nameof(register));
        long value = Execute("ioctl_superio_inb", [register], 1)[0];
        if (value is < 0 or > 255) throw new InvalidOperationException("Invalid byte response");
        return (byte)value;
    }
    public void Write(byte register, byte value)
    {
        if (register is not (0x2E or 0x2F)) throw new ArgumentOutOfRangeException(nameof(register));
        Execute("ioctl_superio_outb", [register, value], 0);
    }
    public byte ReadRegister(byte register)
    {
        if (register is not (0x07 or 0x20 or 0x21 or 0x30 or 0x60 or 0x61 or 0x62 or 0x63))
            throw new ArgumentOutOfRangeException(nameof(register));
        long value = Execute("ioctl_superio_inb", [register], 1)[0];
        if (value is < 0 or > 255) throw new InvalidOperationException("Invalid PNP byte response");
        return (byte)value;
    }
    public void SelectLogicalDevice(byte value) => Execute("ioctl_superio_outb", [0x07, value], 0);
    public byte PeekPmc2Status()
    {
        // Fixed validated PMC2 status port; never read its data FIFO and never write either port.
        long value = Execute("ioctl_pio_inb", [0x6C], 1)[0];
        if (value is < 0 or > 255) throw new InvalidOperationException("Invalid PMC2 status byte");
        return (byte)value;
    }
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputBytes, byte[] output, uint outputBytes, out uint returned, IntPtr overlapped);
}
