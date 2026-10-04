[CmdletBinding()]
param([string]$OutputPath=(Join-Path $PSScriptRoot '../artifacts/diagnostics/vpc-fan-state.json'))
$ErrorActionPreference='Stop'
throw 'VPC fan query retired: firmware may return stale mailbox data. See research/VPC_FAN_FINDINGS.md.'
