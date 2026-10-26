[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet build tests/CodexQuota.Tests -c Release -m:1
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
dotnet run --project tests/CodexQuota.Tests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
dotnet build src/CodexQuota.Tray -c Release -f net48 -m:1 -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Lightweight build failed' }
$destination = 'artifacts/lightweight'
New-Item -ItemType Directory -Force $destination | Out-Null
Copy-Item src/CodexQuota.Tray/bin/Release/net48/CodexQuotaTray.exe $destination -Force
Write-Host 'Ready: artifacts/lightweight/CodexQuotaTray.exe'
Write-Host 'Single EXE; uses built-in .NET Framework 4.8 and the selected installed Codex component.'
