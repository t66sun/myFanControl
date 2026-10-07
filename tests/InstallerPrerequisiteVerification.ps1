$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/PawnIoPrerequisite.ps1')
$taskChecks=0
function Assert-Decision([int]$Expected,[bool]$Registered,[bool]$DriverExists,[string]$Version,[string]$Status,[string]$Signer) {
    $taskActual=Get-PawnIoPrerequisiteDecision -Registered $Registered -DriverExists $DriverExists -Version $Version -SignatureStatus $Status -SignerSubject $Signer
    if($taskActual -ne $Expected){throw "Expected state $Expected, got $taskActual for version $Version / signature $Status"}
    $script:taskChecks++
}
$taskMicrosoft='CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation'
Assert-Decision 10 $false $false '' '' ''
Assert-Decision 10 $true $false '' '' ''
Assert-Decision 0 $true $true '2.2.0' 'Valid' $taskMicrosoft
Assert-Decision 0 $true $true '2.3.0' 'Valid' $taskMicrosoft
Assert-Decision 10 $true $true '2.1.0' 'Valid' $taskMicrosoft
Assert-Decision 20 $true $true '2.2.0' 'Valid' 'CN=namazso.eu'
Assert-Decision 20 $true $true '2.2.0' 'NotSigned' $taskMicrosoft
Assert-Decision 20 $true $true 'unknown' 'Valid' $taskMicrosoft
$taskHash='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
foreach($taskCase in @(
    @{Actual=$taskHash.ToUpperInvariant(); Status='Valid'; Expected=$true},
    @{Actual='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; Status='Valid'; Expected=$false},
    @{Actual=$taskHash; Status='HashMismatch'; Expected=$false}
)) {
    if((Test-PawnIoInstallerTrust -ActualHash $taskCase.Actual -ExpectedHash $taskHash -SignatureStatus $taskCase.Status) -ne $taskCase.Expected){throw 'Installer trust check failed'}
    $taskChecks++
}
Write-Output "$taskChecks offline prerequisite checks passed. No registry, driver, or installer was accessed."
