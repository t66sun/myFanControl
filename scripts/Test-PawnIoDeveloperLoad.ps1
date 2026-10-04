[CmdletBinding()]
param([switch]$ReadPmc2Status, [switch]$ReadPmc2Query, [switch]$RecoverPreviousQueryResponse, [switch]$RpmTrial)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskPrincipal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator token required.' }
$taskDir = Join-Path $taskRoot ('artifacts/diagnostics/pawnio-dev-load-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $taskDir | Out-Null
$taskReport = [ordered]@{
    CapturedUtc=[DateTime]::UtcNow.ToString('o'); IsAdministrator=$true
    HardwareIoAttempted=$false; DriverPackageDeleted=$false; DeviceRemoved=$false
    DisableAttempted=$false; DriverPathChangeAttempted=$false
    DeveloperStarted=$false; ModuleLoadSucceeded=$false; OriginalRestored=$false
    Commands=@(); Error=$null; RestoreError=$null
}
$taskOriginalPath = $null
function Invoke-DevNative {
    param([string]$Exe, [string[]]$Arguments, [string]$Log)
    $taskLines = @(& $Exe @Arguments 2>&1)
    $taskExit = $LASTEXITCODE
    $taskLines | Out-String | Set-Content -LiteralPath (Join-Path $taskDir $Log) -Encoding utf8
    $taskReport.Commands += [pscustomobject]@{Exe=$Exe;Arguments=$Arguments;ExitCode=$taskExit;Log=$Log}
    if ($taskExit -ne 0) { throw "$Exe exit $taskExit; see $Log" }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PawnIoDeveloperProbe {
    [StructLayout(LayoutKind.Sequential)] public struct Ci { public uint Length; public uint Options; }
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, ref Ci info, uint size, out uint returned);
    public static uint Integrity() { var i=new Ci{Length=8};uint n;int s=NtQuerySystemInformation(103,ref i,8,out n);if(s!=0 || n!=8)throw new Exception("Code integrity query failed");return i.Options; }
    [DllImport(@"C:\dev\myfancontrol\artifacts\drivers\PawnIO-2.0.1\tools-x64\PawnIOLib.dll", CallingConvention=CallingConvention.StdCall)]
    public static extern int pawnio_open(out IntPtr handle);
    [DllImport(@"C:\dev\myfancontrol\artifacts\drivers\PawnIO-2.0.1\tools-x64\PawnIOLib.dll", CallingConvention=CallingConvention.StdCall)]
    public static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
    [DllImport(@"C:\dev\myfancontrol\artifacts\drivers\PawnIO-2.0.1\tools-x64\PawnIOLib.dll", CallingConvention=CallingConvention.StdCall)]
    public static extern int pawnio_close(IntPtr handle);
}
'@
if (-not ('PawnIoNativeUnload' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'PawnIoNativeUnload.cs') }
function Invoke-NoHardwareLoad {
    param([ValidateSet('Native','SignedLpcIO')][string]$Fixture='Native')
    $taskHandle = [IntPtr]::Zero
    $taskOpen = [PawnIoDeveloperProbe]::pawnio_open([ref]$taskHandle)
    if ($taskOpen -lt 0 -or $taskHandle -eq [IntPtr]::Zero) { throw ('pawnio_open failed: ' + $taskOpen.ToString('X8')) }
    try {
        $taskFixturePath=if($Fixture -eq 'Native'){'artifacts/pawn-modules/NoHardwareNative.bin'}else{'research/upstream/LibreHardwareMonitor/LibreHardwareMonitorLib/Resources/PawnIo/LpcIO.bin'}
        $taskBlob = [IO.File]::ReadAllBytes((Join-Path $taskRoot $taskFixturePath))
        $taskLoad = [PawnIoDeveloperProbe]::pawnio_load($taskHandle, $taskBlob, [UIntPtr]::new([uint64]$taskBlob.Length))
        return $taskLoad.ToString('X8')
    } finally {
        $taskClose = [PawnIoDeveloperProbe]::pawnio_close($taskHandle)
        if ($taskClose -lt 0) { throw ('pawnio_close failed: ' + $taskClose.ToString('X8')) }
    }
}
try {
    $taskIntegrity = [PawnIoDeveloperProbe]::Integrity()
    $taskReport.CodeIntegrityOptions = $taskIntegrity.ToString('X8')
    if (($taskIntegrity -band 2) -eq 0 -or ($taskIntegrity -band 0x400) -eq 0) { throw 'Test signing and HVCI must both be active.' }
    if ((Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Config' -Name VulnerableDriverBlocklistEnable) -ne 1) { throw 'Driver blocklist baseline differs.' }
    $taskOriginalPath = Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO' -Name ImagePath
    $taskExpectedPath = '\SystemRoot\System32\DriverStore\FileRepository\pawnio.inf_amd64_a72a2f969b8b7496\PawnIO.sys'
    if ($taskOriginalPath -ne $taskExpectedPath) { throw 'Original service path differs from reviewed baseline.' }
    $taskReport.OriginalImagePath = $taskOriginalPath
    $taskOriginalFile = Join-Path $env:SystemRoot $taskOriginalPath.Substring(12)
    if ((Get-FileHash -LiteralPath $taskOriginalFile).Hash -ne 'FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940') { throw 'Original driver hash mismatch.' }
    $taskDevFile = Join-Path $taskRoot 'artifacts/drivers/PawnIO-2.0.1/extracted/15wnIO.sys'
    if ((Get-FileHash -LiteralPath $taskDevFile).Hash -ne 'AC670293A260E48D4199A950EC6EAD52D0B6A6444B5F80920B6C27792FC69CD1') { throw 'Developer driver hash mismatch.' }
    $taskReport.ModuleSha256 = (Get-FileHash -LiteralPath (Join-Path $taskRoot 'artifacts/pawn-modules/NoHardwareNative.bin')).Hash
    if ($taskReport.ModuleSha256 -ne '190DDB35D081AE6B7B9BA80D25FCFF457AFBFCE5A78BBAA9FD442D0C7D1C4660') { throw 'No-hardware module hash mismatch.' }
    if ((Get-FileHash -LiteralPath (Join-Path $taskRoot 'research/upstream/LibreHardwareMonitor/LibreHardwareMonitorLib/Resources/PawnIo/LpcIO.bin')).Hash -ne 'B3896A1CAB0D808FCA31FE2EBCAE045D59DAC690DA87B17C858BB8DA357EB45E') { throw 'Signed control module hash mismatch.' }
    $taskPackages = @(Get-CimInstance Win32_PnPSignedDriver | Where-Object {$_.DeviceID -eq 'ROOT\PAWNIO\0000'})
    if ($taskPackages.Count -ne 1 -or $taskPackages[0].InfName -ne 'oem12.inf' -or $taskPackages[0].DriverVersion -ne '2.2.0.0') { throw 'Original PnP binding differs.' }
    $taskReport.OriginalBinding = $taskPackages[0] | Select-Object DeviceID,InfName,DriverVersion
    if ((Get-Service PawnIO).Status -ne 'Running') { throw 'Original driver is not running.' }
    $taskReport.OriginalSignedControlResult=Invoke-NoHardwareLoad 'SignedLpcIO'
    if ($taskReport.OriginalSignedControlResult -ne '00000000') { throw 'Original signed control module failed to load.' }
    $taskReport.OriginalNoHardwareLoadResult = Invoke-NoHardwareLoad
    if ($taskReport.OriginalNoHardwareLoadResult -eq '00000000') { throw 'Original driver unexpectedly accepted the invalid-signature module.' }
    $taskPnp = Join-Path $env:SystemRoot 'System32/pnputil.exe'
    $taskSc = Join-Path $env:SystemRoot 'System32/sc.exe'
    if ($RpmTrial) {
        $taskTemperatureFile=Join-Path $taskDir 'temperature-before-trial.json'
        $taskTemperatureProcess=Start-Process -FilePath (Join-Path $taskRoot 'artifacts/publish/ThinkBookControl-preview/ThinkBookControl.exe') -ArgumentList @('--probe','--samples','3','--output',('"'+$taskTemperatureFile+'"')) -WindowStyle Hidden -Wait -PassThru
        $taskReport.Commands += [pscustomobject]@{Exe='ThinkBookControl.exe';Arguments=@('--probe','--samples','3');ExitCode=$taskTemperatureProcess.ExitCode;Log='temperature-before-trial.json'}
        if ($taskTemperatureProcess.ExitCode -ne 0) { throw 'CPU temperature probe failed.' }
        $taskTemperature=Get-Content -LiteralPath $taskTemperatureFile -Raw | ConvertFrom-Json
        if (-not $taskTemperature.cpuTemperatureAvailable) { throw 'CPU temperature unavailable; no control trial.' }
        $taskCpuReadings=@($taskTemperature.samples | ForEach-Object {$_.temperatures} | Where-Object {$_.hardwareType -eq 'Cpu'})
        if ($taskCpuReadings.Count -eq 0 -or @($taskCpuReadings | Where-Object {$_.valueCelsius -ge 90}).Count -gt 0) { throw 'CPU temperature preflight failed; no control trial.' }
        $taskReport.CpuTemperaturePreflightPassed=$true
    }
    $taskReport.DisableAttempted=$true
    Invoke-DevNative $taskPnp @('/disable-device','ROOT\PAWNIO\0000') 'disable-original.txt'
    (Get-Service PawnIO).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(10))
    $taskReport.DriverPathChangeAttempted=$true
    Invoke-DevNative $taskSc @('config','PawnIO','binPath=',$taskDevFile) 'configure-developer.txt'
    Invoke-DevNative $taskSc @('start','PawnIO') 'start-developer.txt'
    (Get-Service PawnIO).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(10))
    $taskReport.DeveloperStarted=$true
    $taskReport.DeveloperImagePath=Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO' -Name ImagePath
    $taskNormalizedDevPath = if ($taskReport.DeveloperImagePath.StartsWith('\??\')) { $taskReport.DeveloperImagePath.Substring(4) } else { $taskReport.DeveloperImagePath }
    if ($taskNormalizedDevPath -ne $taskDevFile) { throw 'Developer service path mismatch.' }
    $taskReport.DeveloperSignedControlResult=Invoke-NoHardwareLoad 'SignedLpcIO'
    if ($taskReport.DeveloperSignedControlResult -ne '00000000') { throw 'Developer signed control module failed to load.' }
    $taskReport.DeveloperNoHardwareLoadResult=Invoke-NoHardwareLoad
    if ($taskReport.DeveloperNoHardwareLoadResult -ne '00000000') { throw ('Developer module load failed: '+$taskReport.DeveloperNoHardwareLoadResult) }
    $taskReport.ModuleLoadSucceeded=$true
    if ($ReadPmc2Status) {
        $taskReport.HardwareIoAttempted=$true
        $taskStatusExe=Join-Path $taskRoot 'artifacts/publish/EcPmcConfigProbe/EcPmcConfigProbe.exe'
        $taskStatusOutput=Join-Path $taskDir 'pmc2-status.json'
        Invoke-DevNative $taskStatusExe @('--read-developer-status','--output',$taskStatusOutput) 'pmc2-status-console.txt'
        $taskStatusReport=Get-Content -LiteralPath $taskStatusOutput -Raw | ConvertFrom-Json
        if (-not $taskStatusReport.Succeeded -or -not $taskStatusReport.Pmc2Status.ReadCompleted) { throw 'Fixed status probe did not succeed.' }
        $taskReport.Pmc2StatusResult=$taskStatusReport.Pmc2Status
    }
    if ($ReadPmc2Query) {
        $taskReport.HardwareIoAttempted=$true
        $taskQueryExe=Join-Path $taskRoot 'artifacts/publish/EcPmcConfigProbe/EcPmcConfigProbe.exe'
        $taskQueryOutput=Join-Path $taskDir 'pmc2-query.json'
        $taskQueryMode=if($RecoverPreviousQueryResponse){'--recover-and-read-query'}else{'--read-developer-query'}
        Invoke-DevNative $taskQueryExe @($taskQueryMode,'--output',$taskQueryOutput) 'pmc2-query-console.txt'
        $taskQueryReport=Get-Content -LiteralPath $taskQueryOutput -Raw | ConvertFrom-Json
        if (-not $taskQueryReport.Succeeded) { throw 'Fixed query probe did not succeed.' }
        $taskReport.Pmc2QuerySucceeded=$true
    }
    if ($RpmTrial) {
        $taskReport.HardwareIoAttempted=$true
        $taskTrialExe=Join-Path $taskRoot 'artifacts/publish/EcPmcConfigProbe/EcPmcConfigProbe.exe'
        Invoke-DevNative $taskTrialExe @('--rpm-trial','--output',(Join-Path $taskDir 'rpm-trial.json')) 'rpm-trial-console.txt'
        $taskReport.RpmTrialSucceeded=$true
    }
} catch {
    $taskReport.Error=$_.Exception.Message
} finally {
    if ($taskReport.DisableAttempted) {
        try {
            $taskSc=Join-Path $env:SystemRoot 'System32/sc.exe'
            $taskPnp=Join-Path $env:SystemRoot 'System32/pnputil.exe'
            if ($taskReport.DriverPathChangeAttempted) {
                $taskUnloadError=$null
                try {
                    if ((Get-Service PawnIO).Status -eq 'Running') {
                        $taskUnloadStatus=[PawnIoNativeUnload]::Unload()
                        $taskReport.DeveloperUnloadStatus=$taskUnloadStatus.ToString('X8')
                        if ($taskUnloadStatus -ne 0) { throw ('Native unload failed: '+$taskReport.DeveloperUnloadStatus) }
                        (Get-Service PawnIO).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(10))
                    }
                } catch {
                    $taskUnloadError=$_.Exception.Message
                }
                Invoke-DevNative $taskSc @('config','PawnIO','binPath=',$taskOriginalPath) 'restore-original-path.txt'
                if ($taskUnloadError) { throw ($taskUnloadError+'; original path configured for next manual reboot.') }
            }
            Invoke-DevNative $taskPnp @('/enable-device','ROOT\PAWNIO\0000') 'enable-original.txt'
            (Get-Service PawnIO).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(10))
            $taskRestoredPath=Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO' -Name ImagePath
            if ($taskRestoredPath -ne $taskOriginalPath) { throw 'Restored service path mismatch.' }
            $taskRestoredBinding=@(Get-CimInstance Win32_PnPSignedDriver | Where-Object {$_.DeviceID -eq 'ROOT\PAWNIO\0000'})
            if ($taskRestoredBinding.Count -ne 1 -or $taskRestoredBinding[0].DriverVersion -ne '2.2.0.0') { throw 'Restored PnP binding mismatch.' }
            $taskReport.RestoredImagePath=$taskRestoredPath
            $taskReport.RestoredSignedControlResult=Invoke-NoHardwareLoad 'SignedLpcIO'
            if ($taskReport.RestoredSignedControlResult -ne '00000000') { throw 'Restored signed control module failed to load.' }
            $taskReport.RestoredNoHardwareLoadResult=Invoke-NoHardwareLoad
            if ($taskReport.RestoredNoHardwareLoadResult -eq '00000000') { throw 'Restored driver unexpectedly accepts the invalid-signature module.' }
            $taskReport.OriginalRestored=$true
        } catch { $taskReport.RestoreError=$_.Exception.Message }
    }
    if ($taskReport.Error) {
        try { Get-WinEvent -LogName 'Microsoft-Windows-CodeIntegrity/Operational' -MaxEvents 15 | Select-Object TimeCreated,Id,Message | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $taskDir 'code-integrity-events.json') -Encoding utf8 } catch { }
    }
    $taskReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskDir 'report.json') -Encoding utf8
    Write-Output "Report: $taskDir"
}
if ($taskReport.Error -or $taskReport.RestoreError) { Write-Error ($taskReport.Error+' Restore: '+$taskReport.RestoreError) -ErrorAction Continue; exit 1 }
exit 0
