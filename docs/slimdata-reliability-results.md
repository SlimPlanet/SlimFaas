# SlimData reliability results — 8–9 November 2026

**Verdict: not ready to certify SlimFaas 1.0 LTS.** Sixteen confirmed defects have
corrections and regression evidence across the recorded revisions. Real three-node
Local campaigns demonstrate substantial recovery coverage, including all six
Linux/Native Windows long-absence roles on the final runtime code. Final Native
API/short-outage, official SDK, owner-timeout and empty-PATH bundle checks now pass
on `a95d49e8`. The complete six-role workflow on `571728a9` is green and its
counter observations pass the stronger ledger audit. Performance non-regression
remains inconclusive, and incomplete durability/fault coverage still blocks
certification. Historical failures remain recorded. A passing campaign on one revision or
OS does not override a failed campaign on another.

Tracking: [issue #505](https://github.com/SlimPlanet/SlimFaas/issues/505),
[draft PR #506](https://github.com/SlimPlanet/SlimFaas/pull/506).
See the [campaign guide](slimdata-reliability.md) for executable commands,
accounting rules, Linux fault isolation and CI entry points.

The [13 November integration report](reliability/2026-11-13/README.md) tracks the
subsequent macOS/Podman/Kubernetes work and the ordered integration of PR #504
and PR #514. Its .NET 11 campaigns and preserved .NET 10 observations have
separate source provenance and verdicts.

## Versions and evidence

The baseline is `496e8739e5f7b4326f64520fe4caa4e0e23f8d06` (v0.88.0).
The Windows Native binary used for the first paired benchmarks includes fixes 1–10 and was built from
`7920d9f99d36ef0c68a0b497d4ee4c3526d2a979`:

| Native runtime | SHA-256 |
|---|---|
| Baseline | `4b26047060c649f2760c2ae11aeb7361d35109e3206a706f6f8e5ff48932b66c` |
| Fixes 1–8 | `80700d9d0b357d33ecce4d604fd056470e875ab779c8a117dfd5c5748947f122` |
| Fixes 1–9 | `9be0f2927989de381f2bbc42e463db59ef449d672c825690063016962235a0d4` |
| Fixes 1–10 | `15d7f7cf7dfa59ec3b5ca31a8541b1cd50407e666e3e65edaa0ac616917427e2` |
| Fixes 1–10 + DotNext 6.12.0 controls (before platform default change) | `8bd724a700b3f39eacbb22f104c482cc518235db0724fd401d5cfa6ab09f5627` |
| Fixes 1–14 (`7e1f20a6`) | `4b68aa1831bc0b10d593798432139d108357877645331f713aedf1d6aec1bedd` |
| Fixes 1–15 (`b00224c5`) | `a46a56615beb98ddd48a7b54627a976c708c9ba669fa58551af6886630e4f36d` |
| Fixes 1–16 (`a95d49e8`) | `1f2ea78a7db8f945a0b69b86eeab616eb576fef78624f3f9f180b3b5dc8e05c4` |

The complete stale-leader correction is in `4e80c9a1`; runtime C# is unchanged in
`a95d49e8`, which strengthens the campaign's counter oracle. Managed validation
passes 1,996 tests and the actual leader-route/owner-timeout regressions. Its final
Native publication succeeds with optional DGML/mstat diagnostics disabled and
single-threaded compilation. The source revision is confirmed by the executable's
product version. Fresh Native/benchmark results are tracked separately below.

Local publication encountered three compiler ENOSPC failures. A capacity trace
captured free space dropping to **57,344 bytes** before recovering when compilation
exited; post-exit free space alone had hidden this transient pressure. Changing
output compression alone did not resolve it. The successful publication still
reached only **65,773,568 bytes** free during its staging peak, so its initial
4.3 GB margin must not be treated as a generally safe minimum. These are build
environment results, separate from runtime recovery. Fifteen inactive PDBs were
archived and verified before releasing redundant working copies; executables and
campaign volumes remain retained. All build attempts, archive hashes and effective
compiler flags are in [Native build evidence](reliability/2026-11-08/final-native-build.json).

Historical `parameters.json` files record the checkout at execution time. For a
preserved executable, its recorded runtime hash identifies its implementation;
the checkout may already contain later changes. Original pre-resume reports,
manifests, client/function journals, logs, WALs and snapshots remain retained.
Resumption never deletes or rewrites the original volumes to force success.

Compact evidence is versioned under [reliability/2026-11-08](reliability/2026-11-08/).
The local raw evidence root is `artifacts/slimdata-lts/` (ignored by Git).
CI artifact links below include raw state and expire according to the workflow's
14-day quick / 30-day endurance retention. Downloaded original archives and their
hashes are retained locally. This report is a dated record, not a promise that
expiring hosted artifacts remain available indefinitely.

## Async: 2 MB and 2 MiB

The tested sizes are exactly 65,536; 262,144; 1,048,576; **2,000,000 (2 MB)**;
**2,097,152 (2 MiB)**; and 4,194,304 bytes. HTTP tests use Content-Length and
chunked transfer at concurrency four, with function-side SHA-256 and length.
The original HTTP baseline passed these sizes. A later Native HTTP campaign also
passed 8,388,608 and 16,777,216 bytes with both encodings. A separate explicit
1 MiB configured limit rejects overflow with HTTP 413. No HTTP size limit was
blindly raised.

The confirmed SDK defect occurs **after HTTP acceptance**. A real 2 MiB message
became a 2,997,916-byte JSON/base64 WebSocket envelope, exceeding the Python
WebSocket library's 1,048,576-byte default receive cap. The connection closed
with code 1009 before the callback ran. The Python client now defaults to a
positive, configurable 32 MiB encoded-envelope limit and a four-frame receive
queue. JSON escaping also contributes to the encoded size; the payload limit
must not be confused with the envelope limit. Real-connection SDK regressions
cover 2 MB, 2 MiB, 4 MiB, heavily escaped data and explicit undersized limits.

A second defect could discard an accepted message when its WebSocket owner was
unavailable beyond the delivery timeout. Lease expiry produces status 504, which
was absent from the default retry policy. The Native before case accepts a 2 MiB
message and never observes its callback; managed and Native after cases observe
the checksum after owner recovery. The test changes only the delivery timeout
to eight seconds, suspends the owner for twenty seconds after acceptance and
retains the runtime's default retry statuses. Explicit custom policies remain
authoritative. Already persisted policies are not migrated; already discarded
work cannot be reconstructed by upgrading.

These diagnoses do not explain every large-body failure. Earlier Windows Native
SDK runs failed concurrent large Stream publication. Later complete Native SDK
campaigns pass on `7e1f20a6`, `b00224c5` and final `a95d49e8`, but observed large Stream latency remains
a performance gate.

## Confirmed defects and regressions

| # | Defect and change | Before/after evidence |
|---|---|---|
| 1 | Queue attempt metadata counted reservation attempts incorrectly; preserve total tries, expose zero-based retry number and correct final-attempt flag | Three-case regression; real 2 MiB attempts 0/1/2, final flag only on 2 |
| 2 | Peer file pull rejected zero Content-Length | Empty-file regression fails before; empty and nonempty cases pass after; real cross-node file checks |
| 3 | Default job lease policy omitted 504 and could discard work between pop and launch | Real accepted Native job disappeared after crash; actual policy/interpreter regression fails before and retains it after |
| 4 | Optional local process-log IOException could terminate supervision | Write/dispose regressions, warning-once handling and later output recovery; console closed-pipe handling |
| 5 | Python SDK receive cap rejected the encoded 2 MiB envelope | Real pre-fix close 1009; actual SDK connections and callbacks after fix |
| 6 | MemoryPack root formatter lookup failed during replay and unexpected failures could be classified as skipped commands | CI command 20/index 358/term 5; four valid decoder cases fail against preserved pre-fix DLL; generated readers plus two producer-cache regressions pass |
| 7 | Native requests could arrive before job definitions were loaded, routing accepted work to an undefined Default executable | Real startup refresh delayed five minutes; declared job executes immediately after fix, missing configuration is rejected before mutation |
| 8 | Internal HashSet reads lacked a leader commit barrier | Native DELETE 204 followed by stale cron schedule on another node; deterministic read-blocking regression fails before and passes after; real retained-volume scenario passes |
| 9 | Default Async lease policy omitted 504 | Real 2 MiB owner-timeout before failure; managed and Native after success; actual policy/interpreter regression |
| 10 | Terminal DotNext WAL failure left a live but unrecoverable runtime | Linux ENOSPC CI retained the poisoned-WAL trace; diagnostics now logs critically and rethrows so the host stops and Local can restart it; terminal/transient worker regressions pass; later CI observed this stop and recovered its first disk-full cycle in 4.908 s, but failed a later file read during restarts |
| 11 | Interrupted snapshot transfer could publish a truncated file and state/index | Real Native snapshot 765,436 bytes versus 15,350,868 on peers; transactional installation and nine real-WAL snapshot regressions; archive replay verifies confirmed data while retaining two missing historical job receipts |
| 12 | Native job environment shapes lacked rooted MemoryPack formatters | Accepted real Native job repeatedly failed formatter construction; explicit registrations and actual literal/Kubernetes-reference payload campaign pass |
| 13 | Acknowledged Local jobs existed only in supervisor memory | Gated acknowledged job disappears after full supervisor restart before the fix; durable plan journal, same-identity replay and real managed/Native recovery pass |
| 14 | A cancelled WebSocket attempt waiting for the send lock could retain an impossible callback reservation | Original CI accepted ID has no send and never executes; blocked-send regression fails before; unsent release, failed-active-send abort and complete Native SDK campaigns pass |
| 15 | ENOSPC while persisting a Local job exit could terminate supervision | Forced real job journal failure exits the before-fix supervisor; periodic dirty-transition reconciliation survives three storage-fault cycles, confirms all accepted work and preserves the terminal job checkpoint across restart |
| 16 | An ordered producer waited for the old leader's 100-second HTTP timeout after a new leader was available | Real original/token-only leader-route controls fail at 15 seconds; consensus plus observed-leader epochs pass in 6.156 seconds, with 13 focused tests and the unchanged 8-second/20-second owner-timeout regression |

The suspected formatter publication window is an inference from the CI trace and
matching deterministic regressions, not an upstream formal proof. Generated
static readers retain the existing persisted wire format. Unexpected failures
are propagated rather than silently counted as incompatible payloads.

Fix 10 applies to the SlimFaas diagnostics worker and normal outer-host stop
behavior. It is not a demonstrated standalone SlimData host recovery guarantee.
Transient quorum and ordinary I/O failures do not deliberately stop that worker.

## Executed absence matrix

All successful entries below use real Local processes, real functions/API traffic
and original volumes. Suspension is a process/communication outage, not a claim
of packet-only partition coverage. Dedicated Linux packet-drop coverage is
reported separately.

| Requested absence | Follower | Leader | Quorum |
|---|---|---|---|
| 1 second | Passed, Windows Native fixes 1–8 | Passed, same campaign | Passed, same campaign |
| 5 seconds | Passed, Windows Native fixes 1–8 | Passed, same campaign | Passed, same campaign |
| 10 seconds | Passed, Windows Native fixes 1–8 | Passed, same campaign | Passed, same campaign |
| 30 seconds | Passed, Linux `5db8e568` | Passed, Linux `5db8e568` | Passed, Linux `5db8e568` |
| 60 seconds | Passed, Linux `5db8e568` | Passed, Linux `5db8e568` | Passed, Linux `5db8e568` |
| 300 seconds | Passed, Linux `5db8e568` | Passed, Linux `5db8e568` | Passed, Linux `5db8e568` |
| 900 seconds | Passed, Linux `5db8e568` | Passed, Linux `5db8e568` | Passed, Linux `5db8e568` |
| 1,800 seconds | Passed, Linux `5db8e568` | Passed, Linux `5db8e568` | Passed, Linux `5db8e568` |
| 3,600 seconds | Passed, Linux `5db8e568`; separate Windows state failed | Passed, Linux `5db8e568` | Passed, Linux `5db8e568` |

The [Linux endurance run](https://github.com/SlimPlanet/SlimFaas/actions/runs/42489059631)
has three successful endurance jobs and an independent failed Windows quick job;
the workflow as a whole is failed. Its revision includes generated replay readers
but predates the subsequent job-bootstrap, HashSet barrier, default Async 504 and
terminal-WAL-stop changes. Do not present this matrix as a final-revision rerun.

| Linux role | Final confirmed / unknown | Full recovery after 3,600 seconds | New leader observed |
|---|---:|---:|---:|
| Follower | 9,240 / 654 | 16.061 s | Not applicable |
| Leader | 11,404 / 266 | 12.632 s | 4.835 s |
| Quorum | 120 / 26,426 | 20.009 s | Not measured as a separate milestone |

Each job then passed successive crashes and full persistent restart, with final
business reconciliation. Unknown HTTP outcomes are individually classified;
they are not counted as confirmed loss or as proven rejection.

Additional retained Windows runs passed a leader-hour absence (67.640-second
recovery; final 7,790 confirmed / 2,603 unknown) and a seeded Native quorum-hour
absence (85.812-second recovery; final 42 / 14,093). These used older revisions.
An earlier quorum campaign had only two confirmed prefault probes and is not
proof of the later eight-API seeded invariant.

The original Windows follower campaign passed 1,800 seconds, then failed its
bounded return after 3,600 seconds. A later restart of that same state using the
newer runtime also failed the 300-second all-node readiness budget. No final
reconciliation was completed: an empty `missing` array in a failed report is not
evidence that no confirmed work was lost.

## API and fault coverage

| Area | Executed successful evidence | Remaining coverage or failures |
|---|---|---|
| Sync | Real checksum/status, mixed concurrency, wake 0→1→0, timeout with later execution, leader/follower/quorum faults | Exhaustive cancellation and deterministic crash exactly within a selected request |
| Async | Six sizes × two encodings × concurrency four; Native 8/16 MiB; retries, DLQ repair/requeue, SDK callbacks, owner timeout, crash/restart; final Native and latest Linux/Windows CI pass | Large-body tail latency and performance non-regression remain unresolved; historical failures are retained |
| Streams | Two real independent HTTP consumers, concurrent append, paginated order, contiguous ACK, length/age retention, 410 boundary/gap counters, offline SDK retention/reconnect and restart; final concurrent Native 2 MiB publication passes | Earlier Native publication failures and variable latency; advanced manual reserve/ACK and long-absence retention combinations remain incomplete |
| Files | Zero/one/4/16 MiB, all-node checksums, concurrent mixed traffic, interrupted upload, TTL/delete; original unavailable CI file cold-replays on all nodes | Earlier post-ready 503 remains failed; permanent originating-volume loss and deterministic mid-transfer crash are not proven |
| Sets / HashSets | CRUD, TTL, deletion on all nodes, concurrent INCR/DECR, INCRBY/DECRBY/INCRBYFLOAT, explicit limit, mixed-fault convergence | Not an exhaustive linearizability history/model check |
| Jobs / DLQ | Actual concurrent job processes/arguments, retry expiry policy, DLQ body/repair/requeue, bootstrap and real cron across full restart/deletion | Cron scheduling during prolonged outages and every job crash window remain incomplete |
| Packet faults | Linux isolated bidirectional Raft drops at 1/5/10/30/60 seconds; CI netem 25 ms and restart | Asymmetric partitions and additional latency/loss profiles not executed; WSL lacks netem support |
| Disk full | Real marked 128 MiB ext4 exhaustion; final targeted job-journal ENOSPC survives three cycles in 9.076/9.038/8.026 s, confirms all accepted work and restarts on its retained volume | Earlier general ENOSPC campaigns failed later file reads; targeted journal success does not erase them or establish arbitrary device-I/O guarantees |
| Persistence | Successive abrupt crashes, full original-volume restart, observed WAL/snapshot progress and replay-counter gates | Crashes were not synchronized to exact WAL-write/snapshot-install/compaction instruction windows; other targeted I/O faults not executed |

CI [42501124569](https://github.com/SlimPlanet/SlimFaas/actions/runs/42501124569)
observed the new terminal-WAL critical log and normal BackgroundService host stop,
then passed its first disk-full recovery in 4.908 seconds. Its subsequent crash
sequence timed out reading a newly confirmed 1,024-byte file. The separate repeated
WSL campaign failed with a post-ready 503. Neither overall campaign is successful.
Root causes of these later file/recovery failures are still unproven. A subsequent
read-only inspection of the original CI disk image found identical file metadata
in all three persisted snapshots and the complete 1,024-byte body with the accepted
SHA-256 on node 2. The original image hash was unchanged by inspection. Thus this
specific failure demonstrates prolonged unavailability, not disappearance of those
bytes from the archived image. No runtime replay or general durability proof follows
from that inspection; see
[linux-file-offline-inspection.json](reliability/2026-11-08/linux-file-offline-inspection.json).

CI [42502578270](https://github.com/SlimPlanet/SlimFaas/actions/runs/42502578270)
passed Linux quick/SDK/network/disk/restart coverage, while Windows failed waiting
300 seconds for an accepted **256 KiB Async** callback. Its other eleven size/
encoding callbacks were observed. This is a missed delivery deadline, not proof
that every absent callback was permanently discarded. It is independent evidence
that fixing the original code-1009 size cap does not resolve all delivery failures.

The Native quick campaign with fixes 1–8 passed **266 confirmed / 12 unknown**,
including larger HTTP bodies and all short role absences. Its function journals
contain 96 successful deliveries for 91 distinct IDs: five extra successes across
two IDs, allowed by at-least-once delivery. Two-consumer fanout is counted by
`(message ID, function)` so legitimate fanout is not mislabeled duplication.

## Historical Windows large Stream failures and latency controls

On a fresh Windows Native run with fixes 1–9, all twelve SDK Async size/encoding
cases passed. Four concurrent 2 MiB Stream appends then produced three accepted
writes at 64.063, 109.281 and 113.297 seconds and one unknown at 120.016 seconds.
The campaign failed with 18 confirmed / one unknown. Retained runs also failed
after resuming with class-specific delivery diagnostics and global Warning.

Logs contain repeated 2.5-second Raft RPC cancellations and follower lag. No
terminal `System.Diagnostics.UnreachableException` or format-skip violation was
observed in this case. Neither a serialization-size root cause nor an AOT-specific
cause is established. Raising deadlines, changing Raft timeouts or moving Stream
bytes outside their durability contract is not accepted as a fix. An older
retained SDK run passed with a 120-second budget after an initial failed run;
its maximum accepted Stream latency was 89.812 seconds, not a 30-second guarantee.

### Additional WAL strategy controls

Three later isolated Native runs on the same fixes-1–10 binary passed the complete
SDK matrix, offline retention, successive crashes and full restart with unchanged
120-second request budgets. Explicit strategies were Private / Shared / Private.
The four initial concurrent 2 MiB Stream acceptance latencies were:

| Strategy/run | Seconds per accepted publication | Final confirmed / unknown |
|---|---|---:|
| Private 1 | 1.688 / 6.828 / 8.469 / 10.782 | 66 / 6 |
| Shared control | 15.390 / 49.312 / 71.828 / 73.484 | 58 / 10 |
| Private 2 | 1.531 / 11.219 / 14.297 / 14.297 | 68 / 4 |

Unknowns include the intentionally injected later crashes. See
[the complete profiles](reliability/2026-11-08/wal-strategy-profiles.json).
These controls show substantial strategy sensitivity and intermittent behavior;
they do not erase the earlier failures or prove that this setting alone fixes
them. A Windows PrivateMemory default was trialled in `04476c15` and then withdrawn:
subsequent complete Native recovery and independent CI checks failed. The runtime
retains SharedMemory by default and the existing explicit PrivateMemory option.
More private RAM can be required for a large retained WAL. Earlier recovery
failures are not waived by faster successful publication controls.

The exact upstream implementation performs synchronous mapped-page flushes and
additional Windows metadata/disk flushes for SharedMemory, while PrivateMemory
uses private buffers with asynchronous write-through writes. This is consistent
with the latency difference, rather than proof of a unique cause.
See [the page implementation](https://github.com/dotnet/dotNext/blob/7b9f968209bfc1dd1cabed9c2b54327cb0ac0980/src/cluster/DotNext.Net.Cluster/Net/Cluster/Consensus/Raft/StateMachine/WriteAheadLog.Page.cs).

The original follower-hour state still failed the 300-second startup budget with
both Native/private and managed/private controls. A managed triage dump near the
deadline showed a thread inside `WriteAheadLog.AnonymousPageManager` construction.
One node retained **313,803 data-page files / 1,285,337,088 logical bytes**. The
[upstream loader](https://github.com/dotnet/dotNext/blob/7b9f968209bfc1dd1cabed9c2b54327cb0ac0980/src/cluster/DotNext.Net.Cluster/Net/Cluster/Consensus/Raft/StateMachine/WriteAheadLog.PageManagement.cs)
eagerly populates existing pages. This identifies startup I/O as an investigation
path; permanent data corruption is not established. The managed diagnostic
included a roughly three-second triage dump near its deadline. Its original
source-commit annotation is corrected in a separate artifact; the DLL's embedded
revision and SHA identify fixes 1–10.

A separate 1,800-second cold-start diagnostic completed with the original volumes,
managed fixes 1–10 and `PrivateMemory`. Startup plus the initial confirmed-ledger
verification completed before the first metrics capture at **619.698 seconds**;
the harness did not record an independent readiness timestamp for that historical
attempt. The API scenario and final reconciliation passed, with **4,938 accepted /
2,521 unknown** operations in the cumulative ledger, in **833.270 seconds** overall.
This resumption did not repeat the hour-long outage. Its larger budget does not
change or pass any original 300-second recovery criterion. Supplemental parameters,
historical failures and raw hashes are in
[follower-long-and-lease.json](reliability/2026-11-08/follower-long-and-lease.json).
Existing WAL
chunk geometry must remain unchanged for an existing WAL, according to the
[upstream options contract](https://github.com/dotnet/dotNext/blob/7b9f968209bfc1dd1cabed9c2b54327cb0ac0980/src/cluster/DotNext.Net.Cluster/Net/Cluster/Consensus/Raft/StateMachine/WriteAheadLog.Options.cs);
no chunk-size change or data reset was used to force recovery.

## Upstream leader-lease maintenance

The candidate now updates the central DotNext dependency from 6.11.1 to 6.12.0,
published on 6 November. Its
[release notes](https://github.com/dotnet/dotNext/blob/0c1a6621a397494d8a6f633d8a5176752b5ec9e5/CHANGELOG.md)
include the HTTP-compatible fix for
[issue 309](https://github.com/dotnet/dotNext/issues/309): empty heartbeats after a
write burst could delay lease renewal behind one silent voter despite a healthy
majority. This is a confirmed upstream defect, not an established explanation for
all of this report's Windows failures. The separate TCP framing fix is not evidence
of an HTTP framing defect in SlimData.

A new `lease-burst` Local scenario suspends one follower during a three-second
write burst, stops the producer for one second, and sends one unretried write
within a 15-second request budget. Its final baseline 6.11.1 Native attempt passed
with a **6.969-second** probe; this did not reproduce the upstream catastrophic
stall. An earlier diagnostic also passed but inserted a slow metrics capture before
the probe and is retained separately. The 6.12.0 Native probe passed at **0.016 s**,
with 22 writes during the silent burst versus five in the final 6.11.1 attempt.
The old small-volume campaign then passed forward replay, real APIs, successive
crashes and full restart with **166 cumulative accepted / zero unknown**.

All **1,974 .NET tests** and Native publication passed with 6.12.0. Both quick CI
OS jobs passed on `bc343f05`; Sonar's analysis and FOSSA License Compliance passed.
The analysis workflow itself failed one existing idle-retirement test's initial
five-second wait. Its virtual clock is now armed after observing the first result,
so the simulated retirement cannot block that initial result's continuation.
The seven selected tests and twenty repetitions passed without raising deadlines.

A fresh 6.12.0 Native SDK run using SharedMemory still **failed**: its first four
2 MiB Stream writes took **11.641 / 41.671 / 57.046 / 64.062 s**, then a later
offline-retention publication returned HTTP 503 (**21 accepted / one unknown**).
The same executable with explicit PrivateMemory passed the entire SDK, crash and
restart sequence (**81 accepted / three unknown**) with initial Stream latencies
**6.844 / 12.641 / 18.657 / 20.188 s**. These are strategy controls, not proof that
the trial platform default passes all release gates. Its new Native binary
(`3432ee01631d540bb080ac6e3a1a6d828330d2bec8f8f4eb1f303b7434a0e93d`)
passed the twelve HTTP size/encoding cases with a 15-second request budget, as did
an explicit SharedMemory control on that same binary. However, its complete SDK
campaign failed a 300-second all-node read-barrier gate after crashes, with
**1,643 accepted / 14 unknown**. Two Windows quick CI jobs also failed initial
publication deadlines. These findings do not isolate strategy as their cause;
they are insufficient validation for changing the default.

The failed Native SDK state exposes a separate concrete snapshot defect. At the
end, follower 0 remained at applied index 132 while peers were at 2,482/2,483.
Its final-named snapshot `2440-4` contained **765,436 bytes**, while the same-index
leader snapshot contained **15,350,868 bytes**. The log contains 322 references
to cancelled InstallSnapshot transfers. In the exact 6.12.0
[receiver implementation](https://github.com/dotnet/dotNext/blob/0c1a6621a397494d8a6f633d8a5176752b5ec9e5/src/cluster/DotNext.Net.Cluster/Net/Cluster/Consensus/Raft/StateMachine/SimpleStateMachine.Snapshot.cs),
`ReadFromAsync` commits the temporary snapshot in a `finally` block even when
copying throws. Numerous other truncated final-named copies remain in that
follower's directory. No format-skip violation was observed. This is a published
truncated snapshot, not proof that the healthy majority lost its confirmed data.

The entire failed run was archived before any repair/replay:
`platform-default-native-sdk-before-repair.tar.gz`, 3,303,705 bytes, SHA-256
`4602c271c6c05de613fea8bd1f9fe4d8926e0bc44129d1d544d3cf084b66ba8a`.
The original directory also remains retained. Two regressions through the real
DotNext WAL fail before the fix (cancellation and I/O failure publish a partial
file). SlimData now owns the snapshot lifecycle through public DotNext interfaces,
using a fully flushed and decoded temporary copy before publication and atomic
in-memory replacement. Startup retains unexpected-EOF files and can restore an
older complete snapshot; with no complete snapshot it fails closed. Other decoding
errors propagate. Existing state is preserved if snapshot decoding fails.

The full suite passes **1,983 tests**, including **nine** new snapshot cases,
historical 6.6.0 persistence compatibility and byte-for-byte snapshot transfer
across 64 KiB buffer boundaries. No Raft/WAL page or snapshot payload format was
changed. The adapted lifecycle carries its upstream MIT notice. At that revision,
retained-state and Native verification were pending; the completed follow-up
controls and final comparisons are reported below. See
[windows-default-trial.json](reliability/2026-11-08/windows-default-trial.json).

The dependency update also resolves four System.* packages from 10.0.13 to
10.0.14; their third-party notices are byte-identical. NuGet's direct/transitive
vulnerability audit found no reported vulnerability in SlimData's resolved graph.
All earlier benchmark and endurance outcomes remain tied to their original hashes.

## Additional Local job defects found during snapshot verification

The snapshot repair made all three original failed nodes usable in 13.297 seconds,
and confirmed Sets, HashSets, Files, Streams and counters passed verification on
each node. Delivery reconciliation still failed: two previously accepted jobs had
no independent execution receipt. Both processes were launched immediately before
the original supervisor stopped. This retained campaign is **not a passing run**.

A new controlled `job-recovery` scenario reproduced a separate Local durability
gap on the Native `df03f8b1` runtime: hold a real job before its receipt, observe
`Running` with no queued copy on all three APIs, restart the entire supervisor
without cleaning storage, then release the job. The acknowledged job disappeared
and the receipt deadline expired. Local's process/job dictionaries were volatile,
while the distributed queue had already acknowledged the creation.

Local now flushes a job plan before returning creation success, records execution
transitions and deletions atomically, and reloads unfinished and terminal runs.
Interrupted attempts can replay with the same identity under at-least-once delivery.
Unwritable or malformed storage is explicit. This initial implementation stopped
the supervisor on a failed journal transition; fix 15 below replaces that behavior
with retained dirty-state reconciliation. The managed after-fix
`job-environments,job-recovery,api,restarts` campaign passed with **143 accepted /
eight unknown / no missing receipts**, including another full persistent restart.
The full .NET suite passed **1,989 tests** before the subsequent WebSocket race
correction. The final Native job-recovery controls below also pass.

The initial gated test also exposed an independent Native AOT job defect: an
accepted job containing an environment list stayed queued because the trimmed
binary could not resolve the generated `EnvVarInput` MemoryPack formatter. Its
worker logged repeated decoding failures. The runtime now explicitly registers
the generated job collection element formatters without reflection or a wire-format
change. The `job-environments` scenario checks the literal value in a real process
and decodes the supported reference shapes; Local continues to leave Kubernetes
references unresolved. Native before/after validation is retained separately.

The second full Linux endurance matrix on `bc343f05` also passed all three roles
with DotNext 6.12.0, including retained full-cluster restart. Following the
one-hour absence, full recovery took **22.926 s** (follower), **24.832 s** (leader,
new election observed at **4.245 s**) and **15.365 s** (quorum). Final accounting
was respectively **23,036 / 28**, **21,792 / 212** and **147 / 26,413**
accepted/unknown operations. Original archives and their hashes are indexed in
[linux-endurance-612.json](reliability/2026-11-08/linux-endurance-612.json).
These results predate the new snapshot and job changes.

## Unsent WebSocket attempt retained an impossible callback reservation

The Windows quick job in the snapshot-repair endurance dispatch failed while a
separate quick run on the same revision passed. Its original artifact contains a
**1 MiB** Async publication accepted with HTTP 202 and element ID
`ad5aee92-d0eb-49b6-b73c-b503eec43fe4`. Other sizes, including 2 MB, 2 MiB and 4 MiB,
completed. This ID timed out without an `AsyncRequest sent` event; its later retries
reported no eligible client while another ID completed on that same connection.

An exact regression reproduced the reservation defect: block the connection's
send lock with a binary frame, start an Async dispatch, cancel it before the text
frame enters the socket, and retry. Previously the unsent ID remained reserved
awaiting a callback that could never arrive. A leadership change can cancel such
a locally tracked attempt. The original CI evidence is consistent with this race;
it does not prove every earlier Windows timeout had this cause.

The sender now marks dispatch only after transmission completes. Cancellation while
waiting for the lock releases an unsent reservation. Failure during an active socket
send aborts that ambiguous transport, preventing an old callback from completing a
new attempt. A completed transmission still retains its reservation until callback
or disconnection. Tests cover text/binary active-send cancellation and preserve the
real peer late-callback regression. All **17** focused WebSocket activity, peer
and queue tests pass; the unsent-ID test failed before the fix.

Revision `7e1f20a6` subsequently passed **1,992 .NET tests** and Native AOT
publication (executable SHA-256
`4b68aa1831bc0b10d593798432139d108357877645331f713aedf1d6aec1bedd`).
Its full Native API/short-fault campaign passed **351 accepted / eight unknown /
zero missing receipts**, and its real official SDK campaign passed **84 accepted /
nine unknown / zero missing receipts**. The separate eight-second delivery timeout
with a 20-second owner absence also passed. The four initial 2 MiB Stream appends
still took **1.578 / 8.547 / 45.609 / 47.062 seconds**; functional success within
the declared 120-second SDK budget does not clear the performance gate.

Native replay of a fresh copy of the original pre-repair snapshot archive reached
all three data barriers in **6.141 seconds** and verified confirmed data. Its
overall campaign still failed on the same two old job receipts. No replayed job
was synthesized to change that result.

## Job journal completion I/O terminated Local supervision

A separate endurance dispatch on `7e1f20a6` failed its Linux disk-full phase;
the PR's independent quick matrix passed both OS jobs. Cleanup's state-marker
ENOSPC initially masked the initiating exception. A diagnostic-only commit logs
the original supervision exception before cleanup. Ordinary Linux disk-full
runs recovered in **43.447 s** and **8.660 s**. They did not force the small job
journal write to fail: the latter still had **2,682,880 bytes** free after the
coarse filler returned ENOSPC.

The deterministic `job-diskfull` profile gates a real acknowledged job, suspends
the three Raft processes to prevent cleanup freeing space, exhausts the marked
filesystem with 64 KiB/4 KiB/one-byte writes, and lets the job finish while the
volume is unavailable. On `19b108bc`, `LocalJobStore.Save` threw ENOSPC while
persisting that exit, the job monitor faulted, and Local exited. The primary
stack identifies `LocalJobManager.MonitorLoopAsync`; a later marker-write error
during disposal is retained separately. All original volumes remain retained.

The follow-up keeps failed transitions pending and reports the failure once per
affected run. The monitor reconciles pending state on later cycles and reports
recovery. Until a terminal transition is flushed, its public state stays Pending.
New creations and explicit deletions still require a durable write before success;
no request waits through an unbounded retry loop. Cleanup releases owned job
processes even if its monitor faults. The strengthened real regression compares
the terminal checkpoint before and after full supervisor restart.

The after-fix run on `b00224c5` passed **230 accepted / 102 unknown / zero missing
receipts** on one retained ext4 volume: ordinary ENOSPC (**9.076 s** recovery),
the forced journal/quorum fault (**9.038 s**, including terminal checkpoint
verification across full supervisor restart), a second ordinary ENOSPC (**8.026 s**),
successive crashes and full restart. Logs explicitly show failed journal writes
and subsequent recovery. The full suite still passes **1,992 tests**. The original
failed image was archived before further replay. Later Native publication,
original-image recovery and comparisons are recorded below; the 300-second
recovery gate is unchanged.

## Six-role endurance after transactional snapshot recovery

All six dedicated endurance jobs passed on `df03f8b1`: Linux managed and Windows
Native each retained their volumes through follower, leader and quorum absences
of 30/60/300/900/1800/3600 seconds, successive crashes and full restart. The
workflow's separate Windows quick SDK job failed; it does not change these six
individual job results. This runtime predates later Local job/WebSocket fixes.

| Runtime / role | Full recovery after one-hour absence | Final accepted / unknown |
| --- | ---: | ---: |
| Linux follower | 19.962 s | 23,161 / 27 |
| Linux leader | 25.932 s | 21,624 / 211 |
| Linux quorum | 21.469 s | 159 / 26,424 |
| Windows Native follower | 80.953 s | 22,990 / 24 |
| Windows Native leader | 76.047 s | 20,751 / 219 |
| Windows Native quorum | 64.656 s | 157 / 26,381 |

Original reports, parameters, runtime attribution and complete ZIP hashes are in
[six-role-endurance-df03.json](reliability/2026-11-08/six-role-endurance-df03.json).
Later-revision matrices remain separate evidence.

## Final six-role endurance and stronger counter audit

Workflow [42538554797](https://github.com/SlimPlanet/SlimFaas/actions/runs/42538554797)
is **entirely green** on `571728a9`: both OS quick jobs and all six dedicated
endurance roles. Runtime C# is identical to the validated `a95d49e8` executable;
subsequent changes strengthen the counter/resume harness and documentation.
Every role retains its volumes through 30/60/300/900/1800/3600-second absences,
continuous mixed API traffic, successive crashes and full persistent restart.

| Runtime / role | Full recovery after one-hour absence | Final accepted / unknown |
| --- | ---: | ---: |
| Linux follower | 28.362 s | 23,237 / 30 |
| Linux leader | 28.552 s | 22,761 / 33 |
| Linux quorum | 17.615 s | 164 / 26,168 |
| Windows Native follower | 95.938 s | 22,043 / 24 |
| Windows Native leader | 60.718 s | 22,865 / 21 |
| Windows Native quorum | 84.844 s | 142 / 26,346 |

Leader replacement is observed in **4.642 s** on Linux and **4.359 s** on Native
Windows during the hour-long leader absence. Unknown outcomes are not confirmed
losses; the minority cannot acknowledge a new write before quorum restoration.
The original client journals also pass **66** post-hoc counter observations
against the highest confirmed increment response, possible unknown increments,
unique confirmed results, per-node monotonicity and final convergence. This
read-only audit strengthens the original oracle without rewriting a run's result.
All eight original ZIPs, reports, parameters and hashes are retained in
[six-role-endurance-571.json](reliability/2026-11-08/six-role-endurance-571.json);
the audit is in [final-571-counter-audit.json](reliability/2026-11-08/final-571-counter-audit.json).

## An ordered producer remained attached to an unavailable old leader

Native `b00224c5` passed the complete SDK campaign, but its subsequent owner-timeout
run failed. The measured suspension lasted **124.561 seconds**, because publication
took **99.603 seconds** before HTTP 202 and the scenario then waited 20 seconds.
This does not reproduce the intended short owner absence: finite delivery retries
can already be exhausted during the longer interval. The original failure and
receipt absence remain retained.

The runtime log explicitly records the configured **100-second HttpClient timeout**
in `SlimDataService.SendCommandBatchAsync`, then retries the same ordered batch.
The local client kept its old leader HTTP request alive after consensus changed;
every subsequent mutation in that producer waited behind it. A reduced real Local
`leader-route` case reproduces the blockage with a small Set through a follower:
the old leader is suspended, another leader is available, but the single mutation
expires at the unchanged 15-second deadline. No mutation is retried by the harness.

The first token-only implementation (`ac30f4dd`) passed its unit regressions but
still failed the real Local case at 15 seconds. The trace shows the ingress
follower observing the replacement leader without its captured consensus token
being cancelled. That failed run remains retained.

Outgoing batch requests now link to both the local Raft consensus token and a
leader generation captured before leader selection. The `LeaderChanged` event
retires the latter independently. Losing either epoch cancels the stale HTTP request, including a
stalled response body. The existing consumer retries identical batch identity and
content, preserving deduplication if the prior leader committed before its reply
was interrupted. HTTP deadlines are unchanged. Two regressions with an infinite
test-client timeout fail before the fix in Global and PartitionedByKey modes.
The token-only version passed **11** focused recovery tests. The complete correction
(`4e80c9a1`) passes **13** focused recovery tests and **1,996** total .NET tests.
Real Local now completes the same leader-route case in **6.156 seconds**, then
passes API checks and persistent restarts. Its separate owner-timeout regression
accepts publication in **2.735 seconds** and delivers after the unchanged
eight-second timeout and 20-second owner absence. Final Native functional controls
also pass; performance remains a separate gate below. Exact parameters and failed attempts
are retained in [leader-event evidence](reliability/2026-11-08/leader-event-evidence.json).

## Sequential counter observations with outstanding unknown increments

Native Windows quorum endurance on `b00224c5` stopped after its 300-second absence
with sequential counter values `[2, 3, 3]`, one confirmed increment and 173 unknown
increments. The original run remains failed. Its complete archive has SHA-256
`e95687b272dc8b88c5a83dc220f17550a260b5f97f69b10c1fb0950f32e65c1f`.
A fresh archive copy, using that archive's original Native executable and runtime
assets, cold-started with **25 consecutive `[3, 3, 3]` observations** and verified
all confirmed data. No API mutation was submitted by this diagnostic. An initial
setup attempt omitted the executable's required appsettings file and is excluded;
its separate logs and directory remain retained.

An unknown HTTP outcome does not cancel a queued increment. Such an increment can
commit between sequential node reads, so comparing all three as one atomic
observation was invalid. The archived result is consistent with this overlap;
the replay does not reconstruct its exact original read/commit interleaving.
The campaign now records every counter observation and requires convergence
within the **existing shared recovery deadline** when unknown increments remain.
Every value must stay within the confirmed/possible bounds, remain at least the
highest confirmed increment response, and must not regress
on a node. Duplicate confirmed increment results, out-of-range values, server
errors and divergence without unknown writes still fail immediately. Persistent
divergence still fails the original deadline. No mutation is retried. The new
overlap regression fails before this harness correction; all **21** accounting
and safety tests pass afterwards. A new real endurance matrix is required;
archive replay does not turn the old failed run into a passing campaign.
Original results, replay observations and raw-file hashes are in
[counter archive evidence](reliability/2026-11-08/counter-archive-replay.json).

## Final Native controls and original journal-volume recovery

Native `a95d49e8` passes the full API/1–5–10-second fault campaign with **348
accepted / 13 unknown / zero missing receipts**, the complete official SDK with
**82 / five / zero**, and the separate owner-timeout regression. The latter
accepts its 2 MiB publication in **2.563 seconds**, then observes delivery after
the existing eight-second timeout and 20-second owner absence. The real
leader-route regression completes in **5.922 seconds**. The official tutorial
bundle passes with an empty PATH: three nodes, dashboard, four functions, Sync,
file and job. Five Windows process-ownership/capacity tests and four exporter
safety tests also pass.

The four initial 2 MiB Native Stream appends take **1.516 / 15.484 / 23.375 /
24.828 seconds**. These remain functional results inside the declared 120-second
SDK budget, not acceptable-performance proof or a controlled improvement claim.

The original failed job-journal ENOSPC image is resumed in place with the final
portable runtime code, preserving its pre-repair archive and old reports. Initial
confirmed-data verification succeeds. Its first subsequent API exercise fails
because the harness reused a counter previously left at `32.5`; the API correctly
rejects an integer increment with HTTP 409. The harness now uses a distinct key
for each counter exercise and restores each completed exercise's confirmed float
checkpoint from the client ledger. The old key and value remain in the verification
set. Both regressions fail before their fixes; all **22** harness tests pass after.
The next resume passes the API exercise and full persistent restart on that same
original image: **291 accepted / 130 unknown / zero missing receipts**. One job
has two execution receipts, an allowed and measured replay of the acknowledged
unfinished plan. Runtime and harness failures remain separate recorded outcomes.

A separate focused replay uses a fresh verified copy of the old failed Linux CI
file image. The original file, previously unavailable through its 300-second
deadline but found intact offline on node 2, is fetched from all three APIs with
its 1,024-byte SHA-256 verified in **8.679 seconds**. Confirmed immutable state
also passes. The diagnostic submits GETs only; normal runtime recovery writes the
copied state. It does not reconcile historical job receipts or turn the original
failed campaign into a pass. Details, pre/post image hashes and original parameters
are in [Linux replay and traffic evidence](reliability/2026-11-08/linux-retained-replay-and-traffic.json).

## Performance evidence

The existing Local HTTP/scaling driver and MemoryLab are reused on the same
Windows host, with frozen benchmark scripts and no concurrent compilation or
other workload campaign. The final Native candidate is `a95d49e8`; every runtime,
manifest, driver and orchestrator is attributed by content hash. Measurements
last ten seconds after two-second warmups. These are short screening comparisons,
not statistical proof or maximum production capacity.

The complete six-size HTTP/scaling **ABBA** has two baseline failures and two
complete candidate successes. Both `496e8739` baseline attempts miss the unchanged
**180-second Async queue-drain deadline at 1 MiB/concurrency four**, without an
injected fault; logs show leader unavailability. Both corrected runs finish every
size/encoding and scaling, draining the 200-message burst in **27.148 s / 25.765 s**.
The second candidate still records a **14.233-second** Async request at
1 MiB/concurrency one. With no complete fresh full-profile baseline, full-matrix
performance non-regression cannot be certified. The partial baseline logs and
volumes remain retained in [current performance evidence](reliability/2026-11-08/performance-current.md).

A separate matched **2 MB / 2 MiB ABBA** completes all four runs using the same
driver and configuration on both versions. It preserves the failed full workload
as separate evidence. Raw median Sync throughput changes are **−11.7%** at
2 MiB/concurrency one and **−25.1%** at concurrency four. The existing normalized
comparer fails its Sync guardrails for both candidates against baseline 01 and
passes them against baseline 04. At 2 MiB/concurrency four, the normalized rate
changes are **−28.59% / −31.93%** against the first baseline and **+8.63% / +3.54%**
against the second. All four comparer invocations separately miss the configured
**40% optimization target**; missing that improvement target is not itself a
non-regression failure. The observed variability and tail-latency gates remain
unresolved, with no unsupported causal explanation. See the absolute p50/p95/p99,
throughput, CPU/message, RSS, delivery/duplicate counts, scaling and raw comparer
reports in [the targeted comparison](reliability/2026-11-08/performance-2mb.md).

Fresh MemoryLab **ABBA** passes all **16 measured loads and 16 warmups**, with
all-node state validation and actual Async element-ID/checksum reconciliation.
Median throughput changes are **−3.052% / −4.987%** for Set at concurrency 12/48,
and **+1.201% / +3.057%** for mixed operations. The configuration forces snapshots
every 50 entries or 8 MiB. Per-phase CPU, RSS, allocations, generation-labelled GC
counters, pauses and WAL/snapshot gauges are retained in
[performance-current.json](reliability/2026-11-08/performance-current.json).

An additional real Linux Local **ABBA** observes TCP payload on the three Raft
listeners. Every run publishes **24** checksum-verified Async bodies totaling
**38,670,848 application bytes**. The first capture attempt loses 315 packets in
one baseline and 113 in one candidate; those byte measurements are excluded,
while their functional successes remain recorded. With a larger observer socket
buffer, all four new workloads and all four **zero-drop** gates pass. They observe
**88.81–93.21 MB of TCP payload**, at **3.849–4.551 MiB/s** over complete runs.
This includes internal HTTP framing, control traffic, snapshots and retransmits
on loopback, rather than physical-wire bandwidth or maximum steady-state
replication capacity. Per-second counters, original failed captures, exact
parameters and hashes are in [replication traffic](reliability/2026-11-08/replication-traffic.md).

Sampled resource gauges can miss phase boundaries; missing/reset counter deltas
remain unmeasured. Logical WAL sizes, WAL-since-snapshot and last-snapshot gauges
are not allocated physical disk growth or network byte rate. Historical five-second
comparisons on fixes 1–10, their guardrail failures, the excluded post-driver Bash
editing error and the earlier contaminated process-tree run remain separately
recorded in [performance.md](reliability/2026-11-08/performance.md). Their outcomes
are not relabelled by later successful campaigns.

## Validation and release gates

- Latest runtime code (`4e80c9a1`, unchanged in `a95d49e8`): **1,996 .NET tests
  passed** (SlimData 304, SlimFaas 1,536, MCP 106, Kafka 11, .NET client 39), including
  13 focused recovery cases. Current harness accounting/safety: **22 passed**.
- Full .NET suite after fix 10: **1,974 passed** (SlimData 295, SlimFaas 1,523,
  MCP 106, Kafka 11, .NET client 39). The final style-only adjustment also passed
  all nine selected recovery/read-barrier tests.
- Python SDK: **37 passed**, including actual WebSocket frame-limit cases.
- Evidence exporter path/overwrite safety: **4 passed**; final Windows process
  ownership/capacity checks: **5 passed**. Earlier harness totals are historical.
- Final Native AOT publication and the empty-PATH three-node tutorial bundle pass,
  including dashboard, four real functions, Sync, file and an actual job. The
  final official SDK and API/short-outage campaigns also pass. Existing aggregate third-party trim/AOT
  warnings from MemoryPack.Core/System.Configuration.ConfigurationManager remain
  visible; this is not a zero-warning publish claim.
- Latest dedicated workflow: both quick OS jobs and all six endurance roles pass;
  **66** original counter observations pass the stronger read-only ledger audit.
- DotNext 6.11.1 → 6.12.0 and patched central System.* pins are documented in the
  dependency evidence. Direct/transitive distributions were inspected; FOSSA
  License Compliance passes on `a95d49e8`. No lockfile or persisted-format change
  or new project-wide warning suppression was introduced.
- Final documentation build: **23 pages, 1,657 local links/assets and 394 search
  entries** checked. A separate check resolves the technical reports' relative
  links. [Final validation evidence](reliability/2026-11-08/validation-final.json)
  records log hashes and the runtime source-equivalence check.

Earlier native builds, Sonar, FOSSA and DCO successes are revision-specific;
current PR checks remain the authority for merge readiness. Quick Linux/Windows
and manually dispatched Linux/Native Windows endurance workflows upload evidence even
when the workload fails. The PR remains draft while release gates are open.

The demonstrated guarantees are limited to the recorded successful histories:
confirmed immutable values and delivered bodies matched their checksums, nodes
converged after those faults, counters met their confirmed/unknown bounds,
minority writes were not acknowledged without quorum, and configured delivery
replays remained visible. A real earlier replay skip failed an independent
consensus gate even when business checksums converged.

File metadata commits through Raft; file bytes transfer separately. Metadata
acceptance is not proof of a durable byte copy on a quorum of disks. Mixed-version
rolling upgrades and permanent volume loss remain untested. Formal consensus
verification, exhaustive crash windows and arbitrary device failures are outside
the demonstrated evidence.

Earlier campaigns were also interrupted by an unplanned host disk-full event
from generated AOT diagnostic graphs. Original failures and volumes were kept;
verified orphan processes had to be stopped to release locks. Those combined-fault
resumptions are not claimed as autonomous recovery. Subsequent publications
disabled graph generation; no runtime volume was deleted or compressed to force
a passing result.

The bounded Linux/Native Windows outage matrix, repeated journal/disk-full recovery
and final Native functional controls now pass. Certification still requires a
repeatable acceptable performance comparison and the outstanding durability/fault
coverage above. Neither historical data loss nor unverified power-loss or
permanent-volume-loss guarantees are repaired by relabelling passing tests.
