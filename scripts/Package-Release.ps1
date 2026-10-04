[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [ValidatePattern('^[A-Za-z0-9-]+$')][string]$SelfContainedDirectory='myFanControl-compact',
    [ValidatePattern('^[A-Za-z0-9-]+$')][string]$FrameworkDependentDirectory='myFanControl-framework-dependent'
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskReleaseDirectory=Join-Path $taskRoot 'artifacts/releases'
New-Item -ItemType Directory -Path $taskReleaseDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

$taskPackages=@(
    [pscustomobject]@{Directory=$SelfContainedDirectory;Name=('myFanControl-v'+$Version+'-win-x64.zip');SelfContained=$true},
    [pscustomobject]@{Directory=$FrameworkDependentDirectory;Name=('myFanControl-v'+$Version+'-win-x64-framework-dependent.zip');SelfContained=$false}
)
$taskResults=@()
foreach($taskPackage in $taskPackages){
    $taskSource=(Resolve-Path -LiteralPath (Join-Path $taskRoot ('artifacts/publish/'+$taskPackage.Directory))).Path
    $taskArchivePath=Join-Path $taskReleaseDirectory $taskPackage.Name
    if(Test-Path -LiteralPath $taskArchivePath){throw ('Release archive already exists: '+$taskPackage.Name)}
    $taskFiles=@(Get-ChildItem -LiteralPath $taskSource -Recurse -File | Where-Object {$_.FullName -ne (Join-Path $taskSource 'control-settings.json')})
    $taskArchive=[IO.Compression.ZipFile]::Open($taskArchivePath,[IO.Compression.ZipArchiveMode]::Create)
    try{
        foreach($taskFile in $taskFiles){
            $taskRelative=[IO.Path]::GetRelativePath($taskSource,$taskFile.FullName).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskArchive,$taskFile.FullName,('myFanControl/'+$taskRelative),[IO.Compression.CompressionLevel]::Optimal)|Out-Null
        }
    }finally{$taskArchive.Dispose()}
    $taskCheck=[IO.Compression.ZipFile]::OpenRead($taskArchivePath)
    try{
        foreach($taskRequired in @('myFanControl/myFanControl.exe','myFanControl/myFanControl.dll','myFanControl/EcPmcConfigProbe.exe','myFanControl/README.md')){
            if($null -eq $taskCheck.GetEntry($taskRequired)){throw ('Archive missing '+$taskRequired)}
        }
        if($null -ne $taskCheck.GetEntry('myFanControl/control-settings.json')){throw 'Archive includes personal settings.'}
        if(-not @($taskCheck.Entries|Where-Object {$_.FullName.StartsWith('myFanControl/licenses/')}).Count){throw 'Archive missing licenses.'}
        $taskRuntimeCount=@($taskCheck.Entries|Where-Object {$_.Name -eq 'System.Private.CoreLib.dll'}).Count
        if($taskPackage.SelfContained -and $taskRuntimeCount -ne 1){throw 'Self-contained archive must contain exactly one runtime.'}
        if(-not $taskPackage.SelfContained -and $taskRuntimeCount -ne 0){throw 'Framework-dependent archive contains a bundled runtime.'}
        if($taskCheck.Entries.Count -ne $taskFiles.Count){throw 'Archive file count does not match the package.'}
    }finally{$taskCheck.Dispose()}
    $taskHash=(Get-FileHash -LiteralPath $taskArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $taskResults+=[pscustomobject]@{Archive=$taskArchivePath;Files=$taskFiles.Count;Bytes=(Get-Item -LiteralPath $taskArchivePath).Length;Sha256=$taskHash;SelfContained=$taskPackage.SelfContained}
}
$taskChecksumPath=Join-Path $taskReleaseDirectory 'SHA256SUMS.txt'
$taskChecksumLines=$taskResults|ForEach-Object {$_.Sha256+'  '+[IO.Path]::GetFileName($_.Archive)}
[IO.File]::WriteAllText($taskChecksumPath,($taskChecksumLines -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
$taskResults|ConvertTo-Json
