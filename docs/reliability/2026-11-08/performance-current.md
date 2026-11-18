# Final runtime performance screening — 9 November 2026

Read [the reliability verdict](../../slimdata-reliability-results.md). Baseline `496e8739` and Native candidate `a95d49e8` use the existing Local driver in original ABBA order. Measurements last ten seconds after two-second warmups; six exact body sizes and concurrency 1/4 are followed by a 200-message scaling burst. Compilation and other workload campaigns were stopped.

Complete-run tables contain **0 baseline and 2 candidate runs**. Every failed original attempt remains below and in [the raw evidence index](performance-current.json). No shorter deadline, mutation retry or replacement run turns a failure into a pass. Percentile columns are medians of per-run percentiles, not pooled percentiles.

## Original HTTP/scaling attempts

| Run | Exit | Complete report | Queue drained ms |
|---|---:|---|---:|
| 01-baseline | 1 | False | not reached |
| 02-candidate | 0 | True | 27147.7 |
| 03-candidate | 0 | True | 25765.4 |
| 04-baseline | 1 | False | not reached |

A partial run without `results.json` is not a complete performance baseline. Its logs, manifest, runtime attribution and metric stream remain retained. Inspect the original exception before attributing a failure to a particular defect.

## Sync response

| Bytes | C | Base req/s | Candidate req/s | Change | Base p50/p95/p99 ms | Candidate p50/p95/p99 ms |
|---:|---:|---:|---:|---:|---:|---:|
| 65,536 | 1 | — | 692.59 | — | —/—/— | 1.39/1.73/2.01 |
| 65,536 | 4 | — | 2104.06 | — | —/—/— | 1.77/2.60/3.45 |
| 262,144 | 1 | — | 159.67 | — | —/—/— | 3.63/51.34/97.72 |
| 262,144 | 4 | — | 809.32 | — | —/—/— | 4.53/7.54/9.90 |
| 1,048,576 | 1 | — | 81.70 | — | —/—/— | 10.24/23.52/51.71 |
| 1,048,576 | 4 | — | 252.88 | — | —/—/— | 16.51/22.67/27.34 |
| 2,000,000 | 1 | — | 66.33 | — | —/—/— | 13.55/24.53/42.93 |
| 2,000,000 | 4 | — | 150.98 | — | —/—/— | 25.25/37.74/55.27 |
| 2,097,152 | 1 | — | 52.57 | — | —/—/— | 18.17/24.82/37.50 |
| 2,097,152 | 4 | — | 93.44 | — | —/—/— | 40.89/57.29/86.56 |
| 4,194,304 | 1 | — | 26.92 | — | —/—/— | 35.46/50.67/83.04 |
| 4,194,304 | 4 | — | 79.42 | — | —/—/— | 48.11/69.07/87.33 |

## Async HTTP acceptance

| Bytes | C | Base req/s | Candidate req/s | Change | Base p50/p95/p99 ms | Candidate p50/p95/p99 ms |
|---:|---:|---:|---:|---:|---:|---:|
| 65,536 | 1 | — | 14.01 | — | —/—/— | 73.33/84.46/103.43 |
| 65,536 | 4 | — | 21.18 | — | —/—/— | 208.12/289.31/347.65 |
| 262,144 | 1 | — | 5.09 | — | —/—/— | 188.31/245.97/319.46 |
| 262,144 | 4 | — | 4.60 | — | —/—/— | 830.28/1299.65/1409.30 |
| 1,048,576 | 1 | — | 0.09 | — | —/—/— | 11352.43/14909.48/15225.66 |
| 1,048,576 | 4 | — | 1.39 | — | —/—/— | 2722.76/3861.34/3987.16 |
| 2,000,000 | 1 | — | 5.70 | — | —/—/— | 215.50/504.43/563.53 |
| 2,000,000 | 4 | — | 16.47 | — | —/—/— | 239.16/371.06/475.95 |
| 2,097,152 | 1 | — | 9.55 | — | —/—/— | 102.62/129.79/136.46 |
| 2,097,152 | 4 | — | 17.34 | — | —/—/— | 227.79/310.19/374.19 |
| 4,194,304 | 1 | — | 9.19 | — | —/—/— | 106.44/134.09/156.57 |
| 4,194,304 | 4 | — | 17.27 | — | —/—/— | 245.26/326.94/362.09 |

## Async delivery at concurrency four

Arrival and completion percentiles use the existing real target observations. Missing/duplicate counts are totals across complete runs; percentile columns are medians of per-run values.

| Bytes | Variant | Arrival p95 ms | Completion p95/p99 ms | Observed | Missing | Duplicates |
|---:|---|---:|---:|---:|---:|---:|
| 65,536 | baseline | — | —/— | — | — | — |
| 65,536 | candidate | 854.46 | 854.74/1017.96 | 428 | 0 | 0 |
| 262,144 | baseline | — | —/— | — | — | — |
| 262,144 | candidate | 3475.91 | 3477.61/3681.29 | 99 | 0 | 0 |
| 1,048,576 | baseline | — | —/— | — | — | — |
| 1,048,576 | candidate | 9635.69 | 9642.89/9711.26 | 35 | 0 | 0 |
| 2,000,000 | baseline | — | —/— | — | — | — |
| 2,000,000 | candidate | 1020.74 | 1063.25/1149.78 | 334 | 0 | 0 |
| 2,097,152 | baseline | — | —/— | — | — | — |
| 2,097,152 | candidate | 823.60 | 852.67/956.58 | 354 | 0 | 0 |
| 4,194,304 | baseline | — | —/— | — | — | — |
| 4,194,304 | candidate | 825.89 | 895.18/956.40 | 350 | 0 | 0 |

## MemoryLab validation and throughput

All 16 measured loads and 16 warmups completed with zero failures. Every phase validates the stored values and reconciles distinct actual Async delivery IDs and 4 KiB checksums. The same three-node Local configuration forces snapshots every 50 entries or 8 MiB.

| Scenario | C | Base ops/s | Candidate ops/s | Change | Base p50/p95/p99 ms | Candidate p50/p95/p99 ms |
|---|---:|---:|---:|---:|---:|---:|
| slimdata-set | 12 | 69.29 | 67.17 | -3.1% | 170.81/324.48/412.41 | 178.43/335.16/497.42 |
| slimdata-set | 48 | 160.28 | 152.28 | -5.0% | 267.11/591.65/1295.16 | 250.91/752.28/1486.41 |
| slimdata-mixed | 12 | 130.07 | 131.64 | +1.2% | 57.60/239.75/309.77 | 59.02/225.97/319.48 |
| slimdata-mixed | 48 | 293.39 | 302.36 | +3.1% | 123.37/374.89/789.68 | 119.87/473.68/671.45 |

## Sampled whole-run Native resources

Three runtime processes combined. CPU/allocation/GC are sampled counter deltas, with missing or reset counters left unmeasured. Whole-run values include startup and scaling and cannot be normalized as equal-work cost when a run fails or completes a different workload.

| Run | Peak combined RSS MiB | CPU seconds | Allocated MiB | Sum of generation-labelled GC counters | GC pause seconds | Max node WAL-since-snapshot MiB | Max node last snapshot MiB |
|---|---:|---:|---:|---:|---:|---:|---:|
| 01-baseline | 1100.55 | 655.48 | 5635.29 | 321 | 1.61 | 31.59 | 15.01 |
| 02-candidate | 681.88 | 823.86 | 6223.14 | 360 | 2.21 | 28.67 | 1.00 |
| 03-candidate | 840.82 | 772.08 | 6020.28 | 345 | 1.90 | 31.44 | 1.00 |
| 04-baseline | 672.77 | 417.02 | 4741.31 | 263 | 1.41 | 31.89 | 5.00 |

Per-phase MemoryLab resource samples, all raw field values, complete latency/scaling reports and content hashes are in `performance-current.json`. Short sampled windows can miss boundary work; an absent measurement is not zero cost. Original historical five-second comparisons remain in [performance.md](performance.md), with their failures and different runtime revisions.
