[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskPrincipal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run this script from Administrator PowerShell.'}
$taskDir=Join-Path $taskRoot ('artifacts/diagnostics/product-final-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $taskDir | Out-Null
$taskExe=Join-Path $taskRoot 'artifacts/publish/myFanControl/myFanControl.exe'
$taskReport=[ordered]@{CapturedUtc=[DateTime]::UtcNow.ToString('o');WindowCloseVerified=$false;SleepResumeVerified=$false;BootSettingsRestored=$false;AutomaticReboot=$false;Error=$null}
try {
 $taskWindowFile=Join-Path $taskDir 'window-close.json'
 $taskProcess=Start-Process -FilePath $taskExe -ArgumentList @('--window-test','--output',('"'+$taskWindowFile+'"')) -WindowStyle Hidden -Wait -PassThru
 if($taskProcess.ExitCode -ne 0 -or -not (Get-Content -LiteralPath $taskWindowFile -Raw | ConvertFrom-Json).Succeeded){throw 'Window close recovery did not pass; see window-close.json.'}
 $taskReport.WindowCloseVerified=$true
 Write-Output 'The next window prepares manual sleep testing. Wait for its sleep-test title, use Windows Sleep, wait about 20 seconds, wake, then close the window after the result.'
 $taskSleepFile=Join-Path $taskDir 'sleep-resume.json'
 $taskProcess=Start-Process -FilePath $taskExe -ArgumentList @('--sleep-test','--output',('"'+$taskSleepFile+'"')) -WindowStyle Normal -Wait -PassThru
 if($taskProcess.ExitCode -ne 0 -or -not (Get-Content -LiteralPath $taskSleepFile -Raw | ConvertFrom-Json).Succeeded){throw 'Sleep/resume recovery not verified; boot settings were not changed.'}
 $taskReport.SleepResumeVerified=$true
 $taskRestore=Join-Path $PSScriptRoot 'Restore-PawnIoDeveloperBoot.ps1'
 $taskProcess=Start-Process -FilePath powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$taskRestore+'"')) -WindowStyle Hidden -Wait -PassThru
 if($taskProcess.ExitCode -ne 0){throw 'Boot restoration failed; inspect pawnio-boot-restore diagnostics.'}
 $taskReport.BootSettingsRestored=$true
 Write-Output 'Final pre-reboot checks passed. Save your work and reboot manually; no automatic reboot will run.'
} catch {$taskReport.Error=$_.Exception.Message}
finally {$taskReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskDir 'report.json') -Encoding utf8; Write-Output ('Report: '+$taskDir)}
if($taskReport.Error){Write-Error $taskReport.Error -ErrorAction Continue;exit 1}
