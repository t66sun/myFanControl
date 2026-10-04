[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskPrincipal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator required.'}
$taskDir=Join-Path $taskRoot ('artifacts/diagnostics/modern-standby-repair-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $taskDir | Out-Null
$taskReport=[ordered]@{CapturedUtc=[DateTime]::UtcNow.ToString('o');OldTestStopped=$false;ZeroOverridesVerified=$false;ProductUpdated=$false;Error=$null}
try {
 Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power';Id=506,507;StartTime=[DateTime]::Parse('2026-10-03T03:58:00Z')} -MaxEvents 20 | Select-Object TimeCreated,Id,RecordId,Message | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskDir 'actual-modern-standby-events.json') -Encoding utf8
 $taskOld=Get-CimInstance Win32_Process -Filter 'ProcessId=23124'
 if($taskOld){
  if($taskOld.ExecutablePath -ne (Join-Path $taskRoot 'artifacts/publish/myFanControl/myFanControl.exe') -or -not $taskOld.CommandLine.Contains('--sleep-test') -or -not $taskOld.CommandLine.Contains('product-final-20261003-035810-264')){throw 'Old test process identity mismatch; not stopped.'}
  Stop-Process -Id 23124
  Start-Sleep -Seconds 2
 }
 $taskReport.OldTestStopped=$true
 $taskSource=Join-Path $taskRoot 'artifacts/publish/myFanControl-modern-standby'
 $taskProbe=Join-Path $taskSource 'control-host/EcPmcConfigProbe.exe'
 $taskBaseline=Join-Path $taskDir 'overrides-after-old-test.json'
 & $taskProbe --read-override-baseline --output $taskBaseline
 if($LASTEXITCODE -ne 0){throw 'Post-test baseline unavailable; no product replacement.'}
 $taskData=(Get-Content -LiteralPath $taskBaseline -Raw | ConvertFrom-Json).Result
 if($taskData.RawCandidateOverride0C -ne 0 -or $taskData.RawCandidateOverride0D -ne 0 -or $taskData.RawCandidateOverride0E -ne 0 -or $taskData.RawCandidateOverride0F -ne 0){throw 'Overrides are not zero; stop here.'}
 $taskReport.ZeroOverridesVerified=$true
 Copy-Item -Path (Join-Path $taskSource '*') -Destination (Join-Path $taskRoot 'artifacts/publish/myFanControl') -Recurse -Force
 $taskReport.ProductUpdated=$true
} catch {$taskReport.Error=$_.Exception.Message}
finally {$taskReport | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskDir 'report.json') -Encoding utf8}
if($taskReport.Error){Write-Error $taskReport.Error -ErrorAction Continue;exit 1}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-ProductFinalChecks.ps1')
exit $LASTEXITCODE
