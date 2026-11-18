# PR #506 merge assessment and benchmark handoff

Snapshot: 17 November 2026, 08:03 UTC. Live execution state is authoritative.
This document records the decision evidence and the work that can continue in
a separate validation PR. It does not request or perform a merge.

## Current merge assessment

**Do not merge yet.** GitHub reports no merge conflict, but PR #506 is a draft
and the external Sonar quality gate fails. Main CI, dashboard/documentation,
local reliability and FOSSA pass on head
`929cf76542e1ee189dda17ae4b5767f6747fe9d9`. The Sonar check requires A for
new-code reliability/security and reports C/D. Scanner job success and targeted
benchmark exclusions do not clear that separate gate.

- [Main CI](https://github.com/SlimPlanet/SlimFaas/actions/runs/44035975109)
- [Dashboard/documentation](https://github.com/SlimPlanet/SlimFaas/actions/runs/44035975067)
- [Reliability workflow](https://github.com/SlimPlanet/SlimFaas/actions/runs/44035975171)
- [Sonar check and failed conditions](https://github.com/SlimPlanet/SlimFaas/runs/133883086232)
- [FOSSA check for this head](https://app.fossa.com/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas/refs/branch/main/929cf76542e1ee189dda17ae4b5767f6747fe9d9)

The current product source is
`f2f80124af9b88d0ad4bbd94653372cf393ad1b5`; head `929cf765` changes the Python log
collector. Source `f2f80124` passes **2,477 .NET tests, zero failures/skips**, official
Python SDK 37, Native AOT publications and the documented build controls.
Head `929cf765` passes platform 50, routing 12, chaos 32 and Sonar-boundary 7 local tests,
without skips. See the [publication/source report](../2026-11-16/ownership-publications-f2.md).

The historical seven healthy duplicate successes, incomplete reference Files
recovery and workload-time Raft/routing errors have not all been explained and
cleared on the final source. Complete source `f2f80124` HTTP/MemoryLab non-regression is
also pending. These are actual limits, not a request to weaken any guardrail.
Moving extended tests to another PR cannot turn these open observations into
passing acceptance. A later merge review must explicitly revisit correctness,
Sonar findings, source-specific validation and the remaining performance risk.

## Is reliability improved?

There is stronger evidence for the reproduced failures. The dedicated #467
change rotates already-established function connections; fresh destination
resolution, StatefulSet UID/revision cache keys and terminating-pod filtering
address different stale-address mechanisms. The
[async reservation correction](../2026-11-16/async-reservation-coalescing.md)
has failing-before/passing-after regressions for a second exchange started
while the same ID still has a local owner. It preserves the synchronous single
attempt and does not add another retry layer.

The fresh source `f2f80124` Kubernetes async replacement control
`routing/own-async-c16-a02` reconciles **2,881 accepted IDs and 2,881 successful
SHA/length receipts**, with zero confirmed missing/corrupt/duplicate successes.
Every ID has a complete admission/producer/Raft/dequeue/transport/ack chain:
2,859 use try 0; 22 have a network-unknown try followed by try 1. Every ID has one
real HTTP200 and one successful execution receipt. The compressed per-ID ledger
SHA-256 is
`132fd17c880e8bf087228112be43ca54ceb2c3bad233888a0da77ac2aad82f1c`.
The terminating-pod C16 control separately reconciles 2,694/2,694 calls, first
replacement success at 3.899 s and no old-pod receipt after 15 s.

Those bounded controls do not establish exactly-once behavior, prove the
production incident's cause or attribute the seven historical healthy
duplicates. The [rotation correction](../2026-11-16/runtime-log-history.md)
also prevents missing earlier log prefixes from being mistaken for complete
history. Delivery reconciliation, complete traces and a healthy-window audit
are separate verdicts; startup/injected-fault errors in a broad log window must
not be silently reclassified as a healthy runtime pass.

## CPU and RAM assessment

**A global CPU/RAM improvement is not established.** The isolated #467
[Service comparison](../2026-11-16/kubernetes-service-pool-lifetime.md) measures
approximately 9.83–9.90 ms CPU/call for references and 9.83–10.05 ms for candidates;
sampled aggregate RSS is 485.2–500.3 MiB and 488.9–501.9 MiB respectively. This is
similar observed cost while established connections converge. It is paced C1,
Linux ARM64 managed/trimmed and older isolated sources `18982623`/`29daa097`, includes background
work, and cannot validate current-source capacity or resource acceptance.

Historical application comparisons retain failures: macOS mixed C48 RSS rises
17.23–18.72% against the two references, beyond its 15% budget; Podman mixed C48
throughput falls 16.52–20.29% and p99 rises 22.65–34.39%. The historical
[microbenchmark ABBA](../2026-11-16/micro-abba-189.md) shows routing/allocation
gains alongside seven consistently slower cases. Lower allocations alone are
not proof of less CPU or RSS. See the
[full historical performance summary](../2026-11-13/performance-summary-net11-03.json)
for exact sources and scopes. These results neither disappear after fixes nor
become final-source `f2f80124` measurements.

## Six current long roles remain running

The user explicitly chose **to let all six roles finish**, rather than stop
after the current follower. The detached controller runs Native AOT macOS
follower/leader/quorum, then managed/trimmed kind follower/leader/quorum,
serially. Each retains 30/60/300/900/1800/3600-second absences, continuous mixed
traffic, successive crashes and full original-volume restart. At the snapshot,
Native follower 30/60/300/900 have passed; 1800 is running; 3600 is pending.
There is no current-source long-endurance final verdict yet.

Current defaults stay copies 0 and strict 1 MiB offload. Recovery/drain 180 s and
leader routing 15 s are unchanged. Kubernetes protocol startup 300 s and Job wall
time 28800 s are separate controls. The historical source `18982623` six-role result
(92,332 confirmed, 35,186 unknown, one extra successful fault-time delivery on an
unknown outcome) remains attached to `18982623` and cannot validate the queue change.

## Resume safely

The locally installed Codex skill is `$slimfaas-benchmarks`, under
`~/.codex/skills/slimfaas-benchmarks/SKILL.md`. It uses the existing repository
harnesses and links back here. Its own frontmatter/reference validation is
separate from executing the product campaigns.

On this Mac, use the isolated worktree; inspect the state before invoking any
writer. These are read-only recovery commands:

```bash
cd /Users/a115vc/Desktop/github/SlimFaas-pr506
git status --short --branch
cat artifacts/pr506/preflight/ownership-long-endurance-01.json
tail -n 8 artifacts/pr506/logs/ownership-long-01-native-follower.log
cat artifacts/pr506/preflight/ownership-build-reservation-20261117.json
tail -n 2 artifacts/pr506/preflight/net11-04-disk-samples.jsonl
```

The long script is `artifacts/pr506/run-ownership-long-endurance-01.py` and its
controller log is `artifacts/pr506/logs/ownership-long-endurance-01-controller.log`.
Do not rerun it from the beginning: it owns existing state and refuses reuse.
Verify saved PID identity against the actual command/start time. The exclusive
wrapper lock and `artifacts/platform-measurement.json` fence concurrent work.
No compilation, cleanup, new campaign or product/harness edit may overlap these
roles. Documentation-only changes leave the frozen measurements attributable.

Kubernetes default context is production. Every campaign API command must use
the separate kubeconfig/context, for example:

```bash
kubectl --kubeconfig artifacts/pr506/kubernetes/kubeconfig \
  --context kind-slimfaas-pr506 get nodes
```

Monitor both Mac and VM free space. Below 20 GiB hold new phases; restore at
least 30 GiB on both with stable samples before clearing the hold. Preserve
sessions, sources, original volumes/WAL/snapshots, journals and evidence. Clear
only verified disposable caches/artifacts in the user's authorized scope.
The reservation guard has a 48-hour lease ending 19 November 05:53 UTC; inspect it
before any subsequent phase. Old stopped controllers are not resumable work.

## Follow-up PR scope

1. Close and independently export/index the six current roles after owned
   writers stop; reconcile per-ID confirmed/unknown outcomes and successes,
   complete every log-generation/healthy-window audit, and publish per-role
   timings, resources, limits and source/image/binary hashes. Retain failures.
2. Explain historical healthy duplicate successes with complete timelines;
   recover the old failed reference Files states only on verified copies;
   diagnose healthy Raft and `Not port or IP available` errors. Retain a RED
   reproduction and regression for each established product cause.
3. Complete final-source serial HTTP/async/scaling and MemoryLab ABBA on macOS,
   Podman and kind using common drivers and matching compilation modes. Keep
   both enclosing-reference verdicts and at most one complete contradictory
   block repeat. Incomplete references/mandatory metrics remain INCONCLUSIVE.
4. Continue strict 512/256/128 KiB threshold screens and measured policy 0/1/2
   costs separately; include below/at/above boundary cases, 192/384/512 KiB bodies
   and 1 MiB duplicate reproductions at C1/C16. Do not change the default merely
   because a bounded screen passes.
5. Replay affected real SDK/jobs/demo, Linux process/network/isolated-disk
   controls, ordinary probes, HTTP versions and long streaming requests. Profile
   measured hot paths before accepting more ZLinq changes. New runtime fixes
   need new source-specific validation, not reused historical passes.

See [the benchmark guide](../../benchmarking.md) for reproducible commands and
the implemented non-regression criteria. Keep remaining LTS guarantees open in
#505. Neither this handoff nor an eventual completed soak is LTS certification.
