# Targeted 2 MB / 2 MiB Native comparison

This separate ABBA uses the existing Local driver unchanged, with the same settings for baseline `496e8739` and candidate `a95d49e8`: concurrency 1/4, two-second warmups, ten-second measurements and the original 200-message scaling burst. All four runs complete. The two full-profile baseline 180-second queue-drain failures remain in [performance-current.md](performance-current.md). This targeted experiment does not replace those failed workloads.

## Throughput and latency

| Mode | Bytes | C | Base req/s | Candidate req/s | Change | Base p50/p95/p99 ms | Candidate p50/p95/p99 ms |
|---|---:|---:|---:|---:|---:|---:|---:|
| sync | 2,000,000 | 1 | 68.00 | 71.20 | +4.7% | 13.15/25.17/37.83 | 13.05/19.29/33.87 |
| sync | 2,000,000 | 4 | 175.91 | 174.16 | -1.0% | 22.08/29.21/36.10 | 22.48/29.25/34.44 |
| sync | 2,097,152 | 1 | 62.03 | 54.78 | -11.7% | 15.31/22.91/35.26 | 17.20/24.36/34.43 |
| sync | 2,097,152 | 4 | 133.62 | 100.09 | -25.1% | 30.89/42.74/64.49 | 38.38/54.66/66.42 |
| async | 2,000,000 | 1 | 11.86 | 12.06 | +1.6% | 78.48/106.06/168.72 | 78.44/108.97/136.88 |
| async | 2,000,000 | 4 | 24.64 | 25.80 | +4.7% | 159.83/228.23/261.09 | 150.44/214.88/275.97 |
| async | 2,097,152 | 1 | 10.73 | 9.00 | -16.1% | 91.35/116.05/144.43 | 108.43/134.12/164.72 |
| async | 2,097,152 | 4 | 17.78 | 17.30 | -2.7% | 223.02/309.97/353.53 | 228.08/336.72/395.75 |

## Existing normalized comparison

The existing comparer evaluates each candidate against both bracketing baselines. Its optimization target of 40% large-payload p50 reduction is separate from its 10% Sync non-regression guardrails. All candidates have no request failures. Both candidates fail Sync guardrails against baseline 01 and pass them against baseline 04. This observed variability does not certify non-regression.

| Baseline | Candidate | Sync guardrails | 2 MiB/C4 normalized rate change | Added p95 / p99 change |
|---|---|---|---:|---:|
| 01-baseline | 02-candidate | False | -28.59% | +362.4% / +683.9% |
| 01-baseline | 03-candidate | False | -31.93% | +327.5% / +837.5% |
| 04-baseline | 02-candidate | True | +8.63% | -4.6% / -49.4% |
| 04-baseline | 03-candidate | True | +3.54% | -11.8% / -39.5% |

Added latency subtracts the direct target percentile. A small baseline difference can produce a large percentage; inspect the absolute raw fields before interpreting these tail ratios. The complete reports, actual samples, CPU/message, RSS, Raft-entry observations, failure/duplicate counts and hashes are in [performance-2mb.json](performance-2mb.json). The cause of the variability is not established and the remaining performance gate is explicit.

## Scaling

| Run | First ready ms | Four ready ms | Queue drained ms |
|---|---:|---:|---:|
| 01-baseline | 5374.5 | 7549.8 | 24784.3 |
| 02-candidate | 3298.3 | 6521.5 | 22713.2 |
| 03-candidate | 5331.9 | 7525.3 | 23739.0 |
| 04-baseline | 4257.9 | 10747.9 | 24422.9 |
