param([string]$Target = 'x86_64-pc-windows-msvc')
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    cargo build --locked --release --target $Target --manifest-path native/quota-reader/Cargo.toml
    if ($LASTEXITCODE -ne 0) { throw 'Rust reader build failed.' }
    python scripts/pack-reader.py "native/quota-reader/target/$Target/release/codex-quota-reader.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Reader packaging failed.' }
} finally { Pop-Location }
