# Run normally, not as Administrator. Tests only isolated temporary fixture programs.
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this smoke test as a normal user, not Administrator.' }
& (Join-Path $PSScriptRoot 'UpdateSmoke.exe')
if ($LASTEXITCODE -ne 0) { throw "Update lifecycle smoke failed ($LASTEXITCODE). See the REPORT path above." }
