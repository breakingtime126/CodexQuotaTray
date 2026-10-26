#!/usr/bin/env python3
"""Package the compiled native reader. Build-time only; never runs it or reads credentials."""
import gzip
import hashlib
import json
import pathlib
import struct
import sys
import tomllib
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parents[1]
binary = pathlib.Path(sys.argv[1]).read_bytes()
if not 1024 <= len(binary) <= 50 * 1024 * 1024 or binary[:2] != b"MZ":
    raise SystemExit("Invalid or oversized reader PE")
pe = struct.unpack_from("<I", binary, 0x3C)[0]
if binary[pe:pe + 4] != b"PE\0\0" or struct.unpack_from("<H", binary, pe + 4)[0] != 0x8664:
    raise SystemExit("Reader must be a Windows x64 PE")
if struct.unpack_from("<H", binary, pe + 24)[0] != 0x20B:
    raise SystemExit("Reader must be PE32+")
version = ET.parse(root / "Directory.Build.props").findtext(".//Version")
crate = tomllib.loads((root / "native/quota-reader/Cargo.toml").read_text())
if version != crate["package"]["version"]:
    raise SystemExit("Reader and tray release versions must match")
dest = root / "native/quota-reader/dist"
dest.mkdir(parents=True, exist_ok=True)
payload = gzip.compress(binary, compresslevel=9, mtime=0)
(dest / "reader.exe.gz").write_bytes(payload)
manifest = dict(helperVersion=version, sha256=hashlib.sha256(binary).hexdigest(),
                executableLength=len(binary), compressedLength=len(payload))
(dest / "reader-manifest.json").write_text(json.dumps(manifest, separators=(",", ":")) + "\n")
print(json.dumps(manifest))
