# Quota Digits outline provenance

`src/CodexQuota.Core/QuotaDigitGeometry.cs` contains **Quota Digits**, a renamed,
modified subset of the Inter 4.1 font, containing only `0123456789?` as numeric
quadratic paths. The outline data remains licensed under the SIL Open Font
License 1.1; the complete original copyright notice and license accompany it in
`QuotaDigits-OFL.txt`. This derivative does not use the reserved font name as its
name and is not an installable font. Inter is credited here solely as the source.

- Original project: https://github.com/rsms/inter
- Pinned release: https://github.com/rsms/inter/tree/v4.1
- Source binary: https://raw.githubusercontent.com/rsms/inter/v4.1/docs/font-files/InterVariable.ttf
- Source license: https://raw.githubusercontent.com/rsms/inter/v4.1/LICENSE.txt
- Binary SHA-256: `4989b125924991b90d05b2d16e0e388c48f7d5bb8b30539bbf9c755278d0ccaf`
- Original copyright: Copyright (c) 2016 The Inter Project Authors
  (https://github.com/rsms/inter)

## Modifications

The variable source was instantiated with `wght=600` (semibold), `opsz=14`.
Only the eleven requested glyph outlines and their horizontal metrics were
retained. Composite quadratic segments were decomposed to simple quadratic
curves, coordinates were rounded to font units, and paths were encoded as
small C# integer arrays. The renderer then optically fits these outlines into
native tray sizes and converts them to cached grayscale coverage masks. No
bitmap alphabet, installed font, proprietary system-font outline, font engine,
or full font binary is used or distributed at runtime.

## Reproduction

Download the pinned source binary above, verify its SHA-256, and run
`python3 scripts/extract-quota-digits.py /path/to/InterVariable.ttf` from the
repository root. This optional source-maintenance step requires FontTools; the
application's build and runtime do not. The script verifies the source hash
before extracting the paths. Keep the OFL license with any redistributed copy
of the outlines, source code, or application that embeds them.
