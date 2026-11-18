# Final-source Native quorum-loss long endurance

All three final-source macOS ARM64 Native AOT roles pass the recovery/integrity
harness. The quorum-loss role closes with **146 confirmed operations, 26,107
separately retained unknown outcomes, zero confirmed missing operations, receipt
hash/length failures or successful repetitions**. Successive crashes and the
complete original-volume restart also pass. Together, the three Native roles
account for **46,386 confirmed operations and 26,167 unknown outcomes**.

The frozen source `189826231fe43fe3069e8a1f7724f83589432c6f`, runtime hash,
unchanged copying/offload defaults and mixed producers match the
[follower](README.md) and [leader](native-leader-189.md) roles. Two follower
processes are suspended to remove the majority. The direct minority-write probe
must not acknowledge a successful write before quorum restoration. Ambiguous
HTTP failures stay unknown; they are not presented as definitive rejections.

| Quorum-loss absence | Full recovery after resume | Confirmed cumulative | Unknown cumulative | Verdict |
|---|---:|---:|---:|---|
| 30 s | 1.412 s | 114 | 64 | PASS |
| 60 s | 3.437 s | 115 | 253 | PASS |
| 300 s | 3.494 s | 115 | 1,380 | PASS |
| 900 s | 3.396 s | 115 | 4,877 | PASS |
| 1,800 s | 6.628 s | 116 | 11,925 | PASS |
| 3,600 s | 14.441 s | 117 | 26,092 | PASS |

The small confirmed-count changes occur at the traffic/suspension boundaries;
the direct minority probe separately checks response timing against restoration.
The final crash/restart phase brings the totals to 146 confirmed and 26,107
unknown operations. Recovery/confirmed-drain remains bounded at 180 seconds,
client requests at 15 seconds. The whole role takes approximately 112.95 minutes.

Independent export reconstructs the accepted/unknown maps, verifies successful
async/stream receipts and confirmed job arguments, and matches all accepted
handler SHA-256/length observations. It counts 37 successful handler attempts,
five deliberate failed retry/DLQ attempts and ten job receipts, including one
job with unknown client acceptance. There are no successful repeated
ID/function pairs or repeated confirmed job executions.

| Unknown client outcome after recovery | Count |
|---|---:|
| Stored mutation committed on every node | 13 |
| Handler executed | 4 |
| Job executed | 1 |
| Stored mutation absent at final all-node observation | 9,866 |
| Unresolved | 16,223 |

Known later execution/commit does not retroactively turn an unknown response
into confirmed client acceptance. One additional deliberately truncated upload
is intent-only and verified absent after a write barrier. All **26,254 client
intents** remain accounted for, including that separate negative control.

Engine error records have UTC timestamps through existing console configuration;
early namespace warnings can bypass the configured logger. Both complete
supervisor logs remain available, including **5,443 error-level tags** under
startup and deliberate quorum failures. A whole-log scan finds zero managed
`OutOfMemoryException` or unhandled-exception strings and zero critical tags.
Tag counts are not counts of distinct exceptions, and healthy-window triage
remains a separate audit. The closed follower/leader logs retain their original
timestamp limitation. Passing reconciliation alone does not establish a clean
healthy-runtime window or LTS acceptance.

[Structured evidence](native-quorum-189.json) retains the original report,
parameters, independent reconciliation and hashes for **50 closed files**.
The [raw archive](native-quorum-189-raw.tar.gz), SHA-256
`adaba6eebe77617e5fb7fa48044bae8e020e46901bc03ad1fa2b27ff30a7c911`,
includes all top-level reports/logs/metrics/configuration and **15 per-ID
journals**. Receipt filenames keep distinct writer UUIDs across process restarts.

After all Native writers and the failed kind-startup control stop, the
[consolidated original-state index](native-original-state-and-failed-kind-index-189.json.gz)
hashes **36,037 Native files**, including all original state, WALs, snapshots and
journals, plus **76 files** in the failed kind root. Its SHA-256 is
`314e22fdc25b7f53b7f363b2de28b7d07a61e9e2a2253bb798cb6a7086b31b2c`.
The earlier compact exports correctly record full-tree hashing as pending at
their own capture time; this later index completes that step without modifying
the originals.

Reproduce using the [follower command](README.md), choosing `--roles quorum`,
`--port-base 51220` and a fresh output directory. To obtain the observed engine
log format, additionally set
`Logging__Console__FormatterOptions__TimestampFormat=O` and
`Logging__Console__FormatterOptions__UseUtcTimestamp=true`.

The first kind control fails before any long absence because startup and
recovery bounds were incorrectly coupled; its
[preserved failure and tested harness correction](protocol-startup-budget.md)
remain separate observations. Fresh corrected kind roles and general validation
follow serially with the unchanged runtime/image. Final formal non-regression,
remaining fault coverage and tracked guarantees stay open. No merge or LTS
certification is claimed.
