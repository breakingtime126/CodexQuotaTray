#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1
runtime="${1:-win-x64}"
case "$runtime" in win-x64) ;; *) echo 'The tray is Windows x64 only.' >&2; exit 2;; esac
dotnet build tests/CodexQuota.Tests -c Release -m:1
dotnet run --project tests/CodexQuota.Tests -c Release --no-build
dotnet publish src/CodexQuota.Tray -c Release -f net8.0-windows -r "$runtime" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None -p:DebugSymbols=false -o "artifacts/$runtime"
cp README.md "artifacts/$runtime/"
cp -R third_party docs "artifacts/$runtime/"
(cd "artifacts/$runtime" && zip -q -r "../CodexQuotaTray-$runtime.zip" .)
