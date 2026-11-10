# PR 504 follow-up: count and projection costs

This investigation continues the [M4 campaign](performance-benchmarks-m4.md).
Its incremental baseline is `999ca1524df7a47e24e9aa84a14ec0fcf4e99531`.
The original PR comparison against `496e8739` remains separate; measurements
from different hosts must not be combined.

## Collection changes

- Queue counts classify a captured queue once without allocating selections.
  `maximum` caps only Available; Running and WaitingForRetry remain uncapped.
  Availability-only reads can stop at the cap. Dispatch state uses the same
  classifier, tested against the original predicates at timeout/retry boundaries.
- Stream catalog/subscription pagination filters and selects definitions before
  projecting metadata/counters. Single-resource reads project only that resource.
  The lookahead row still contributes to the catalog page revision.
- Pending counters retrieve their message/pending dictionaries once. Complete
  current messages with ASCII UTF-8 or UTF-16 string encodings can be validated
  without constructing metadata objects. Empty, serialized-null, older shapes,
  non-ASCII UTF-8 and malformed values retain generated-decoder behavior.
  Retention and subscription-generation filtering are unchanged.
- Stream reservation counts aggregate active attempts and queue reservations
  once per snapshot. Dispatch rankings aggregate replica counts once before
  awaiting reservations; the state machine still enforces actual capacity.
- Batch pop results combine the running/transaction predicates before projection,
  preserving queue order, whole-buffer sharing and sliced-buffer copies.

## SonarCloud evidence

At baseline `999ca152`, the public quality-gate API reports Security E on new
code; reliability/maintainability are A and hotspot review is 100%. The two open
vulnerabilities are `pythonsecurity:S2083`, lines 349/354 of
`.bin/slimfaas-zlinq-benchmark.py`: archiving the running script's own bytes.
Neither location uses HTTP response content or a manifest-provided source path.
The provenance copy now uses `shutil.copyfile` with a fixed repository source;
the caller-selected campaign directory remains the intentional destination.
No analyzer is suppressed.

The first follow-up analysis (`5251820a`) clears those two S2083 findings but
flags the new diagnostic's arbitrary executable-path CLI option with
`pythonsecurity:S8701`. The diagnostic now selects from four fixed publication
paths using `--variant`; command arguments remain separate and no shell is used.
This also prevents accidentally running a different executable than the
published variant being recorded.

SonarCloud subsequently analyzed `df4f8d8b12c71555dad4f4281ddee2175fa5df30`
at 2026-11-09 21:17:21 +01:00: Quality Gate **OK**, new-code Security **A**,
zero vulnerabilities, reliability/maintainability A and hotspot review 100%.
This evidence applies to that analysis; later commits require their own check.

## Fixed measurement protocol

Host: Windows x64, SDK 10.0.403, Release. Linux production validation and a
repeat of the Apple M4 results are outside this host's capabilities.
The host has an AMD Ryzen 5 3500U (4 cores/8 logical processors),
14,977,466,368 bytes RAM, Windows 11 build 26300 and the ASUS Recommended
power plan. Runtime and NativeAOT compiler are 10.0.14.

Before timed execution, build all variants sequentially. The standalone
`benchmarks/SlimFaas.CollectionProbe` uses the same instrumentation for both
revisions. `PR504_BASELINE` selects the original count/projection/ranking calls;
all product source remains at the stated revision. Friend-assembly entries in
both baseline projects are instrumentation only. Measure self-contained untrimmed JIT and
actual NativeAOT executables separately.

Each case calibrates for 100 ms and takes exactly three samples at that fixed
invocation count. Repeat each version/mode twice, reversing the variant order
on the second round. Preserve every sample, unfavorable result and failed
attempt. Report managed bytes/op separately from time, GC and process RSS.
Before timing, `--smoke` executes each of the 69 cases once without a timer;
baseline/candidate results must agree. The count probe preserves the real
reader's `IList<CountType>` dispatch and empty-queue guard; consensus/read
barriers are outside this method-cost measurement.
Queue cases cover 0/200/2,000 elements and 0/25/100% running plus mixed
Available/Running/WaitingForRetry/Finished states; page cases use
1/25-row limits with 0/200/2,000 definitions/subscriptions.
Batch-pop cases execute the real composite-command result path on fresh states;
reservation cases execute the state machine on fresh states with active leases.
Their costs include cloning, validation, serialization and mutation as well as
the targeted reads, so they are reported separately from pure count queries.
The page population is N generated definitions/subscriptions plus the populated
`orders`/`billing` fixture outside the requested cursor. Count zero is an empty
page with that off-page fixture, rather than a completely empty catalog.

The local runner records per-node GC metrics and Windows process I/O counters,
as well as CPU and summed runtime RSS. Windows I/O counters include cached,
device and network transfers; they do not identify physical disk traffic or
thread wait stacks. Verified file cycles retain upload/read p95/p99 separately.
The diagnostic correlates HTTP receipts, queue IDs, byte-verified deliveries,
runtime logs and 500 ms file-presence observations. Short-lived files can be
missed by polling, and disappearance is not a timestamped deletion syscall.
Windows supervisors receive Ctrl+Break through their own hidden console, so
normal disposal removes ephemeral benchmark state. Forced termination is a
reported cleanup failure. Diagnostic offload state is persistent and retained.

```powershell
dotnet publish benchmarks/SlimFaas.CollectionProbe -c Release -r win-x64 `
  -p:SkipClientAppBuild=true -o artifacts/probe-measured/candidate-aot
dotnet publish benchmarks/SlimFaas.CollectionProbe -c Release -r win-x64 `
  -p:SkipClientAppBuild=true -p:PublishAot=false -p:PublishTrimmed=false `
  --self-contained true -o artifacts/probe-measured/candidate-jit
artifacts/probe-measured/candidate-aot/SlimFaas.CollectionProbe.exe artifacts/candidate-aot.json
```

## Measured collection costs

The fixed campaign completed eight executions: 69 cases × three samples × two
executions × four variants = **1,656 samples**. All 69 behavior results agree
between baseline/candidate and JIT/NativeAOT. The runtime candidate source is
`5251820a5f215c1c9f30eddc4486321cdd1eaeb7`; instrumentation is frozen at
`c4cdf8cfffb400f1e28227dce35c855ce9c15033`. The baseline remains `999ca152`.
The later Windows file fix is outside these collection binaries.

The [complete table](benchmark-results/pr504-windows-collections.md) and
[all samples, execution means and binary hashes](benchmark-results/pr504-windows-collections.json)
retain every control and unfavorable result. Selected costs at 2,000 elements:

| Case | JIT before → after B/op | NativeAOT before → after B/op |
|---|---:|---:|
| Count all, mixed states | 28,272 → 0 | 28,272 → 0 |
| Available only, cap 10, all available | 208 → 0 | 240 → 0 |
| Dispatch state, mixed | 16,544 → 16,584 | 16,584 → 16,584 |
| Batch pop, all running | 187,664 → 171,552 | 187,776 → 171,680 |
| Stream counters | 1,512,504 → 1,000,472 | 1,512,504 → 1,000,472 |
| Counts for four replicas | 4,001,312 → 1,000,640 | 4,001,248 → 1,000,640 |
| Reservation, including cloning/mutation | 5,219,872 → 4,219,344 | 5,225,328 → 4,224,768 |
| Catalog page of one | 2,562,832 → 434,552 | 2,562,824 → 434,616 |
| Function page of one | 3,417,744 → 546,192 | 3,417,752 → 546,160 |

Mixed queue counting takes 67.9 → 19.1 µs in JIT and 68.1 → 26.8 µs in
NativeAOT. Stream counters take 2.10 → 1.44 ms and 2.59 → 1.83 ms respectively.
These are local method costs, excluding Raft/read barriers. Dispatch JIT
allocations increase by 40 bytes; NativeAOT dispatch allocations are unchanged.
Some empty/small controls are slower (for example empty JIT batch-pop /0/0:
2.89 → 4.30 µs). Two repetitions do not establish precise timing confidence,
and tiering/host noise particularly affects these short cases. Allocation
savings are useful without implying lower process RSS or global throughput.

## Windows file sharing regression

Eight predetermined attempts (two rounds of all four original publications)
all fail the reused-ID, byte-verified file test. Unique-ID tests are recorded
even when reused IDs fail. Failed reused-ID rates are invalid performance
comparisons because workers stop on their first error. They are retained as
correctness evidence rather than filtered into successful repetitions.

Logs identify `UnauthorizedAccessException` in `File.Move` on replacement and
sharing violations while reading bodies/metadata, in both baseline and
candidate. A deterministic regression holds a download and metadata reader
open, overwrites the ID, deletes it, and verifies both the old and new streams'
bytes. It fails on the original implementation and passes with the separate
fix `d843e59fb177d6593c23c28915e7c4932015e597`.

Readers now share deletion. Existing Windows destinations use `File.Replace`
rather than `File.Move`, including metadata; other platforms retain their move
path. The need for replacement agrees with [Windows file replacement semantics](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilea).
This neither demonstrates nor explains the M4 NativeAOT file regression:
that host uses different file semantics, hardware and an earlier comparison.

The first fix's four-attempt campaign is also retained: three complete, and
the first JIT attempt still fails. Its stack traces now originate in opening
new readers during replacement. Windows `ReplaceFile` temporarily retains a
writable handle to the new version; those readers must share write access as
well as deletion. A second deterministic test holds this handle open and
reproduces the read-open exception. Commit
`dc7e48126c979d5ce7dcbd0993a158cb5cedf148` changes the reader sharing flags;
the test and the full **1,994-test .NET suite** pass. Neither fix adds sleeps,
retry attempts or a longer timeout. Each changed implementation has its own
fixed-size campaign rather than replacing earlier failed attempts.

Native publication of the first fix initially exhausted disk while writing
multi-gigabyte compiler dependency graphs. The failed build log is retained.
Subsequent file-fix publications use `IlcGenerateDgmlFile=false`, which disables
diagnostic graphs, retaining the product's NativeAOT optimization settings.
Closed generated symbols/objects were compressed with matching SHA-256 before
and after; measured executables/DLLs were not compressed. Existing third-party
IL2104/IL3053 summary warnings for MemoryPack.Core/ConfigurationManager occur
in baseline and candidate. No new warning suppression was introduced.

The final fixed four-attempt file campaign passes **4,803 byte-verified cycles**:
2,410 reused IDs and 2,393 unique IDs, with no HTTP, sampling or content errors.
Both JIT and NativeAOT run twice. The earlier campaigns remain visible:
3,743 valid reused-ID reads before 96 worker failures and 4,966 successful
unique-ID cycles before the fix; then 2,260 reused/2,240 unique verified cycles
with four worker failures in one of the first fix's four attempts. Failed
attempts' partial latencies/CPU/RSS are descriptive records, not valid
16-client performance comparisons.

PE version resources independently identify the original publications as
`999ca152` and `5251820a`, and the final fixed publication as `dc7e4812`.
Windows NativeAOT executable sizes are 73,084,928 → 73,086,976 bytes for the
collection change, and 73,079,808 bytes with the final file fix. Executable
size is distinct from resident memory.

## Real sets and Streams

The first fixed matrix completes all eight attempts: **52,809 set operations**
validated on three nodes and **1,600 Stream publications** with unique target
IDs, committed ACKs, zero lag and zero pending deliveries. No builds overlap
these runs. Each set phase has five seconds of warmup, sixty seconds of load,
16 workers and 4 KiB values. Each Stream phase publishes a 200-message burst
with 16 publishers.

Stream drain throughput remains about four publications/second. HTTP append
p95 is separate from target arrival p95, which includes roughly 46 seconds of
backlog in this burst. Lower counter/query costs do not remove dispatcher
cadence or demonstrate a publish-throughput gain.

Initial set medians are 111.17 → 119.34 op/s in JIT (+7.3%) and
112.58 → 95.51 op/s in NativeAOT (−15.2%). NativeAOT candidate repetitions range
83.62–107.39/s versus 105.58–119.57/s before; runtime CPU/op also rises slightly.
The negative result is retained. These attempts ran with low disk reserve,
so neither the improvement nor regression establishes causality. A separate
fixed set-only control with at least 4 GiB initial free space tests this
environmental confound without replacing any original result.

That control also completes all eight attempts, validating **53,643 set
operations**. JIT medians are 120.99 → 112.82 op/s (−6.75%); NativeAOT medians
are 115.03 → 96.34 op/s (−16.25%). NativeAOT ranges are 103.55–126.51/s before
and 91.75–100.92/s after. Median runtime CPU/op rises about 4.3% in JIT and
2.7% in NativeAOT. The NativeAOT slowdown therefore does not disappear with
adequate initial disk reserve. The JIT direction changes between campaigns,
and only two repetitions are available per variant. The changed method costs
are bounded wins; the set throughput regression remains open and no global
service improvement is claimed. Sampled process I/O and GC counters, HTTP
p95/p99, CPU and RSS for every attempt accompany the report; they do not isolate
the remaining wait/transport cause or replace same-host native wait profiling.

In that control, NativeAOT sampled read/write bytes per set remain roughly
11.7–12.2 / 12.8–13.1 KiB across versions. This does not show transfer
amplification, and these counters include network/cache traffic. Observed
generation 0/1/2 collections across three nodes are 55/18/16 and 46/17/12
before, versus 44/15/11 and 44/14/9 after. Fewer collections coexist with lower
throughput; neither those totals nor CPU/op identify the remaining waits.

## Async diagnostics and disk preconditions

The first baseline trace exhausts disk while journaling retained replicated
bodies. It has 509 receipt lines and 130 delivery lines, but no completed
drain/summary and an empty resource file. Those are incomplete records, not
an established count of lost deliveries. Three recorded orphan node PIDs were
verified against their supervisor and executable before forced cleanup. Logs,
persistent state and the interrupted plan remain archived. This cannot explain
the M4 incident, which had 23 GiB free.

The diagnostic now rejects startup below 4 GiB free, tested before binding the
target or starting a runtime. A new matrix fixes two rounds of all six original,
optimized and file-fixed JIT/NativeAOT variants, reversing order in round two.
Every attempt runs once; failures are retained. Load remains 2 MiB, 16 clients,
20 seconds, dispatch capacity 16 and up to 300 seconds delivery drain.
All nodes must discover a ready function before load. Only stopped retained
states are compressed between attempts with matching content hashes; live
state and runtime executables/DLLs remain uncompressed.

The target hashes and journals every body and matches client IDs, HTTP receipt
queue IDs and delivery queue IDs. UTC runtime metadata tags join queue IDs to
offload IDs and per-node file observations. Startup elections and shutdown
errors must be distinguished from events in the load/drain window.
The Python HTTP/1.0 target and Debug logging differ from the original common
.NET/M4 performance target. These are reliability diagnostics, not comparable
throughput measurements or a demonstration that the original M4 cause is fixed.

Production NativeAOT has `EventSourceSupport=false`; optional
[EventPipe support requires enabling it](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics).
The retained metrics provide CPU, sampled RSS, GC and process I/O counters,
not native thread wait stacks or physical disk attribution.

All twelve headroom attempts complete their prescribed observation window:
**8,985 HTTP 202 receipts, 8,933 observed deliveries, 52 not observed within
300 seconds**, zero duplicates, zero received-body hash/length mismatches and
zero receipt/delivery queue-ID mismatches. No HTTP acceptance or resource-sampling
error is recorded in this matrix. Three attempts pass all checks and nine fail the
missing-delivery check; all twelve remain in the evidence.

| Publication | Mode | Repetitions | HTTP 202 | Observed | Not observed |
|---|---|---:|---:|---:|---:|
| Baseline `999ca152` | JIT | 2 | 1,243 | 1,231 | 12 |
| Collections `5251820a` | JIT | 2 | 1,438 | 1,410 | 28 |
| File fix `dc7e4812` | JIT | 2 | 1,173 | 1,165 | 8 |
| Baseline `999ca152` | NativeAOT | 2 | 1,544 | 1,544 | 0 |
| Collections `5251820a` | NativeAOT | 2 | 1,794 | 1,793 | 1 |
| File fix `dc7e4812` | NativeAOT | 2 | 1,793 | 1,790 | 3 |

These different acceptance counts, two repetitions and diagnostic target do
not establish relative reliability rates or a global file-fix improvement.
The Windows sharing fix passes its direct regression/load checks while async
delivery failures remain.

One baseline example joins client request `ca21a4a115b749dda16eb9302686d751`
to queue element `f6518756-9acb-49e9-b5e2-aa915620da77` and offload
`sf-internal-de21b0ca12634b67b9b1de1820d2086d-internal`.
At 21:51:22 UTC, origin node 1 logs the stored metadata before the HTTP 202.
Peer transfers and node 2's metadata loads follow. Four sends to that request's
target URI return HTTP 500 at approximately 21:51:29, :31, :35 and :44.
The same time windows contain body-copy transport failures with Windows
SocketException 10053. The exception messages lack a per-request ID, so the
transport association is a temporal inference rather than a unique exception
join. The underlying connection-abort cause remains open.

The cleaner removes that offload after the failed queue attempts are terminal,
at about 21:52:52; other nodes remove their copies afterward. This sequence
does not establish premature deletion. Two separately logged metadata sharing
violations on other queue IDs recovered on retries. Neither these observations
nor collection allocation savings establish the cause of the M4 FileNotFound
and quorum incidents. The full attempt table, ID timelines, raw logs, resource
samples and source hashes are retained in [the runtime evidence](benchmark-results/pr504-windows-local.md).

A separate [error-to-delivery join](benchmark-results/pr504-windows-async-errors.json)
retains the analysis source and identifies all 23 body/metadata error lines
in the observation windows with 22 distinct queue IDs. Every one has a
byte-verified target delivery. This includes the two unhandled body-file
`FileNotFoundException` lines in the optimized JIT run: both occur after
the same message was received. The final file-fix variants retain five
file-not-found warnings (three peer range pulls and two metadata reads),
each after its message's target receipt, by 0.19–8.45 seconds. These are
consistent with replication/enumeration racing terminal cleanup, rather than
evidence explaining the 52 unobserved deliveries. The precise race is not
proven and no warning is suppressed.

All 8,859 broad leadership/quorum matches in the first-send-to-summary
windows are Debug `Local changes are not committed. Quorum is ...` messages.
No logged leader-change or explicit consensus-failure message is present in
those windows. An uncommitted-index diagnostic is not an outage count; this
does not prove continuous quorum availability or reproduce the original M4
quorum interruption. Startup/shutdown events remain in the original logs.

The [byte-exact evidence ZIP](benchmark-results/pr504-windows-evidence.zip)
contains 634 original files with an internal SHA-256/length manifest;
its SHA-256 is `af9006c937d1ed9fac6ca819069f4ecdf98c36e0653b6f8f523dd814be7edfd0`.
The derived error analysis records its input hash and source separately.

## Additional final-runtime campaigns after storage recovery

On 10 November 2026, available space had fallen to about 2.7 GiB. A renewed,
explicitly authorized attempt to remove an obsolete Codex cache was rejected
by automatic tool policy (`blocked by policy`), before execution. No cache
deletion was performed. Closed cache versions/copies and a closed Copilot
history database were compressed instead, checking unchanged content hashes.
The initial standard-compression pass reduced the observed reserve; that
negative audit is retained locally. LZX compression subsequently recovered
space. Observed whole-volume free space rose from 2,929,360,896 to
6,459,047,936 bytes across the complete preparation, a net increase of about
3.29 GiB. Conversations, credentials and measured executable/DLL contents
were preserved. Whole-volume observations also include unrelated activity.

Two new fixed campaigns compare baseline `999ca152` with the final runtime
`dc7e4812`, rather than the earlier collection-only publication `5251820a`.
The four frozen publications are exposed through NTFS hard-linked aliases;
none is rebuilt or overwritten. The orchestration runs at report/helper head
`0c901e7a`. Each campaign has exactly two rounds of all four JIT/NativeAOT
variants, reversing order in round two, without retries or concurrent builds
or compression. Every attempt requires at least 4 GiB free before startup.
The common HTTP driver DLL identifies build commit `d843e59f`; the MemoryLab
DLL identifies `c4cdf8cf`. Their exact hashes are recorded in the plan.
The HTTP observer records 500 ms runtime GC/I/O/RSS samples and an emergency
disk cutoff; no HTTP attempt triggers that cutoff.

The [additional attempt tables](benchmark-results/pr504-windows-extra.md),
[structured summaries and plans](benchmark-results/pr504-windows-extra.json)
and [separate original-evidence archive](benchmark-results/pr504-windows-extra-evidence.zip)
retain all failures and supplement the earlier evidence without replacing it.

### Sets, mixed operations and warm files

Each of the eight attempts runs five seconds of warmup and sixty seconds of
load per scenario, with 16 workers, 4 KiB values and 256 KiB files. The four
final-runtime attempts pass all three scenarios without operation errors.
All four baseline attempts fail the file scenario: the JIT timed phases
record HTTP 500 errors, while NativeAOT fails preparation/warmup before its
timed file phase. The existing Windows replacement/sharing exception is
reproduced despite adequate disk reserve. Missing timed phases remain marked
not started. File throughput is therefore not a valid before/after comparison.

All set/mixed phases complete. Descriptive medians, two repetitions per side:

| Scenario | JIT before → after op/s | NativeAOT before → after op/s |
|---|---:|---:|
| Sets | 102.23 → 109.80 (+7.40%) | 97.06 → 116.11 (+19.63%) |
| Mixed KV/hashset/async | 219.54 → 235.16 (+7.12%) | 205.83 → 236.12 (+14.72%) |

These positive medians do not remove the earlier negative controls. NativeAOT
baseline set repetitions range from 72.06 to 122.05/s, versus 114.80–117.42/s
after; the new direction differs from the earlier −15.2%/−16.25% observations.
The final file fix, storage preparation, host/leadership variability and two
repetitions do not isolate a causal performance effect. The evidence establishes
variable throughput, rather than a general service speedup or a demonstrated
cache-induced fix for the previous regressions.

### Common .NET target, 2 MiB and 16 clients

All eight HTTP attempts complete using the original common .NET target and
driver, 20 seconds of load, five seconds of warmup, dispatch capacity 16 and
the unchanged 300-second delivery observation limit. The measured heavy-body
phases record **4,799 accepted IDs and 4,799 observed IDs**, with no missing
or duplicate delivery. The additional 4 KiB paced/burst phases accept and
observe **8,800 messages**. Direct, sync, focused async and scale-to-zero/
scale-out results remain available separately in the JSON and raw archive.

The .NET target drains the complete HTTP body before recording an ID, but
does not hash the body contents. The earlier byte-verified file/hash diagnostic
remains distinct. This target, Error-level runtime logging and the extra
resource observer differ from the Python HTTP/1.0/Debug diagnostic that had
52 unobserved deliveries. The new successful attempts do not establish that
those failures or the original M4 incidents have been fixed. Neither the
Windows and M4 results nor the different diagnostic protocols are pooled.

The minimum sampled HTTP free reserve is 4,485,509,120 bytes (about 4.18 GiB),
with zero resource-sampling errors. All twenty additional supervisors shut
down normally with exit code zero. The four valid final-runtime file phases
complete **11,650 operations**; the two invalid JIT baseline phases retain
7,866 successful operations and 18 errors. The two NativeAOT baseline timed
file phases remain not started after preparation failed.

### Native thread-state observations

The separate four-attempt NativeAOT set-only diagnostic also completes,
using the same 16 workers, 4 KiB values and sixty-second load, with five seconds
of warmup. A capability preflight succeeds before any measured attempt.
Across the four timed phases, **443 thread-state frames** are captured with
zero thread-query errors. Both versions' samples are dominated by the Windows
`UserRequest` and `EventPairLow` wait-reason labels. Idle runtime/GC/pool
threads contribute, so these counts are not wait durations or evidence that
a particular request is blocked on storage or network I/O.

| Set-only diagnostic | Before repetitions | After repetitions |
|---|---:|---:|
| Throughput, op/s | 84.36 / 90.76 | 114.22 / 125.28 |
| Runtime CPU, ms/op | 10.71 / 9.80 | 10.07 / 9.67 |
| Sampled process read KiB/op | 9.91 / 11.76 | 11.88 / 11.88 |
| Sampled process write KiB/op | 11.26 / 13.02 | 12.88 / 12.79 |

These process I/O values include cache/network transfers and approximate
sample boundaries. The observer has its own overhead; these are a separate
diagnostic, not extra repetitions pooled into the preceding throughput table.
The snapshots and modest CPU/op differences do not identify the remaining
throughput-variance cause. WPR is installed but the current token is not
elevated; no kernel/native wait stacks or elevation were attempted.

The additional archive contains **688 byte-verified original files**, separate
from the earlier 634-file archive. Its SHA-256 is
`034214c41ad03544de3c7db0bd9ef5498959f8621fd3859614c319643d0f7927`.

## Reproduction on Windows

Use SDK 10.0.403/runtime 10.0.14, Node 24 and Python 3.11. Keep builds, other
loads and state compression outside measurement windows. Do not reuse a
campaign output directory or retry until only successful attempts remain.
The baseline for this follow-up is `999ca152`, not the original PR base.

To build the identical frozen probe against both revisions, extract only its
instrumentation and add the two baseline friend-assembly entries:

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null
git worktree add --detach ../pr504-probe-baseline 999ca1524df7a47e24e9aa84a14ec0fcf4e99531
git worktree add --detach ../pr504-probe-candidate 5251820a5f215c1c9f30eddc4486321cdd1eaeb7
git archive --format=tar --output=artifacts/pr504-probe-source.tar c4cdf8cfffb400f1e28227dce35c855ce9c15033 benchmarks/SlimFaas.CollectionProbe
tar -xf artifacts/pr504-probe-source.tar -C ../pr504-probe-baseline
tar -xf artifacts/pr504-probe-source.tar -C ../pr504-probe-candidate
git diff --output=artifacts/pr504-probe-friends.patch 999ca152 c4cdf8cf -- src/SlimFaas/SlimFaas.csproj src/SlimData/SlimData.csproj
git -C ../pr504-probe-baseline apply ../SlimFaas-pr504/artifacts/pr504-probe-friends.patch
```

Adjust the final patch path to the source checkout's name. Publish the baseline
probe with `-p:DefineConstants=PR504_BASELINE`; the probe removes that property
from its product references so the product keeps its normal Release constants.
Publish both worktrees in untrimmed self-contained JIT and NativeAOT, using
distinct `baseline-jit`, `candidate-jit`, `candidate-aot`, `baseline-aot`
directories. Run each executable once with an output path followed by
`--smoke` and verify all 69 results match. Then run the fixed order and reverse
order with output names `round-1-baseline-jit.json`, etc. The adjacent collection
JSON records the exact manifest, binary hashes and source commitments.

```powershell
python .bin/summarize-collection-probe.py artifacts/pr504-windows-probe --output docs/benchmark-results/pr504-windows-collections

# Frozen three-node publications: original999 vs optimized525.
python .bin/slimfaas-zlinq-benchmark.py --publish-root artifacts/runtime --output artifacts/pr504-files-streams --rounds 2 --duration 20 --warmup 5 --data-duration 60 --data-scenarios= --stream-messages 200 --verified-file-duration 20 --skip-http --continue-after-failure

# Isolated phases; the previous command's file failures prevented its Streams.
python .bin/slimfaas-zlinq-benchmark.py --publish-root artifacts/runtime --output artifacts/pr504-sets-streams --rounds 2 --duration 20 --warmup 5 --data-duration 60 --data-scenarios slimdata-set --stream-messages 200 --skip-http --continue-after-failure

# Publish dc7e4812 separately under artifacts/runtime-filefix-final first.
python .bin/slimfaas-zlinq-benchmark.py --publish-root artifacts/runtime-filefix-final --output artifacts/pr504-files-final --rounds 2 --duration 20 --warmup 5 --data-duration 60 --data-scenarios= --stream-messages 0 --verified-file-duration 20 --skip-http --continue-after-failure --variants candidate-jit,candidate-aot

# One diagnostic attempt; apply the recorded six-variant order twice, reversed.
python .bin/trace-async-offload.py --variant baseline-jit --output artifacts/pr504-async-headroom/round-1-baseline-jit --duration 20 --clients 16

# Additional isolated set control, once, after recovering at least 4 GiB free.
python .bin/slimfaas-zlinq-benchmark.py --publish-root artifacts/runtime --output artifacts/pr504-sets-headroom --rounds 2 --duration 20 --warmup 5 --data-duration 60 --data-scenarios slimdata-set --stream-messages 0 --skip-http --continue-after-failure
```

The local evidence archive includes the exact orchestration, publication flags,
native-demo overlay, extraction program and raw source hashes. Replaying the
commands creates new evidence; do not overwrite the retained campaigns.

For the additional campaigns, extract the helper sources from the new ZIP's
`artifacts/validation/` into that directory in a clean checkout. Prepare the
original baseline publications under `artifacts/runtime/baseline-{jit,aot}`
and final `dc7e4812` publications under
`artifacts/runtime-filefix-final/candidate-{jit,aot}`, using the preceding
publish commands/flags against their respective frozen worktrees. Prepare
the common driver and MemoryLab before starting measurements. The source
map and plan record the exact measured binaries and helper hashes.

```powershell
python artifacts/validation/prepare-final-comparison.py
python artifacts/validation/run-extra-benchmarks.py
python artifacts/validation/run-native-wait-profiles.py
```

These helpers require fresh output directories, fix repetition counts before
execution, and retain unsuccessful attempts. The archive also includes the
extraction source used for the original results; its storage-recovery audit
inputs are private local metadata, summarized in the public JSON.

## Validation and remaining work

- The full Release .NET suite passes **1,994 tests**, zero failed; the eight
  Python harness tests pass, including the low-disk startup precondition.
- Real self-contained JIT and NativeAOT publications succeed, with the existing
  third-party warning summaries noted above.
- The final production documentation build passes under Node 24/pnpm 10.14.0:
  23 pages, 1,652 local links/assets and 394 search entries checked.
- The final NativeAOT Fibonacci demo uses the original four-function,
  three-job, three-node manifest with a prebuilt/ephemeral overlay and schedules
  disabled. Status, `Hello local!`, Fibonacci(10)=55, async receipt-to-completion
  element IDs, both `orders` subscriber ACKs, catalog/function page reads and a
  successful batch job pass. The first unprepared attempt, which lacked sample
  DLLs, is retained separately; both samples were then built with zero warnings
  or errors before the successful attempt. Shutdown exits zero.
- SonarCloud analyzes report/helper head `0c901e7a01c77cbc5d49aec967c7d816ea796e99`
  with Quality Gate OK and Security A at 2026-11-10 00:18:32 +01:00. The head's
  29 executed CI checks pass, including DCO and FOSSA; seven are conditionally
  skipped. Later report commits require a fresh head check.

Open work is the async connection-abort/missing-delivery cause, the earlier
set slowdown and subsequent throughput variability, and the original M4
file/set/quorum investigation on that host
or a representative Linux production environment. The Windows sharing fix
does not establish a solution to those separate incidents. No Linux production
benchmark or native thread-wait stack trace was captured here; the additional
Windows thread-state snapshots are explicitly scoped above.
