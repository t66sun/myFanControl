[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/diagnostics/lenovo-thermal-state.json'))
$ErrorActionPreference = 'Stop'
# Only these commands have been checked against this machine's HYCN42WW DSDT.
# There is deliberately no user-supplied raw command or write method.
$biosKey = Get-ItemProperty -LiteralPath 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS' -Name SystemProductName,BIOSVersion
if ($biosKey.SystemProductName -ne '21CX' -or $biosKey.BIOSVersion -ne 'HYCN42WW') {
    throw 'This query is verified only against 21CX / HYCN42WW.'
}
if (-not ('ThinkBook.ThermalQuery' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace ThinkBook {
    public sealed class ThermalQuery : IDisposable {
        private readonly SafeFileHandle handle;
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref uint input, uint inputBytes, out uint output, uint outputBytes, out uint returned, IntPtr overlapped);
        public ThermalQuery() {
            // FILE_ANY_ACCESS IOCTL: request no file data read/write access.
            handle=CreateFile(@"\\.\EnergyDrv", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { int code=Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
        }
        public uint Read(uint command) {
            if (command!=0 && command!=2 && command!=3 && command!=6 && command!=7)
                throw new ArgumentOutOfRangeException("command", "Only audited status queries are allowed.");
            uint output, returned;
            if (!DeviceIoControl(handle, 0x8310213C, ref command, 4, out output, 4, out returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (returned!=4) throw new InvalidOperationException("Driver did not return exactly one DWORD.");
            return output;
        }
        public void Dispose() { handle.Dispose(); }
    }
}
'@
}
$errors = [Collections.Generic.List[object]]::new()
$records = [Collections.Generic.List[object]]::new()
$query = $null
try {
    $query = [ThinkBook.ThermalQuery]::new()
    foreach ($item in @(
        @{Name='Protocol';Command=0},
        @{Name='State';Command=2},
        @{Name='FunctionCapabilities';Command=3},
        @{Name='MmcCapabilities';Command=6},
        @{Name='MmcState';Command=7}
    )) {
        try {
            $raw = $query.Read([uint32]$item.Command)
            $records.Add([pscustomobject]@{
                Name=$item.Name; Command=$item.Command; Raw=('0x{0:X8}' -f $raw)
                FirmwareStatus=($raw -band 0xFF)
                FirmwareSucceeded=(($raw -band 0xFF) -eq 1)
                Payload=($raw -shr 16)
            })
        } catch { $errors.Add([pscustomobject]@{Name=$item.Name;Error=$_.Exception.Message}) }
    }
} catch { $errors.Add([pscustomobject]@{Name='OpenEnergyDrv';Error=$_.Exception.Message}) }
finally { if ($null -ne $query) { $query.Dispose() } }
$who = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
$document = [pscustomobject]@{
    SchemaVersion=1; CapturedUtc=[DateTime]::UtcNow.ToString('o')
    Model=$biosKey.SystemProductName; Bios=$biosKey.BIOSVersion
    IsAdministrator=$who.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    QueryOnly=$true; Transport='EnergyDrv / DYTC'; Ioctl='0x8310213C'
    Queries=@($records.ToArray()); Errors=@($errors.ToArray())
}
$fullPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($fullPath)) -Force | Out-Null
$document | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $fullPath -Encoding utf8
Write-Output $fullPath
if ($errors.Count -gt 0 -or @($records | Where-Object { -not $_.FirmwareSucceeded }).Count -gt 0) { exit 2 }
