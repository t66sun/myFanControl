using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EcPmcConfigProbe;

// The embedded module exports only a zero-input, one-byte read of port 6C.
// Construct this only after the caller's machine/EC/PMC configuration gates.
internal sealed class DeveloperStatusIo : IDisposable
{
    internal const string ExpectedModuleHash = "74C4EB71035DC956B711466FBD97A4FC11FB43178B7C5BD013FA9A3F40006972";
    private readonly SafeFileHandle handle;
    internal DeveloperStatusIo()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Pmc2Status.bin") ?? throw new InvalidOperationException("Missing status module");
        using var buffer = new MemoryStream(); resource.CopyTo(buffer);
        byte[] module = buffer.ToArray();
        if (Convert.ToHexString(SHA256.HashData(module)) != ExpectedModuleHash)
            throw new InvalidOperationException("Status module hash mismatch");
        handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        try {
            if (!DeviceIoControl(handle, 0xA1B22084, module, (uint)module.Length, [], 0, out uint loaded, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (loaded != 0) throw new InvalidDataException("Unexpected module load output");
        } catch { handle.Dispose(); throw; }
    }
    internal byte ReadStatus()
    {
        byte[] input = new byte[32]; Encoding.ASCII.GetBytes("ioctl_read_status").CopyTo(input, 0);
        byte[] output = new byte[8];
        if (!DeviceIoControl(handle, 0xA1B22104, input, 32, output, 8, out uint returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (returned != 8) throw new InvalidDataException("Unexpected status response size");
        long value = BitConverter.ToInt64(output);
        if (value is < 0 or > 255) throw new InvalidDataException("Invalid status byte");
        return (byte)value;
    }
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputBytes, byte[] output, uint outputBytes, out uint returned, IntPtr overlapped);
}
