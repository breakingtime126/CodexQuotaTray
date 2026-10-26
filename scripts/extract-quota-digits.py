#!/usr/bin/env python3
from fontTools.ttLib import TTFont
from fontTools.pens.basePen import BasePen
from fontTools.pens.boundsPen import BoundsPen
from pathlib import Path
import argparse
import hashlib
from fontTools.varLib.instancer import instantiateVariableFont

parser = argparse.ArgumentParser(description="Extract the licensed Quota Digits vector subset; not needed to build or run the app.")
parser.add_argument("font", type=Path, help="Official InterVariable.ttf from rsms/inter v4.1")
parser.add_argument("--output", type=Path, default=Path("src/CodexQuota.Core/QuotaDigitGeometry.cs"))
args = parser.parse_args()
expected = "4989b125924991b90d05b2d16e0e388c48f7d5bb8b30539bbf9c755278d0ccaf"
if hashlib.sha256(args.font.read_bytes()).hexdigest() != expected:
    raise SystemExit("Source font SHA-256 does not match official Inter 4.1; refusing extraction")
f = instantiateVariableFont(TTFont(args.font), {'wght': 600, 'opsz': 14}, inplace=False)
gs = f.getGlyphSet()
class Pen(BasePen):
 def __init__(self): super().__init__(gs);self.v=[]
 def _moveTo(self,p): self.v += [0,*map(round,p)]
 def _lineTo(self,p): self.v += [1,*map(round,p)]
 def _qCurveToOne(self,c,p): self.v += [2,*map(round,c),*map(round,p)]
 def _closePath(self): self.v += [3]
lines=['// Quota Digits: modified numeric outline subset of Inter 4.1 (wght 600, opsz 14).', '// Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter).', '// The outline data in this file is licensed under SIL OFL 1.1.', '// See third_party/QuotaDigits-OFL.txt and third_party/QuotaDigits-PROVENANCE.md.', '// This renamed derivative is not an installable font; no font library is needed at runtime.', 'namespace CodexQuota.Core;', '', 'internal static class QuotaDigitGeometry', '{', '    internal readonly record struct Glyph(int Advance, int Left, int Right, short[] Path);', '    // Commands: 0 move, 1 line, 2 quadratic curve, 3 close. Source Y points upward.', '    internal static Glyph Get(char character) => character switch', '    {']
for index, c in enumerate('0123456789?'):
 g=gs[f.getBestCmap()[ord(c)]];p=Pen();g.draw(p);b=BoundsPen(gs);g.draw(b)
 lines.append(f"        '{c}' => new({g.width}, {round(b.bounds[0])}, {round(b.bounds[2])}, Paths[{index}]),")
lines += ['        _ => throw new ArgumentOutOfRangeException(nameof(character))', '    };', '', '    private static readonly short[][] Paths =', '    [']
for c in '0123456789?':
 g=gs[f.getBestCmap()[ord(c)]];p=Pen();g.draw(p)
 lines.append(f"        // {c}")
 chunks=[', '.join(map(str,p.v[i:i+24])) for i in range(0,len(p.v),24)]
 lines.append('        ['+',\n         '.join(chunks)+'],')
lines += ['    ];','}']
args.output.write_text('\n'.join(lines)+'\n')
