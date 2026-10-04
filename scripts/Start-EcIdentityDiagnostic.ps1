[CmdletBinding()]
param([switch]$PlanOnly)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskExe=Join-Path $taskRoot 'artifacts/publish/EcIdentityProbe/EcIdentityProbe.exe'
$taskOutput=Join-Path $taskRoot 'artifacts/diagnostics/ec-identity-admin.json'
if($PlanOnly) {
    [pscustomobject]@{Executable=$taskExe;Arguments=@('--read-identity','--output',$taskOutput);RequiresUac=$true;LaunchesExecuted=$false}
    return
}
if(-not(Test-Path -LiteralPath $taskExe)){throw 'Build the identity diagnostic first.'}
# Invocation is a deliberate one-time UAC request; never retry automatically.
$taskRun=Start-Process -FilePath $taskExe -ArgumentList @('--read-identity','--output',('"'+$taskOutput+'"')) -Verb RunAs -WindowStyle Hidden -WorkingDirectory $taskRoot -PassThru -Wait
[pscustomobject]@{ExitCode=$taskRun.ExitCode;Output=$taskOutput}
