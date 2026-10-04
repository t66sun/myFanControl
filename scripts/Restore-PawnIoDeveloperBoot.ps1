[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskPrincipal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator required.'}
$taskDir=Join-Path $taskRoot ('artifacts/diagnostics/pawnio-boot-restore-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $taskDir|Out-Null
$taskReport=[ordered]@{CapturedUtc=[DateTime]::UtcNow.ToString('o');OriginalDriverVerified=$false;TestSigningElementRemoved=$false;ManualRebootRequired=$true;AutomaticReboot=$false;Error=$null}
try {
    $taskGuid='{64290bc4-929d-11f1-9754-f7968b89ab03}'
    $taskBaseline=Get-Content -LiteralPath (Join-Path $taskRoot 'artifacts/diagnostics/pawnio-bootstrap-20261002-143256-127/bcd-current-before.txt') -Raw
    if(-not $taskBaseline.Contains($taskGuid) -or $taskBaseline -match '(?m)^testsigning\s'){throw 'Original boot snapshot differs.'}
    $taskPath=Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO' -Name ImagePath
    if($taskPath -ne '\SystemRoot\System32\DriverStore\FileRepository\pawnio.inf_amd64_a72a2f969b8b7496\PawnIO.sys' -or (Get-Service PawnIO).Status -ne 'Running'){throw 'Original driver must be restored first.'}
    if((Get-FileHash -LiteralPath (Join-Path $env:SystemRoot $taskPath.Substring(12))).Hash -ne 'FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940'){throw 'Original driver hash mismatch.'}
    $taskBinding=@(Get-CimInstance Win32_PnPSignedDriver|Where-Object {$_.DeviceID -eq 'ROOT\PAWNIO\0000'})
    if($taskBinding.Count -ne 1 -or $taskBinding[0].InfName -ne 'oem12.inf' -or $taskBinding[0].DriverVersion -ne '2.2.0.0'){throw 'Original device binding differs.'}
    $taskReport.OriginalDriverVerified=$true
    $taskBcd=Join-Path $env:SystemRoot 'System32/bcdedit.exe'
    $taskBefore=@(& $taskBcd /enum '{current}' /v);if($LASTEXITCODE -ne 0){throw 'BCD read failed.'}
    $taskBefore|Set-Content -LiteralPath (Join-Path $taskDir 'bcd-before.txt') -Encoding utf8
    if(-not ($taskBefore -join "`n").Contains($taskGuid)){throw 'Current boot identity changed.'}
    if(($taskBefore -join "`n") -match '(?m)^testsigning\s') {
        & $taskBcd /deletevalue $taskGuid testsigning |Set-Content -LiteralPath (Join-Path $taskDir 'delete-test-signing.txt') -Encoding utf8
        if($LASTEXITCODE -ne 0){throw 'BCD element removal failed.'}
    }
    $taskAfter=@(& $taskBcd /enum $taskGuid /v);if($LASTEXITCODE -ne 0){throw 'BCD readback failed.'}
    $taskAfter|Set-Content -LiteralPath (Join-Path $taskDir 'bcd-after.txt') -Encoding utf8
    if(($taskAfter -join "`n") -match '(?m)^testsigning\s'){throw 'Test signing still present in boot configuration.'}
    $taskReport.TestSigningElementRemoved=$true
    $taskReport.HvciRegistryEnabled=Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' -Name Enabled
    $taskReport.BlocklistRegistryEnabled=Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Config' -Name VulnerableDriverBlocklistEnable
    if($taskReport.HvciRegistryEnabled -ne 1 -or $taskReport.BlocklistRegistryEnabled -ne 1){throw 'Security baseline differs.'}
} catch {$taskReport.Error=$_.Exception.Message}
finally {$taskReport|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $taskDir 'report.json') -Encoding utf8;Write-Output "Report: $taskDir"}
if($taskReport.Error){Write-Error $taskReport.Error -ErrorAction Continue;exit 1}
exit 0
