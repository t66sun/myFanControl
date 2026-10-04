[CmdletBinding()]
param([switch]$PlanOnly,[string]$OutputPath=(Join-Path $PSScriptRoot '../artifacts/diagnostics/vpc-mode-roundtrip.json'))
$ErrorActionPreference='Stop'
if ($PlanOnly) {
    [pscustomobject]@{Retired=$true;WritesExecuted=$false;Reason='Firmware command table lacks 0x22/0x2B; prior readback may be mailbox echo.'}
    return
}
throw 'VPC fan experiment retired: mailbox echo does not prove fan control. See research/VPC_FAN_FINDINGS.md.'
