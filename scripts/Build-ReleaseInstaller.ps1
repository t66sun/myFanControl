[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version='0.2.0',
    [string]$IsccPath,
    [string]$ReleaseDirectory='artifacts/releases'
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskReleaseDir=[IO.Path]::GetFullPath($ReleaseDirectory,$taskRoot)
$taskName='myFanControl-v'+$Version+'-win-x64'
$taskZip=Join-Path $taskReleaseDir ($taskName+'.zip')
$taskOutput=Join-Path $taskReleaseDir ($taskName+'-setup.exe')
$taskChecksum=Join-Path $taskReleaseDir 'SHA256SUMS.txt'
if(-not (Test-Path -LiteralPath $taskZip)){throw "Release ZIP missing: $taskZip"}
if(Test-Path -LiteralPath $taskOutput){throw "Installer already exists: $taskOutput"}
$taskExpected=@(Get-Content -LiteralPath $taskChecksum | Where-Object {$_ -match ('^([0-9a-fA-F]{64})  '+[regex]::Escape($taskName+'.zip')+'$')} | ForEach-Object {$Matches[1]})
if($taskExpected.Count -ne 1){throw 'Release ZIP must have exactly one SHA256SUMS.txt entry.'}
$taskActual=(Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash
if($taskActual -ne $taskExpected[0]){throw 'Release ZIP checksum mismatch.'}
if(-not $IsccPath){$IsccPath=Join-Path $taskRoot '.tools/innosetup/ISCC.exe'}
if(-not (Test-Path -LiteralPath $IsccPath)){throw "Inno Setup compiler missing: $IsccPath"}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskArchive=[IO.Compression.ZipFile]::OpenRead($taskZip)
try {
    foreach($taskEntry in $taskArchive.Entries){
        $taskParts=$taskEntry.FullName.Split('/')
        if($taskParts[0] -ne 'myFanControl' -or $taskParts -contains '..' -or $taskEntry.FullName.Contains('\') -or $taskEntry.FullName.StartsWith('/')){
            throw "Unexpected ZIP path: $($taskEntry.FullName)"
        }
    }
    foreach($taskRequired in @('myFanControl/myFanControl.exe','myFanControl/EcPmcConfigProbe.exe','myFanControl/README.md')){
        if($null -eq $taskArchive.GetEntry($taskRequired)){throw "Release ZIP missing $taskRequired"}
    }
    if($null -ne $taskArchive.GetEntry('myFanControl/control-settings.json')){throw 'Release ZIP contains personal settings.'}
    if(-not @($taskArchive.Entries | Where-Object {$_.FullName.StartsWith('myFanControl/licenses/')}).Count){throw 'Release ZIP has no licenses.'}
} finally {$taskArchive.Dispose()}

$taskStageRoot=Join-Path $taskRoot 'artifacts/installer-staging'
$taskStage=Join-Path $taskStageRoot ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($taskZip,$taskStage)
    $taskPayload=Join-Path $taskStage 'myFanControl'
    & $IsccPath '--quiet' ('--define=SourceDir='+$taskPayload) ('--define=AppVersion='+$Version) ('--output-dir='+$taskReleaseDir) ('--output-filename='+$taskName+'-setup') (Join-Path $PSScriptRoot 'ReleaseInstaller.iss')
    if($LASTEXITCODE -ne 0){throw "Inno Setup compilation failed: $LASTEXITCODE"}
    if(-not (Test-Path -LiteralPath $taskOutput)){throw 'Installer output missing.'}
    $taskOutputHash=(Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $taskReleaseDir ($taskName+'-setup.sha256')),($taskOutputHash+'  '+[IO.Path]::GetFileName($taskOutput)+"`n"),[Text.UTF8Encoding]::new($false))
    Get-Item -LiteralPath $taskOutput | Select-Object FullName,Length
    Write-Output ('SHA256='+$taskOutputHash)
} finally {
    $taskVerifiedRoot=[IO.Path]::GetFullPath($taskStageRoot).TrimEnd('\')+'\'
    $taskVerifiedStage=[IO.Path]::GetFullPath($taskStage)
    if(-not $taskVerifiedStage.StartsWith($taskVerifiedRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe staging cleanup path.'}
    Remove-Item -LiteralPath $taskVerifiedStage -Recurse -Force
}
