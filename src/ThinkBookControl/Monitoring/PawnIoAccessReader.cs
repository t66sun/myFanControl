using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ThinkBookControl.Monitoring;

public sealed record PawnIoAccessSnapshot(bool OpenSucceeded, int? Win32Error, string? ErrorMessage, bool IsAdministrator)
{
    public bool DeviceOpenAttempted { get; init; }
}

/// <summary>Checks only whether the installed PawnIO device can be opened. No driver IOCTLs.</summary>
public static class PawnIoAccessReader
{
    public static PawnIoAccessSnapshot Read(bool deviceOpenPermitted = true)
    {
        bool administrator = false;
        bool deviceOpenAttempted = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            if (!administrator)
                return new(false, null, "当前进程未具备管理员权限；未尝试打开 PawnIO。", false);
            if (!deviceOpenPermitted)
                return new(false, null, "机型或 BIOS 未匹配 EC 读取范围；未尝试打开 PawnIO。", true);

            // These flags match the upstream LHM PawnIo.LoadModuleFromResource call.
            deviceOpenAttempted = true;
            using var handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (!handle.IsInvalid) return new PawnIoAccessSnapshot(true, 0, null, administrator)
                { DeviceOpenAttempted = true };
            int error = Marshal.GetLastWin32Error();
            return new PawnIoAccessSnapshot(false, error, new Win32Exception(error).Message, administrator)
                { DeviceOpenAttempted = true };
        }
        catch (Exception exception)
        {
            return new PawnIoAccessSnapshot(false, null, $"{exception.GetType().Name}: {exception.Message}", administrator)
                { DeviceOpenAttempted = deviceOpenAttempted };
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}
