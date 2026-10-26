# Tray clarity and motion update

The reviewed user screenshot showed a smooth ring but deliberately blocky 5×7 digits. The primary issue was the glyph geometry, not an assumption about remote transport. The current remote Windows session reported 192 DPI (200%), so its icon is rendered at 32 physical pixels.

## Adopted design improvements

- Smooth compact semibold vector glyphs, grayscale coverage, and per-size optical fitting replace bitmap rectangles
- Text centers on actual outline ink boundaries rather than a font line box. The 16px two-digit fit has a small optical inset
- `100` keeps the same cap height and condenses horizontally; it does not become smaller vertically
- Native small-icon dimensions come from GetSystemMetricsForDpi, and each HICON contains exactly the requested-size frame
- A successful read may briefly highlight the actual filled ring; a changed quota endpoint eases for roughly 200 ms. Digits switch to the true value once and stay still
- At most ~15 animation frames per second,420 ms total sweep. Idle animation timer is stopped; the refresh scheduler uses one-shot wakes rather than an idle repaint loop
- Failure, unknown, stale and zero states never get a fake success/full-ring animation. Windows client-area animation preferences are respected
- Fresh quota uses green above 30%, amber from 10% through 30%, and coral red below 10%. The current reading controls the hue even while the previous arc length eases toward it; the brief glint lightens that same hue. Unknown or stale uses neutral gray dashes, distinct from low quota. At 0% the unfilled track remains visible without a fake red progress dot

## References and licenses

The glyph data is a renamed numeric subset of official Inter 4.1 under SIL OFL. Full license and reproducible source provenance are in `third_party/QuotaDigits-*`.

Reviewed MIT source: [NotNull92/codex-weekusage-tray, native/src/tray_icon.cpp](https://github.com/NotNull92/codex-weekusage-tray/blob/main/native/src/tray_icon.cpp), specifically its ink-bound fitting, supersampled grayscale coverage, and horizontal condensation while retaining height. Those clarity techniques informed this implementation; the runtime renderer remains the compact C# vector implementation. The upstream MIT notice is retained in `third_party/NotNull92-codex-weekusage-tray-MIT.txt`.

Retrieved 2026-10-05; reviewed source SHA256: 83e9ff30f281dbefc49c34a2b24166faafd426c087c79f358ec391eba555067c.

## Menu dismissal

The reported stuck popup was the right-click ContextMenuStrip, not the native tooltip. The menu now creates its visible HWND before activation, explicitly takes ordinary foreground/focus, and completes Shell cleanup after Closed. Outside-focus/cancel messages are generation-checked so old closes cannot dismiss a reopened menu. If Windows rejects foreground activation, that popup is closed rather than left orphaned. Outside clicks do not return focus away from the selected application. No global hooks, focus-stealing loops, or input-queue attachment are used.

A click-opened menu intentionally remains open when the pointer merely leaves it; outside click and Escape dismiss it. The hover card has its own nonactivating lifecycle; see [IOS-HOVER.md](IOS-HOVER.md).

Cloud PNGs and state-machine tests do not replace Windows interaction QA. Test the first **real** right-click after process startup, then blank desktop/taskbar/another app, Escape, repeated reopen, overflow, and Win+B. Synthetic callbacks may lack foreground permission and are supplementary evidence only.

## 2026-10-08 iOS-style hover card

The approved hover concept was inspected at full pixels. It is the sole visual target for the new card; an incompletely downloaded user attachment was explicitly excluded. The native card follows its milky rounded surface, large quota typography, mint progress, calendar row and individually separated expiry rows. Live warning colors override mint when the quota is low or a card is near expiry. The compact tray icon remains unchanged.

The production GDI+ painter, rather than a second mockup implementation, generates the previews under `docs/previews/hover`. Linux libgdiplus uses substitute Noto CJK / DejaVu fonts for QA. Windows uses Microsoft YaHei UI / Segoe UI. Per-pixel alpha supplies soft edges and shadow without third-party runtime assets. This is a graceful glass-like fallback, not real backdrop blur.
