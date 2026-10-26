#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
mkdir -p artifacts/update-smoke/old artifacts/update-smoke/new
dotnet build tools/UpdateSmoke/Fixture -c Release -m:1 -p:FixtureVersion=0.0.1.0 -p:DebugType=None -p:DebugSymbols=false
cp tools/UpdateSmoke/Fixture/bin/0.0.1.0/Release/net48/CodexQuotaTray.exe artifacts/update-smoke/old/
dotnet build tools/UpdateSmoke/Fixture -c Release -m:1 -p:FixtureVersion=0.0.2.0 -p:DebugType=None -p:DebugSymbols=false
cp tools/UpdateSmoke/Fixture/bin/0.0.2.0/Release/net48/CodexQuotaTray.exe artifacts/update-smoke/new/
dotnet build tools/UpdateSmoke/Driver -c Release -m:1 -p:DebugType=None -p:DebugSymbols=false
cp tools/UpdateSmoke/Driver/bin/Release/net48/UpdateSmoke.exe artifacts/update-smoke/
cp tools/UpdateSmoke/run-smoke.ps1 artifacts/update-smoke/
cp tools/UpdateSmoke/README.txt artifacts/update-smoke/
cp src/CodexQuota.Core/Resources/Fetch-Update.ps1 artifacts/update-smoke/
