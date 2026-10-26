# Build isolated fixtures using an already installed .NET SDK. Does not install software.
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..\..')
try {
    New-Item -ItemType Directory -Force 'artifacts\update-smoke\old', 'artifacts\update-smoke\new' | Out-Null
    & dotnet build tools/UpdateSmoke/Fixture -c Release -m:1 -p:FixtureVersion=0.0.1.0 -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Old fixture build failed.' }
    Copy-Item 'tools\UpdateSmoke\Fixture\bin\0.0.1.0\Release\net48\CodexQuotaTray.exe' 'artifacts\update-smoke\old\' -Force
    & dotnet build tools/UpdateSmoke/Fixture -c Release -m:1 -p:FixtureVersion=0.0.2.0 -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'New fixture build failed.' }
    Copy-Item 'tools\UpdateSmoke\Fixture\bin\0.0.2.0\Release\net48\CodexQuotaTray.exe' 'artifacts\update-smoke\new\' -Force
    & dotnet build tools/UpdateSmoke/Driver -c Release -m:1 -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Driver build failed.' }
    Copy-Item 'tools\UpdateSmoke\Driver\bin\Release\net48\UpdateSmoke.exe', 'tools\UpdateSmoke\run-smoke.ps1', 'tools\UpdateSmoke\README.txt', 'src\CodexQuota.Core\Resources\Fetch-Update.ps1' 'artifacts\update-smoke\' -Force
} finally { Pop-Location }
