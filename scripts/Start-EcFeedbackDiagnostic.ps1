[CmdletBinding()]
param([switch]$PlanOnly)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskExe=Join-Path $taskRoot 'artifacts/publish/EcFeedbackProbe/EcFeedbackProbe.exe'
$taskOutput=Join-Path $taskRoot 'artifacts/diagnostics/ec-feedback-admin.json'
$taskIdentity=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $taskIsAdministrator=([Security.Principal.WindowsPrincipal]::new($taskIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} finally { $taskIdentity.Dispose() }
if($PlanOnly) {
    [pscustomobject]@{
        Executable=$taskExe; Arguments=@('--sample-feedback','--output',$taskOutput)
        RequiresUac=(-not $taskIsAdministrator); ExecutionIsAdministrator=$taskIsAdministrator; LaunchesExecuted=$false; Samples=20; DelayBetweenSamplesMs=500
        TargetReads=@('2000/2001','1060 (must remain 00)','0800-080F twice','8800-880F twice')
        FanPayloadWrites=$false; MappingChanges=$false
        PriorIdentityAuthorizationDoesNotCoverThisRun=$true
    }
    return
}
if(-not(Test-Path -LiteralPath $taskExe)){throw 'Publish the fixed feedback diagnostic first.'}
if(Test-Path -LiteralPath $taskOutput){throw 'Diagnostic evidence already exists; no automatic rerun or overwrite.'}
# Separate explicit authorization is required. Invoke once, never retry automatically.
$taskLaunch=@{
    FilePath=$taskExe
    ArgumentList=@('--sample-feedback','--output',('"'+$taskOutput+'"'))
    WindowStyle='Hidden'; WorkingDirectory=$taskRoot; PassThru=$true; Wait=$true
}
if(-not $taskIsAdministrator){$taskLaunch.Verb='RunAs'}
$taskRun=Start-Process @taskLaunch
[pscustomobject]@{ExitCode=$taskRun.ExitCode;Output=$taskOutput}
