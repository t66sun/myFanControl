[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskPrincipal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator required.'}
$taskDir=Join-Path $taskRoot ('artifacts/diagnostics/signed-rpm-trial-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $taskDir | Out-Null
$taskReport=[ordered]@{CapturedUtc=[DateTime]::UtcNow.ToString('o');DriverChanged=$false;BootChanged=$false;Succeeded=$false;Error=$null}
try {
 $taskPath=Get-ItemPropertyValue 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO' -Name ImagePath
 if($taskPath -ne '\SystemRoot\System32\DriverStore\FileRepository\pawnio.inf_amd64_a72a2f969b8b7496\PawnIO.sys' -or (Get-Service PawnIO).Status -ne 'Running'){throw 'Original driver baseline mismatch.'}
 $taskReport.DriverPath=$taskPath
 $taskTemperatureFile=Join-Path $taskDir 'temperature-before-trial.json'
 $taskTemperatureProcess=Start-Process -FilePath (Join-Path $taskRoot 'artifacts/publish/myFanControl-preview/myFanControl.exe') -ArgumentList @('--probe','--samples','3','--output',('"'+$taskTemperatureFile+'"')) -WindowStyle Hidden -Wait -PassThru
 if($taskTemperatureProcess.ExitCode -ne 0){throw 'Temperature probe failed.'}
 $taskTemperature=Get-Content -LiteralPath $taskTemperatureFile -Raw | ConvertFrom-Json
 $taskCpuReadings=@($taskTemperature.samples | ForEach-Object {$_.temperatures} | Where-Object {$_.hardwareType -eq 'Cpu'})
 if(-not $taskTemperature.cpuTemperatureAvailable -or $taskCpuReadings.Count -eq 0 -or @($taskCpuReadings | Where-Object {$_.valueCelsius -ge 90}).Count -gt 0){throw 'CPU temperature preflight failed.'}
 $taskExe=Join-Path $taskRoot 'artifacts/publish/EcPmcConfigProbe/EcPmcConfigProbe.exe'
 & $taskExe --signed-rpm-trial --output (Join-Path $taskDir 'rpm-trial.json')
 if($LASTEXITCODE -ne 0){throw 'Signed RPM trial incomplete; see rpm-trial.json.'}
 $taskReport.Succeeded=$true
} catch {$taskReport.Error=$_.Exception.Message}
finally {$taskReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskDir 'report.json') -Encoding utf8}
if(-not $taskReport.Succeeded){exit 1}
