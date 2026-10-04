[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9-]+$')][string]$DirectoryName='ThinkBookControl',
    [switch]$FrameworkDependent
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $taskRoot '.tools/cli-home'
$env:NUGET_PACKAGES=Join-Path $taskRoot '.tools/nuget-packages'
$taskDotnet=Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$taskConfig=Join-Path $taskRoot 'NuGet.Config'
$taskSelfContained=if($FrameworkDependent){'false'}else{'true'}
$taskOutput=Join-Path $taskRoot ('artifacts/publish/'+$DirectoryName)
if(Test-Path -LiteralPath $taskOutput){throw 'Publish to a new directory to avoid retaining files from an older package.'}
$taskHostOutput=Join-Path $taskOutput '_host-publish'
foreach($taskProject in @('src/ThinkBookControl/ThinkBookControl.csproj','src/EcPmcConfigProbe/EcPmcConfigProbe.csproj')){
    & $taskDotnet restore (Join-Path $taskRoot $taskProject) --configfile $taskConfig -r win-x64 -p:SelfContained=$taskSelfContained
    if($LASTEXITCODE -ne 0){throw ('Restore failed: '+$taskProject)}
}
& $taskDotnet publish (Join-Path $taskRoot 'src/ThinkBookControl/ThinkBookControl.csproj') -c Release -r win-x64 --self-contained $taskSelfContained --no-restore -o $taskOutput
if($LASTEXITCODE -ne 0){throw 'Desktop product publish failed.'}
& $taskDotnet publish (Join-Path $taskRoot 'src/EcPmcConfigProbe/EcPmcConfigProbe.csproj') -c Release -r win-x64 --self-contained $taskSelfContained --no-restore -o $taskHostOutput
if($LASTEXITCODE -ne 0){throw 'Control host publish failed.'}
# Both executables remain independent processes, with one shared runtime on disk.
foreach($taskHostFile in Get-ChildItem -LiteralPath $taskHostOutput -Recurse -File){
    $taskRelative=[IO.Path]::GetRelativePath($taskHostOutput,$taskHostFile.FullName)
    $taskDestination=Join-Path $taskOutput $taskRelative
    if(Test-Path -LiteralPath $taskDestination){
        if((Get-FileHash -LiteralPath $taskHostFile.FullName).Hash -ne (Get-FileHash -LiteralPath $taskDestination).Hash){throw ('Publish file conflict: '+$taskRelative)}
        continue
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
    Copy-Item -LiteralPath $taskHostFile.FullName -Destination $taskDestination
}
$taskResolvedOutput=(Resolve-Path -LiteralPath $taskOutput).Path
$taskResolvedHost=(Resolve-Path -LiteralPath $taskHostOutput).Path
if([IO.Path]::GetDirectoryName($taskResolvedHost) -ne $taskResolvedOutput -or [IO.Path]::GetFileName($taskResolvedHost) -ne '_host-publish'){throw 'Invalid temporary publish directory.'}
Remove-Item -LiteralPath $taskResolvedHost -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'PRODUCT_README.md') -Destination (Join-Path $taskOutput 'README.md') -Force
Get-FileHash -LiteralPath (Join-Path $taskOutput 'ThinkBookControl.dll'),(Join-Path $taskOutput 'EcPmcConfigProbe.dll') -Algorithm SHA256
