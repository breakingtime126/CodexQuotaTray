# Tray icon artwork previews

These files are **code-rendered artwork**, not captures of an installed Windows app. The light and dark strips on the contact sheet are illustrative surfaces. The runtime and PNG previews use the exact same `TrayIconRasterizer` C# implementation.

![Tray artwork contact sheet](contact-sheet.png)

## Regenerate

From the repository root, using the .NET 8 SDK:

```sh
dotnet run --project tools/CodexQuota.Preview -- docs/previews
python scripts/preview-sheet.py docs/previews
```

The C# renderer and preview project have no NuGet or platform drawing dependencies. The optional contact-sheet compositor requires Python and Pillow; its labels use DejaVu Sans if available. It never redraws the icons. A restricted build environment may need a writable `DOTNET_CLI_HOME` and `NUGET_PACKAGES` directory.

## Included states and sizes

- Remaining allowance: 0%, 7%, 68%, and 100%
- Unknown: question mark and a neutral dashed ring
- Stale: retained 68% and an amber dashed ring
- Each state has transparent PNGs at 16, 20, 24, 32, and 64 pixels
- Each ICO includes all five PNG-backed sizes, with 32-bit RGBA
- `manifest.json` records filenames, dimensions, and represented states
- `contact-sheet.png` shows 16/20/24/32 px artwork at 4× nearest-neighbor magnification and 1× native size, plus light/dark surface checks

## Runtime API

Namespace: `CodexQuota.Core`

```csharp
RgbaImage image = TrayIconRasterizer.Render(20, 68);
byte[] png = TrayIconRasterizer.EncodePng(image);
byte[] ico = TrayIconRasterizer.EncodeIco(68);
byte[] unknown = TrayIconRasterizer.EncodeIco(null);
byte[] stale = TrayIconRasterizer.EncodeIco(68, stale: true);
```

`RgbaImage` exposes `Width`, `Height`, and `Pixels` (top-to-bottom straight-alpha RGBA8). `Render` accepts square dimensions from 16 to 256 and an integer percentage from 0 through 100, or null. Invalid ranges throw `ArgumentOutOfRangeException`.

## Small-size design

Mint indicates **remaining** allowance, clockwise from twelve o'clock. Zero has no mint arc; 100% fills the ring. A dark backing keeps the white hand-coded pixel glyphs legible against both light and dark taskbars. Circles use 8×8 supersampling; glyphs align to the pixel grid. The icon omits `%` so the center can use larger, bold digits; the tooltip and menu retain the percent sign. At 16 px, two-digit values use bold 5×7 forms (7 px high instead of the earlier 5 px). The value 100 uses narrow digits to preserve spacing inside the same ring. Unknown and stale use a distinct dashed pattern as well as a different color.

Text is necessarily tiny at native tray sizes. The app's tooltip and menu should provide the full value, freshness, selected quota window, and errors. Artwork review does not replace Windows taskbar/DPI testing.

## Verification

The preview executable checks RGBA length/transparency, PNG signatures/dimensions, and ICO directory entries. Generated PNGs and all embedded ICO frames were also independently decoded with Pillow. Visual review covered the labeled contact sheet, including the number 100, larger digits, stale/unknown, and both background surfaces.
