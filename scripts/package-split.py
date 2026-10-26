#!/usr/bin/env python3
"""Generate four <16 MiB delivery ZIPs with a hash pinned to this exact release."""
import argparse
import hashlib
import math
from pathlib import Path
import zipfile


def package(source: Path, output: Path) -> list[Path]:
    source = source.resolve()
    payload = source.read_bytes()
    with zipfile.ZipFile(source) as archive:
        if archive.testzip() is not None:
            raise ValueError("The source ZIP failed its integrity check")
    digest = hashlib.sha256(payload).hexdigest()
    templates = Path(__file__).resolve().parent / "distribution"
    helpers = {}
    for name in ("assemble.cmd", "assemble-review.ps1"):
        content = (templates / (name + ".template")).read_bytes()
        if content.count(b"__ZIP_SHA256__") != 1:
            raise ValueError("Expected exactly one SHA256 placeholder in " + name)
        helpers[name] = content.replace(b"__ZIP_SHA256__", digest.encode("ascii"))
    instructions = """Codex 周额度托盘：分包使用说明

1. 下载四个 Part1～Part4 ZIP，把内容全部解压到同一个空文件夹。
   文件夹中应直接包含 assemble.cmd 和 CodexQuotaTray.part1.bin～part4.bin。
2. 双击 assemble.cmd。脚本仅合并、校验 SHA256 并解压到 CodexQuotaTray 子目录。
   不启动程序、不修改系统或开机启动设置、不覆盖已有输出；无需管理员权限。
3. 校验成功后，在长期保留的文件夹内运行 CodexQuotaTray.exe。
   应用首次运行默认启用当前用户开机启动，可在托盘右键菜单取消。
4. 图标仅显示较大的粗体数字和圆环，悬停/菜单保留完整百分比。

assembly-review.ps1 是可读参考；请通过 assemble.cmd 使用。
脚本使用 .NET SHA256 和 ZipFile，不依赖 Get-FileHash / Expand-Archive。
如果已有同名输出，请换一个空文件夹重试。不要混合不同版本的分段。

本包原始 ZIP 的 SHA256：
""".replace("assembly-review.ps1", "assemble-review.ps1") + digest + "\n"
    helpers["README-安装.txt"] = instructions.encode("utf-8-sig")
    output.mkdir(parents=True, exist_ok=True)
    result = [output / f"CodexQuotaTray-Windows-Part{i}-of-4.zip" for i in range(1, 5)]
    if any(path.exists() for path in result):
        raise FileExistsError("Part packages already exist; choose a fresh output directory")
    chunk_size = math.ceil(len(payload) / 4)
    # Stored ZIP framing plus script/README overhead; fail before writing any oversized package.
    if chunk_size + sum(len(value) for value in helpers.values()) + 4096 >= 16 * 1024 * 1024:
        raise ValueError("This release is too large for four packages below 16 MiB")
    for index, path in enumerate(result):
        with zipfile.ZipFile(path, "x", compression=zipfile.ZIP_STORED) as archive:
            archive.writestr(f"CodexQuotaTray.part{index+1}.bin", payload[index*chunk_size:(index+1)*chunk_size])
            if index == 0:
                for name, content in helpers.items():
                    archive.writestr(name, content)
    # Verify exact reconstructed bytes and per-part integrity before advertising success.
    reconstructed = bytearray()
    for index, path in enumerate(result):
        assert path.stat().st_size < 16 * 1024 * 1024
        with zipfile.ZipFile(path) as archive:
            assert archive.testzip() is None
            reconstructed.extend(archive.read(f"CodexQuotaTray.part{index+1}.bin"))
    assert hashlib.sha256(reconstructed).hexdigest() == digest
    print("Verified exact reassembly; SHA256:", digest)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Current release ZIP, not an old set of parts")
    parser.add_argument("--output", type=Path, help="Fresh output folder (default: sibling split-delivery)")
    args = parser.parse_args()
    for item in package(args.source, args.output or args.source.parent / "split-delivery"):
        print(item.resolve())
