# 0.2.30 verification

Baseline: delivered 0.2.29 commit 67c79cd81a21ef107520b6ec6bc02ac224bc471a.

## Diagnostic-only change

The existing connection diagnostic window now includes an immutable public-reset
request snapshot. Current and last-completed attempts remain separate. It reports
safe timestamps, outcome, HTTP status, stage, elapsed time, fixed failure categories,
numeric error codes, parse/data presence, visible/hidden reasons and next-poll time.
There is no exception text, response body, private data, address or disk log.
Opening the dialog only formats the existing snapshot and never starts a request.

The diagnostic additions do not change transport/TLS policy, polling, retries,
normal hover/tray artwork, quota, or speed behavior. Added offline tests cover
success, 304, HTTP errors, schema/body failures, timeout, caller cancellation,
safe exception classification, separate in-flight/completed state, clock-ahead
and null data, missing latest data despite a visible forecast, unknown enum values,
and hostile exception messages that must never reach diagnostic text.

The final pipeline reruns the complete portable suite, builds net8.0-windows and
net48, compiles FrameworkSmoke, and verifies the exact release EXE/embedded Core
and source archive against the clean commit. Exact counts and hashes are recorded
in the release receipt. The unchanged renderer retains the 0.2.29 evidence below;
this diagnostic-only release does not claim a new Windows screenshot/runtime run.

The provided browser JSON is accepted, and a cloud production-client probe
returned success followed by a valid 304 with the latest row visible. Neither
proves connectivity from the user's .NET Framework process. The user's missing-row
cause remains unresolved pending this new local diagnostic evidence; this release
does not claim to fix it.

## Retained 0.2.29 verification

Baseline: delivered 0.2.28 commit bd3a52b0271a40e8eb21def634bc64afde2ba616.

## Public reset feed

The optional anonymous client only requests the fixed Codex Resets HTTPS status
endpoint, independently of account quota, hover/manual refresh and TPS. Each
response is bounded to 64 KiB and 12 seconds; redirects, authentication and cookies
are disabled. Requests revalidate with no-cache and ETag. A 304 is accepted only
when a corresponding cached snapshot and sent validator exist. Failure hides the
entire optional presentation, including both dots, with no blank layout space.

Offline tests cover v1 envelope parsing, explicit nulls vs malformed payloads,
nullable source/timestamps, untrusted URLs, unknown authors, expired forecasts,
scheduled priority and no invented completion, deadline/cancellation/late-response
cleanup, oversized bodies, content type, rate limits, Retry-After, ETag/304,
clock/date extremes, stable event identity and bounded scheduling. Live API access
in the cloud is not treated as evidence that the user's Windows machine can
connect. No live non-null forecast has been observed; forecast fixtures are
explicitly synthetic schema-based cases.

Only direct validated Tibo X/Twitter status URLs are opened for original posts;
the row labels separately link to Codex Resets with source-attribution tooltips.
Missing source URLs disable value links. The complete forecast is available in
the label/value tooltip and accessibility text even when the visible text uses a
measured prefix ellipsis. The popup checks DPI, scroll, clipping, scrollbar and
matching mouse-down/up targets, and revalidates current data at activation. It
closes/suppresses itself before launching a URL. Tests never launch a browser.
Keyboard users have equivalent original-source actions in the existing tray menu.

The forecast remains marked in the hover card while valid. Only unread effective
forecasts mark the tray icon. Tray selection or a successful forecast-source
launch acknowledges the event. At most 32 event hashes persist; API content and
user information do not. Repeated polls, chance/text edits and temporary failures
cannot re-notify the same acknowledged event.

## Quota behavior

Window visibility comes from actual valid durations, not plan labels or primary/
secondary slot order. 300/10080-minute windows retain the existing side-by-side
layout; one real window uses one column. Unknown durations stay unknown, zero is
real quota, and unavailable reset time does not remove quota. Original preferred
tray-window selection remains with a valid fallback when the preference is absent.
Malformed snapshots become ProtocolError and retain trusted stale state; valid
absence removes obsolete rows. Regression tests cover renamed/missing plans,
reversed slots, disappearance, malformed partial objects and transient failures.

Backend cleanup ownership/retry, private Job handling, Desktop trust verification,
portability/single-EXE loading and per-response TPS parsing retain their previous
behavior. Narrow reader allocation/I/O optimizations are covered separately in
[PERFORMANCE.md](PERFORMANCE.md), including the unresolved Desktop hash cost.

## Build, tests and renderer checks

The final release pipeline builds net8.0-windows and net48, runs the complete
portable suite, compiles FrameworkSmoke, and builds the shared production hover
renderer preview. Exact counts and hashes are in the release receipt. The EXE is
checked for AMD64, assembly/file/product versions and final commit, byte-identical
embedded Core, embedded dependencies/licenses, and asInvoker/uiAccess=false/
PerMonitorV2 manifest. The source ZIP is compared byte-for-byte with every tracked
file at the same commit.

Production GDI+ preview fixtures include light/dark/high-contrast modes at 100%,
125%, 150%, 200% scaling; single/double quota windows; latest-only, forecast-only,
no-link, long forecast, expired and failed feed cases. Intentional value truncation
uses measured ellipsis; all other measured text fits. Offline and expired-feed
PNG output is byte-identical to the corresponding no-feature baseline. Tray badge
artwork is checked at 16, 20, 24, 32, 48, 64 pixels for unchanged percentage digits and
unchanged pixels outside the small upper-right region.

## Limits

Linux previews use synthetic data and Linux fonts; they are not Windows screenshots.
FrameworkSmoke is compiled, not executed on Windows. Native hover/click/browser
launch, screen readers, Explorer and multi-monitor behavior still need Windows
validation. No real Codex account, credentials, private rollout logs or model
request were used. No user computer was modified, no GitHub publication occurred,
and no Windows whole-process CPU/RAM/disk measurement is claimed.
