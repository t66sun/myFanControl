using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;

namespace myFanControl;

public partial class MainWindow
{
    private IntPtr _powerNotification;
    private HwndSource? _powerSource;
    private PowerModes? _lastNativePowerMode;
    internal event Action<PowerModes>? SuspendResumeObserved;
    internal bool SuspendResumeNotificationsRegistered => _powerNotification != IntPtr.Zero;

    private void OnPowerSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _powerSource = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("Window message source unavailable.");
        _powerSource.AddHook(PowerBroadcastHook);
        // Opt in to DAM/Modern Standby notifications, including while the window is hidden in the tray.
        _powerNotification = RegisterSuspendResumeNotification(hwnd, 0); // DEVICE_NOTIFY_WINDOW_HANDLE
        if (_powerNotification == IntPtr.Zero) {
            _powerSource.RemoveHook(PowerBroadcastHook);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Sleep/resume notification registration failed.");
        }
    }

    private IntPtr PowerBroadcastHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0218) return IntPtr.Zero; // WM_POWERBROADCAST
        PowerModes? mode = wParam.ToInt64() switch {
            4 => PowerModes.Suspend, // PBT_APMSUSPEND
            7 or 0x12 => PowerModes.Resume, // PBT_APMRESUMESUSPEND / PBT_APMRESUMEAUTOMATIC
            _ => null
        };
        if (mode is not null && mode != _lastNativePowerMode) {
            _lastNativePowerMode = mode;
            SuspendResumeObserved?.Invoke(mode.Value);
        }
        return IntPtr.Zero;
    }

    private void DisposePowerNotifications()
    {
        if (_powerNotification != IntPtr.Zero) {
            UnregisterSuspendResumeNotification(_powerNotification);
            _powerNotification = IntPtr.Zero;
        }
        _powerSource?.RemoveHook(PowerBroadcastHook);
        _powerSource = null;
    }

    [DllImport("user32.dll", SetLastError=true)]
    private static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient, uint flags);
    [DllImport("user32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterSuspendResumeNotification(IntPtr registration);
}
