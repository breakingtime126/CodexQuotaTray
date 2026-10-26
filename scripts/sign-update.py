#!/usr/bin/env python3
"""Sign an existing EXE for the fixed public update repository using an EXTERNAL PEM private key.

Does not generate or save keys, upload anything, or mutate the executable. Keep the
private key outside the repository; only update.json and CodexQuotaTray.exe are release assets.
Requires the separately installed OpenSSL command. Example:
  python3 scripts/sign-update.py --exe artifacts/lightweight/CodexQuotaTray.exe \
    --version 0.2.34.0 --key /private/path/update-signing.pem --output artifacts/update.json
"""
import argparse
import base64
import datetime
import hashlib
import json
import pathlib
import re
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", required=True, type=pathlib.Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--key", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)", args.version):
        parser.error("--version must be canonical four-part numeric version")
    if any(int(p) > 2147483647 for p in args.version.split(".")) or args.version == "0.0.0.0":
        parser.error("unsupported version")
    repo = pathlib.Path(__file__).resolve().parents[1]
    key = args.key.resolve(strict=True)
    if key == repo or repo in key.parents:
        parser.error("private key must be kept outside the repository")
    exe = args.exe.resolve(strict=True)
    size = exe.stat().st_size
    if not 1 <= size <= 30 * 1024 * 1024:
        parser.error("EXE size must be in 1..30 MiB")
    if args.output.resolve() in (key, exe):
        parser.error("output must not overwrite the private key or EXE")
    content = exe.read_bytes()
    if len(content) != size or not content.startswith(b"MZ"):
        parser.error("input must be a stable PE executable")
    payload = json.dumps({
        "schemaVersion": 1,
        "app": "CodexQuotaTray",
        "channel": "stable",
        "version": args.version,
        "assetUrl": f"https://github.com/breakingtime126/CodexQuotaTray/releases/download/v{args.version}/CodexQuotaTray.exe",
        "size": size,
        "sha256": hashlib.sha256(content).hexdigest(),
        "publishedAt": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }, separators=(",", ":"), ensure_ascii=True).encode("utf-8")
    # PKCS#1 v1.5 with SHA-256. Never print the key or pass its contents on argv.
    signature = subprocess.run(["openssl", "dgst", "-sha256", "-sign", str(key), "-sigopt", "rsa_padding_mode:pkcs1"],
                               input=payload, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True).stdout
    if len(signature) not in (256, 384, 512):
        parser.error("signing key must be RSA 2048, 3072 or 4096 bits")
    envelope = json.dumps({"payload": base64.b64encode(payload).decode("ascii"),
                           "signature": base64.b64encode(signature).decode("ascii")},
                          separators=(",", ":")).encode("utf-8") + b"\n"
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(envelope)
    print(f"Signed manifest: {args.output}; EXE SHA-256: {hashlib.sha256(content).hexdigest()}")


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError:
        sys.exit("OpenSSL signing failed; no manifest was created.")
