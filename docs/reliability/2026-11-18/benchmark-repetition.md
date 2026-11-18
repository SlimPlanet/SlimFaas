# Independent repeat of the bounded macOS benchmark campaign

Updated 2026-11-18 03:39 CET. Campaign state: **in progress**.

Product candidate: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`. The .NET 11 reference is `f36084643a75d803051a6918d2950d1a4f674ada`.
Both products use macOS ARM64 Native AOT. Driver source: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`, SDK 11.0.100.

The [previous campaign](supervisor-repair-benchmarks.md) measured the same frozen binaries. Every profile in this repeat starts with fresh state.
The disk reserve is 20 GiB, with a 1 GiB margin for stopping active workloads. Timed work is scheduled to stop by 08:45 CET.

This rerun uses fresh state and the repaired supervisor. Omitted arguments run the manifest command without additions. A job whose preparation fails follows its retry policy while other jobs continue.
The WebSocket async runner checks refreshed queue totals on all nodes after receipt reconciliation. File scenarios have an explicit byte budget.
The bounded HTTP comparison uses 5-second load windows, 1-second warmups, two repetitions, concurrency 1/16, and five payload sizes from 4 KiB through 4 MiB. It supplies a shorter ABBA comparison. Quiescent offload files are transparently compressed between roles, with their bytes and SHA-256 preserved.

| Profile | Evidence status | Rows | Observed | Failed | Incomplete |
|---|---|---:|---:|---:|---:|
| jobs-rerun | OBSERVED | 4 | 4 | 0 | 0 |

An observation means that the measured run supplied the expected integrity and resource evidence. Comparative acceptance comes from the ABBA verdicts.

## Receiver reconciliation

| Profile | Requests | Expected receipts | Completions | Missing | Duplicates | Corrupt |
|---|---:|---:|---:|---:|---:|---:|
| jobs-rerun | 800 | 800 | 800 | 0 | 0 | 0 |

## Comparisons

ABBA results are pending.

## Candidate observations

Throughput and CPU are medians across the listed repetitions. RAM is the highest sampled cluster working set among those repetitions during the load window. For Streams and Jobs, accepted throughput covers submission and completed throughput covers submission and drain. The table CPU and RAM values cover submission. Drain resources remain in the raw reports.

| Profile | Scenario | Bytes | C | Runs | Median rate | Unit | Completed/s incl. drain | CPU ms/op | Peak MiB |
|---|---|---:|---:|---:|---:|---|---:|---:|---:|
| jobs-rerun | jobs/http/fanout-1 | n/a | 1 | 2 | 140.98 | accepted job/s | 13.15 | 5.29 | 338.91 |
| jobs-rerun | jobs/http/fanout-1 | n/a | 16 | 2 | 1403.81 | accepted job/s | 13.16 | 1.00 | 341.44 |

Full CSV, JSON, Markdown, resource samples and raw evidence remain in the local campaign directory. The final report includes completed comparisons and identifies profiles that did not finish.

## Queue and routing microbenchmarks

Status: **queued**.

## Run status

- **jobs-rerun**: OBSERVED.
