# TPS discovery cadence (unpublished candidate)

The default full-tree discovery interval changes from 3 to 15 seconds. Already
tracked files retain their normal 3-second incremental polling. Pending directory
traversal, history catch-up, read budgets, fingerprints and safety checks are
unchanged. This reduces repeat enumeration of sessions and archived_sessions.

Tradeoff: new or currently untracked sessions may wait roughly 15 seconds before
being discovered, plus polling alignment and traversal time. This is not a hard
15-second end-to-end bound. Existing tracked sessions are not delayed.

## Verification

- 1790/1790 core tests passed on Linux/.NET 8.
- .NET Framework 4.8 lightweight application build passed with zero warnings/errors.
- New tests cover skipped scans alongside tracked appends, eventual new-session
  discovery, unfinished traversal continuing before the next fresh-scan deadline,
  and replacement-session discovery after a tracked file disappears.
- Tests advance only the private discovery deadline rather than sleeping.
- No Windows installation, real session content, account data or credentials were
  used. No release or update feed was changed.

## Synthetic discovery benchmark

A temporary fixture contains 2,000 header-only synthetic rollout files, divided
between sessions and archived_sessions, with 25 subdirectories under each root.
After initial discovery, 24 files are tracked. Twenty polls represent 60 seconds
of normal 3-second content polling; the harness advances the discovery deadline
to model 3-second versus 15-second discovery. Initial creation/startup is excluded.
The same reader binary is used for both configurable intervals. Figures below
are medians of five alternating trials after warmup on Linux/.NET 8.

| Metric | 3-second discovery | 15-second discovery |
| --- | ---: | ---: |
| Full discovery rounds | 20 | 4 |
| Discovery entries visited | 41,000 | 8,200 |
| Directories visited | 1,040 | 208 |
| Logical content bytes read | 110,400 | 110,400 |
| Elapsed reader time (ms) | 162.69 | 37.44 |
| Process CPU time (ms) | 190 | 40 |
| Managed bytes allocated | 53,739,344 | 12,767,360 |

Directory discovery work drops 80% in this settled synthetic workload while
tracked-file read work stays the same. These are not Windows whole-process CPU
measurements or a promise of an 80% tray-app CPU reduction. Shared-host CPU timing
is noisy, allocations are cumulative rather than retained memory, and logical
read counts are not physical disk traffic. Desktop executable/signature query
costs are outside this change.
