#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build tests/CodexQuota.Tests -c Release -m:1
dotnet run --project tests/CodexQuota.Tests -c Release --no-build
dotnet build src/CodexQuota.Tray -c Release -f net48 -m:1 -p:DebugType=None -p:DebugSymbols=false
mkdir -p artifacts/lightweight
cp src/CodexQuota.Tray/bin/Release/net48/CodexQuotaTray.exe artifacts/lightweight/
echo 'Ready: artifacts/lightweight/CodexQuotaTray.exe'
echo 'Single EXE; uses Windows built-in .NET Framework 4.8 and the selected installed Codex component.'
