[CmdletBinding()]
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localSdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$project = Join-Path $projectRoot 'src/myFanControl/myFanControl.csproj'
$config = Join-Path $projectRoot 'NuGet.Config'
& $dotnetCommand restore $project --configfile $config -p:SelfContained=$($Publish.IsPresent.ToString().ToLowerInvariant()) -r win-x64
if ($LASTEXITCODE -ne 0) { throw "Restore failed: $LASTEXITCODE" }
if ($Publish) {
    & $dotnetCommand publish $project -c Release -r win-x64 --self-contained true --no-restore -o (Join-Path $projectRoot 'artifacts/publish/myFanControl-preview')
} else {
    & $dotnetCommand build $project -c Release --no-restore
}
if ($LASTEXITCODE -ne 0) { throw "Build/publish failed: $LASTEXITCODE" }
