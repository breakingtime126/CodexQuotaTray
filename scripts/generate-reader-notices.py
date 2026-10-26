#!/usr/bin/env python3
"""Audit the locked Windows GNU reader dependencies and regenerate legal notices.

Requires Python 3.11+, Cargo, and the checksum-verified Cargo registry archives
already downloaded by `cargo fetch --locked`. Never executes the helper, reads
user credentials, builds code, or changes Cargo.toml/Cargo.lock. No network calls.

Run after dependency changes:
  python3 scripts/generate-reader-notices.py
Verify checked-in notices:
  python3 scripts/generate-reader-notices.py --check

The upstream Codex and native toolchain notices are separately curated, pinned
files. Updating Rust, GCC, MinGW, the target, or adapted upstream code requires a
fresh audit of those files as well as regenerating the dependency inventory.
"""
from __future__ import annotations

import argparse
import difflib
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import tomllib

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "native/quota-reader/Cargo.toml"
TARGET = "x86_64-pc-windows-gnu"
REGISTRY = "registry+https://github.com/rust-lang/crates.io-index"

# This is a single reviewed source exception, not a general path-dependency
# allowance. Every original archive file must remain present, and every byte
# outside these explicit changes must match the checksum-verified archive.
# Updating the patch requires a new source review and new pinned hashes here.
HYPER_UTIL_PATH = "native/quota-reader/upstream/hyper-util-0.1.20"
HYPER_UTIL_ARCHIVE_SHA256 = "96547c2556ec9d12fb1578c4eaf448b04993e7fb79cbaad930a656880a6bdfa0"
HYPER_UTIL_MODIFIED = {
    "src/client/proxy/matcher.rs": "368cb6c9d56e700b5bf9df05c7b33e5e5937483b21a90fc71ebaba026f1e7ae2",
}
HYPER_UTIL_ADDED = {
    "src/client/proxy/matcher/diagnostics.rs": "ad986d04b2aad421ce7980edbea692b994560953841148f7be618b70bc2fd84e",
}
HYPER_UTIL_PATCH = "native/quota-reader/upstream/hyper-util-0.1.20-diagnostics.patch"
HYPER_UTIL_PROVENANCE = "native/quota-reader/upstream/HYPER_UTIL_PATCH.md"
HYPER_UTIL_PROVENANCE_SHA256 = "3cb59b1cdb8afabee40e95f53083153ca0d07656012775a1425b17a2e424c0d1"

# Frozen upstream license fallbacks will be populated below. These files are
# absent from the published crate archives; their URLs use the exact commit
# recorded by each archive's .cargo_vcs_info.json. Do not substitute HEAD.
UPSTREAM_LICENSES = {'age 0.11.5': [{'url': 'https://raw.githubusercontent.com/str4d/rage/7f29934dc58e058e7a588ac3a9a7e946198a73a7/LICENSE-MIT',
                 'sha256': 'fe56c030c2d8c3404a37fe711cebb27ebea13d0aacf7e3524db4198335ad8cd1',
                 'text': 'The MIT License (MIT)\n'
                         '\n'
                         'Copyright (c) 2019 Jack Grigg\n'
                         '\n'
                         'Permission is hereby granted, free of charge, to any person obtaining a copy\n'
                         'of this software and associated documentation files (the "Software"), to '
                         'deal\n'
                         'in the Software without restriction, including without limitation the rights\n'
                         'to use, copy, modify, merge, publish, distribute, sublicense, and/or sell\n'
                         'copies of the Software, and to permit persons to whom the Software is\n'
                         'furnished to do so, subject to the following conditions:\n'
                         '\n'
                         'The above copyright notice and this permission notice shall be included in\n'
                         'all copies or substantial portions of the Software.\n'
                         '\n'
                         'THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR\n'
                         'IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,\n'
                         'FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE\n'
                         'AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER\n'
                         'LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING '
                         'FROM,\n'
                         'OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN\n'
                         'THE SOFTWARE.\n'}],
 'age-core 0.11.0': [{'url': 'https://raw.githubusercontent.com/str4d/rage/17446612f865c705e44fe392d9d41dd102fed137/LICENSE-MIT',
                      'sha256': 'fe56c030c2d8c3404a37fe711cebb27ebea13d0aacf7e3524db4198335ad8cd1',
                      'text': 'The MIT License (MIT)\n'
                              '\n'
                              'Copyright (c) 2019 Jack Grigg\n'
                              '\n'
                              'Permission is hereby granted, free of charge, to any person obtaining a '
                              'copy\n'
                              'of this software and associated documentation files (the "Software"), to '
                              'deal\n'
                              'in the Software without restriction, including without limitation the '
                              'rights\n'
                              'to use, copy, modify, merge, publish, distribute, sublicense, and/or '
                              'sell\n'
                              'copies of the Software, and to permit persons to whom the Software is\n'
                              'furnished to do so, subject to the following conditions:\n'
                              '\n'
                              'The above copyright notice and this permission notice shall be included '
                              'in\n'
                              'all copies or substantial portions of the Software.\n'
                              '\n'
                              'THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS '
                              'OR\n'
                              'IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF '
                              'MERCHANTABILITY,\n'
                              'FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL '
                              'THE\n'
                              'AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER\n'
                              'LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING '
                              'FROM,\n'
                              'OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS '
                              'IN\n'
                              'THE SOFTWARE.\n'}],
 'fluent-langneg 0.13.1': [{'url': 'https://raw.githubusercontent.com/projectfluent/fluent-langneg-rs/7fdc590466ea63ca2367ac10fb1b881fe561f1a8/LICENSE-MIT',
                            'sha256': '016fcc02048817d628b04b140c54db290fd9d29a5e8a55b77871194e810716ac',
                            'text': 'Copyright Mozilla Foundation\n'
                                    '\n'
                                    'Permission is hereby granted, free of charge, to any person '
                                    'obtaining a copy of\n'
                                    'this software and associated documentation files (the "Software"), '
                                    'to deal in\n'
                                    'the Software without restriction, including without limitation the '
                                    'rights to\n'
                                    'use, copy, modify, merge, publish, distribute, sublicense, and/or '
                                    'sell copies\n'
                                    'of the Software, and to permit persons to whom the Software is '
                                    'furnished to do\n'
                                    'so, subject to the following conditions:\n'
                                    '\n'
                                    'The above copyright notice and this permission notice shall be '
                                    'included in all\n'
                                    'copies or substantial portions of the Software.\n'
                                    '\n'
                                    'THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, '
                                    'EXPRESS OR\n'
                                    'IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF '
                                    'MERCHANTABILITY,\n'
                                    'FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT '
                                    'SHALL THE\n'
                                    'AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR '
                                    'OTHER\n'
                                    'LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, '
                                    'ARISING FROM,\n'
                                    'OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER '
                                    'DEALINGS IN THE\n'
                                    'SOFTWARE.\n'}],
 'cookie-factory 0.3.3': [{'url': 'https://raw.githubusercontent.com/rust-bakery/cookie-factory/d36b805dbd7dd65f2df947235c5bcc573afe2c76/LICENSES/MIT.txt',
                           'sha256': 'd09216dc1ea5f273997667935a8d6514d80f00b89fb6954ecc3a43e0a6e360de',
                           'text': 'Copyright (c) 2017-2023 Geoffroy Couprie\n'
                                   '\n'
                                   'Permission is hereby granted, free of charge, to any person '
                                   'obtaining\n'
                                   'a copy of this software and associated documentation files (the\n'
                                   '"Software"), to deal in the Software without restriction, '
                                   'including\n'
                                   'without limitation the rights to use, copy, modify, merge, '
                                   'publish,\n'
                                   'distribute, sublicense, and/or sell copies of the Software, and to\n'
                                   'permit persons to whom the Software is furnished to do so, subject '
                                   'to\n'
                                   'the following conditions:\n'
                                   '\n'
                                   'The above copyright notice and this permission notice shall be\n'
                                   'included in all copies or substantial portions of the Software.\n'
                                   '\n'
                                   'THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,\n'
                                   'EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF\n'
                                   'MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND\n'
                                   'NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS '
                                   'BE\n'
                                   'LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN '
                                   'ACTION\n'
                                   'OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN '
                                   'CONNECTION\n'
                                   'WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.\n'}]}


# These published archives explicitly declare the standard Apache-2.0 license
# but omit its text. Use the unmodified canonical terms from a verified crate,
# while retaining each subject package's own authors/declared grant.
CANONICAL_APACHE = {'url': 'canonical Apache-2.0 terms from aead 0.5.2/LICENSE-APACHE (archive SHA-256 '
        'd122413f284cf2d62fb1b7db97e02edb8cda96d769b16e443a4f6195e35662b0); grant is the subject '
        'package Cargo.toml/README',
 'sha256': 'b1cf9a3333ca78152b859012cd4a804156e5243e9ca20ad1df7327ba5ea7405c',
 'text': '                              Apache License\n'
         '                        Version 2.0, January 2004\n'
         '                     http://www.apache.org/licenses/\n'
         '\n'
         'TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION\n'
         '\n'
         '1. Definitions.\n'
         '\n'
         '   "License" shall mean the terms and conditions for use, reproduction,\n'
         '   and distribution as defined by Sections 1 through 9 of this document.\n'
         '\n'
         '   "Licensor" shall mean the copyright owner or entity authorized by\n'
         '   the copyright owner that is granting the License.\n'
         '\n'
         '   "Legal Entity" shall mean the union of the acting entity and all\n'
         '   other entities that control, are controlled by, or are under common\n'
         '   control with that entity. For the purposes of this definition,\n'
         '   "control" means (i) the power, direct or indirect, to cause the\n'
         '   direction or management of such entity, whether by contract or\n'
         '   otherwise, or (ii) ownership of fifty percent (50%) or more of the\n'
         '   outstanding shares, or (iii) beneficial ownership of such entity.\n'
         '\n'
         '   "You" (or "Your") shall mean an individual or Legal Entity\n'
         '   exercising permissions granted by this License.\n'
         '\n'
         '   "Source" form shall mean the preferred form for making modifications,\n'
         '   including but not limited to software source code, documentation\n'
         '   source, and configuration files.\n'
         '\n'
         '   "Object" form shall mean any form resulting from mechanical\n'
         '   transformation or translation of a Source form, including but\n'
         '   not limited to compiled object code, generated documentation,\n'
         '   and conversions to other media types.\n'
         '\n'
         '   "Work" shall mean the work of authorship, whether in Source or\n'
         '   Object form, made available under the License, as indicated by a\n'
         '   copyright notice that is included in or attached to the work\n'
         '   (an example is provided in the Appendix below).\n'
         '\n'
         '   "Derivative Works" shall mean any work, whether in Source or Object\n'
         '   form, that is based on (or derived from) the Work and for which the\n'
         '   editorial revisions, annotations, elaborations, or other modifications\n'
         '   represent, as a whole, an original work of authorship. For the purposes\n'
         '   of this License, Derivative Works shall not include works that remain\n'
         '   separable from, or merely link (or bind by name) to the interfaces of,\n'
         '   the Work and Derivative Works thereof.\n'
         '\n'
         '   "Contribution" shall mean any work of authorship, including\n'
         '   the original version of the Work and any modifications or additions\n'
         '   to that Work or Derivative Works thereof, that is intentionally\n'
         '   submitted to Licensor for inclusion in the Work by the copyright owner\n'
         '   or by an individual or Legal Entity authorized to submit on behalf of\n'
         '   the copyright owner. For the purposes of this definition, "submitted"\n'
         '   means any form of electronic, verbal, or written communication sent\n'
         '   to the Licensor or its representatives, including but not limited to\n'
         '   communication on electronic mailing lists, source code control systems,\n'
         '   and issue tracking systems that are managed by, or on behalf of, the\n'
         '   Licensor for the purpose of discussing and improving the Work, but\n'
         '   excluding communication that is conspicuously marked or otherwise\n'
         '   designated in writing by the copyright owner as "Not a Contribution."\n'
         '\n'
         '   "Contributor" shall mean Licensor and any individual or Legal Entity\n'
         '   on behalf of whom a Contribution has been received by Licensor and\n'
         '   subsequently incorporated within the Work.\n'
         '\n'
         '2. Grant of Copyright License. Subject to the terms and conditions of\n'
         '   this License, each Contributor hereby grants to You a perpetual,\n'
         '   worldwide, non-exclusive, no-charge, royalty-free, irrevocable\n'
         '   copyright license to reproduce, prepare Derivative Works of,\n'
         '   publicly display, publicly perform, sublicense, and distribute the\n'
         '   Work and such Derivative Works in Source or Object form.\n'
         '\n'
         '3. Grant of Patent License. Subject to the terms and conditions of\n'
         '   this License, each Contributor hereby grants to You a perpetual,\n'
         '   worldwide, non-exclusive, no-charge, royalty-free, irrevocable\n'
         '   (except as stated in this section) patent license to make, have made,\n'
         '   use, offer to sell, sell, import, and otherwise transfer the Work,\n'
         '   where such license applies only to those patent claims licensable\n'
         '   by such Contributor that are necessarily infringed by their\n'
         '   Contribution(s) alone or by combination of their Contribution(s)\n'
         '   with the Work to which such Contribution(s) was submitted. If You\n'
         '   institute patent litigation against any entity (including a\n'
         '   cross-claim or counterclaim in a lawsuit) alleging that the Work\n'
         '   or a Contribution incorporated within the Work constitutes direct\n'
         '   or contributory patent infringement, then any patent licenses\n'
         '   granted to You under this License for that Work shall terminate\n'
         '   as of the date such litigation is filed.\n'
         '\n'
         '4. Redistribution. You may reproduce and distribute copies of the\n'
         '   Work or Derivative Works thereof in any medium, with or without\n'
         '   modifications, and in Source or Object form, provided that You\n'
         '   meet the following conditions:\n'
         '\n'
         '   (a) You must give any other recipients of the Work or\n'
         '       Derivative Works a copy of this License; and\n'
         '\n'
         '   (b) You must cause any modified files to carry prominent notices\n'
         '       stating that You changed the files; and\n'
         '\n'
         '   (c) You must retain, in the Source form of any Derivative Works\n'
         '       that You distribute, all copyright, patent, trademark, and\n'
         '       attribution notices from the Source form of the Work,\n'
         '       excluding those notices that do not pertain to any part of\n'
         '       the Derivative Works; and\n'
         '\n'
         '   (d) If the Work includes a "NOTICE" text file as part of its\n'
         '       distribution, then any Derivative Works that You distribute must\n'
         '       include a readable copy of the attribution notices contained\n'
         '       within such NOTICE file, excluding those notices that do not\n'
         '       pertain to any part of the Derivative Works, in at least one\n'
         '       of the following places: within a NOTICE text file distributed\n'
         '       as part of the Derivative Works; within the Source form or\n'
         '       documentation, if provided along with the Derivative Works; or,\n'
         '       within a display generated by the Derivative Works, if and\n'
         '       wherever such third-party notices normally appear. The contents\n'
         '       of the NOTICE file are for informational purposes only and\n'
         '       do not modify the License. You may add Your own attribution\n'
         '       notices within Derivative Works that You distribute, alongside\n'
         '       or as an addendum to the NOTICE text from the Work, provided\n'
         '       that such additional attribution notices cannot be construed\n'
         '       as modifying the License.\n'
         '\n'
         '   You may add Your own copyright statement to Your modifications and\n'
         '   may provide additional or different license terms and conditions\n'
         '   for use, reproduction, or distribution of Your modifications, or\n'
         '   for any such Derivative Works as a whole, provided Your use,\n'
         '   reproduction, and distribution of the Work otherwise complies with\n'
         '   the conditions stated in this License.\n'
         '\n'
         '5. Submission of Contributions. Unless You explicitly state otherwise,\n'
         '   any Contribution intentionally submitted for inclusion in the Work\n'
         '   by You to the Licensor shall be under the terms and conditions of\n'
         '   this License, without any additional terms or conditions.\n'
         '   Notwithstanding the above, nothing herein shall supersede or modify\n'
         '   the terms of any separate license agreement you may have executed\n'
         '   with Licensor regarding such Contributions.\n'
         '\n'
         '6. Trademarks. This License does not grant permission to use the trade\n'
         '   names, trademarks, service marks, or product names of the Licensor,\n'
         '   except as required for reasonable and customary use in describing the\n'
         '   origin of the Work and reproducing the content of the NOTICE file.\n'
         '\n'
         '7. Disclaimer of Warranty. Unless required by applicable law or\n'
         '   agreed to in writing, Licensor provides the Work (and each\n'
         '   Contributor provides its Contributions) on an "AS IS" BASIS,\n'
         '   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or\n'
         '   implied, including, without limitation, any warranties or conditions\n'
         '   of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A\n'
         '   PARTICULAR PURPOSE. You are solely responsible for determining the\n'
         '   appropriateness of using or redistributing the Work and assume any\n'
         '   risks associated with Your exercise of permissions under this License.\n'
         '\n'
         '8. Limitation of Liability. In no event and under no legal theory,\n'
         '   whether in tort (including negligence), contract, or otherwise,\n'
         '   unless required by applicable law (such as deliberate and grossly\n'
         '   negligent acts) or agreed to in writing, shall any Contributor be\n'
         '   liable to You for damages, including any direct, indirect, special,\n'
         '   incidental, or consequential damages of any character arising as a\n'
         '   result of this License or out of the use or inability to use the\n'
         '   Work (including but not limited to damages for loss of goodwill,\n'
         '   work stoppage, computer failure or malfunction, or any and all\n'
         '   other commercial damages or losses), even if such Contributor\n'
         '   has been advised of the possibility of such damages.\n'
         '\n'
         '9. Accepting Warranty or Additional Liability. While redistributing\n'
         '   the Work or Derivative Works thereof, You may choose to offer,\n'
         '   and charge a fee for, acceptance of support, warranty, indemnity,\n'
         '   or other liability obligations and/or rights consistent with this\n'
         '   License. However, in accepting such obligations, You may act only\n'
         '   on Your own behalf and on Your sole responsibility, not on behalf\n'
         '   of any other Contributor, and only if You agree to indemnify,\n'
         '   defend, and hold each Contributor harmless for any liability\n'
         '   incurred by, or claims asserted against, such Contributor by reason\n'
         '   of your accepting any such warranty or additional liability.\n'
         '\n'
         'END OF TERMS AND CONDITIONS\n'
         '\n'
         'APPENDIX: How to apply the Apache License to your work.\n'
         '\n'
         '   To apply the Apache License to your work, attach the following\n'
         '   boilerplate notice, with the fields enclosed by brackets "[]"\n'
         "   replaced with your own identifying information. (Don't include\n"
         '   the brackets!)  The text should be enclosed in the appropriate\n'
         '   comment syntax for the file format. We also recommend that a\n'
         '   file or class name and description of purpose be included on the\n'
         '   same "printed page" as the copyright notice for easier\n'
         '   identification within third-party archives.\n'
         '\n'
         'Copyright [yyyy] [name of copyright owner]\n'
         '\n'
         'Licensed under the Apache License, Version 2.0 (the "License");\n'
         'you may not use this file except in compliance with the License.\n'
         'You may obtain a copy of the License at\n'
         '\n'
         '   http://www.apache.org/licenses/LICENSE-2.0\n'
         '\n'
         'Unless required by applicable law or agreed to in writing, software\n'
         'distributed under the License is distributed on an "AS IS" BASIS,\n'
         'WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.\n'
         'See the License for the specific language governing permissions and\n'
         'limitations under the License.\n'
         '\n'}
DECLARED_APACHE_FALLBACKS = {"intl_pluralrules 7.0.2", "io_tee 0.1.1"}

# These reviewed crates carry additional per-file copyright/license notices
# beyond their root license files. Keep the original comment bytes, including
# ISC copyright holders in ring/webpki and the original Ryu author attribution.
# The archive checksum, rather than a possibly dirty VCS checkout, is the source
# of truth. Tests/examples are conservatively retained when present.
INLINE_NOTICE_PACKAGES = {
    "ring 0.17.14", "rustls-webpki 0.103.15", "untrusted 0.9.0", "ryu 1.0.23",
}
RING_LICENSE_FILES = {
    "LICENSE", "LICENSE-BoringSSL", "LICENSE-other-bits",
    "src/polyfill/once_cell/LICENSE-APACHE", "src/polyfill/once_cell/LICENSE-MIT",
    "third_party/fiat/LICENSE",
}


def inline_license_documents(files: dict[str, bytes]):
    """Preserve complete source comments carrying reviewed embedded notices."""
    suffixes = {".rs", ".c", ".h", ".inl", ".S", ".pl", ".asm"}
    notice = re.compile(
        rb"copyright|permission to use|permission is hereby|public[ \t\r\n/*#;]+domain|licensed under",
        re.IGNORECASE,
    )
    for name, raw in sorted(files.items()):
        if Path(name).suffix not in suffixes:
            continue
        # C/Rust blocks and consecutive line comments are copied losslessly.
        patterns = [rb"/\*.*?\*/", rb"(?:^[ \t]*//[^\n]*(?:\n|$))+"]
        if Path(name).suffix in {".pl", ".S", ".asm"}:
            patterns.append(rb"(?:^[ \t]*[;#][^\n]*(?:\n|$))+")
        matches = sorted(
            (match for pattern in patterns for match in re.finditer(pattern, raw, re.MULTILINE | re.DOTALL)),
            key=lambda match: match.start(),
        )
        for match in matches:
            if notice.search(match.group()):
                line = raw[:match.start()].count(b"\n") + 1
                yield f"crate:{name}:{line} (embedded copyright/license notice)", match.group()

def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def cargo(*args: str) -> str:
    return subprocess.check_output(
        ["cargo", *args, "--locked", "--offline", "--manifest-path", str(MANIFEST)],
        cwd=ROOT, text=True, encoding="utf-8",
    )


def registry_cache(package: dict) -> Path:
    source = Path(package["manifest_path"]).parent
    return source.parents[2] / "cache" / source.parent.name


def archive_files(package: dict, checksum: str, cache: Path | None = None) -> dict[str, bytes]:
    if cache is None:
        cache = registry_cache(package)
    archive = cache / f"{package['name']}-{package['version']}.crate"
    raw = archive.read_bytes()
    if sha256(raw) != checksum:
        raise ValueError(f"Registry archive checksum mismatch: {archive.name}")
    prefix = f"{package['name']}-{package['version']}/"
    with tarfile.open(fileobj=io.BytesIO(raw), mode="r:gz") as tar:
        return {
            entry.name.removeprefix(prefix): tar.extractfile(entry).read()
            for entry in tar.getmembers()
            if entry.isfile() and entry.name.startswith(prefix)
        }


def audited_hyper_util(package: dict, metadata_packages: list[dict]) -> tuple[dict[str, bytes], list[str]]:
    vendor = ROOT / HYPER_UTIL_PATH
    if (package["name"], package["version"]) != ("hyper-util", "0.1.20") or package["source"] is not None:
        raise ValueError("Unreviewed non-registry dependency")
    if Path(package["manifest_path"]) != vendor / "Cargo.toml" or vendor.resolve() != vendor:
        raise ValueError("Vendored hyper-util manifest is not at the audited, non-symlink path")
    # A path package has no registry source path in Cargo metadata. Locate its
    # cached upstream archive through other packages from the same registry;
    # the pinned archive checksum remains mandatory. Never fetch or trust an
    # unpacked registry directory as the upstream baseline.
    caches = sorted({registry_cache(p) for p in metadata_packages if p["source"] == REGISTRY})
    cache = next((c for c in caches if (c / "hyper-util-0.1.20.crate").is_file()), None)
    if cache is None:
        raise ValueError("Missing cached, reviewed hyper-util 0.1.20 registry archive")
    original = archive_files(package, HYPER_UTIL_ARCHIVE_SHA256, cache)
    current = {}
    for path in sorted(vendor.rglob("*")):
        if path.is_symlink():
            raise ValueError(f"Symlink in vendored hyper-util: {path.relative_to(vendor)}")
        if path.is_file():
            current[path.relative_to(vendor).as_posix()] = path.read_bytes()
        elif not path.is_dir():
            raise ValueError(f"Non-regular entry in vendored hyper-util: {path.relative_to(vendor)}")
    if not HYPER_UTIL_MODIFIED.keys() <= original.keys() or HYPER_UTIL_ADDED.keys() & original.keys():
        raise ValueError("Reviewed hyper-util change paths disagree with the upstream archive")
    if current.keys() != original.keys() | HYPER_UTIL_ADDED.keys():
        raise ValueError("Vendored hyper-util file inventory differs from the reviewed archive and additions")
    reviewed = HYPER_UTIL_MODIFIED | HYPER_UTIL_ADDED
    for name, raw in current.items():
        if name in reviewed:
            if sha256(raw) != reviewed[name]:
                raise ValueError(f"Unreviewed vendored hyper-util change: {name}")
            if name in original and raw == original[name]:
                raise ValueError(f"Reviewed hyper-util patch is missing: {name}")
        elif raw != original[name]:
            raise ValueError(f"Unexpected vendored hyper-util change: {name}")
    # Canonical unified diff: sorted paths, original/new UTF-8 lines retained,
    # a/ and b/ labels, and an empty original for the one added source file.
    patch = "".join(
        line
        for name in sorted(reviewed)
        for line in difflib.unified_diff(
            original.get(name, b"").decode("utf-8").splitlines(keepends=True),
            current[name].decode("utf-8").splitlines(keepends=True),
            fromfile=f"a/{name}", tofile=f"b/{name}",
        )
    ).encode("utf-8")
    patch_path = ROOT / HYPER_UTIL_PATCH
    provenance_path = ROOT / HYPER_UTIL_PROVENANCE
    if patch_path.is_symlink() or patch_path.read_bytes() != patch:
        raise ValueError("Vendored hyper-util patch file is missing, redirected, or stale")
    if provenance_path.is_symlink() or sha256(provenance_path.read_bytes()) != HYPER_UTIL_PROVENANCE_SHA256:
        raise ValueError("Vendored hyper-util provenance is missing, redirected, or unreviewed")
    rows = [
        f"  Build source: audited local patch at {HYPER_UTIL_PATH}",
        "  Changes: CodexQuotaTray passive system-proxy route diagnostics (MIT).",
        "  Unchanged files: byte-for-byte verified against the upstream archive.",
        f"  Local patch: {HYPER_UTIL_PATCH}",
        f"  Local patch SHA-256: {sha256(patch)}",
        f"  Patch provenance: {HYPER_UTIL_PROVENANCE}",
        f"  Patch provenance SHA-256: {HYPER_UTIL_PROVENANCE_SHA256}",
        "  Complete patched source and patch are included in the source distribution.",
    ]
    for name in sorted(reviewed):
        rows.append(f"  {'Modified' if name in original else 'Added'} source: {name}")
        if name in original:
            rows.append(f"    Upstream SHA-256: {sha256(original[name])}")
        rows.append(f"    Patched SHA-256: {reviewed[name]}")
    return original, rows


def select_license(expression: str) -> str:
    # Legacy Cargo slash spelling means OR. Explicitly choose permissive options.
    expression = expression.replace("/", " OR ")
    if expression == "(MIT OR Apache-2.0) AND Unicode-3.0":
        return "MIT AND Unicode-3.0"
    if expression in {"MIT", "BSD-3-Clause", "Unicode-3.0", "MPL-2.0", "Apache-2.0", "ISC", "Apache-2.0 AND ISC", "CDLA-Permissive-2.0"}:
        return expression
    if " AND " not in expression and "MIT" in expression.split():
        return "MIT"
    if expression in {"Apache-2.0 OR GPL-2.0-only", "Apache-2.0 OR BSL-1.0"}:
        return "Apache-2.0"
    raise ValueError(f"New or unreviewed license expression: {expression}")


def license_documents(package: dict, files: dict[str, bytes], selected: str):
    key = f"{package['name']} {package['version']}"
    # Preserve selected-license text, additional Unicode grants, COPYRIGHT and
    # NOTICE files, and nested component licenses. ring's composite grant is
    # not an OR choice: preserve BoringSSL, ISC, once_cell and fiat texts in full.
    candidates = []
    if key == "ring 0.17.14" and not RING_LICENSE_FILES.issubset(files):
        raise ValueError(f"Missing reviewed ring license documents: {sorted(RING_LICENSE_FILES - files.keys())}")
    for name, raw in sorted(files.items()):
        base = Path(name).name.lower()
        if not any(word in base for word in ("license", "copying", "copyright", "notice")):
            continue
        if key == "ring 0.17.14" and name in RING_LICENSE_FILES:
            candidates.append((f"crate:{name}", raw))
            continue
        if "gpl" in base or "unlicense" in base:
            continue
        if "apache" in base and selected != "Apache-2.0":
            continue
        if "mit" in base and "MIT" not in selected:
            continue
        if "bsd" in base and not selected.startswith("BSD"):
            continue
        if "boost" in base and selected != "BSL-1.0":
            continue
        # COPYING in MIT/Unlicense packages describes both alternatives, which
        # is useful provenance; keeping it does not select the Unlicense.
        candidates.append((f"crate:{name}", raw))
    if key in INLINE_NOTICE_PACKAGES:
        candidates.extend(inline_license_documents(files))
    if key == "bech32 0.9.1":
        header = files["src/lib.rs"].split(b"\n\n", 1)[0]
        candidates.append(("crate:src/lib.rs (license header)", header))
    fallback_entries = UPSTREAM_LICENSES.get(key, [])
    if key in DECLARED_APACHE_FALLBACKS:
        fallback_entries = [CANONICAL_APACHE]
    if fallback_entries:
        for entry in fallback_entries:
            raw = entry["text"].encode("utf-8")
            if sha256(raw) != entry["sha256"]:
                raise ValueError(f"Upstream license checksum mismatch: {key}")
            candidates.append((entry["url"], raw))
    if not candidates:
        raise ValueError(f"No license text found for {key}; audit upstream before releasing")
    return candidates


def generate() -> dict[Path, str]:
    lock_path = MANIFEST.with_name("Cargo.lock")
    lock_bytes = lock_path.read_bytes()
    manifest_bytes = MANIFEST.read_bytes()
    locked = tomllib.loads(lock_bytes.decode("utf-8"))
    checksums = {(p["name"], p["version"]): p.get("checksum") for p in locked["package"]}
    tree = cargo("tree", "--target", TARGET, "--edges", "normal,build", "--prefix", "none", "--format", "{p}")
    keys = set(re.findall(r"^(\S+) v(\S+)", tree, re.MULTILINE))
    metadata = json.loads(cargo("metadata", "--format-version", "1"))
    packages = [p for p in metadata["packages"] if (p["name"], p["version"]) in keys and p["name"] != "codex-quota-reader"]
    packages.sort(key=lambda p: (p["name"], p["version"]))
    if len(packages) != len(keys) - 1:
        raise ValueError("Cargo tree and metadata package sets disagree")
    documents = {}
    rows = []
    option_source = None
    uses_vendored_hyper_util = False
    for p in packages:
        key = (p["name"], p["version"])
        vendored_rows = []
        checksum = checksums[key]
        if key == ("hyper-util", "0.1.20") and p["source"] is None and checksum is None:
            files, vendored_rows = audited_hyper_util(p, metadata["packages"])
            checksum = HYPER_UTIL_ARCHIVE_SHA256
            uses_vendored_hyper_util = True
        elif p["source"] == REGISTRY and checksum:
            files = archive_files(p, checksum)
        else:
            raise ValueError(f"Non-registry dependency needs manual provenance review: {key}")
        selected = "Apache-2.0" if f"{p['name']} {p['version']}" in DECLARED_APACHE_FALLBACKS else select_license(p["license"])
        refs = []
        for origin, raw in license_documents(p, files, selected):
            digest = sha256(raw)
            documents.setdefault(digest, {"text": raw.decode("utf-8"), "uses": []})["uses"].append(f"{p['name']} {p['version']}: {origin}")
            refs.append((digest, origin))
        rows.extend([
            f"{p['name']} {p['version']}",
            f"  Registry: https://crates.io/crates/{p['name']}/{p['version']}",
            f"  Archive: https://static.crates.io/crates/{p['name']}/{p['name']}-{p['version']}.crate",
            f"  Archive SHA-256: {checksum}",
            f"  Declared license: {p['license']}",
            f"  Selected license: {selected}",
            f"  Repository: {p['repository'] or '(not declared)'}",
            f"  Authors: {'; '.join(p['authors']) or '(not declared; retain copyright notices below)'}",
        ])
        rows.extend(vendored_rows)
        if ".cargo_vcs_info.json" in files:
            vcs = json.loads(files[".cargo_vcs_info.json"])
            rows.append(f"  Source commit: {vcs['git']['sha1']}")
            if vcs["git"].get("dirty"):
                rows.append("  VCS tree marked dirty by publisher; verified registry archive is authoritative.")
        rows.extend(f"  License document SHA-256: {digest} ({origin})" for digest, origin in refs)
        rows.append("")
        if key == ("option-ext", "0.2.0"):
            # All original source/configuration files, losslessly represented as
            # UTF-8 text. Cargo's generated marker is not source.
            source_rows = [
                "option-ext 0.2.0 - MPL-2.0 source availability", "",
                "This distribution includes unmodified option-ext 0.2.0 code, licensed under",
                "Mozilla Public License 2.0. Its full source archive is available at:",
                "https://static.crates.io/crates/option-ext/option-ext-0.2.0.crate",
                f"Archive SHA-256: {checksums[key]}", "",
                "For offline access, every original archive file is reproduced below with its",
                "relative path and SHA-256. Recreate those files in an option-ext directory to",
                "obtain the complete source form; .cargo-ok is Cargo's local marker, not source.",
                "The source has not been modified. The MPL applies to option-ext covered files,",
                "not automatically to the separate CodexQuotaTray application or reader code.", "",
            ]
            for name, raw in sorted(files.items()):
                source_rows.extend(["=" * 78, f"FILE: {name}", f"SHA-256: {sha256(raw)}", f"UTF-8 bytes: {len(raw)}", "BEGIN ORIGINAL FILE", raw.decode("utf-8"), "END ORIGINAL FILE", ""])
            option_source = "\n".join(source_rows)
    header = [
        "CodexQuotaTray native quota reader - Rust dependency licenses", "",
        f"Target: {TARGET}",
        "Scope: Cargo normal + build dependency closure, including proc macros.",
        "Dev-only packages and packages exclusive to other targets are excluded.",
        "Build dependencies are included for conservative attribution; this inventory",
        "does not claim every build-time crate contributes machine code to the EXE.",
        f"Cargo.lock SHA-256: {sha256(lock_bytes)}",
        f"Cargo.toml SHA-256: {sha256(manifest_bytes)}",
        f"Resolved third-party packages: {len(packages)}", "",
        "License selection is explicit per package. Legacy MIT/Apache-2.0 slash",
        "expressions are alternatives. self_cell 1.3.0 is used under Apache-2.0,",
        "not its optional GPL-2.0 license. unicode-ident additionally requires",
        "Unicode-3.0. option-ext is MPL-2.0; its complete unmodified source and",
        "source-availability notice are in Codex-quota-reader-Option-ext-Source.txt.", "",
        "ring requires Apache-2.0 AND ISC. Its complete BoringSSL, ISC, once_cell",
        "and fiat license files and embedded source notices are retained below.",
        "Embedded notices from webpki, untrusted and Ryu are also retained. Ryu",
        "is used under Apache-2.0. webpki's excluded third-party/chromium test",
        "corpus is not present in its published source archive or this build.", "",
        "webpki-roots contains root-certificate data derived from CCADB. Its",
        "CDLA-Permissive-2.0 agreement is reproduced in full with this distribution.", "",
        "License texts are reproduced from SHA-256-verified crates.io archives.",
        *([
            "hyper-util 0.1.20 uses the explicitly audited local patch recorded below;",
            "its upstream MIT license remains complete and unchanged in this bundle.",
        ] if uses_vendored_hyper_util else []),
        "Files omitted by a published archive are recovered from its pinned upstream",
        "source commit where explicitly indicated. intl_pluralrules and io_tee omit",
        "license files but declare Apache-2.0; its standard terms are reproduced from",
        "the verified aead archive, retaining their own metadata. Texts are deduplicated",
        "by SHA-256; each package above references the complete applicable texts below.",
        "This notice covers these components only, not the entire Codex CLI or a",
        "blanket license for the tray application. No upstream code was relicensed.", "",
        "PACKAGE INVENTORY", "=" * 78, "",
    ]
    out = header + rows + ["LICENSE TEXTS", "=" * 78, ""]
    for digest, doc in sorted(documents.items()):
        out.extend(["=" * 78, f"DOCUMENT SHA-256: {digest}", "Applies to:", *[f"  {u}" for u in doc["uses"]], "", doc["text"], ""])
    if lock_path.read_bytes() != lock_bytes:
        raise ValueError("Cargo.lock changed during generation; retry after dependencies freeze")
    if MANIFEST.read_bytes() != manifest_bytes:
        raise ValueError("Cargo.toml changed during generation; retry after dependencies freeze")
    outputs = {ROOT / "third_party/Codex-quota-reader-Dependencies.txt": "\n".join(out)}
    if option_source is not None:
        outputs[ROOT / "third_party/Codex-quota-reader-Option-ext-Source.txt"] = option_source
    return outputs


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if generated dependency notices differ")
    args = parser.parse_args()
    try:
        outputs = generate()
        for path, text in outputs.items():
            if args.check:
                if not path.exists() or path.read_bytes() != text.encode("utf-8"):
                    raise ValueError(f"Notice is missing or stale: {path.relative_to(ROOT)}")
            else:
                path.write_text(text, encoding="utf-8", newline="\n")
            print(f"{'Verified' if args.check else 'Wrote'} {path.relative_to(ROOT)} ({len(text.encode('utf-8')):,} bytes)")
        return 0
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print(f"License audit failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
