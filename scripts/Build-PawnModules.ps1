[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskCompiler=Join-Path $taskRoot '.tools/pawn-compiler/bin/pawncc.exe'
if(-not(Test-Path -LiteralPath $taskCompiler)){throw 'Extract the official compiler with Extract-PawnCompiler.py first.'}
Push-Location $taskRoot
try {
 New-Item -ItemType Directory -Path 'artifacts/pawn-modules' -Force | Out-Null
 & $taskCompiler 'src/PawnIO/NoHardwareMain.p' '-C64' '-O1' '-p' '-oartifacts/pawn-modules/NoHardwareMain.amx'
 if($LASTEXITCODE -ne 0){throw 'NoHardwareMain compilation failed.'}
 & $taskCompiler 'src/PawnIO/Pmc2Status.p' '-iresearch/upstream/PawnIO.Modules/include' '-C64' '-O1' '-p' '-oartifacts/pawn-modules/Pmc2Status.amx'
 if($LASTEXITCODE -ne 0){throw 'Pmc2Status compilation failed.'}
 & $taskCompiler 'src/PawnIO/NoHardwareNative.p' '-iresearch/upstream/PawnIO.Modules/include' '-C64' '-O1' '-p' '-oartifacts/pawn-modules/NoHardwareNative.amx'
 if($LASTEXITCODE -ne 0){throw 'NoHardwareNative compilation failed.'}
 & $taskCompiler 'src/PawnIO/Pmc2Query.p' '-iresearch/upstream/PawnIO.Modules/include' '-C64' '-O1' '-p' '-oartifacts/pawn-modules/Pmc2Query.amx'
 if($LASTEXITCODE -ne 0){throw 'Pmc2Query compilation failed.'}
 & $taskCompiler 'src/PawnIO/Pmc2Control.p' '-iresearch/upstream/PawnIO.Modules/include' '-C64' '-O1' '-p' '-oartifacts/pawn-modules/Pmc2Control.amx'
 if($LASTEXITCODE -ne 0){throw 'Pmc2Control compilation failed.'}
 foreach($taskName in @('NoHardwareMain','Pmc2Status','NoHardwareNative','Pmc2Query','Pmc2Control')) {
  $taskAmx=[IO.File]::ReadAllBytes((Join-Path $taskRoot "artifacts/pawn-modules/$taskName.amx"))
  if($taskAmx.Length -lt 60 -or [BitConverter]::ToUInt16($taskAmx,4) -ne 0xF1E1 -or $taskAmx[6] -ne 11){throw 'Unexpected AMX ABI.'}
  $taskAmxSize=[BitConverter]::ToUInt32($taskAmx,0)
  if($taskAmxSize -gt $taskAmx.Length -or $taskAmxSize -lt 60){throw 'Invalid AMX size.'}
  $taskBlob=New-Object byte[] (516+$taskAmxSize)
  [BitConverter]::GetBytes([uint32]512).CopyTo($taskBlob,0)
  [Array]::Copy($taskAmx,0,$taskBlob,516,$taskAmxSize)
  [IO.File]::WriteAllBytes((Join-Path $taskRoot "artifacts/pawn-modules/$taskName.bin"),$taskBlob)
 }
 Get-FileHash -LiteralPath 'artifacts/pawn-modules/NoHardwareMain.bin','artifacts/pawn-modules/Pmc2Status.bin','artifacts/pawn-modules/NoHardwareNative.bin','artifacts/pawn-modules/Pmc2Query.bin' -Algorithm SHA256
}finally{Pop-Location}
