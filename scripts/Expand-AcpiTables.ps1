[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/acpi'),
    [string]$AcpiToolDirectory = (Join-Path $PSScriptRoot '../.tools/acpica')
)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$toolPath = [IO.Path]::GetFullPath($AcpiToolDirectory)
& (Join-Path $PSScriptRoot 'Export-AcpiTables.ps1') -OutputDirectory $outputPath
$manifest = Get-Content -LiteralPath (Join-Path $outputPath 'manifest.json') -Raw | ConvertFrom-Json
$expectedSsdtCount = ($manifest.Tables | Where-Object Signature -eq SSDT).EnumeratedCount
Push-Location $outputPath
try {
    # ACPICA's Windows implementation can enumerate duplicate SSDTs.
    # Never dump all tables: MSDM may contain a Windows product key.
    & (Join-Path $toolPath 'acpidump.exe') -b -n SSDT *> (Join-Path $outputPath 'acpidump-ssdt.log')
    $dumpExitCode = $LASTEXITCODE
} finally { Pop-Location }
$files = @(Get-ChildItem -LiteralPath $outputPath -Filter '*.dat' | Where-Object { $_.Name -match '^(DSDT|ssdt\d*)\.dat$' })
$records = foreach ($file in $files) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($bytes.Length -lt 36) { throw "Invalid header: $($file.Name)" }
    $signature = [Text.Encoding]::ASCII.GetString($bytes, 0, 4)
    if ($signature -notin @('DSDT', 'SSDT')) { throw "Unexpected signature: $($file.Name)" }
    if ([BitConverter]::ToUInt32($bytes, 4) -ne $bytes.Length) { throw "Invalid length: $($file.Name)" }
    $sum = 0
    foreach ($value in $bytes) { $sum = ($sum + $value) -band 255 }
    if ($sum -ne 0) { throw "Invalid checksum: $($file.Name)" }
    [pscustomobject]@{File=$file.Name; Signature=$signature; Bytes=$bytes.Length; Sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}
}
$uniqueSsdt = @($records | Where-Object Signature -eq SSDT | Select-Object -ExpandProperty Sha256 -Unique)
if ($uniqueSsdt.Count -ne $expectedSsdtCount) { throw "SSDT count mismatch: expected $expectedSsdtCount, exported $($uniqueSsdt.Count). Inspect acpidump-ssdt.log." }
foreach ($file in $files) {
    & (Join-Path $toolPath 'iasl.exe') -d $file.FullName *> ($file.FullName + '.log')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($file.FullName, '.dsl')))) {
        throw "Disassembly failed: $($file.Name)"
    }
}
[pscustomobject]@{
    CapturedUtc=[DateTime]::UtcNow.ToString('o')
    ReadOnly=$true
    AcpiDumpExitCode=$dumpExitCode
    ExpectedSsdtCount=$expectedSsdtCount
    UniqueSsdtCount=$uniqueSsdt.Count
    Tables=@($records)
    Limitations=@('Tables disassembled separately. External references may remain unresolved. Disassembly does not prove a method is present or callable in the live ACPI namespace.')
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'expanded-manifest.json') -Encoding utf8
Write-Output "Validated DSDT and $($uniqueSsdt.Count) unique SSDTs."
