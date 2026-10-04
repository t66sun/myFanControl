[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/diagnostics/pawnio-access.json'))
$ErrorActionPreference = 'Stop'
if (-not ('ThinkBook.DriverAccess' -as [type])) {
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace ThinkBook {
    public static class DriverAccess {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        public static int ProbePawnIo() {
            // Match the upstream PawnIo.LoadModuleFromResource access/share/open flags.
            // No DeviceIoControl or module loading is performed here.
            using (var handle=CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero)) {
                return handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            }
        }
    }
}
"@
}
$code = [ThinkBook.DriverAccess]::ProbePawnIo()
$who = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
$version = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO' -Name DisplayVersion -ErrorAction SilentlyContinue
$service = Get-Service -Name PawnIO -ErrorAction SilentlyContinue
$document = [pscustomobject]@{
    SchemaVersion=1;CapturedUtc=[DateTime]::UtcNow.ToString('o');QueryOnly=$true
    IsAdministrator=$who.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    PawnIoVersion=$version;ServiceStatus=if ($null -ne $service) { $service.Status.ToString() } else { 'NotFound' }
    DevicePath='\\?\GLOBALROOT\Device\PawnIO';RequestedAccess=3
    OpenSucceeded=($code -eq 0);Win32Error=$code
    ErrorText=if ($code -ne 0) { [ComponentModel.Win32Exception]::new($code).Message } else { $null }
    ModuleLoadAttempted=$false;HardwareIoAttempted=$false
}
$fullPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($fullPath)) -Force | Out-Null
$document | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $fullPath -Encoding utf8
$document
