# PR 504 follow-up: count and projection costs

This investigation continues the [M4 campaign](performance-benchmarks-m4.md).
Its incremental baseline is `999ca1524df7a47e24e9aa84a14ec0fcf4e99531`.
The original PR comparison against `496e8739` remains separate; measurements
from different hosts must not be combined.

## Changes under validation

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
No analyzer is suppressed. Confirmation requires a fresh analysis of the new
head; the previous failure must not be described as cleared before that check.

The first follow-up analysis (`5251820a`) clears those two S2083 findings but
flags the new diagnostic's arbitrary executable-path CLI option with
`pythonsecurity:S8701`. The diagnostic now selects from four fixed publication
paths using `--variant`; command arguments remain separate and no shell is used.
This also prevents accidentally running a different executable than the
published variant being recorded.

## Fixed measurement protocol

Host: Windows x64, SDK 10.0.403, Release. Linux production validation and a
repeat of the Apple M4 results are outside this host's capabilities.

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
  -p:SkipClientAppBuild=true -o artifacts/probe/candidate-aot
dotnet publish benchmarks/SlimFaas.CollectionProbe -c Release -r win-x64 `
  -p:SkipClientAppBuild=true -p:PublishAot=false -p:PublishTrimmed=false `
  --self-contained true -o artifacts/probe/candidate-jit
artifacts/probe/candidate-aot/SlimFaas.CollectionProbe.exe artifacts/probe/candidate-aot.json
```

Reproduction commands, exact measured candidate commits/hashes, local-runtime
results and remaining reliability questions will accompany the final evidence.
No whole-service performance or reliability improvement is established by
these implementation changes alone.
