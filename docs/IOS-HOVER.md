# Native iOS-style hover card

Version 0.2.4, 2026-10-08.

## Design and implementation

The approved CodexQuotaTray-iOS-hover-concept.png is the visual reference. The app paints real data into a compact milk-white rounded panel with an opaque text surface with a soft shadow, large quota, rounded progress, reset date and a separated ticket list. It does not embed the concept image, its example 90% quota, its card count or sample dates.

`HoverCardModel` maps the existing validated quota state into structured presentation. Plus keeps five-hour and weekly slots, with explicit missing slots; all Pro-family variants show weekly only. Unknown and zero differ. Missing reset timestamps, missing/partial card details, non-expiring cards, expired cards and old snapshots stay explicit. The official `availableCount` remains the reported quantity; an expired detail is marked 已到期 rather than described as usable. The card cannot redeem a credit.

Quota tones use the existing displayed-percent thresholds: >30 green, 10–30 amber, <10 red, unknown/stale gray. Expiry warning tones use injected-clock duration: ≤7 days amber, ≤24 hours red (including already expired); unknown/non-expiring neutral. Old card snapshots gray their expiry tones. The scheduler wakes at relevant exact thresholds without an idle visual polling loop.

The original compact tray rasterizer, startup policy, fixed-GUID fallback registration, hover refresh/backoff and icon-monitor placement are retained. The card has no Pro badge. The header ring represents the actual selected quota, including a Plus weekly fallback.

## Native behavior

- Windows built-in WinForms/GDI+; no web view, browser, image asset, additional runtime, package download or DLL beside the shipped single EXE.
- WS_EX_NOACTIVATE + ShowWithoutActivation + MA_NOACTIVATE. Right-click intentionally opens the existing focusable context menu.
- A per-pixel layered window supplies rounded antialiasing and soft shadow. The milky body is opaque so small text has a stable background; only antialiased edges and shadow are translucent; it does **not** implement true backdrop blur/Acrylic. A failed layered presentation switches to an opaque rounded fallback; high contrast uses Windows colors.
- One viewport-sized bitmap, not a bitmap for the entire long list. Visible rows are computed directly; 10,000 synthetic rows are scrollable through the final row. The scrollbar is a custom thin track with drag and wheel support.
- Pointer departure uses the existing 200 ms crossing grace, 50 ms hover-only sampler. Escape, outside click, foreground change, opening a menu and application lifecycle dismiss the card. Extremely brief input can fall between samples and remains a Windows acceptance concern.
- Drag capture is supplemented by the existing pointer sampler, including left-button release and movement beyond the card. Mouse entry takes over a keyboard-opened Shell session.
- Content updates preserve/clamp the logical scroll position and do not recreate the card. DPI layout uses the icon's monitor and physical work area. Time-zone/time setting changes invalidate cached local date strings.

## Visual QA

`tools/CodexQuota.HoverPreview` links the **same production painter**. It creates synthetic weekly, Plus, low/zero/full quota, warning/expired/unknown/non-expiring cards, stale, missing and 10,000-row fixtures at 100%, 125%, 150% and 200%. The Linux QA path uses development-only System.Drawing.Common 6 + libgdiplus and Noto CJK/DejaVu fonts; these are never shipped in the Windows EXE. Generated PNGs are renderer evidence, not native Windows/Shell screenshots.

The renderer checks do not verify Win32 layered composition, actual Windows fonts, OS hit testing, focus/input, high-contrast integration, DPI message ordering, Explorer or account behavior. See LIGHTWEIGHT-QA.md for the pending native checks.

## Release version ordering

Every future release must increment the numeric Version, AssemblyVersion and FileVersion in Directory.Build.props together. Library revision numbers and filenames do not establish upgrade ordering. This release is 0.2.0 / 0.2.0.0. Launching an identical version from another directory intentionally keeps the existing process and startup path; only a strictly newer protocol-enabled release can take over. The first transition from old v6 / 0.1.0 still needs a manual exit.

## Matching native context menu (0.2.1)

The context menu now uses a light milky surface, rounded region, subtle blue selection, mint checked marker and more generous item spacing. It remains the same ContextMenuStrip with the original functions, first-open foreground path, AutoClose, Escape and icon-monitor anchoring. The shared painter has 16 synthetic previews covering checked/unchecked, normal/high contrast and four scales; native ToolStrip layout and message routing require Windows verification. Preview row layout is a fixture, not a running Windows ToolStrip.

DefaultPadding preserves native check/image gutter reservations; added row padding is accompanied by vertically recentered text/check painting. Rounded Region is regenerated on resize/DPI. DropShadowEnabled requests the system menu shadow; OS/theme/session can affect it, and no guaranteed DWM backdrop effect is claimed. The ordinary menu font is owned/disposed with the existing menu lifetime. Numeric release version is now 0.2.1 / 0.2.1.0.

## Compact size and native text (0.2.3)

The user's Windows screenshot showed an approximately384×473px visible body, matching the old96DPI base design rather than demonstrating a second DPI scale. The new visible body is280px wide, outer window300px including10px shadow on each side. Three-card Pro is300×342px; Plus uses two adjacent quota blocks and is300×350px. Each Plus block keeps its own percentage, progress, full reset date and clock, split across lines instead of truncating the timestamp. The cloud menu fixture is320×319px; actual ToolStrip width remains content-dependent. No new local measurement was attempted after the user canceled that inspection.

Body text is13px at96DPI, close to the native menu's9.5pt (~12.7px). Main percentage is46px for a single window and36px in the dual-window layout. Font size and text bounds are rounded in physical pixels once per DPI; measurement uses the same physical font as drawing. The card is rebuilt at the monitor scale and text tiles are never resized.

Windows runtime injects NativeHoverTextPainter: TextRenderer paints on an opaque24-bit RGB tile, then the tile is copied unscaled into the layered PArgb frame. This deliberately avoids GDI writing/zeroing alpha directly on an ARGB target. The menu's opaque native HWND uses TextRenderer directly. Both retain ClearType-compatible opaque backgrounds and source-over composition. Card outline is repainted after text so scrolled tiles cannot cover the hairline edge.

Linux previews now share production geometry, colors and layout but use an explicitly labeled GDI+ fallback text backend and substitute fonts. They validate layout, not Windows GDI sharpness. FrameworkSmoke additionally links the production painter/native text backend and checks once-per-DPI sizing, opaque text pixels and visible digits offscreen for Pro/Plus at96/120/192DPI; these Windows-only checks were compiled but not executed by the cloud task.

## Reset-card seconds (0.2.4)

Only reset-card expiry formatting changed: `M月dd日 HH:mm:ss`, with `yyyy年` when the local year differs. UI and accessible/fallback text share `ResetCard.ExpiryText`; parser timestamps, warnings, status, sorting and normal quota reset times remain unchanged. Missing/invalid dates and non-expiring cards do not invent a clock. The card remains280px wide with32px rows; long cross-year expiry text uses12px rather than13px to keep the complete timestamp beside its status.

## System theme and compact hierarchy (0.2.7)

The approved light/dark reference was inspected directly. The unwanted pacing indicator and use-card warning box are omitted. The 280px card uses a small status dot, restrained separators, relative quota reset plus complete absolute reset time, two-line reset-card rows, and the model/TPS/sample timestamp in a48px footer. The header explicitly labels the quota refresh; it is not the sample time. Three-card Pro is300×380px including shadow and TPS; Plus is300×416px. At125/150/200% every dimension/text font is painted once at physical DPI, never bitmap-resized.

Expiry dates use one fixed12px size and the full second line; all original seconds/day padding/year rules remain. Relative durations floor remaining time, use days+hours or hours+minutes, and become “已到期” only at/past the actual instant. Countdown refresh uses existing scheduling at minute resolution plus exact warning/expiry transitions.

WindowsAppTheme reads only HKCU AppsUseLightTheme. Missing, malformed, inaccessible or older-system preferences fall back to light. WM_SETTINGCHANGE/WM_THEMECHANGED invalidate the surfaces; Windows high-contrast colors override the application palette. The menu uses the same light/dark direction while retaining ToolStrip behavior. No system settings are written. Native GDI RGB text tiles and per-pixel edges/shadows remain unchanged.

Cloud visual checks use the production geometry/palette with substitute fonts, not native Windows screenshots. Theme broadcasts, registry preference variants, ClearType and HWND interaction remain Windows acceptance items.

## Single-line refinement (0.2.8)

The user's single-line crops were inspected directly. Each quota reset is now one left-relative/right-absolute row, without the repeated reset caption. Plus retains two percentage/progress columns followed by two full-width, explicitly scoped reset rows. Every reset card is one 28px row: remaining days plus hours on the left and the complete local expiry on the right. Dates keep day padding, seconds and a cross-year year; when the Chinese form does not fit, a numeric date is used at the same fixed 12px size. No date precision is discarded.

The footer is one 32px row: historical model, confirmed historical reasoning effort in Chinese and estimated TPS. Old/unavailable samples retain a short 上次 marker; complete sample timestamp, token scope and raw metadata remain in accessible/fallback text. Only the model may ellipsize. Recovery-in-progress and a reached safety limit have distinct labels, rather than masquerading as absent metadata.

Visible width remains280px. Three-card Pro with TPS is300×320px including shadow; Plus is300×354px. The native GDI text backend, theme handling and menu/input lifecycle are unchanged.

## 0.2.9 grouped speed footer

Model, real historical effort and speed form one naturally spaced left-aligned phrase. Only a long model truncates; reset rows and card expiry rows retain their existing two-ended alignment. Card dimensions, DPI-aware native text and themes are unchanged. The context menu adds a user-opened, persistent connection-diagnostics snapshot with allowlisted fields only.
