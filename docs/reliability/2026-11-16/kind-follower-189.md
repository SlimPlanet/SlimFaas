# Final-source managed kind follower long endurance

The dedicated kind follower role passes the recovery/integrity harness with
**22,831 confirmed operations, 48 separately retained unknown outcomes, zero
confirmed missing operations, receipt hash/length failures or successful
repetitions**. All six absences, three successive process crashes and the
complete original-volume restart pass. The driver takes approximately 120.29
minutes, including preliminary controls and reconciliation.

This is Linux musl ARM64 **self-contained managed/trimmed** execution, separate
from the [Native AOT macOS results](README.md). Production source remains
`189826231fe43fe3069e8a1f7724f83589432c6f`. Image
`localhost/slimfaas-pr506:after-tools-net11-71` has configuration digest
`0203a61d16dbba284fb9ad4d6321cd40adc0000a4b460b519b958f3670e389f0`;
the SlimFaas assembly SHA-256 is
`c218344c8e72f4ca1ac6371bc510fa59c2d34b71003e260fd98d29de89bd1127`
and the SlimData assembly SHA-256 is
`69b8499b9f79ad5d13cb3a585efabd77800f98500145caa9e6149233943780c6`.
The generic managed apphost hash in the harness parameters is not a substitute
for assembly/image identity.

Three runtime replicas occupy three dedicated kind workers. The generator,
functions and real Jobs execute inside the cluster using Services and pod DNS.
The copying policy remains zero and the offload threshold remains strict 1 MiB.
Endurance probes permit deliberate process suspension. Normal-probe validation,
opt-in two-copy storage-loss controls and formal performance comparisons retain
their separate scopes. The workers share one Podman VM filesystem; these results
do not establish independent-storage hardware guarantees.

| Follower absence | Ready after resume | Full recovery/reconciliation | Confirmed cumulative | Unknown cumulative |
|---|---:|---:|---:|---:|
| 30 s | 0.091 s | 0.591 s | 160 | 8 |
| 60 s | 0.247 s | 0.980 s | 314 | 15 |
| 300 s | 0.088 s | 2.689 s | 1,300 | 21 |
| 900 s | 0.169 s | 7.010 s | 4,360 | 27 |
| 1,800 s | 0.659 s | 16.308 s | 10,498 | 34 |
| 3,600 s | 0.417 s | 54.679 s | 22,787 | 40 |

Recovery/confirmed drain remains bounded at 180 seconds, requests at 15 seconds.
The [separate protocol startup bound](protocol-startup-budget.md) is 300 seconds;
the actual restart-to-listener observation is 202.094 seconds, followed by
0.464 seconds for majority readiness/write under the unchanged 15-second guard.
The earlier failed startup control remains preserved. The long role does not
claim that the underlying approximately 200-second startup limitation is fixed.

Independent export reconstructs the confirmed/unknown maps from the client
journal, checks confirmed async/stream receipts and job arguments, and verifies
handler SHA-256 and lengths. It finds 8,422 successful handler attempts, five
deliberate failed retry/DLQ attempts, 3,188 job receipts, zero successful repeated
ID/function pairs and zero repeated confirmed jobs. The harness also reports
zero repeated job executions. Legacy repeated-delivery counters retain the
deliberate failed attempts; they are not silently dropped.

| Unknown client outcome after recovery | Count |
|---|---:|
| Handler executed | 11 |
| Stored mutation committed on every node | 3 |
| Stored mutation absent at final all-node observation | 13 |
| Unresolved | 21 |

Later execution or commit does not turn an unknown response into confirmed
client acceptance. One additional deliberately truncated upload remains
intent-only and is checked absent after a write barrier by the harness. All
**22,880 client intents** are accounted for, including that negative control.

The [structured evidence and complete original-tree index](kind-follower-189.json.gz)
has SHA-256 `a4b6e218677dc7c58946d1f2b09c5ebbc5485c2898216581b8b6930990a1ab4d`.
It covers **9,562 original files**, including retained state, WALs, snapshots and
journals, hashed after campaign writers stop. The [raw archive](kind-follower-189-raw.tar.gz)
has SHA-256 `eb61c60c73d419536c31d74f8ff623e8226b0f5ae03ce712ddb329e514f3ab4a`:
184 files containing the campaign reports, configuration, captured logs/metrics
and two receipt journals plus the retained repair sentinel. Every archive member is verified against the
original index after copying to the Mac. The original WAL/state remains in the
VM; the archive does not duplicate that tree.

The separate [runtime resource summary](kind-follower-runtime-resources-189.json)
and [717 raw cgroup observations](kind-follower-runtime-resources-189.jsonl.gz)
cover nine observed container lifetimes and six pod UIDs. The maximum observed
kernel `memory.peak` is 691.36 MiB under a 1 GiB limit; observed per-lifetime
`max`, `oom`, `oom_kill` and `sock_throttled` counters remain zero. These are
cgroup measurements, including file/kernel memory, rather than process RSS.
Short unobserved lifetimes and each counter's uncaptured final value remain
outside this observation. Functions and Jobs have separate resource scopes.

Runtime captures retain pod UID/generation identity; a supplementary observer
also saves previous-container logs separately. Complete lifetime log coverage
and healthy-window error classification still require their own audit. Driver
PASS alone does not establish either. The historical unhealthy inline row,
policy-cost failures and [other open limits](../2026-11-15/wal-threshold-policy-controls-189.md)
remain recorded. Leader/quorum endurances, formal non-regression and remaining
general controls are still pending. No merge or LTS certification is claimed.

## Reproduce

Use the dedicated cluster and recorded image from the
[benchmark setup](../../benchmarking.md), with a fresh namespace/output. The
launcher explicitly selects context `kind-slimfaas-pr506`; keep the separate
campaign kubeconfig. Collect and stop only this campaign even after a failed
wait, retain its original exit status, and inspect the original report.

```bash
campaign_ns="slimfaas-pr506-follower-repro-$(date +%s)"
campaign_options=(
  --kubeconfig "$PWD/artifacts/pr506/kubernetes/kubeconfig"
  --namespace "$campaign_ns"
  --output "$PWD/artifacts/pr506/kubernetes/$campaign_ns"
)
python3 .bin/run-kubernetes-campaign.py launch "${campaign_options[@]}" \
  --image localhost/slimfaas-pr506:after-tools-net11-71 \
  --controller-image localhost/slimfaas-pr506:after-tools-net11-71 \
  --source-commit 189826231fe43fe3069e8a1f7724f83589432c6f \
  --probes endurance --suite chaos --roles follower \
  --durations 30,60,300,900,1800,3600 --traffic-interval 2 \
  --recovery-timeout 180 --protocol-startup-timeout 300 \
  --scenarios protocol-timeout,payloads,api,files,retries,job-during-node-stop,chaos,restarts
campaign_exit=0
python3 .bin/run-kubernetes-campaign.py wait "${campaign_options[@]}" \
  --timeout 28800 || campaign_exit=$?
python3 .bin/run-kubernetes-campaign.py collect "${campaign_options[@]}"
python3 .bin/run-kubernetes-campaign.py stop "${campaign_options[@]}"
test "$campaign_exit" -eq 0
```

## CI observation

At documentation head `095dc16e`, the
[main CI](https://github.com/SlimPlanet/SlimFaas/actions/runs/43834299384),
[reliability workflow](https://github.com/SlimPlanet/SlimFaas/actions/runs/43834299529),
[dashboard/documentation workflow](https://github.com/SlimPlanet/SlimFaas/actions/runs/43834299649)
and FOSSA License Compliance pass. The external
[SonarCloud quality gate](https://sonarcloud.io/dashboard?id=SlimPlanet_SlimFaas&pullRequest=506)
fails with 5.7% new-code coverage against 80%, security rating E and reliability
rating D. The scanner job's success does not waive that gate.
