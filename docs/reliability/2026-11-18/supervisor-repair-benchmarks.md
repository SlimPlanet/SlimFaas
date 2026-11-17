# Overnight macOS rerun after the supervisor repair

Updated 2026-11-17 20:12 CET. Campaign state: **in progress**.

Native supervisor smoke: **PASS**. An HTTP job without arguments succeeded, a malformed job failed individually, and the next job succeeded. Results agreed on all three nodes and the supervisor shut down cleanly.

Product candidate: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`. The .NET 11 reference is `f36084643a75d803051a6918d2950d1a4f674ada`.
Both products use macOS ARM64 Native AOT. Driver source: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`, SDK 11.0.100.
Preflight passed 2,504 .NET tests, 58 Python controller tests and 11 documentation tests.

The prior campaign and its unrepaired runtime results remain in the [initial campaign report](overnight-benchmarks.md).
This rerun uses fresh state and the repaired supervisor. Omitted arguments run the manifest command without additions. A job whose preparation fails follows its retry policy while other jobs continue.
The WebSocket async runner checks refreshed queue totals on all nodes after receipt reconciliation. File scenarios have an explicit byte budget within the 30 GiB disk reserve.

| Profile | Evidence status | Rows | Observed | Failed | Incomplete |
|---|---|---:|---:|---:|---:|
| jobs-rerun | OBSERVED | 4 | 4 | 0 | 0 |

An observation means that the measured run supplied the expected integrity and resource evidence. Comparative acceptance comes from the ABBA verdicts.

## Comparisons

ABBA results are pending.

## Candidate observations

Throughput and CPU are medians across the listed repetitions. RAM is the highest sampled cluster working set among those repetitions during the load window. For Streams and Jobs, accepted throughput covers submission and completed throughput covers submission and drain. The table CPU and RAM values cover submission. Drain resources remain in the raw reports.

| Profile | Scenario | Bytes | C | Runs | Median rate | Unit | Completed/s incl. drain | CPU ms/op | Peak MiB |
|---|---|---:|---:|---:|---:|---|---:|---:|---:|
| jobs-rerun | jobs/http/fanout-1 | n/a | 1 | 2 | 131.27 | accepted job/s | 13.03 | 4.96 | 348.52 |
| jobs-rerun | jobs/http/fanout-1 | n/a | 16 | 2 | 1246.31 | accepted job/s | 13.09 | 0.96 | 352.75 |

Full CSV, JSON, Markdown, resource samples and raw evidence remain in the local campaign directory. The final report includes completed comparisons and identifies profiles that did not finish.

## Run status

- **jobs-rerun**: OBSERVED.
