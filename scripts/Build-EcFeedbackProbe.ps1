[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $taskRoot '.tools/cli-home'
$env:NUGET_PACKAGES=Join-Path $taskRoot '.tools/nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$taskDotnet=Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$taskProject=Join-Path $taskRoot 'src/EcFeedbackProbe/EcFeedbackProbe.csproj'
$taskPublish=Join-Path $taskRoot 'artifacts/publish/EcFeedbackProbe'
& $taskDotnet publish $taskProject -c Release -r win-x64 --self-contained true --configfile (Join-Path $taskRoot 'NuGet.Config') -o $taskPublish
if($LASTEXITCODE -ne 0){throw "Feedback publish failed: $LASTEXITCODE"}
# Mock only. This script never starts the hardware diagnostic or requests UAC.
& (Join-Path $taskPublish 'EcFeedbackProbe.exe') --self-check
if($LASTEXITCODE -ne 0){throw "Feedback self-check failed: $LASTEXITCODE"}
