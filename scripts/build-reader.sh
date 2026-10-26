#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
target="${READER_TARGET:-x86_64-pc-windows-gnu}"
cargo build --locked --release --target "$target" --manifest-path native/quota-reader/Cargo.toml
python3 scripts/pack-reader.py "native/quota-reader/target/$target/release/codex-quota-reader.exe"
