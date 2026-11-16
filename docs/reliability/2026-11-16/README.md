# Final-source long endurance: all six roles completed

All six final-source long roles pass the recovery/integrity harness and
independent journal reconciliation: **92,332 confirmed operations, 35,186
separately retained unknown outcomes, zero confirmed loss or receipt
corruption**. One additional successful delivery belongs to an unknown client
outcome during an injected kind leader suspension and remains explicitly
counted. Every role completes 30/60/300/900/1800/3600-second absences, successive
crashes and a complete original-volume restart. These results do not establish
formal performance acceptance or LTS certification.

General validation also retains a
[Windows coverage bootstrap failure and its test-only RED/GREEN correction](bootstrap-seed-fixture.md):
the corrected fixture passes 2,425 full-solution tests and both membership cases
under coverage. Product source189 and all deadlines stay unchanged. The managed
micro driver then passes build/catalog validation after preserving an initial
`NETSDK1151` publication-configuration failure. The
[four managed microbenchmark runs](micro-abba-189.md) now complete with 296 valid
case executions, preserving gains and seven consistently slower cases above
110% of reference time. Formal HTTP/MemoryLab comparisons continue serially;
their numerical verdicts remain open.

| Environment / compilation | Closed roles | Confirmed | Unknown | Extra successful fault-time deliveries |
|---|---:|---:|---:|---:|
| macOS ARM64 Native AOT | 3 | 46,386 | 26,167 | 0 |
| kind Linux ARM64 managed/trimmed | 3 | 45,946 | 9,019 | 1, on an unknown client outcome |

Six additional deliberately truncated upload intents are separate negative
controls, checked absent after write barriers. All **127,524 client intents**
remain accounted for. Complete original-tree indices cover **55,524 files**
across the six closed roles, including retained state/WAL/snapshots. The earlier
failed kind startup control retains its own additional 76-file index. The three
kind resource exports retain 2,124 runtime cgroup observations across 26
observed container lifetimes and 18 pod UIDs; short unobserved lifetimes and
uncaptured final counter values remain outside that observation.
The [structured rollup](endurance-summary-189.json) independently reconstructs
all six client journals and verifies disjoint confirmed/unknown sets and these
totals against the published raw archives.

The **macOS ARM64 Native AOT follower role passes the recovery and integrity
harness** on source `189826231fe43fe3069e8a1f7724f83589432c6f`. All six absence
durations, successive crashes and the complete restart on original volumes
finish successfully. Independent journal reconciliation confirms **23,170
accepted operations, zero confirmed missing async/stream/job operations, zero
receipt hash/length failures and zero successful repetitions**. Twenty-six
unknown outcomes remain separate.

The [Native leader role](native-leader-189.md) also passes recovery/integrity,
with 23,070 confirmed operations and 34 separately retained unknown outcomes.
The [Native quorum role](native-quorum-189.md) also passes, with 146 confirmed
operations and 26,107 separately retained unknown outcomes. All three Native
roles are closed: **46,386 confirmed operations, 26,167 unknown outcomes**, zero
confirmed missing operations, receipt corruption or successful repetitions.
The first kind startup control fails before its long absences; its
[budget correction](protocol-startup-budget.md) preserves that failure and the
original data. The corrected [managed kind follower role](kind-follower-189.md)
now also passes all six absences, successive crashes and the original-volume
restart: **22,831 confirmed operations, 48 unknown outcomes**, zero confirmed
missing operations, receipt corruption or successful repetitions. Its complete
9,562-file original tree is indexed after shutdown. The
[managed kind leader role](kind-leader-189.md) also passes recovery/integrity:
22,885 confirmed operations, 31 unknown outcomes and zero confirmed loss or
receipt corruption. One additional successful delivery belongs to an unknown
client outcome during an injected suspension and remains explicitly counted.
The [managed kind quorum role](kind-quorum-189.md) also passes with 230 confirmed
operations, 8,940 unknown outcomes and no confirmed loss, receipt corruption or
successful repetitions. Its complete 396-file original tree is indexed after
shutdown. The three closed kind roles total **45,946 confirmed operations and
9,019 unknown outcomes**. General validation follows serially.
The [timestamped Kubernetes log audit](kind-runtime-log-audit-189.md) finds no
observed error headers outside recorded initialization/fault/recovery windows;
complete retired/rotated history remains INCONCLUSIVE. Six retained request
exceptions are interrupted Raft bodies during injected suspensions, with their
stacks and timestamps preserved. The final performance verdict remains open.
No merge or LTS certification is
claimed. Earlier results remain attached to their actual source versions.

## Absence and recovery

The runtime is the frozen source189 Native AOT publication, SHA-256
`6f1288a118a48f3eecd8c9e27e40a94b89a2c4645186f19696a59abc78121a17`.
The shared local storage uses the unchanged default copying policy0 and strict
1 MiB offload threshold. This is separate from the opt-in two-copy managed/kind
[storage fault controls](../2026-11-15/persisted-copy-validation-189.md).

Eight independent producers continuously exercise async, sync, Streams, Files,
Hashsets, Sets, counters and real jobs, with a two-second interval per producer.
Async bodies rotate through the six historical sizes up to 4 MiB. The request
deadline is 15 seconds; recovery/confirmed drain remains bounded at 180 seconds.
The independent silent-follower protocol probe retains its 15-second ready/write
guard after the listener opens.

| Requested follower absence | Actual absence | Ready after resume | Full recovery/reconciliation | Confirmed cumulative | Unknown cumulative |
|---|---:|---:|---:|---:|---:|
| 30 s | 30.005 s | 0.225 s | 0.494 s | 206 | 2 |
| 60 s | 60.001 s | 0.231 s | 0.652 s | 395 | 5 |
| 300 s | 300.000 s | 0.171 s | 1.540 s | 1,426 | 7 |
| 900 s | 900.001 s | 0.763 s | 4.774 s | 4,538 | 9 |
| 1,800 s | 1,800.001 s | 0.682 s | 11.208 s | 10,739 | 12 |
| 3,600 s | 3,600.005 s | 0.423 s | 25.930 s | 23,137 | 15 |

The final crash/restart phase brings the totals to 23,170 confirmed and 26
unknown operations. All stored-value/counter checks and confirmed async/stream/
job reconciliation pass after reopening the original volumes. No accepted work
is resubmitted to obtain this result. The full role takes approximately 114.2
minutes, including preliminary controls, absence time, recovery and shutdown.

## Per-ID accounting and log limits

Independent export reconstructs 23,170 accepted IDs and 26 unknown IDs from
the client journal. Of the unknown outcomes, thirteen stored mutations are
absent at the final all-node observation and thirteen remain unresolved. Neither
class is reclassified as confirmed acceptance. There is also one deliberately
truncated upload recorded as intent-only; the original harness checks its ID
returns 404 on every node after a successful write barrier. Thus all **23,197
client intents** remain accounted for, including this separate negative control.

The receipts contain 8,511 successful handler attempts, five deliberate failed
retry/DLQ attempts and 3,258 job receipts. There are no repeated successful
ID/function pairs or repeated job executions. Receipt hashes match both the
fixture's expected checksum and the corresponding accepted client body; lengths
also match. Receipt filenames include a writer UUID in addition to the PID, so
process restarts and PID reuse do not overwrite earlier writers.

Both supervisor logs are retained. A whole-log scan finds zero managed
`OutOfMemoryException` or unhandled-exception strings and zero critical tags.
It also retains **40 error-level tags** across startup and deliberate failures.
These tags are not a count of distinct exceptions. Their lines lack sufficient
per-line timestamps to establish a complete healthy-window classification from
this export alone: that audit remains **INCONCLUSIVE**, rather than a claim of
zero healthy runtime errors. Delivery reconciliation does not resolve this log
coverage limitation. The Kubernetes collector retains timestamped pod UID/restart
generations separately; its completeness requires its own review.

## Evidence and reproducible command

[Structured evidence](native-follower-189.json) includes the original report and
parameters, independent counts, supervisor-log scan and hashes for **3,307 closed
files**. The [raw archive](native-follower-189-raw.tar.gz) contains all top-level
reports, logs, metrics/configuration and **3,272 per-ID journals**, including the
client journal. Its SHA-256 is
`347a424b3e49cf1e4f93d7b55c78e3d3892551f66c02b11c506c09fadae61e1d`.
Original state, WALs and snapshots remain on disk; full-tree hashing is deferred
to the consolidated quiescent export. This archive does not contain the WAL tree.

The later [consolidated original-state index](native-original-state-and-failed-kind-index-189.json.gz)
now completes full-tree hashing after all Native writers stop. It covers 36,037
Native original files and 76 files in the stopped failed kind-startup root,
including state/WAL/snapshots. The earlier export's pending-hashing statement
remains an accurate record of its own capture time.

Build/reuse the recorded native publication with the
[benchmark setup](../../benchmarking.md), then use a fresh output directory and
unused ports. Preserve the failed output if any check fails.

```bash
SlimData__Files__RequiredDurableCopies=0 \
SlimFaas__AsyncBodyOffloadThresholdBytes=1048576 \
python3 .bin/test-slimdata-local-chaos.py \
  --runtime artifacts/pr506/builds/candidate-osx-net11-70/SlimFaas \
  --source-commit 189826231fe43fe3069e8a1f7724f83589432c6f \
  --output "artifacts/pr506/macos/follower-repro-$(date +%s)" \
  --port-base 51020 --request-timeout 15 --recovery-timeout 180 \
  --traffic-interval 2 --roles follower \
  --durations 30,60,300,900,1800,3600 \
  --scenarios protocol-timeout,payloads,api,files,retries,job-during-node-stop,chaos,restarts
```

The queued general campaign keeps macOS Native AOT and Linux ARM64 managed/trimmed
results separate and uses the same frozen drivers for both enclosing references
and candidates. Existing HTTP/MemoryLab guards and the single complete repeat
rule remain unchanged. The
[closed WAL/threshold/policy diagnostics](../2026-11-15/wal-threshold-policy-controls-189.md)
retain the unhealthy inline row and policy-cost failures; this first long role
does not erase those observations or establish final non-regression acceptance.
