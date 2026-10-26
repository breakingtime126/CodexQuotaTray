#!/usr/bin/env python3
"""Lay out C#-generated PNGs. Requires Pillow; never redraws or alters the icon artwork."""
from pathlib import Path
import argparse
from PIL import Image, ImageDraw, ImageFont

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("directory", nargs="?", default="docs/previews")
args = parser.parse_args()
root = Path(args.directory)
fontroot = Path("/usr/share/fonts/truetype/dejavu")
def font(size, bold=False):
    path = fontroot / ("DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf")
    return ImageFont.truetype(str(path), size) if path.exists() else ImageFont.load_default(size=size)

W, H = 1440, 1325
canvas = Image.new("RGB", (W, H), "#0B111B")
d = ImageDraw.Draw(canvas)
white, muted, mint = "#F0F5FA", "#97A8BC", "#65ECB7"
d.text((44, 30), "CODEX / QUOTA TRAY", font=font(13, True), fill=mint)
d.text((42, 55), "Sharper at every tray size", font=font(34, True), fill=white)
d.text((44, 105), "The exact C# runtime renderer. Smooth vector digits; percentage stays in the tooltip.", font=font(16), fill=muted)
d.text((44, 132), "4× nearest-neighbor detail above · actual 1× artwork below · transparent PNGs", font=font(13), fill=muted)

states = [("0", "0%", "empty"), ("7", "7%", "single digit"), ("11", "11%", "narrow pair"),
          ("68", "68%", "open counters"), ("88", "88%", "paired counters"), ("97", "97%", "diagonal test"),
          ("100", "100%", "three digits"), ("unknown", "Unknown", "unconfirmed"), ("stale-68", "Stale 68%", "last known")]
left, cw, gap, top, rh = 112, 136, 8, 225, 178
for col, (_, title, subtitle) in enumerate(states):
    cx = left + col * (cw + gap) + cw // 2
    d.text((cx, 181), title, font=font(18, True), fill=muted if col >= 7 else white, anchor="mm")
    d.text((cx, 206), subtitle, font=font(11), fill=muted, anchor="mm")

for row, size in enumerate((16, 20, 24, 32)):
    y = top + row * rh
    d.text((47, y + 59), str(size), font=font(28, True), fill=white)
    d.text((46, y + 92), "px", font=font(14), fill=muted)
    for col, (key, _, _) in enumerate(states):
        x = left + col * (cw + gap)
        d.rounded_rectangle((x, y, x + cw, y + rh - 8), radius=13, fill="#141E2B", outline="#223143")
        icon = Image.open(root / f"quota-{key}-{size}.png").convert("RGBA")
        assert icon.size == (size, size)
        zoom = icon.resize((size * 4, size * 4), Image.Resampling.NEAREST)
        canvas.paste(zoom, (x + (cw - zoom.width) // 2, y + (132 - zoom.height) // 2), zoom)
        canvas.paste(icon, (x + (cw - size) // 2, y + 136 + (32 - size) // 2), icon)
        d.text((x + cw - 28, y + 152), "1×", font=font(10), fill=muted, anchor="mm")

base = 958
d.line((44, base - 7, W - 44, base - 7), fill="#28384B", width=1)
d.text((44, base + 5), "Light + dark surface check", font=font(16, True), fill=white)
d.text((44, base + 34), "Illustrative surfaces, not Windows screenshots", font=font(12), fill=muted)
for col, bg in enumerate(("#202B39", "#F0F2F5")):
    x = 480 + col * 345
    d.rounded_rectangle((x, base + 4, x + 318, base + 78), radius=12, fill=bg)
    for i, size in enumerate((16, 20, 24, 32)):
        icon = Image.open(root / f"quota-97-{size}.png").convert("RGBA")
        cx = x + 43 + i * 74
        canvas.paste(icon, (cx - size // 2, base + 14 + (32 - size) // 2), icon)
        d.text((cx, base + 59), f"{size}px", font=font(10), fill="#97A8BC" if col == 0 else "#516172", anchor="mm")
d.text((44, 1294), "Green >30%     Amber 10–30%     Red <10%     Slate dashes = unknown / stale", font=font(13), fill=muted)
d.text((44, 1073), "One brief ring glint", font=font(16, True), fill=white)
d.text((44, 1102), "Digits stay steady. The highlight only follows the real quota arc.", font=font(13), fill=muted)
for index in range(7):
    icon = Image.open(root / f"quota-glint-97-{index}.png").convert("RGBA")
    canvas.paste(icon, (610 + index * 108, 1070), icon)
    d.text((626 + index * 108, 1121), f"{index}/6", font=font(11), fill=muted, anchor="mm")
d.text((44, 1182), "Exact warning boundaries", font=font(16, True), fill=white)
d.text((44, 1211), "Fresh quota only. Old readings use neutral dashes.", font=font(13), fill=muted)
for index, value in enumerate((9, 10, 30, 31)):
    icon = Image.open(root / f"quota-{value}-32.png").convert("RGBA")
    x = 610 + index * 150
    canvas.paste(icon, (x, 1180), icon)
    d.text((x + 16, 1231), f"{value}%", font=font(13), fill=muted, anchor="mm")
output = root / "contact-sheet.png"
canvas.save(output)
print(output.resolve())
