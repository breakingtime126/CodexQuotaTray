[CmdletBinding()]
param([ValidateSet('win-x64')][string]$Runtime = 'win-x64', [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet build tests/CodexQuota.Tests -c Release -m:1
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
dotnet run --project tests/CodexQuota.Tests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
$destination = Join-Path 'artifacts' $Runtime
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
dotnet publish src/CodexQuota.Tray -c Release -f net8.0-windows -r $Runtime --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item README.md $destination
Copy-Item third_party $destination -Recurse -Force
Copy-Item docs $destination -Recurse -Force
Compress-Archive -Path "$destination/*" -DestinationPath "artifacts/CodexQuotaTray-$Runtime.zip" -Force
Write-Host "Ready: artifacts/CodexQuotaTray-$Runtime.zip"
