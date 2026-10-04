[CmdletBinding()]
param([switch]$EnableTestSigning)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskPrincipal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator token required. No system change attempted.'
}
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$taskDir = Join-Path $taskRoot "artifacts/diagnostics/pawnio-bootstrap-$taskStamp"
New-Item -ItemType Directory -Path $taskDir | Out-Null
$taskReport = [ordered]@{
    CapturedUtc = [DateTime]::UtcNow.ToString('o'); IsAdministrator = $true
    RequestedEnableTestSigning = [bool]$EnableTestSigning; TestSigningChangeAttempted = $false
    TestSigningCommandSucceeded = $false; RebootInitiated = $false
    DriverChangeAttempted = $false; HardwareIoAttempted = $false
    NativeCommands = @(); Error = $null
}
function Invoke-BootstrapNative {
    param([string]$Executable, [string[]]$Arguments, [string]$LogName)
    $taskLines = @(& $Executable @Arguments 2>&1)
    $taskExit = $LASTEXITCODE
    $taskLines | Out-String | Set-Content -LiteralPath (Join-Path $taskDir $LogName) -Encoding utf8
    $taskReport.NativeCommands += [pscustomobject]@{ Executable=$Executable; Arguments=$Arguments; ExitCode=$taskExit; Log=$LogName }
    if ($taskExit -ne 0) { throw "$Executable failed with exit $taskExit; see $LogName" }
    return $taskLines
}
try {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PawnIoBootIntegrity {
    [StructLayout(LayoutKind.Sequential)] public struct Info { public uint Length; public uint Options; }
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int cls, ref Info info, uint size, out uint returned);
    public static uint Read() {
        var info = new Info { Length = 8 }; uint returned;
        int status = NtQuerySystemInformation(103, ref info, 8, out returned);
        if (status != 0 || returned != 8) throw new InvalidOperationException("Code integrity query failed: " + status.ToString("X8"));
        return info.Options;
    }
}
'@
    $taskIntegrity = [PawnIoBootIntegrity]::Read()
    $taskReport.RuntimeCodeIntegrityOptions = $taskIntegrity.ToString('X8')
    $taskReport.RuntimeTestSigningEnabled = ($taskIntegrity -band 2) -ne 0
    $taskReport.RuntimeHvciEnabled = ($taskIntegrity -band 0x400) -ne 0
    $taskService = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO'
    $taskImage = $taskService.ImagePath
    if (-not $taskImage.StartsWith('\SystemRoot\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unexpected PawnIO driver path; inspect before changing boot settings.'
    }
    $taskDriver = Join-Path $env:SystemRoot $taskImage.Substring(12)
    $taskHash = (Get-FileHash -LiteralPath $taskDriver -Algorithm SHA256).Hash.ToLowerInvariant()
    $taskReport.InstalledDriverPath = $taskDriver
    $taskReport.InstalledDriverSha256 = $taskHash
    $taskReport.InstalledDriverVersion = (Get-Item -LiteralPath $taskDriver).VersionInfo.FileVersion
    if ($taskHash -ne 'fca6e7d58b0cf38dbb913a2b9e532f48629145d395f454b16a9f58e97b8d3940') {
        throw 'Installed driver changed from the reviewed 2.2.0 baseline.'
    }
    $taskDevHash = (Get-FileHash -LiteralPath (Join-Path $taskRoot 'artifacts/drivers/PawnIO-2.0.1/extracted/15wnIO.sys') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($taskDevHash -ne 'ac670293a260e48d4199a950ec6ead52d0b6a6444b5f80920b6c27792fc69cd1') {
        throw 'Developer driver hash mismatch.'
    }
    $taskReport.SecureBootEnabled = Confirm-SecureBootUEFI
    $taskReport.HvciRegistryEnabled = Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' -Name Enabled
    $taskReport.BlocklistRegistryEnabled = Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Config' -Name VulnerableDriverBlocklistEnable
    $taskBitLocker = Get-BitLockerVolume -MountPoint $env:SystemDrive
    $taskReport.BitLocker = [pscustomobject]@{ MountPoint=$taskBitLocker.MountPoint; ProtectionStatus=$taskBitLocker.ProtectionStatus.ToString(); VolumeStatus=$taskBitLocker.VolumeStatus.ToString() }
    $taskBcd = Join-Path $env:SystemRoot 'System32/bcdedit.exe'
    Invoke-BootstrapNative $taskBcd @('/enum', '{current}', '/v') 'bcd-current-before.txt' | Out-Null
    Invoke-BootstrapNative $taskBcd @('/export', (Join-Path $taskDir 'bcd-before.bak')) 'bcd-export.txt' | Out-Null
    $taskPackages = @(Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceName -eq 'PawnIO' })
    $taskReport.PawnIoPackages = @($taskPackages | Select-Object DeviceName, DeviceID, InfName, DriverVersion, IsSigned)
    if ($taskPackages.Count -ne 1 -or $taskPackages[0].InfName -notmatch '^oem\d+\.inf$') {
        throw 'Cannot uniquely identify the installed PawnIO OEM INF for export.'
    }
    $taskExport = Join-Path $taskDir 'original-driver-package'
    New-Item -ItemType Directory -Path $taskExport | Out-Null
    Invoke-BootstrapNative (Join-Path $env:SystemRoot 'System32/pnputil.exe') @('/export-driver', $taskPackages[0].InfName, $taskExport) 'driver-export.txt' | Out-Null
    $taskExportedSys = @(Get-ChildItem -LiteralPath $taskExport -Recurse -File -Filter PawnIO.sys)
    if ($taskExportedSys.Count -ne 1 -or (Get-FileHash -LiteralPath $taskExportedSys[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskHash) {
        throw 'Exported package does not match the actual installed driver.'
    }
    $taskReport.ExportedDriverPackage = $taskExport
    if ($EnableTestSigning) {
        if ($taskReport.SecureBootEnabled) { throw 'Secure Boot is enabled; this script does not change it.' }
        if (-not $taskReport.RuntimeHvciEnabled -or $taskReport.HvciRegistryEnabled -ne 1 -or $taskReport.BlocklistRegistryEnabled -ne 1) {
            throw 'HVCI or driver blocklist differs from the agreed baseline.'
        }
        if ($taskReport.BitLocker.ProtectionStatus -ne 'Off') {
            throw 'BitLocker protection is active; boot changes need a recovery arrangement first. Protection was not suspended.'
        }
        $taskReport.TestSigningChangeAttempted = $true
        Invoke-BootstrapNative $taskBcd @('/set', '{current}', 'testsigning', 'on') 'testsigning-set.txt' | Out-Null
        $taskReport.TestSigningCommandSucceeded = $true
        Invoke-BootstrapNative $taskBcd @('/enum', '{current}', '/v') 'bcd-current-after.txt' | Out-Null
    }
} catch {
    $taskReport.Error = $_.Exception.Message
} finally {
    $taskReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskDir 'report.json') -Encoding utf8
    Write-Output "Report: $taskDir"
}
if ($taskReport.Error) { Write-Error $taskReport.Error -ErrorAction Continue; exit 1 }
exit 0
