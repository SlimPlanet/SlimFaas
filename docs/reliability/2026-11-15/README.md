# Async memory, WAL geometry and Files reliability investigation

This follow-up preserves the [completed 1ecba4d1 validation](../2026-11-13/README.md)
and its failed or inconclusive performance verdicts. The bounded diagnostics below
are separate from the formal HTTP/MemoryLab matrices. They do not establish LTS
certification or close the unverified guarantees in issue #505.

## Reproduced failures

Each diagnostic sends 200 messages of 1,048,576 bytes at concurrency 16 to real
Kubernetes functions, with a 1 GiB runtime limit, normal probes and a full
180-second observation after sending. Function journals fsync per-ID started and
completed records, including the payload SHA-256, queue element, delivery attempt,
writer and pod UID. This instrumentation differs from the aggregate-only formal
HTTP target. Both variants use identical resources, snapshot policy, probes,
load driver and fresh-volume bootstrap policy.

The original runtime is `1ecba4d13d29ae68909979e168ff5b53aae6fea1`.
The first correction is `766ada9b9b92b5ad520c27c43b4ba770bce45e33`.
All eight passes below completed their observation window with valid host
isolation. Their writers were stopped before original VM state was indexed.

| WAL geometry | Order | Runtime | Accepted / unknown | Not delivered at 180 s | Extra successful deliveries | Maximum container restarts |
| --- | --- | --- | --- | --- | --- | --- |
| 4 KiB | A1 | original | 200 / 0 | 176 | 0 | 3 |
| 4 KiB | B2 | first correction | 200 / 0 | 0 | 17 | 0 |
| 4 KiB | B3 | first correction | 200 / 0 | 0 | 71 | 0 |
| 4 KiB | A4 | original | 200 / 0 | 182 | 0 | 3 |
| 64 KiB | A1 | original | 200 / 0 | 183 | 0 | 4 |
| 64 KiB | B2 | first correction | 200 / 0 | 0 | 0 | 0 |
| 64 KiB | B3 | first correction | 200 / 0 | 0 | 64 | 0 |
| 64 KiB | A4 | original | 200 / 0 | 181 | 0 | 4 |

Missing at this observation bound is not evidence of permanent loss. Original
volumes and accepted/unknown ledgers remain retained for recovery checks. Healthy
extra deliveries remain failures. The final original 64 KiB pass also failed its
final capture with HTTP 503; its completed observation and failure are preserved.
Detailed records: [4 KiB block](producer-receipts-07.json),
[64 KiB block](producer-receipts-08.json).

The first fault was a managed `OutOfMemoryException` while serializing a persisted
producer session during Raft apply. A session contains its last response, rather
than an unbounded response history. The first correction borrows that response on
metadata reads and writes large sessions into an exactly sized array, preserving
the generated MemoryPack format and sequence checks.

This correction was insufficient. The second corrected 64 KiB pass logged 26
managed-OOM occurrences in its leader's current log despite zero restarts. Stacks
identify HTTP response serialization (`ReusableLinkedArrayBufferWriter.ToArrayAndReset`)
and the client's buffered `ReadAsByteArrayAsync` copy. Log occurrences are not a
count of distinct exceptions. Kubernetes OOMKilled and restart counters alone
cannot detect this failure. This evidence does not establish the cause of the
original ten formal HTTP duplicates, whose per-ID receipts were not exported.

A single clean 64 KiB pass does not establish that a geometry change fixes the
problem. Product WAL defaults remain unchanged.

## Batch response correction

Commit `c2fa989a068256a09e4fa7a2192f373eee00ad91` reduces the remaining copies:

- Large dequeue replies use the generated formatter twice: a counting pass with
  reusable scratch storage, followed by a write into their final array. A final
  oversized UTF-8 reservation uses a scratch tail and commits only actual bytes.
  Small replies keep the previous serialization path.
- The HTTP client reads a known Content-Length directly into one array. Its
  configured timeout still covers the whole body and remains linked to both
  leader and consensus epochs. Unknown-length peers keep bounded buffering.
- Persisted session layout, response bytes, duplicate and sequence behavior,
  queue retry settings, deadlines and dependencies are unchanged.

All **2,150 .NET tests pass**, with zero failures or skips. Two client copy
regressions fail with the original reader. Existing epoch/shutdown/body-timeout
checks and two new known-length stalled-body checks pass. Six large-reply tests
compare exact old/new wire bytes and decoded delivery fields. An intermediate
prototype failed three of these controls because a formatter's final reservation
exceeded its committed UTF-8 size; the failure and corrected replay are retained.

The isolated managed serializer probe uses 64 distinct 2 MiB arrays. With the
same 320 MiB GC heap limit, the legacy API fails with OOM and the corrected writer
succeeds. Its 134,220,372-byte output matches the unbounded legacy control's
SHA-256 `93dae168ab925248d7388be2e92dc154f4451755a4f49e267cf2009fd3ad024a`.
This is a reproducible allocation regression, not an application throughput, RSS
or peak-memory comparison. [Probe observations](batch-serializer-memory-probe.json).

Both macOS ARM64 Native AOT publications, the Linux ARM64 managed/trimmed
publication, full UI builds and native API/payload/Files/Streams/job/retry/DLQ/crash
and original-state restart controls pass. The subsequent blocks compare
`766ada9b` (A) with `c2fa989a` (B), with all eight observation windows completed:

| WAL geometry | Order | Accepted / unknown | Missing at 180 s | Extra deliveries | Restarts | Managed OOM log occurrences | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 4 KiB | A1 | 200 / 0 | 0 | 63 | 0 | 0 | FAIL |
| 4 KiB | B2 | 200 / 0 | 0 | 16 | 0 | 0 | FAIL |
| 4 KiB | B3 | 200 / 0 | 0 | 51 | 0 | 0 | FAIL |
| 4 KiB | A4 | 200 / 0 | 0 | 15 | 0 | 0 | FAIL |
| 64 KiB | A1 | 200 / 0 | 0 | 0 | 0 | 0 | PASS |
| 64 KiB | B2 | 200 / 0 | 0 | 0 | 0 | 0 | PASS |
| 64 KiB | B3 | 200 / 0 | 0 | 0 | 0 | 6 | FAIL |
| 64 KiB | A4 | 200 / 0 | 0 | 0 | 0 | 0 | PASS |

The host verdict includes an explicit managed-OOM log audit. The third 64 KiB
pass fails despite successful delivery accounting and zero restarts: its stack
is now `CustomRequest.Deserialize` in `SlimQueuesWorker.PrepareMessagesAsync`.
The first corrected 4 KiB pass logs only the initial leader on each node;
leadership loss does not explain those duplicates. These observations again do
not justify changing product WAL defaults. See the [4 KiB results](batch-response-09.json)
and [64 KiB results](batch-response-10.json).

## Local ownership and queued body correction

Commit `e5883fa8e28ba2775c4dee8e5e6a31a850d22c82` addresses two reproduced paths:

- A queue snapshot can return after an in-flight reservation expires, or after
  its HTTP completion enters the mailbox. With spare parallel slots, the worker
  could reserve that same ID again and overwrite its local tracking. Two tests
  gate the snapshot and completion explicitly; both fail before the correction.
  The worker now drains completions again and defers a new dequeue for that
  function while an expired delivery remains locally owned. It does not consume
  another retry or change reservation timestamps or timeouts.
- Preparing a dequeued batch cloned every inline request body before any send.
  Bodies of at least 64 KiB now use a read-only stream over their original owned
  packet. The generated request format, metadata, small-body behavior and file
  reference path remain unchanged. Tests verify the exact backing array,
  byte-compatible metadata, malformed input rejection, dispatch and disposal.

All **2,161 .NET tests pass**, with zero failures or skips and full UI builds.
The isolated request preparation probe retains 96 distinct 2 MiB packets. The
legacy decoder fails under a 320 MiB GC heap limit; the borrowed-body path
succeeds under the same limit with 201,326,592 body bytes and aggregate SHA-256
`99aabde3d1ea67d0e617d1e311e9cd44d37c2b640a8b4f98b31171089637fa43`,
identical to the unbounded legacy control. This does not measure peak RSS or
application throughput, and does not prove that the original formal ten
duplicates had the same cause.

The new Linux ARM64 managed/trimmed image is built and imported on all four
dedicated kind nodes. Both macOS ARM64 Native AOT publications and native
API/payload/Files/Streams/job/retry/DLQ/crash/original-state restart controls pass.
The native campaign reconciles 198 confirmed operations without confirmed loss
or corruption; 12 unknown outcomes and 13 extra deliveries during fault scenarios
remain counted separately. The healthy-majority leader-route check takes
6.331 seconds, below its unchanged 15-second bound. Subsequent diagnostics are
recorded below. Older runtime passes do not validate this source retroactively.

## Further dispatch diagnostics

The normal 4 KiB block compares `c2fa989a` (A) with `e5883fa8` (B):

| Order | Confirmed / unknown | Missing confirmed | Extra confirmed deliveries | Restarts | Current-log OOM occurrences |
| --- | --- | --- | --- | --- | --- |
| A1 | 200 / 0 | 0 | 70 | 0 | 0 |
| B2 | 178 / 22 | 0 | 110 | 0 | 0 |
| B3 | 200 / 0 | 0 | 85 | 0 | 0 |
| A4 | 181 / 19 | 0 | 46 | 0 | 0 |

All four fail. Both candidate passes also show leader changes and temporary
quorum unavailability under load. The local ownership correction does not resolve
all redispatch paths. Unknown client outcomes remain separate, including when
later function receipts exist.

The separate 64 KiB block completes all 200 confirmed deliveries without unknowns
or extras in every pass. A1, B3 and A4 pass their then-current controls. B2 fails
with one runtime restart and six connection-refused samples. Its current logs
contain no managed OOM, but the previous process log was not exported. Kubernetes
events show failed liveness probes followed by a restart; exit 137 with reason
`Error` does not establish OOMKilled. The original process failure remains
unexplained. This observation motivated the log-collection correction rather
than a retrospective passing verdict.

The complete per-pass observations are retained separately for
[4 KiB](dispatch-ownership-net11-11.json) and
[64 KiB](dispatch-ownership-net11-12.json).

Collector commit `0fd04a59` retains timestamped current/previous logs by pod UID,
container ID and restart index, including retrieval errors and missing history.
Two regressions fail with the original collector; all 33 platform tests pass
without skips. The healthy-workload audit also rejects error-level, critical and
unhandled runtime failures even when receipts reconcile and no OOM is logged.

## WAL block screen at fixed offload threshold

All five passes use the same `e5883fa8` managed/trimmed image, 1 GiB runtime
limit, normal probes, 200 × 1 MiB at C16 and a full 180-second observation.
Every WAL is fresh with its own marker. The strict 1 MiB Files threshold remains
fixed, so these bodies remain inline.

| WAL block | Send duration | Confirmed / unknown | Extra deliveries | Runtime errors during workload | Verdict |
| --- | --- | --- | --- | --- | --- |
| 4 KiB | 43.136 s | 200 / 0 | 118 | 14 | FAIL |
| 8 KiB | 15.407 s | 200 / 0 | 9 | 7 | FAIL |
| 16 KiB | 9.212 s | 200 / 0 | 6 | 1 | FAIL |
| 32 KiB | 5.340 s | 200 / 0 | 0 | 2 | FAIL |
| 64 KiB | 3.674 s | 200 / 0 | 0 | 1 | FAIL |

All five have complete observed runtime log coverage, zero managed-OOM log
occurrences, zero runtime restarts and zero missing confirmed deliveries.
Send duration is for this bounded batch, not an acceptance/delivery latency
distribution or formal throughput result. Larger blocks improve this observation,
but no geometry is accepted from this screen. The 16/32/64 KiB errors include
failed body transfers with connection reset by peer, followed by successful
retries. Successful delivery attempts include two retries at 32 KiB and one at
64 KiB. These errors are retained as failures.

The fixture image's Python 3.14 TCP accept backlog is five, while SlimFaas can
dispatch 64 requests to its function pod. A gated 64-connection socket test fails
with the original capacity and passes with 128. A separate, fixed-64-KiB
Kubernetes control completed once per capacity (5, then 128). Both delivered all
200 confirmed messages without unknowns, extras, restarts, observed managed OOM
or error-level runtime logs. Both had complete observed log history and zero
TCP ListenDrops/ListenOverflows deltas on the same function pod. Send durations
were 4.367 s and 3.963 s. This control does not establish the cause of the earlier
resets; neither the socket regression nor this single pair proves that causal
link. No runtime geometry or offload default is changed.
The [TCP control observations](tcp-backlog-net11-14.json) include the full
per-pod counter deltas and exact source for each pass.

The [screen observations](wal-block-screen-13.json) include a separate read-only
allocation inventory, with every original WAL size/hash reverified after all
writers stopped. The initial private coordinator's unavailable allocation field
used a zero placeholder; the public observations replace it with actual stat
measurements. Retained post-compaction file sizes/counts are not cumulative WAL
writes or peak file counts. No original index or state was rewritten.

## Async offload threshold screen

Commit `f14b05b3` makes the strict threshold configurable through
`SlimFaas__AsyncBodyOffloadThresholdBytes`, preserving the 1 MiB default.
Twelve boundary regressions fail when the endpoint ignores the setting; all
49 endpoint cases and nine option controls pass after correction. They cover
128/256/512/1024 KiB minus one/equal/plus one, root/path routes and known/unknown
Content-Length. All **2,226 full solution tests** pass without failures/skips,
with UI builds and active analyzers/audits. Both macOS Native AOT publications
and native API/payload/Files/Streams/job/retry/DLQ/crash/restart controls pass:
213 confirmed operations and 11 unknown outcomes remain separate.

The initial Kubernetes screen holds a fresh 64 KiB WAL fixed, with the same
`f14b05b3` managed/trimmed image, 128-capacity receipt fixture, 1 GiB per runtime,
normal probes, 200 × 1 MiB at C16, known Content-Length and a full 180-second
post-send window. Its instrumentation differs from diagnostics 11–14, so their
timings are not direct comparison brackets.

| Threshold | Send duration | Extra deliveries | Runtime errors | Acceptance p99 | Delivery p99 | Sampled aggregate RSS | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 MiB | 15.725 s | 54 | 2 | 4.666 s | 15.214 s | 2,241.4 MiB | FAIL |
| 512 KiB | 1.335 s | 0 | 0 | 0.148 s | 2.010 s | 544.9 MiB | PASS |
| 256 KiB | 1.267 s | 0 | 0 | 0.156 s | 1.632 s | 560.3 MiB | PASS |
| 128 KiB | 1.348 s | 0 | 0 | 0.159 s | 1.702 s | 547.6 MiB | PASS |

All four deliver 200 confirmed IDs, without unknowns, confirmed missing
deliveries, observed managed OOM or restarts, with complete observed runtime
log history. The default pass has temporary quorum/read-barrier failures and
leadership instability. Healthy extra deliveries and internal failures retain
the failing verdict.

The three lower thresholds all offload this same 1 MiB body. They therefore
screen the Files path against inline storage; they do not establish which lower
boundary is best for smaller bodies. No default or production recommendation
changes from this single sequential screen. Repeated brackets, other sizes,
concurrences and chunked application workloads remain required.

Acceptance uses client HTTP elapsed time; delivery runs from the client intent
timestamp through the first successful handler completion, before receipt
fsync. All pods share this VM's UTC clock. Resource observations cover all three
runtime processes. Through the first all-confirmed reconciliation sample, CPU
cost is 105.9/53.1/53.2/52.2 ms per confirmed message and cumulative allocation
is 37,337,575/1,698,365/1,732,171/1,723,507 bytes per confirmed message. That
sample occurs within the five-second observer cadence; these windows include
their real wait/measurement overhead and the default's retries. RSS is sampled
each second. Kernel I/O combines WAL and Files; pod traffic includes functions,
probes and measurement traffic. These are scoped diagnostic observations,
not formal non-regression acceptance or an isolated durability-cost comparison.

The [complete observations](async-offload-screen-15.json),
[closed raw index](diagnostic-15-raw-file-index.json.gz),
[index metadata](diagnostic-15-raw-file-index-summary.json),
[per-ID ledgers](delivery-ledgers-15.json.gz) and
[ledger metadata](delivery-ledgers-15-summary.json) retain the original controls.

## Local file integrity correction

Commit `19031477` verifies actual body length and streamed SHA-256 before a local
copy is considered available. Both truncated-body and same-length corruption
regressions fail before correction. All **374 SlimData tests** and **2,163 full
solution tests** pass with zero failures or skips, including full UI builds and
active analyzers/audits. Verification adds a bounded local read/hash cost.
Local Native AOT/container publications and application performance checks for
this correction remain pending. Earlier `e5883fa8` images do not contain it.

The [Files audit](files-audit.md) traces the original save, announcement,
metadata, enqueue, acceptance, delivery and cleanup path. Remote durable
confirmation, admission protection, version races, reference reconciliation and
their measured cost remain implementation/validation work. This integrity
correction alone does not establish the requested two-copy acceptance guarantee.

Commit `24e36d71` fixes a separate, reproduced peer-replacement failure: a
checksum mismatch previously published the invalid body and then deleted the
ID, destroying an earlier valid local version. The regression fails on that
source; its truncated-transfer control already passes. Peer pulls now use
`SaveVerifiedAsync`, checking the existing streaming hash and actual length
before the temporary body is published. Failed validation leaves the previous
body/metadata intact without a second hashing pass. Empty and valid replacements,
wrong expected lengths and temporary-file cleanup are covered. All **380
SlimData tests** and **2,169 full solution tests** pass without failures/skips,
with full UI builds and analyzers/audits. The initial helper signature failed
CA1068 and was corrected without suppressing the analyzer; its log is retained.
Native publications and application performance checks for this source remain
pending. This does not yet make file/metadata publication jointly atomic or
provide the requested remote durable acknowledgement.
The [closed regression evidence index](storage-integrity-tests-index.json.gz)
and [index metadata](storage-integrity-tests-index-summary.json) retain the
failing/passing tests, full-suite logs and documentation build. The latter
checks 23 rendered pages, 1,672 local links/assets and 401 search entries.

## Revision and lifetime correction

Commit `0e98ed08` preserves checksum-selected revisions while an earlier Raft
reference still names their bytes. Two regressions fail on `f14b05b3`:
replacing a body makes its earlier checksum unavailable, including to a pull
using that earlier reference. Fresh IDs and same-checksum repairs retain the
historical layout; changed checksums retain revision metadata/body names and a
hard link for the historical current-body path. Multiple links on the same
volume remain one replica.

The cleaner checks durable checksums and queue/DLQ ownership before deleting
one revision. A separate regression reproduces premature deletion when local
upload expiry has elapsed but the Raft metadata TTL is still live. Its failing
control uses the original `f14b05b3` cleaner over the draft repository to isolate
that ordering; it is not an unmodified application benchmark. Both temporary
source overlays were restored with hash verification. Legacy layout migration,
retiring either old/current revisions, revision metadata temporaries and a body
disappearing after HTTP metadata lookup are covered too. Missing bodies return
404 and remain eligible for peer repair.

All **2,236 full solution tests** pass without failures or skips, with full UI
builds and active analyzers/audits. Intermediate analyzer, fixture and test
compilation failures remain retained. Native/container application checks for
this commit remain pending. Earlier `f14b05b3` measurements do not contain it.
This correction does not provide a durable pending-admission pin, file/directory
persistence barriers, remote persisted-copy acknowledgement or reference-based
background catch-up. Their fault tests and isolated costs remain open work.

The [closed regression index](file-revision-tests-index.json.gz) and
[index metadata](file-revision-tests-index-summary.json) preserve those controls.
The remote branch subsequently received a merge of `main` containing Windows
shutdown/bundle cleanup and MCP parameter fixes. Signed merge `85b628d5`
retains both histories; its combined source passes **2,247 full solution tests**
with zero failures/skips. The updated local-bundle helpers pass 30 tests, with
two platform-specific skips on macOS. This is
separate validation, not a reassignment of older measurements to the merge.

## Reproduction and provenance

The [bounded Kubernetes reproducer](producer-receipts-reproducer/README.md)
retains the driver, observer and isolation checks used for the first diagnostic.
The [serializer probe](serializer-probe/README.md) provides the separate limited
heap regression. These controls retain failures and reject reused campaign
names or existing WAL directories.

The dedicated cluster uses an explicit kubeconfig/context. Runtime source,
image hashes and actual executable/DLL hashes are checked independently. Original
state is never cold-bootstrapped or reinterpreted to obtain recovery.

The [delivery ledger export](delivery-ledgers.json.gz) and
[export metadata](delivery-ledgers-summary.json) retain 10,098 per-ID receipt
records across 32 closed diagnostics 05–12. Complete original JSONL files are
also archived losslessly, with every original size/SHA-256 checked against the
retained VM index. Public records expose the controlled client/receipt fields
needed to correlate IDs, attempts, hashes and pod/writer identities. They omit
arbitrary request/response fields. Unknown outcomes remain distinct. The original
formal ten-duplicate observation has no exported per-ID receipts and is not
reconstructed by these later diagnostics.

A [separate ledger export for diagnostics 13–14](delivery-ledgers-13-14.json.gz)
and its [metadata](delivery-ledgers-13-14-summary.json) preserve the subsequent
WAL/TCP-screen IDs without rewriting the earlier export. The
[closed raw index for diagnostics 11–14](diagnostic-11-14-raw-file-index.json.gz)
and [index metadata](diagnostic-11-14-raw-file-index-summary.json) cover 625 local
files and 15 original VM indices, including the available log generations and
the separate post-stop allocation measurements. Missing previous-process logs
in diagnostic 12 remain a recorded gap.

The measured runtime source `e5883fa8` remains available on
`codex/pr506-evidence-e5883fa8`. Its missing DCO trailer was corrected, with the
user's approval, by replacing only that commit with identical-tree `f94030d2`
after measurements closed. The leased push preserved all dirty work and the
original source object. Binary/image provenance remains attached to the actual
build source; the metadata-only correction does not change those artifacts.

The [source/publication provenance](followup-provenance.json) records Native AOT
and managed/trimmed Linux modes separately. The closed
[raw file index](diagnostic-raw-file-index.json.gz) and
[index metadata](diagnostic-raw-file-index-summary.json) retain 1,671 local file
hashes and 26 original VM state indices from diagnostics 04–10, regressions,
allocation probes and native controls 04–06. Active monitoring and diagnostic 11
are explicitly excluded; they require a later closed index. Verify and expand:

```bash
shasum -a 256 docs/reliability/2026-11-15/diagnostic-raw-file-index.json.gz
gzip -dc docs/reliability/2026-11-15/diagnostic-raw-file-index.json.gz > /tmp/slimfaas-followup-index.json
shasum -a 256 /tmp/slimfaas-followup-index.json
```

Compressed SHA-256:
`58c250560b7d36af69948fe69cb236e401dc2740bd515edff972e3135c87edb6`.
Expanded SHA-256:
`79c2d9904729010fcf83d70467f21f67cf4f1ab48c7fd1f689aa15434c6840c0`.

Earlier fixture and observer failures are retained too. Diagnostic 04 precreated
a WAL marker without enabling fresh bootstrap, so the conservative existing-state
guard prevented readiness; its unmarked control became ready. This is an invalid
fixture, not evidence of a product bootstrap defect. Diagnostic 05 sent traffic
but its observer stopped on a connection failure before the full window; all four
observations are incomplete. Diagnostic 06 corrected the observer and completed
all windows on `1ecba4d1`; all four failed, with accepted operations still missing
at the bound and runtime restarts. Missing at a bound remains distinct from
confirmed permanent loss. Their original state and failed captures remain indexed.

## CI and remaining work

The `766ada9b` main, dashboard and reliability workflows pass, including the
five Native AOT platforms, standalone bundles, .NET/Python SDK contracts and
Linux/Windows fault checks. FOSSA License Compliance passes; the external
SonarCloud Code Analysis gate still fails. No analyzer or audit was disabled.
For `c2fa989a`, dashboard/documentation, Linux/Windows reliability and FOSSA pass.
Its main workflow fails in the Windows Sonar job: the concurrent PrivateMemory
WAL test reaches its existing 20-second deadline, and an early-response WebSocket
test times out while connecting to a peer. Other main-workflow Native AOT and
bundle jobs pass. These failures remain open; no deadline was extended and no
test was skipped.
The earlier `b7aed84c` Ubuntu job-diskfull run failed its 300-second readiness
bound. Its original artifact and isolated filesystem image remain preserved;
a later successful workflow does not establish that this availability defect
has been corrected.

Formal non-regression, original-state recovery for bounded missing receipts,
renewed endurances and final-source performance comparisons remain open.

## Persistence and atomic-admission components

Commit `f36f7c5a` adds optional local file/directory persistence barriers and an
explicit persisted-copy RPC. Its complete .NET suite passes 2,269 tests with
zero failures or skips. Real macOS tests exercise directory synchronization;
transfer tests reject incomplete/corrupt bodies and mismatched ID, checksum,
length, node and declared volume receipts. Announcement ACKs remain distinct
from persisted-copy receipts. `DurableWrites` stays disabled by default.

The next component adds a pending admission pin and a single Raft mutation that
publishes metadata, a queue element or public alias, and its active reference
together. The 23 new tests cover shared/missing/wrong copy proofs, idempotent
commit, conflicting tokens/packets, pending expiry, late commands after retirement
and clock rollback, alias replacement/TTL, queue collision and DLQ/replay lineage.
The complete .NET suite passes 2,292 tests with zero failures or skips, including
full UI builds, analyzers and audits. No dependencies or lockfiles changed.
The documentation build checks 23 pages, 1,679 links/assets and 403 search entries.

This additive operation advertises **SLDC/6**. Upgrade all Raft nodes together;
mixed SLDC/5 and SLDC/6 clusters are deliberately incompatible. Existing command
envelopes and historical snapshots/producer receipts retain their formats.

These components are not yet connected to HTTP acceptance or the durable-reference
repair worker. They do not establish the requested two-copy `202`/Files `200`
guarantee or its performance cost. Independent backing volumes remain a deployment
requirement: distinct persisted root markers alone do not establish physical
independence, and kind workers share the host VM. Native/container publication,
the dedicated acceptance-fault matrix and final-source comparisons remain open.

The [component evidence](file-admission-components.json),
[closed raw index](file-admission-tests-index.json.gz) and
[index hashes](file-admission-tests-index-summary.json) preserve exact tested
sources, full-suite results, compiler failures and successful checks.

## Opt-in acceptance and reference reconciliation

The runtime follow-up connects the components through
`SlimData__Files__RequiredDurableCopies`: default `0` retains historical
acceptance, `1` confirms local persistence, and `2` requires local persistence
and a distinct confirmed persisted remote copy before atomic publication.
The async packet retains its seven-field MemoryPack format. Durable public
uploads keep public names as aliases to fresh immutable physical IDs; GET and
listing retain those public names. Copy failure prevents publication and
returns `503`; capacity still returns `429`. Durable upload TTLs must be positive
when supplied. Copies and membership are checked before publication; an
uncertain HTTP outcome is still distinct from confirmed acceptance.

A periodic worker reads current Raft references, including historical file
metadata, and repairs missing or invalid bytes without depending on historical
announcements or blocking general readiness. It limits selected IDs, bytes and
concurrent transfers, retains no historical payload snapshot across awaits, and
revisits failures. Existing announcement copies also receive a persistence
barrier. Managed retirement is authoritative and preserves pending, retry,
callback and DLQ/replay owners. Late copies remain eligible for cleanup after
their original admission expires; physical IDs are never reused by the HTTP path.

A further actual two-repository/RPC regression reproduces a positive copy
receipt after the copied admission marker was dropped. The corrected **v2**
copy protocol binds the token and original deadline, rejects missing/mismatched
markers and echoes the token in the receipt. A v1 notification or persisted-copy
reply cannot satisfy the v2 request. The failing control and positive/negative
transfer cases remain in the evidence index.

The final combined suite passes **2,388 .NET tests, zero failures and skips**,
with full UI builds, analyzers and audits active. This includes both strict
offload boundaries for Content-Length/chunked requests, root/path endpoints,
copy-confirmation gating, membership loss before publication, historical aliases,
reference repair without announcements, interrupted repair, bounded progress,
DLQ ownership and guarded cleanup. Three failed Range controls become green
with bounded offset continuation; original deadline/disposal controls stay green.
The documentation build checks 23 pages, 1,683 links/assets and 405 search entries.
Dependencies and lockfiles are unchanged. See the
[exact tested source and results](file-admission-runtime-evidence.json),
[closed raw index](file-admission-runtime-tests-index.json.gz) and
[index hashes](file-admission-runtime-tests-index-summary.json).

This remains an opt-in implementation under validation. No current-source native
publication, permanent ingress-plus-volume-loss test, final endurance, or
comparative two-copy cost is claimed by these unit/component results. Kind
workers still share their host VM; distinct declared volume markers do not prove
independent backing hardware. Existing performance failures, incomplete references
and the original accepted-operation recovery checks remain open. The next phase
must exercise dedicated process/volume faults and measure the selected configuration
before recommending it for Kubernetes. This is not an exactly-once execution or
LTS certification claim.

## Application controls and first-peer repair failure

Source `49e9801f25e4ef13c4b9dfeb9e9cf4a5ece107aa` publishes all three macOS
ARM64 Native AOT executables and the Linux musl ARM64 managed/trimmed image.
The Native two-copy quick control stops at Files `503`: native Local nodes
declare one shared storage root, so they cannot supply distinct-volume receipts.
This is a rejected configuration, not a successful two-volume durability test.
The separate local-persistence control completes API, six-size Content-Length and
chunked payloads, Files through 16 MiB, retries/DLQ, real jobs, Streams and full
original-state restart: **205 confirmed operations, ten unknown outcomes and no
confirmed missing operations**. Its 49 completed handler attempts comprise 44
successes and five deliberate retry failures. Eight publications reach two
different subscribers. No successful ID/function pair repeats. Original raw
accounting retains all attempts and is not rewritten.

The dedicated Kubernetes controls each submit **one** 1 MiB request; inherited
common load arguments do not turn them into a 200-message C16 measurement.
They pause the third runtime before upload, confirm the remaining two copies,
kill the ingress after acceptance and move all its data-volume contents outside
active mounts without restoring them. The paused runtime is killed to discard
old in-memory announcements before returning. Internal local-only HEAD and body
hash checks distinguish background catch-up from a GET-triggered pull.

| Request and ingress | Acceptance | Returned-node repair | Fresh-volume ingress repair | Result |
| --- | --- | --- | --- | --- |
| Async, follower | 202 | 3.063 s after readiness | 3.862 s after readiness | PASS, one delivery, full 180 s observation |
| Async, leader | 202 | 2.862 s after readiness | 4.067 s after readiness | PASS, one delivery, full 180 s observation |
| Files, follower | 200 | Absent after 180 s | Not reached | FAIL, peer-search starvation |

The async controls have no unknown outcomes, confirmed missing/corrupt bodies or
extra deliveries. Healthy-majority readiness takes 1.818/2.023 s. Complete
current/previous logs cover five process instances per control, with zero managed
OOM occurrences. Two/twelve error-level no-leader occurrences remain recorded
during injected restart/recovery; none occurs after fresh-ingress repair. These
are fault-control results, not clean healthy-load log verdicts. All workers share
one VM filesystem, limiting the result to logical PV-content loss.

The Files failure preserves a valid body on the surviving active volume and the
quarantined original body, both matching the acknowledged SHA-256 and length.
It does not demonstrate permanent loss. Its logs show repeated connection refusal
from the first peer. An exception during that peer's HEAD escaped the member loop,
preventing a request to the healthy holder. Five new tests fail before correction;
the caller-cancellation control already passes. The correction continues after
probe transport failures and bounds a silent HEAD at five seconds, preserving
caller cancellation and the enclosing transfer deadline. **106 targeted tests
and 2,395 full .NET tests pass, with zero failures/skips**; 28 campaign tests and
41 platform tests pass. Analyzers, audits and full UI builds remain active.
Three failing-before reporting tests also separate retries and fanout destinations
from successful repeated ID/function deliveries, while keeping legacy fields.

See [application provenance and observations](file-admission-application-evidence-01.json),
the [closed raw index](file-admission-application-index-01.json.gz) and its
[hashes](file-admission-application-index-summary-01.json), plus
[peer-fallback regression evidence](file-peer-fallback-evidence.json) and its
[raw index](file-peer-fallback-index.json.gz).
The [frozen Kubernetes reproducer](file-admission-fault-reproducer-49.tar.gz)
contains the actual source49 profile; follow the [reproduction instructions](file-admission-fault-reproducer.md).
New-source AOT/image publication, replay of the failed Files fault, remaining
volume cases, durability-cost brackets and final long campaigns remain pending.
The original Windows Sonar job also retains a WebSocket peer-connect failure;
its targeted retry is separate from local passing tests. No performance or LTS
acceptance follows from these controls.
