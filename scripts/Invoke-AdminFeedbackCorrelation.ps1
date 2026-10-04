[CmdletBinding()]
param([switch]$PlanOnly)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskDir=Join-Path $taskRoot 'artifacts/diagnostics'
$taskFeedbackExe=Join-Path $taskRoot 'artifacts/publish/EcFeedbackProbe/EcFeedbackProbe.exe'
$taskTempExe=Join-Path $taskRoot 'artifacts/publish/myFanControl-preview/myFanControl.exe'
$taskFeedback=Join-Path $taskDir 'ec-feedback-mode-correlation.json'
$taskTemp=Join-Path $taskDir 'temperature-admin-correlation.json'
$taskModes=Join-Path $taskDir 'thermal-mode-correlation.json'
$taskReport=Join-Path $taskDir 'admin-feedback-correlation-run.json'
$taskPsExe=Join-Path $PSHOME 'pwsh.exe'
if(-not(Test-Path -LiteralPath $taskPsExe)){$taskPsExe=Join-Path $PSHOME 'powershell.exe'}
if($PlanOnly){
    [pscustomobject]@{Samples=60;SampleDelayMs=500;TemperatureSamples=3;QuietHoldSeconds=12;RestoreHoldSeconds=3;ManualFanWrites=$false;MappingChanges=$false;Output=@($taskTemp,$taskFeedback,$taskModes,$taskReport);LaunchesExecuted=$false}
    return
}
foreach($taskPath in @($taskTemp,$taskFeedback,$taskModes,$taskReport)){
    if(Test-Path -LiteralPath $taskPath){throw 'Existing experiment evidence; no rerun or overwrite.'}
}
$taskWho=[Security.Principal.WindowsIdentity]::GetCurrent()
try{$taskAdmin=([Security.Principal.WindowsPrincipal]::new($taskWho)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)}finally{$taskWho.Dispose()}
if(-not $taskAdmin){throw 'This worker must already be elevated; it never requests UAC.'}
$taskBios=Get-ItemProperty -LiteralPath 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS'
if($taskBios.SystemProductName -ne '21CX' -or $taskBios.BIOSVersion -ne 'HYCN42WW'){throw 'Unsupported machine/BIOS.'}
$taskFeedbackHash=(Get-FileHash -LiteralPath (Join-Path $taskRoot 'artifacts/publish/EcFeedbackProbe/EcFeedbackProbe.dll') -Algorithm SHA256).Hash
$taskTempHash=(Get-FileHash -LiteralPath (Join-Path $taskRoot 'artifacts/publish/myFanControl-preview/myFanControl.dll') -Algorithm SHA256).Hash
if($taskFeedbackHash -ne '00E5020C9832D3D28D0CA8666EB60A43E648D92B0BFFC1DDC37F00CBDC16A64F' -or $taskTempHash -ne 'CB5A83ED63AEC20C11A867FE8BD5058AAF4D252E37BBA4FA5A6A96977925AB6D'){throw 'Unchecked binary; no experiment.'}
$taskOutputStream=[IO.File]::Open($taskReport,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
$taskErrors=[Collections.Generic.List[string]]::new()
$taskFeedbackProc=$null;$taskFeedbackExit=$null;$taskTempExit=$null;$taskModeExit=$null
$taskStarted=[DateTimeOffset]::UtcNow
try{
    $taskTempProc=Start-Process -FilePath $taskTempExe -ArgumentList @('--probe','--samples','3','--output',('"'+$taskTemp+'"')) -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru -Wait
    $taskTempExit=$taskTempProc.ExitCode
    $taskFeedbackProc=Start-Process -FilePath $taskFeedbackExe -ArgumentList @('--sample-feedback-long','--output',('"'+$taskFeedback+'"')) -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
    $taskReadyDeadline=[DateTime]::UtcNow.AddSeconds(5)
    while(-not(Test-Path -LiteralPath $taskFeedback) -and -not $taskFeedbackProc.HasExited -and [DateTime]::UtcNow -lt $taskReadyDeadline){Start-Sleep -Milliseconds 100}
    if($taskFeedbackProc.HasExited -or -not(Test-Path -LiteralPath $taskFeedback)){throw 'Feedback sampling did not start; no mode changes.'}
    Start-Sleep -Seconds 2
    $taskModeProc=Start-Process -FilePath $taskPsExe -ArgumentList @('-NoProfile','-File',('"'+(Join-Path $taskRoot 'scripts/Test-LenovoThermalModes.ps1')+'"'),'-HoldSeconds','12','-OutputPath',('"'+$taskModes+'"')) -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru -Wait
    $taskModeExit=$taskModeProc.ExitCode
    if($taskModeExit -ne 0){$taskErrors.Add('Mode roundtrip failed; inspect its restoration record.')}
    if(-not $taskFeedbackProc.WaitForExit(90000)){throw 'Feedback observation timeout; child may still be live, do not relaunch.'}
    $taskFeedbackExit=$taskFeedbackProc.ExitCode
    if($taskFeedbackExit -ne 0){$taskErrors.Add('Feedback read did not complete.')}
}catch{$taskErrors.Add($_.Exception.Message)}
finally{
    $taskLive=$false
    if($null -ne $taskFeedbackProc){$taskLive=-not $taskFeedbackProc.HasExited;if(-not $taskLive){$taskFeedbackExit=$taskFeedbackProc.ExitCode}}
    $taskRecord=[pscustomobject]@{SchemaVersion=1;StartedUtc=$taskStarted.ToString('O');CompletedUtc=[DateTimeOffset]::UtcNow.ToString('O');WorkerIsAdministrator=$taskAdmin;TemperatureExitCode=$taskTempExit;FeedbackExitCode=$taskFeedbackExit;ModeExitCode=$taskModeExit;FeedbackProcessId=if($taskFeedbackProc){$taskFeedbackProc.Id}else{$null};FeedbackProcessStillLive=$taskLive;ManualFanWrites=$false;MappingChanges=$false;PhysicalRpmVerified=$false;Errors=@($taskErrors.ToArray())}
    $taskWriter=[IO.StreamWriter]::new($taskOutputStream,[Text.UTF8Encoding]::new($false));try{$taskWriter.Write(($taskRecord|ConvertTo-Json -Depth 6))}finally{$taskWriter.Dispose()}
}
if($taskErrors.Count -gt 0){exit 2}
