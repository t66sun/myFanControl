[CmdletBinding()]
param([ValidatePattern('^[A-Za-z0-9-]+$')][string]$DirectoryName='ThinkBookControl')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $taskRoot '.tools/cli-home'
$env:NUGET_PACKAGES=Join-Path $taskRoot '.tools/nuget-packages'
$taskDotnet=Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
$taskOutput=Join-Path $taskRoot ('artifacts/publish/'+$DirectoryName)
& $taskDotnet publish (Join-Path $taskRoot 'src/ThinkBookControl/ThinkBookControl.csproj') -c Release -r win-x64 --self-contained true --no-restore -o $taskOutput
if($LASTEXITCODE -ne 0){throw 'Desktop product publish failed.'}
& $taskDotnet publish (Join-Path $taskRoot 'src/EcPmcConfigProbe/EcPmcConfigProbe.csproj') -c Release -r win-x64 --self-contained true --no-restore -o (Join-Path $taskOutput 'control-host')
if($LASTEXITCODE -ne 0){throw 'Control host publish failed.'}
Copy-Item -LiteralPath (Join-Path $taskRoot 'PRODUCT_README.md') -Destination (Join-Path $taskOutput 'README.md') -Force
Get-FileHash -LiteralPath (Join-Path $taskOutput 'ThinkBookControl.dll'),(Join-Path $taskOutput 'control-host/EcPmcConfigProbe.dll') -Algorithm SHA256
