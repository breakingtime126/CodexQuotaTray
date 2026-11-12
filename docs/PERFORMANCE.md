Current unpublished changes: [Desktop metadata-only checks](PERFORMANCE-DESKTOP-METADATA.md)
and [TPS discovery cadence](PERFORMANCE-TPS-DISCOVERY.md). The sections below
record earlier release behavior; their signature-retention statements do not apply
to the current candidate.

# 0.2.40 preview: remove explicit Desktop image hashing

Desktop quota discovery and pre-launch validation no longer compute or compare
an application-level SHA-256 of the package/runtime executables. This is removal,
not a cached digest. The removed successful runtime path previously made four
full-image passes (about 1.39 GiB for four 373,698,352-byte image reads).

The production metadata inspection now reads only the required x64 PE header
fields. Synthetic regression tests exercise that exact method with 1 KiB,
373,698,352-byte and 512 MiB virtual images whose body throws if read; each
inspection consumes 16 bytes of explicit PE reads and reports zero hash work.
This count excludes Windows Authenticode and version APIs. Their implementation
may still read/hash the image. Four signature validations remain, so this is not
a claim that the whole Windows query reads only 64 bytes or that every CPU spike
has been eliminated.

Store registration/origin/publisher, manifest identity, trusted OpenAI signature,
PE architecture and version, size limits/matching, bounded local runtime paths,
no-reparse checks, read-only leases denying write/delete, and launch revalidation
remain. The evidence label is now `RegisteredPackageVersionAndSignature`.
**Matching size/version/signature no longer proves byte-for-byte equality to the
package image.** Both missing PE versions remain compatible, as before, with the
same-size and trusted-signature requirements but without the old digest binding.

Updates still verify their downloaded packages. TPS file fingerprints are also
unchanged. Overall verification timeout and cancellation remain cooperative.
The preview is not a published release and has not been installed on the user's
computer. Windows whole-process performance and real signature behavior require
separate validation; see [verification](VERIFY-0.2.40.md).

---

# 0.2.29 resource audit

## Scope and limits

These are offline synthetic Linux/.NET 8 reader benchmarks, not measurements of
the complete Windows/.NET Framework tray application. No real account, credentials
or private logs were read. CPU timings are noisy on a shared machine. Allocated
bytes are cumulative managed allocations, **not** live memory or working set.
Logical reads are bytes requested by the reader, **not** physical disk I/O. OS page
cache served the measured warm runs, so physical reads were zero in those runs.

## Safe changes

The unchanged-file path still verifies the file prefix and previous end anchor,
including same-size, same-timestamp replacements. When no new bytes, history or
model recovery remain, it skips a redundant end-anchor read and an 8 KiB line
buffer. Incomplete-line append and pending cold-history behavior stay covered by
regression tests. There is no timestamp-only trust shortcut or reduced TPS sample
coverage.

For 24 unchanged synthetic files and 2,000 immediate polls, medians of three
interleaved before/after runs of this EOF change were:

- Cumulative allocations: 746,544,896 to 337,772,584 bytes (54.8% lower).
- Logical reads: 36,864,000 to 24,576,000 bytes (33.3% lower).
- CPU time: 3,670 to 2,580 ms (29.7% lower in this synthetic workload).
- After-state live managed memory was about 0.48 MB for this harness; process
  working set was about 53 MB and includes the .NET 8 benchmark runtime. Neither
  number is a Windows tray-app memory claim.

The new public reset feature uses a separate one-shot timer, normally around five
minutes with jitter, bounded exponential backoff, and server Retry-After. It has a
12-second request deadline and a 64 KiB response cap. It cannot trigger account or
TPS reads. Presentation and unread state are cached; animation/TPS wakeups do not
rebuild or hash them. There are no continuous network requests, frame-rate API
polling, animations for the notification dot, or idle disk writes. A click may
persist at most 32 event hashes in a roughly 2 KiB settings file.

## Remaining material costs

Desktop quota discovery and the pre-launch trust check currently perform four
full-image SHA-256 passes and four signature checks across the package/runtime
pair per successful query. Those checks were intentionally **not** removed or
replaced by timestamp trust caching. This can be a substantial logical read cost
and may create physical I/O on cold/uncached data. A safe within-query dedup needs
explicit ownership of immutable evidence/leases through launch and separate
security review. The original backend refresh cadence is unchanged.

The historical TPS reader retains its existing bounded discovery/catch-up policy:
up to 4,096 entries/128 directories per poll, a three-second full-cycle restart,
and bounded incremental history reads. Cold synthetic history still requires
approximately 33.87 MB for one 32 MiB file, 107.24 MB for 24 four-MiB files, and
269.09 MB for one 256 MiB file, over multiple polls. Large archives therefore still
consume real catch-up work. No claim of uniformly low whole-process CPU, RAM or
disk utilization is made without Windows measurements on the relevant installation.

## Reproduce the scoped reader benchmark

`tools/CodexQuota.Performance` creates and removes isolated synthetic logs. It is
Linux-only because it samples `/proc/self/io`; never point it at real user logs.

```sh
DOTNET_TieredCompilation=0 dotnet run --project tools/CodexQuota.Performance -c Release -- idle 24 0 2000
DOTNET_TieredCompilation=0 dotnet run --project tools/CodexQuota.Performance -c Release -- discovery 10000 0 40
DOTNET_TieredCompilation=0 dotnet run --project tools/CodexQuota.Performance -c Release -- cold 1 32 1
```

The JSON output separates cumulative allocation, live managed memory, process
working set, logical reads, and kernel physical I/O counters. Run equivalent
before/after source snapshots and interleave several runs before comparing.

A second narrow change caches the oldest tracked candidate within one discovery
slice instead of sorting the same 24 tracked entries for each rejected old file.
The cache is invalidated when a tracked write time changes or a replacement is
chosen. Write-time promotion/rollback and equal-time ordering have focused tests.
One paired synthetic discovery run (not a median) observed cumulative allocations
of 133,594,312 to 113,079,496 bytes (15.4% lower), with identical 80,000 entries and
491,520 logical bytes; CPU was 1,480 to 1,110 ms. This is supporting evidence only,
not a Windows performance guarantee or a change to discovery cadence/coverage.
