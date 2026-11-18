# Final-source Native leader long endurance

The macOS ARM64 Native AOT leader role passes the recovery/integrity harness on
source `189826231fe43fe3069e8a1f7724f83589432c6f`: **23,070 confirmed operations,
34 separately retained unknown outcomes, zero confirmed missing operations,
receipt hash/length failures or successful repetitions**. Successive crashes and
the complete original-volume restart also pass. Native quorum is running; the
three kind roles and general comparison/SDK/demo/fault campaigns remain queued.

This uses the same frozen runtime and unchanged copying/offload defaults as the
[closed follower role](README.md), with a distinct manifest, output root and
ports 51120–51180. Eight independent mixed-traffic producers continue throughout
each absence. Requests remain bounded at 15 seconds and recovery at 180 seconds.
The whole role takes approximately 114.2 minutes.

| Leader absence | New leader observed | Full recovery after resume | Verdict |
|---|---:|---:|---|
| 30 s | 4.390 s | 1.357 s | PASS |
| 60 s | 4.169 s | 0.648 s | PASS |
| 300 s | 4.588 s | 1.343 s | PASS |
| 900 s | 4.170 s | 4.426 s | PASS |
| 1,800 s | 4.566 s | 10.455 s | PASS |
| 3,600 s | 3.779 s | 24.663 s | PASS |

New-leader observations are elapsed times from suspension. They are separate
from the endpoint-routing test and from complete data convergence. Actual
absences and all individual phase results remain in the original report.

Independent export reconstructs the confirmed/unknown maps from the client
journal and rechecks successful async/stream receipts and real-job arguments.
All observed accepted handler bodies match the accepted client SHA-256 and
length. There are 8,482 successful handler attempts, five deliberate failed
retry/DLQ attempts and 3,237 job receipts, with no repeated successful ID/function
pairs or repeated job executions. Ten unknown stored mutations are absent at the
final all-node observation; twenty-four remain unresolved. One additional
deliberately truncated upload is intent-only and verified absent after a write
barrier. All 23,105 client intents remain accounted for without converting
unknowns or the negative control into confirmed acceptance.

The complete supervisor logs retain **161 error-level tags** during startup and
deliberate failures. A full scan finds zero managed `OutOfMemoryException` or
unhandled-exception strings and zero critical tags. The old console format lacks
the per-line timestamps required for complete healthy-window classification;
that audit remains **INCONCLUSIVE**. A passing delivery ledger does not remove
this limitation or establish zero healthy internal errors.

The next Native quorum role receives existing console formatter settings through
the task wrapper: `Logging__Console__FormatterOptions__TimestampFormat=O` and
`Logging__Console__FormatterOptions__UseUtcTimestamp=true`. Live observations
confirm UTC timestamps on the engine's error records. Early namespace warnings
can bypass that configured logger. Verbosity, runtime bytes, source,
deadlines and durability/offload settings remain unchanged. These settings do
not retrofit the closed follower/leader logs. Future host-side comparisons apply
the same formatter settings to both variants; Kubernetes already retains outer
kubelet timestamps.

[Structured evidence](native-leader-189.json) preserves the original report,
parameters, independent reconciliation and hashes for **3,287 closed files**.
The [raw archive](native-leader-189-raw.tar.gz), SHA-256
`ea9574200f2a8e47d37de3662bc7feba4f29321be828569957de461857ccfa79`,
contains all top-level reports/logs/metrics/configuration and **3,252 per-ID
journals**. Original WALs/snapshots/state remain retained, with full-tree hashing
deferred to the consolidated quiescent export.

Reproduce using the [follower command](README.md), replacing `--roles follower`
with `--roles leader`, choosing `--port-base 51120` and a new output directory.
The same exact native runtime hash is required. Preserve any failed output.
The two closed Native roles jointly account for **46,240 confirmed operations
and 60 unknown outcomes**. Final three-environment non-regression, complete log
audits and the remaining tracked guarantees remain open; no merge or LTS
certification is claimed.
