[CmdletBinding()]
param(
    [switch]$PlanOnly,
    [ValidateRange(3,15)][int]$HoldSeconds=3,
    [string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/diagnostics/thermal-mode-roundtrip.json')
)
$ErrorActionPreference = 'Stop'
# Audited against the local DSDT: MMC function 0xB, valid=1, modes 2/3.
# This deliberately has no raw-command, reset, full-speed, or EC-register option.
if ($PlanOnly) {
    [pscustomobject]@{Model='21CX';Bios='HYCN42WW';Performance='0x0012B001';Quiet='0x0013B001';HoldSeconds=$HoldSeconds;RestoresOriginal=$true;WritesExecuted=$false}
    return
}
$bios = Get-ItemProperty -LiteralPath 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS'
if ($bios.SystemProductName -ne '21CX' -or $bios.BIOSVersion -ne 'HYCN42WW') { throw 'Unsupported model or BIOS.' }
if (-not ('ThinkBook.ModeRoundTrip' -as [type])) {
Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace ThinkBook {
    public sealed class ModeRoundTrip : IDisposable {
        private readonly SafeFileHandle handle;
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref uint input, uint inputBytes, out uint output, uint outputBytes, out uint returned, IntPtr overlapped);
        public ModeRoundTrip() {
            handle=CreateFile(@"\\.\EnergyDrv", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { int code=Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
        }
        private uint Execute(uint command) {
            uint output, returned;
            if (!DeviceIoControl(handle, 0x8310213C, ref command, 4, out output, 4, out returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (returned!=4 || (output & 255)!=1) throw new InvalidOperationException("DYTC failed or returned invalid length: 0x"+output.ToString("X8"));
            return output;
        }
        public uint Query(uint command) {
            if (command!=0 && command!=2 && command!=3 && command!=6 && command!=7) throw new ArgumentOutOfRangeException("command");
            return Execute(command);
        }
        public uint SetMmc(uint mode) {
            if (mode!=2 && mode!=3) throw new ArgumentOutOfRangeException("mode");
            return Execute(1u | (11u << 12) | (mode << 16) | (1u << 20));
        }
        public void Dispose() { handle.Dispose(); }
    }
}
"@
}
$events = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[string]]::new()
$client = $null
$attempted = $false
$restored = $false
$targetVerified = $false
$originalMode = $null
$originalState = $null
function Record-Value([string]$Name, [uint32]$Value) {
    $events.Add([pscustomobject]@{TimeUtc=[DateTime]::UtcNow.ToString('o');Name=$Name;Raw=('0x{0:X8}' -f $Value)})
}
try {
    $client = [ThinkBook.ModeRoundTrip]::new()
    $protocol = $client.Query(0); Record-Value Protocol $protocol
    $caps = $client.Query(3); Record-Value FunctionCapabilities $caps
    $modes = $client.Query(6); Record-Value ModeCapabilities $modes
    $originalState = $client.Query(2); Record-Value BeforeState $originalState
    $mmc = $client.Query(7); Record-Value BeforeMmc $mmc
    $originalMode = ($mmc -shr 16)
    if ($protocol -ne 0x50000101 -or (($caps -shr 16) -band 0x800) -eq 0 -or (($modes -shr 16) -band 12) -ne 12) {
        throw 'Expected protocol and MMC performance/quiet capabilities were not advertised.'
    }
    # Other active functions can override MMC; do not change a state we cannot restore.
    if (($originalState -shr 16) -ne 0x801 -or (($originalState -shr 8) -band 15) -ne 11 -or
        $originalMode -notin @(2,3) -or (($originalState -shr 12) -band 15) -ne $originalMode) {
        throw 'Initial state is not an isolated, restorable MMC mode.'
    }
    if ($client.Query(2) -ne $originalState -or $client.Query(7) -ne $mmc) { throw 'Mode changed before experiment.' }
    $target = if ($originalMode -eq 2) { 3 } else { 2 }
    $attempted = $true
    Record-Value SetTargetResponse ($client.SetMmc($target))
    Start-Sleep -Seconds $HoldSeconds
    $state = $client.Query(2); Record-Value TargetState $state
    $mode = $client.Query(7); Record-Value TargetMmc $mode
    $expected = (($originalState -band 0xFFFF0FFF) -bor ($target -shl 12))
    if ($state -ne $expected -or ($mode -shr 16) -ne $target) { throw 'Target mode did not persist or another function intervened.' }
    $targetVerified = $true
} catch { $errors.Add($_.Exception.Message) }
finally {
    if ($attempted -and $null -ne $client) {
        try {
            Record-Value RestoreResponse ($client.SetMmc([uint32]$originalMode))
            Start-Sleep -Seconds 3
            $state = $client.Query(2); Record-Value RestoredState $state
            $mode = $client.Query(7); Record-Value RestoredMmc $mode
            $restored = ($state -eq $originalState -and ($mode -shr 16) -eq $originalMode)
            if (-not $restored) { $errors.Add('Original firmware state was not restored; inspect the OEM thermal mode immediately.') }
        } catch { $errors.Add('Restoration failed: ' + $_.Exception.Message) }
    }
    if ($null -ne $client) { $client.Dispose() }
    $fullPath = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($fullPath)) -Force | Out-Null
    [pscustomobject]@{
        SchemaVersion=1;Model=$bios.SystemProductName;Bios=$bios.BIOSVersion
        CapturedUtc=[DateTime]::UtcNow.ToString('o');WritesAttempted=$attempted
        TargetVerified=$targetVerified;OriginalRestored=$restored
        Passed=($targetVerified -and $restored -and $errors.Count -eq 0)
        Events=@($events.ToArray());Errors=@($errors.ToArray())
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $fullPath -Encoding utf8
    Write-Output $fullPath
}
if ($errors.Count -gt 0 -or -not $targetVerified -or -not $restored) { exit 2 }
