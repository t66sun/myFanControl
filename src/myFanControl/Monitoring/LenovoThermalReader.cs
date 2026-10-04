using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace myFanControl.Monitoring;

public sealed record LenovoThermalSnapshot(bool Available, uint? Protocol, uint? FunctionCapabilities,
    uint? ModeCapabilities, uint? State, uint? MmcMode, IReadOnlyList<string> Errors)
{
    public string ModeName => State is uint state && ((state >> 8) & 0xF) == 0xB
        ? MmcMode switch { 2 => "性能", 3 => "安静", _ => "未确认" }
        : "其它固件模式";
    public bool FullSpeedAdvertised => FunctionCapabilities is uint caps && (caps & (1u << 2)) != 0;
}

/// <summary>Queries only the five DYTC commands audited on 21CX/HYCN42WW. No setting API.</summary>
public static class LenovoThermalReader
{
    public static LenovoThermalSnapshot Read()
    {
        var errors = new List<string>();
        try
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (bios?.GetValue("SystemProductName") as string != "21CX" ||
                bios.GetValue("BIOSVersion") as string != "HYCN42WW")
                return new(false, null, null, null, null, null, ["固件模式查询尚未适配此型号或 BIOS。"]);
            using var handle = CreateFile(@"\\.\EnergyDrv", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            uint Query(uint input)
            {
                if (input is not (0 or 2 or 3 or 6 or 7)) throw new ArgumentOutOfRangeException(nameof(input));
                if (!DeviceIoControl(handle, 0x8310213C, ref input, 4, out var output, 4, out var returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (returned != 4) throw new InvalidOperationException("驱动返回长度无效。");
                if ((output & 0xFF) != 1) throw new InvalidOperationException($"DYTC 查询 {input} 失败：0x{output:X8}");
                return output;
            }
            var protocol = Query(0);
            var state = Query(2);
            var caps = Query(3) >> 16;
            var modeCaps = Query(6) >> 16;
            var mode = Query(7) >> 16;
            return new(true, protocol, caps, modeCaps, state, mode, errors);
        }
        catch (Exception e)
        {
            return new(false, null, null, null, null, null, [$"固件模式查询失败：{e.GetType().Name}: {e.Message}"]);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref uint input, uint inputBytes, out uint output,
        uint outputBytes, out uint returned, IntPtr overlapped);
}
