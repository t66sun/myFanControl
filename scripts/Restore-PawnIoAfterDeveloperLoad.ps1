[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator token required.' }
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskDir=Join-Path $taskRoot ('artifacts/diagnostics/pawnio-dev-restore-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $taskDir | Out-Null
$taskOriginal='\SystemRoot\System32\DriverStore\FileRepository\pawnio.inf_amd64_a72a2f969b8b7496\PawnIO.sys'
$taskDev=Join-Path $taskRoot 'artifacts/drivers/PawnIO-2.0.1/extracted/15wnIO.sys'
$taskReport=[ordered]@{CapturedUtc=[DateTime]::UtcNow.ToString('o');HardwareIoAttempted=$false;UnloadStatus=$null;OriginalPathConfigured=$false;DeviceEnabled=$false;OriginalRestored=$false;Error=$null}
if (-not ('PawnIoNativeUnload' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'PawnIoNativeUnload.cs') }try {
 $taskCurrent=Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO' -Name ImagePath
 $taskReport.BeforeImagePath=$taskCurrent
 $taskNormalized=if($taskCurrent.StartsWith('\??\')){$taskCurrent.Substring(4)}else{$taskCurrent}
 if($taskNormalized -ne $taskDev -and $taskCurrent -ne $taskOriginal){throw 'Unexpected service path; not changing an unreviewed driver.'}
 if((Get-FileHash -LiteralPath (Join-Path $env:SystemRoot $taskOriginal.Substring(12))).Hash -ne 'FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940'){throw 'Original driver hash mismatch.'}
 if((Get-Service PawnIO).Status -eq 'Running' -and $taskNormalized -eq $taskDev){
   $taskStatus=[PawnIoNativeUnload]::Unload();$taskReport.UnloadStatus=$taskStatus.ToString('X8')
 }
 & (Join-Path $env:SystemRoot 'System32/sc.exe') config PawnIO 'binPath=' $taskOriginal | Out-String | Set-Content -LiteralPath (Join-Path $taskDir 'restore-path.txt') -Encoding utf8
 if($LASTEXITCODE -ne 0){throw 'Failed to restore original service path.'}
 $taskReport.OriginalPathConfigured=$true
 if($taskReport.UnloadStatus -and $taskReport.UnloadStatus -ne '00000000'){throw ('Driver unload failed: '+$taskReport.UnloadStatus+'. Original path configured for next manual reboot.')}
 & (Join-Path $env:SystemRoot 'System32/pnputil.exe') /enable-device 'ROOT\PAWNIO\0000' | Out-String | Set-Content -LiteralPath (Join-Path $taskDir 'enable-original.txt') -Encoding utf8
 if($LASTEXITCODE -ne 0){throw 'Failed to enable original device.'}
 $taskReport.DeviceEnabled=$true
 (Get-Service PawnIO).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(10))
 $taskReport.AfterImagePath=Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO' -Name ImagePath
 $taskReport.AfterBinding=@(Get-CimInstance Win32_PnPSignedDriver | Where-Object {$_.DeviceID -eq 'ROOT\PAWNIO\0000'} | Select-Object DeviceID,InfName,DriverVersion)
 if($taskReport.AfterImagePath -ne $taskOriginal -or $taskReport.AfterBinding.Count -ne 1 -or $taskReport.AfterBinding[0].DriverVersion -ne '2.2.0.0'){throw 'Original binding/path validation failed.'}
 $taskReport.OriginalRestored=$true
}catch{$taskReport.Error=$_.Exception.Message}
$taskReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskDir 'report.json') -Encoding utf8
Write-Output "Report: $taskDir"
if($taskReport.Error){Write-Error $taskReport.Error -ErrorAction Continue;exit 1}
exit 0
