# Overnight macOS rerun after the supervisor repair

Updated 2026-11-18 02:32 CET. Campaign state: **in progress**.

Native supervisor smoke: **PASS**. An HTTP job without arguments succeeded, a malformed job failed individually, and the next job succeeded. Results agreed on all three nodes and the supervisor shut down cleanly.

Product candidate: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`. The .NET 11 reference is `f36084643a75d803051a6918d2950d1a4f674ada`.
Both products use macOS ARM64 Native AOT. Driver source: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`, SDK 11.0.100.
Preflight passed 2,504 .NET tests, 58 Python controller tests and 11 documentation tests.

The operator set the minimum free disk reserve to 20 GiB for subsequent blocks. The initial phases used the original 30 GiB cutoff. Historical run statuses retain the cutoff that applied at the time.

The prior campaign and its unrepaired runtime results remain in the [initial campaign report](overnight-benchmarks.md).
This rerun uses fresh state and the repaired supervisor. Omitted arguments run the manifest command without additions. A job whose preparation fails follows its retry policy while other jobs continue.
The WebSocket async runner checks refreshed queue totals on all nodes after receipt reconciliation. File scenarios have an explicit byte budget.
The original HTTP reference stopped at the disk reserve while testing large bodies. The bounded HTTP rerun uses 5-second load windows, 1-second warmups, two repetitions, concurrency 1/16, and five payload sizes from 4 KiB through 4 MiB. It supplies a shorter ABBA comparison. Quiescent offload files are transparently compressed between roles, with their bytes and SHA-256 preserved.

| Profile | Evidence status | Rows | Observed | Failed | Incomplete |
|---|---|---:|---:|---:|---:|
| data-rerun | OBSERVED | 36 | 36 | 0 | 0 |
| files-bounded-files | OBSERVED | 12 | 12 | 0 | 0 |
| files-bounded-files-download | OBSERVED | 12 | 12 | 0 | 0 |
| files-bounded-files-unique | OBSERVED | 12 | 12 | 0 | 0 |
| files-bounded-files-upload | OBSERVED | 12 | 12 | 0 | 0 |
| files-retry | INCONCLUSIVE | 2 | 2 | 0 | 0 |
| http-bounded-abba | FAIL | 448 | 448 | 0 | 0 |
| http-bounded-abba-20 | FAIL | 512 | 512 | 0 | 0 |
| http-reference-comparison | INCONCLUSIVE | 0 | 0 | 0 | 0 |
| jobs-rerun | OBSERVED | 4 | 4 | 0 | 0 |
| memory-abba-replay | FAIL | 20 | 20 | 0 | 0 |
| memory-reference-comparison | INCONCLUSIVE | 0 | 0 | 0 | 0 |
| streams-rerun | OBSERVED | 8 | 8 | 0 | 0 |
| threshold-512kib-bounded | FAIL | 128 | 128 | 0 | 0 |
| threshold-512kib-retry | FAIL | 256 | 256 | 0 | 0 |
| wal-64kib-retry | FAIL | 208 | 208 | 0 | 0 |
| websocket-rerun | OBSERVED | 48 | 48 | 0 | 0 |

An observation means that the measured run supplied the expected integrity and resource evidence. Comparative acceptance comes from the ABBA verdicts.

## Receiver reconciliation

| Profile | Requests | Expected receipts | Completions | Missing | Duplicates | Corrupt |
|---|---:|---:|---:|---:|---:|---:|
| jobs-rerun | 800 | 800 | 800 | 0 | 0 | 0 |
| streams-rerun | 1600 | 2400 | 2400 | 0 | 0 | 0 |
| websocket-rerun | 9600 | 12800 | 12800 | 0 | 0 | 0 |

## Comparisons

- **http-bounded-abba-20 / http**: FAIL. 2 ABBA block(s), pair verdicts FAIL, FAIL, FAIL, FAIL, FAIL, FAIL, FAIL, FAIL.
  - Async p99/throughput/delivery guardrail failed for 1048577 bytes at concurrency 16 (7 pair comparisons).
  - Async paced workload guardrail failed at concurrency 1 (6 pair comparisons).
  - Async p99/throughput/delivery guardrail failed for 4194304 bytes at concurrency 1 (4 pair comparisons).
- **memory-abba-replay / set-c12**: FAIL. 1 ABBA block(s), pair verdicts FAIL, PASS, FAIL, PASS.
  - throughput (2 pair comparisons).
- **memory-abba-replay / set-c48**: PASS. 1 ABBA block(s), pair verdicts PASS, PASS, PASS, PASS.
- **memory-abba-replay / mixed-c12**: FAIL. 2 ABBA block(s), pair verdicts FAIL, FAIL, PASS, PASS, PASS, PASS, PASS, PASS.
  - throughput (2 pair comparisons).
- **memory-abba-replay / mixed-c48**: PASS. 1 ABBA block(s), pair verdicts PASS, PASS, PASS, PASS.
- **threshold-512kib-retry / http**: FAIL. 2 ABBA block(s), pair verdicts FAIL, FAIL, FAIL, FAIL, FAIL, FAIL, FAIL, FAIL.
  - Sync p95/p99/throughput guardrail failed for 1048576 bytes at concurrency 1 (7 pair comparisons).
  - Async burst workload guardrail failed at concurrency 64 (6 pair comparisons).
  - Sync p95/p99/throughput guardrail failed for 524288 bytes at concurrency 1 (5 pair comparisons).
- **wal-64kib-retry / http**: FAIL. 2 ABBA block(s), pair verdicts FAIL, FAIL, FAIL, FAIL, FAIL, FAIL, FAIL, FAIL.
  - Async paced workload guardrail failed at concurrency 1 (8 pair comparisons).
  - Sync p95/p99/throughput guardrail failed for 1048576 bytes at concurrency 1 (7 pair comparisons).
  - Async burst workload guardrail failed at concurrency 64 (6 pair comparisons).

A and B refer to the first and second variants, respectively. For code comparisons, A is the reference and B is the candidate. For settings comparisons, A uses the current settings and B uses the measured change. Each table includes matched cases with evidence across all four roles of the attempt. The displayed throughput, p95 and CPU are medians across repetitions. RAM is the highest sampled cluster working set. The verdicts above use the comparison criteria for the corresponding profile.

<details><summary>http-bounded-abba / http-attempt-1 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| async | 4096 | 1 | accepted message/s | 102.92 → 97.18 | 83.88 → 76.00 | 11.77 → 12.68 | 7.66 → 10.29 | 397.17 → 411.09 |
| async | 4096 | 16 | accepted message/s | 1107.69 → 1087.77 | 883.77 → 852.91 | 20.40 → 20.57 | 1.78 → 1.79 | 498.17 → 491.61 |
| async | 262144 | 1 | accepted message/s | 38.86 → 37.23 | 29.86 → 29.22 | 39.80 → 41.87 | 50.62 → 51.02 | 687.84 → 630.67 |
| async | 262144 | 16 | accepted message/s | 51.81 → 51.84 | 42.20 → 42.01 | 503.72 → 476.33 | 37.25 → 40.05 | 1655.31 → 1009.00 |
| async | 1048576 | 1 | accepted message/s | 11.89 → 12.55 | 9.34 → 9.95 | 127.43 → 124.80 | 158.44 → 165.24 | 1563.48 → 927.17 |
| async | 1048576 | 16 | accepted message/s | 13.04 → 13.96 | 10.86 → 11.25 | 1701.24 → 1593.42 | 131.48 → 130.90 | 3262.16 → 2029.48 |
| async | 1048577 | 1 | accepted message/s | 40.99 → 44.96 | 35.73 → 35.88 | 28.78 → 26.43 | 45.45 → 46.16 | 3084.91 → 1896.52 |
| async | 1048577 | 16 | accepted message/s | 256.55 → 279.69 | 219.13 → 225.17 | 76.79 → 78.18 | 18.10 → 19.81 | 2818.17 → 1205.67 |
| async | 4194304 | 1 | accepted message/s | 36.23 → 35.58 | 30.85 → 28.37 | 31.34 → 31.28 | 91.51 → 98.46 | 2161.72 → 801.11 |
| async | 4194304 | 16 | accepted message/s | 92.35 → 82.44 | 68.97 → 65.53 | 231.38 → 334.33 | 61.55 → 65.90 | 1905.91 → 652.75 |
| async-burst | 4096 | 64 | accepted message/s | 2126.47 → 2104.64 | 861.32 → 537.36 | 46.90 → 49.29 | 1.33 → 1.48 | 1503.27 → 628.97 |
| async-paced | 4096 | 1 | accepted message/s | 9.24 → 8.91 | 8.33 → 7.91 | 13.34 → 17.57 | 38.80 → 48.99 | 1488.00 → 603.66 |
| direct | 4096 | 1 | request/s | 9729.34 → 9972.11 | n/a → n/a | 0.12 → 0.11 | 0.01 → 0.01 | 377.22 → 391.61 |
| direct | 4096 | 16 | request/s | 23444.60 → 22865.84 | n/a → n/a | 0.74 → 0.75 | 0.00 → 0.00 | 482.38 → 491.52 |
| direct | 262144 | 1 | request/s | 2784.36 → 2790.47 | n/a → n/a | 0.43 → 0.43 | 0.03 → 0.03 | 622.67 → 601.62 |
| direct | 262144 | 16 | request/s | 5964.13 → 5976.21 | n/a → n/a | 2.83 → 2.84 | 0.01 → 0.01 | 1406.28 → 874.72 |
| direct | 1048576 | 1 | request/s | 677.26 → 597.43 | n/a → n/a | 2.05 → 2.13 | 0.12 → 0.13 | 1655.31 → 916.30 |
| direct | 1048576 | 16 | request/s | 1811.17 → 1836.27 | n/a → n/a | 9.39 → 9.34 | 0.05 → 0.05 | 2781.22 → 1799.19 |
| direct | 1048577 | 1 | request/s | 627.28 → 582.87 | n/a → n/a | 2.09 → 2.09 | 0.12 → 0.13 | 3264.44 → 2031.27 |
| direct | 1048577 | 16 | request/s | 1820.51 → 1872.48 | n/a → n/a | 9.33 → 9.23 | 0.05 → 0.05 | 2717.34 → 1384.72 |
| direct | 4194304 | 1 | request/s | 240.17 → 227.37 | n/a → n/a | 6.66 → 7.05 | 0.36 → 0.37 | 2162.66 → 815.12 |
| direct | 4194304 | 16 | request/s | 463.33 → 460.89 | n/a → n/a | 37.33 → 37.73 | 0.29 → 0.20 | 2039.30 → 686.59 |
| sync | 4096 | 1 | request/s | 3015.76 → 3001.76 | n/a → n/a | 0.36 → 0.37 | 0.52 → 0.55 | 395.28 → 401.34 |
| sync | 4096 | 16 | request/s | 7808.82 → 7754.39 | n/a → n/a | 2.36 → 2.39 | 0.47 → 0.47 | 500.05 → 505.91 |
| sync | 262144 | 1 | request/s | 1170.83 → 1190.55 | n/a → n/a | 0.96 → 0.94 | 1.67 → 1.63 | 620.98 → 599.97 |
| sync | 262144 | 16 | request/s | 1594.86 → 1602.27 | n/a → n/a | 10.51 → 10.48 | 1.89 → 1.89 | 1406.20 → 955.22 |
| sync | 1048576 | 1 | request/s | 383.68 → 383.21 | n/a → n/a | 2.81 → 2.81 | 5.32 → 5.42 | 1655.81 → 949.64 |
| sync | 1048576 | 16 | request/s | 447.95 → 447.98 | n/a → n/a | 37.06 → 37.17 | 6.74 → 6.84 | 2779.78 → 1859.94 |
| sync | 1048577 | 1 | request/s | 381.42 → 381.24 | n/a → n/a | 2.82 → 2.83 | 5.33 → 5.37 | 3266.22 → 2032.05 |
| sync | 1048577 | 16 | request/s | 448.65 → 448.20 | n/a → n/a | 37.25 → 36.82 | 6.63 → 6.73 | 2719.64 → 1384.92 |
| sync | 4194304 | 1 | request/s | 104.34 → 104.94 | n/a → n/a | 10.35 → 10.27 | 19.60 → 19.64 | 2162.67 → 824.83 |
| sync | 4194304 | 16 | request/s | 111.79 → 111.56 | n/a → n/a | 150.67 → 151.76 | 25.48 → 25.85 | 2046.56 → 686.67 |

</details>

<details><summary>http-bounded-abba-20 / http-attempt-1 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| async | 4096 | 1 | accepted message/s | 91.57 → 95.29 | 71.81 → 76.04 | 14.03 → 12.91 | 10.96 → 9.79 | 402.08 → 412.62 |
| async | 4096 | 16 | accepted message/s | 1114.22 → 1113.58 | 873.34 → 875.75 | 20.21 → 19.92 | 1.83 → 1.74 | 485.55 → 512.97 |
| async | 262144 | 1 | accepted message/s | 38.53 → 38.97 | 30.09 → 30.01 | 39.54 → 39.27 | 51.41 → 50.98 | 667.38 → 634.20 |
| async | 262144 | 16 | accepted message/s | 53.80 → 53.70 | 43.64 → 45.20 | 480.49 → 476.76 | 36.70 → 37.80 | 1656.45 → 1005.73 |
| async | 1048576 | 1 | accepted message/s | 12.60 → 12.63 | 9.70 → 9.71 | 124.68 → 123.86 | 156.22 → 161.80 | 1577.48 → 846.45 |
| async | 1048576 | 16 | accepted message/s | 13.85 → 13.51 | 11.25 → 11.72 | 1642.81 → 1697.23 | 129.53 → 126.11 | 3321.31 → 2021.80 |
| async | 1048577 | 1 | accepted message/s | 44.73 → 41.10 | 38.14 → 35.07 | 28.48 → 28.28 | 43.03 → 47.18 | 3236.31 → 1922.58 |
| async | 1048577 | 16 | accepted message/s | 287.02 → 250.87 | 245.62 → 213.84 | 74.96 → 84.65 | 17.68 → 20.36 | 2894.47 → 1302.28 |
| async | 4194304 | 1 | accepted message/s | 35.51 → 34.93 | 29.65 → 29.12 | 32.48 → 32.95 | 94.26 → 99.77 | 2061.11 → 783.27 |
| async | 4194304 | 16 | accepted message/s | 93.07 → 69.06 | 79.54 → 35.31 | 220.45 → 278.46 | 61.17 → 123.95 | 1965.22 → 651.45 |
| async-burst | 4096 | 64 | accepted message/s | 2189.00 → 2073.50 | 543.20 → 531.69 | 44.43 → 48.99 | 1.39 → 1.46 | 1649.73 → 595.20 |
| async-paced | 4096 | 1 | accepted message/s | 9.13 → 9.01 | 8.31 → 8.09 | 14.35 → 15.32 | 49.20 → 48.33 | 1650.98 → 583.09 |
| direct | 4096 | 1 | request/s | 10032.16 → 10146.83 | n/a → n/a | 0.11 → 0.11 | 0.01 → 0.01 | 387.78 → 408.28 |
| direct | 4096 | 16 | request/s | 22826.08 → 22694.93 | n/a → n/a | 0.75 → 0.75 | 0.00 → 0.00 | 471.83 → 474.55 |
| direct | 262144 | 1 | request/s | 2775.99 → 2794.17 | n/a → n/a | 0.43 → 0.43 | 0.03 → 0.03 | 677.34 → 544.42 |
| direct | 262144 | 16 | request/s | 5953.18 → 5983.70 | n/a → n/a | 2.83 → 2.82 | 0.01 → 0.01 | 1332.50 → 919.84 |
| direct | 1048576 | 1 | request/s | 720.51 → 622.55 | n/a → n/a | 1.96 → 2.10 | 0.11 → 0.12 | 1618.02 → 994.47 |
| direct | 1048576 | 16 | request/s | 1830.18 → 1836.03 | n/a → n/a | 9.42 → 9.34 | 0.05 → 0.05 | 2744.00 → 1766.56 |
| direct | 1048577 | 1 | request/s | 609.63 → 550.80 | n/a → n/a | 2.10 → 2.12 | 0.13 → 0.13 | 3322.41 → 1900.22 |
| direct | 1048577 | 16 | request/s | 1849.05 → 1827.58 | n/a → n/a | 9.29 → 9.39 | 0.05 → 0.05 | 2894.95 → 1408.72 |
| direct | 4194304 | 1 | request/s | 233.70 → 224.03 | n/a → n/a | 6.85 → 7.25 | 0.40 → 0.36 | 2286.42 → 800.86 |
| direct | 4194304 | 16 | request/s | 467.79 → 469.50 | n/a → n/a | 37.21 → 36.71 | 0.19 → 0.21 | 2012.80 → 632.38 |
| sync | 4096 | 1 | request/s | 3015.83 → 3041.20 | n/a → n/a | 0.36 → 0.36 | 0.52 → 0.52 | 390.31 → 412.33 |
| sync | 4096 | 16 | request/s | 7629.48 → 7605.20 | n/a → n/a | 2.41 → 2.42 | 0.47 → 0.47 | 489.12 → 511.75 |
| sync | 262144 | 1 | request/s | 1180.43 → 1187.52 | n/a → n/a | 0.95 → 0.94 | 1.64 → 1.65 | 675.22 → 553.03 |
| sync | 262144 | 16 | request/s | 1582.88 → 1578.97 | n/a → n/a | 10.57 → 10.61 | 1.89 → 1.88 | 1419.75 → 927.22 |
| sync | 1048576 | 1 | request/s | 385.74 → 383.08 | n/a → n/a | 2.79 → 2.81 | 5.36 → 5.30 | 1618.86 → 994.83 |
| sync | 1048576 | 16 | request/s | 445.70 → 447.37 | n/a → n/a | 37.33 → 37.36 | 6.65 → 6.68 | 2741.78 → 1762.19 |
| sync | 1048577 | 1 | request/s | 381.70 → 381.97 | n/a → n/a | 2.81 → 2.82 | 5.35 → 5.28 | 3334.33 → 1917.08 |
| sync | 1048577 | 16 | request/s | 446.67 → 445.19 | n/a → n/a | 37.21 → 37.30 | 6.74 → 6.66 | 2901.42 → 1414.20 |
| sync | 4194304 | 1 | request/s | 104.21 → 104.18 | n/a → n/a | 10.35 → 10.33 | 19.46 → 19.59 | 2286.44 → 812.17 |
| sync | 4194304 | 16 | request/s | 110.79 → 110.74 | n/a → n/a | 151.91 → 152.47 | 25.64 → 25.26 | 2019.61 → 638.44 |

</details>

<details><summary>http-bounded-abba-20 / http-attempt-2 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| async | 4096 | 1 | accepted message/s | 91.82 → 91.70 | 73.13 → 73.20 | 13.99 → 14.04 | 11.75 → 11.89 | 400.05 → 408.95 |
| async | 4096 | 16 | accepted message/s | 1096.44 → 1060.40 | 859.65 → 831.56 | 20.01 → 21.24 | 1.80 → 1.77 | 476.41 → 522.92 |
| async | 262144 | 1 | accepted message/s | 38.75 → 38.16 | 29.39 → 31.61 | 38.35 → 38.58 | 50.03 → 48.00 | 619.05 → 651.33 |
| async | 262144 | 16 | accepted message/s | 55.28 → 55.49 | 44.50 → 45.79 | 471.85 → 482.24 | 36.31 → 37.49 | 1580.23 → 964.02 |
| async | 1048576 | 1 | accepted message/s | 12.76 → 12.47 | 10.36 → 10.24 | 120.70 → 123.37 | 151.28 → 164.54 | 1480.28 → 870.66 |
| async | 1048576 | 16 | accepted message/s | 14.21 → 14.17 | 12.28 → 11.81 | 1540.89 → 1598.72 | 132.89 → 130.16 | 3280.28 → 1903.72 |
| async | 1048577 | 1 | accepted message/s | 42.20 → 44.01 | 35.98 → 36.63 | 27.30 → 26.66 | 43.35 → 44.28 | 3188.64 → 1745.17 |
| async | 1048577 | 16 | accepted message/s | 288.60 → 280.83 | 238.84 → 232.33 | 73.73 → 76.21 | 18.39 → 20.13 | 2953.19 → 1302.58 |
| async | 4194304 | 1 | accepted message/s | 35.13 → 35.51 | 29.71 → 28.45 | 32.16 → 31.19 | 94.16 → 98.91 | 1902.58 → 580.19 |
| async | 4194304 | 16 | accepted message/s | 90.17 → 93.55 | 74.95 → 76.25 | 235.56 → 225.08 | 64.41 → 65.89 | 1729.14 → 576.56 |
| async-burst | 4096 | 64 | accepted message/s | 2141.92 → 2025.75 | 1207.41 → 579.45 | 53.99 → 44.09 | 1.19 → 1.47 | 1579.20 → 638.36 |
| async-paced | 4096 | 1 | accepted message/s | 9.07 → 8.70 | 8.24 → 8.08 | 14.75 → 19.91 | 48.40 → 50.28 | 1580.44 → 623.94 |
| direct | 4096 | 1 | request/s | 9962.70 → 10039.17 | n/a → n/a | 0.11 → 0.11 | 0.01 → 0.01 | 390.45 → 393.05 |
| direct | 4096 | 16 | request/s | 22732.84 → 22752.18 | n/a → n/a | 0.75 → 0.75 | 0.00 → 0.00 | 486.00 → 502.62 |
| direct | 262144 | 1 | request/s | 2773.56 → 2789.32 | n/a → n/a | 0.43 → 0.43 | 0.03 → 0.03 | 522.38 → 587.25 |
| direct | 262144 | 16 | request/s | 5983.37 → 5968.79 | n/a → n/a | 2.82 → 2.82 | 0.01 → 0.01 | 1324.58 → 942.11 |
| direct | 1048576 | 1 | request/s | 757.57 → 640.73 | n/a → n/a | 1.74 → 2.09 | 0.11 → 0.12 | 1512.66 → 934.17 |
| direct | 1048576 | 16 | request/s | 1804.34 → 1806.25 | n/a → n/a | 9.40 → 9.41 | 0.05 → 0.05 | 2786.64 → 1698.91 |
| direct | 1048577 | 1 | request/s | 554.32 → 538.83 | n/a → n/a | 2.15 → 2.16 | 0.14 → 0.14 | 3219.02 → 1813.91 |
| direct | 1048577 | 16 | request/s | 1830.93 → 1849.07 | n/a → n/a | 9.34 → 9.28 | 0.05 → 0.05 | 3038.28 → 1503.86 |
| direct | 4194304 | 1 | request/s | 221.77 → 228.91 | n/a → n/a | 7.14 → 7.05 | 0.38 → 0.36 | 1905.83 → 594.14 |
| direct | 4194304 | 16 | request/s | 460.04 → 462.34 | n/a → n/a | 38.24 → 37.39 | 0.22 → 0.19 | 1722.45 → 570.44 |
| sync | 4096 | 1 | request/s | 3038.99 → 3027.49 | n/a → n/a | 0.36 → 0.36 | 0.51 → 0.52 | 388.41 → 395.69 |
| sync | 4096 | 16 | request/s | 7637.68 → 7642.90 | n/a → n/a | 2.41 → 2.41 | 0.47 → 0.47 | 497.75 → 506.39 |
| sync | 262144 | 1 | request/s | 1194.29 → 1183.85 | n/a → n/a | 0.93 → 0.95 | 1.64 → 1.63 | 528.48 → 584.31 |
| sync | 262144 | 16 | request/s | 1587.62 → 1582.68 | n/a → n/a | 10.51 → 10.59 | 1.89 → 1.89 | 1396.12 → 942.03 |
| sync | 1048576 | 1 | request/s | 383.74 → 383.94 | n/a → n/a | 2.81 → 2.81 | 5.26 → 5.30 | 1497.66 → 944.48 |
| sync | 1048576 | 16 | request/s | 446.30 → 444.86 | n/a → n/a | 37.18 → 37.33 | 6.73 → 6.65 | 2784.66 → 1840.95 |
| sync | 1048577 | 1 | request/s | 383.36 → 382.75 | n/a → n/a | 2.81 → 2.81 | 5.27 → 5.30 | 3225.80 → 1814.47 |
| sync | 1048577 | 16 | request/s | 445.22 → 444.49 | n/a → n/a | 37.36 → 37.30 | 6.71 → 6.74 | 3043.14 → 1507.05 |
| sync | 4194304 | 1 | request/s | 104.49 → 103.99 | n/a → n/a | 10.34 → 10.35 | 19.77 → 19.62 | 1905.83 → 602.62 |
| sync | 4194304 | 16 | request/s | 110.60 → 112.48 | n/a → n/a | 151.30 → 153.92 | 25.59 → 25.30 | 1721.98 → 565.36 |

</details>

<details><summary>memory-abba-replay / mixed-c12-attempt-1 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| slimdata-mixed | n/a | 12 | operation/s | 434.89 → 425.58 | n/a → n/a | 92.03 → 91.46 | 1.72 → 1.79 | 423.00 → 440.89 |

</details>

<details><summary>memory-abba-replay / mixed-c12-attempt-2 (PASS)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| slimdata-mixed | n/a | 12 | operation/s | 422.63 → 470.61 | n/a → n/a | 91.54 → 90.82 | 1.81 → 1.66 | 443.77 → 424.19 |

</details>

<details><summary>memory-abba-replay / mixed-c48-attempt-1 (PASS)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| slimdata-mixed | n/a | 48 | operation/s | 2674.46 → 2619.31 | n/a → n/a | 26.21 → 26.11 | 0.85 → 0.83 | 594.47 → 603.91 |

</details>

<details><summary>memory-abba-replay / set-c12-attempt-1 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| slimdata-set | 4096 | 12 | operation/s | 111.49 → 104.00 | n/a → n/a | 246.57 → 249.88 | 3.50 → 3.64 | 495.17 → 505.73 |

</details>

<details><summary>memory-abba-replay / set-c48-attempt-1 (PASS)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| slimdata-set | 4096 | 48 | operation/s | 971.37 → 964.44 | n/a → n/a | 112.26 → 114.09 | 1.18 → 1.10 | 506.52 → 505.95 |

</details>

<details><summary>threshold-512kib-bounded / http-attempt-1 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| async | 524287 | 1 | accepted message/s | 23.77 → 22.67 | 17.30 → 15.55 | 65.14 → 68.63 | 91.48 → 91.70 | 590.22 → 616.81 |
| async | 524287 | 16 | accepted message/s | 29.36 → 28.98 | 23.58 → 22.12 | 966.53 → 945.14 | 68.81 → 67.26 | 1267.97 → 1143.73 |
| async | 524288 | 1 | accepted message/s | 22.91 → 23.90 | 15.73 → 16.45 | 66.92 → 69.70 | 84.86 → 84.07 | 1003.59 → 922.44 |
| async | 524288 | 16 | accepted message/s | 29.70 → 29.16 | 22.97 → 24.17 | 881.93 → 862.30 | 64.94 → 64.22 | 1282.48 → 1184.73 |
| async | 524289 | 1 | accepted message/s | 24.13 → 51.19 | 16.60 → 34.17 | 66.44 → 21.10 | 82.34 → 31.95 | 970.44 → 1153.09 |
| async | 524289 | 16 | accepted message/s | 27.16 → 380.88 | 21.82 → 255.43 | 911.64 → 55.17 | 72.74 → 16.89 | 1208.50 → 827.69 |
| async | 1048576 | 1 | accepted message/s | 13.07 → 45.23 | 9.02 → 31.00 | 121.89 → 26.37 | 162.63 → 46.52 | 1016.08 → 629.05 |
| async | 1048576 | 16 | accepted message/s | 15.03 → 286.49 | 11.39 → 186.46 | 1487.72 → 75.10 | 128.35 → 23.54 | 1713.12 → 576.61 |
| async | 1048577 | 1 | accepted message/s | 44.50 → 45.05 | 33.45 → 33.54 | 26.74 → 26.99 | 47.73 → 47.51 | 1484.31 → 552.23 |
| async | 1048577 | 16 | accepted message/s | 284.68 → 284.96 | 211.30 → 190.96 | 76.64 → 73.05 | 21.41 → 23.39 | 1269.09 → 562.03 |
| async-burst | 4096 | 64 | accepted message/s | 2177.84 → 2179.31 | 541.59 → 629.37 | 58.20 → 49.02 | 1.44 → 1.45 | 1039.50 → 584.05 |
| async-paced | 4096 | 1 | accepted message/s | 8.97 → 9.00 | 8.24 → 8.00 | 16.14 → 14.82 | 38.50 → 51.08 | 1034.91 → 541.64 |
| direct | 524287 | 1 | request/s | 1620.34 → 1677.25 | n/a → n/a | 0.80 → 0.76 | 0.05 → 0.05 | 313.66 → 315.84 |
| direct | 524287 | 16 | request/s | 3537.50 → 3552.44 | n/a → n/a | 4.78 → 4.80 | 0.03 → 0.02 | 589.33 → 606.38 |
| direct | 524288 | 1 | request/s | 1629.67 → 1626.91 | n/a → n/a | 0.86 → 0.88 | 0.05 → 0.05 | 1204.84 → 1143.86 |
| direct | 524288 | 16 | request/s | 3565.05 → 3550.06 | n/a → n/a | 4.76 → 4.77 | 0.02 → 0.02 | 773.61 → 731.84 |
| direct | 524289 | 1 | request/s | 1629.04 → 1610.97 | n/a → n/a | 0.80 → 0.88 | 0.05 → 0.05 | 1283.61 → 1184.77 |
| direct | 524289 | 16 | request/s | 3556.23 → 3610.10 | n/a → n/a | 4.79 → 4.72 | 0.02 → 0.02 | 809.48 → 941.53 |
| direct | 1048576 | 1 | request/s | 689.22 → 542.77 | n/a → n/a | 2.03 → 2.18 | 0.14 → 0.16 | 1208.50 → 610.41 |
| direct | 1048576 | 16 | request/s | 1857.64 → 1871.41 | n/a → n/a | 9.14 → 9.10 | 0.05 → 0.05 | 897.81 → 557.98 |
| direct | 1048577 | 1 | request/s | 697.22 → 791.93 | n/a → n/a | 2.06 → 1.98 | 0.11 → 0.12 | 1620.05 → 543.03 |
| direct | 1048577 | 16 | request/s | 1869.14 → 1836.02 | n/a → n/a | 9.11 → 9.28 | 0.04 → 0.08 | 1378.31 → 556.59 |
| sync | 524287 | 1 | request/s | 702.04 → 707.52 | n/a → n/a | 1.57 → 1.56 | 2.90 → 2.91 | 340.09 → 348.39 |
| sync | 524287 | 16 | request/s | 873.07 → 873.74 | n/a → n/a | 19.18 → 19.11 | 3.49 → 3.52 | 585.34 → 602.22 |
| sync | 524288 | 1 | request/s | 706.63 → 705.08 | n/a → n/a | 1.55 → 1.56 | 2.92 → 2.91 | 1214.41 → 1145.36 |
| sync | 524288 | 16 | request/s | 868.44 → 865.95 | n/a → n/a | 19.18 → 19.21 | 3.56 → 3.55 | 773.30 → 740.59 |
| sync | 524289 | 1 | request/s | 702.27 → 702.14 | n/a → n/a | 1.57 → 1.56 | 2.93 → 2.95 | 1287.58 → 1186.69 |
| sync | 524289 | 16 | request/s | 868.20 → 885.32 | n/a → n/a | 19.12 → 18.89 | 3.56 → 3.58 | 806.31 → 992.48 |
| sync | 1048576 | 1 | request/s | 386.65 → 387.60 | n/a → n/a | 2.79 → 2.79 | 5.28 → 5.23 | 1104.05 → 613.64 |
| sync | 1048576 | 16 | request/s | 457.11 → 454.25 | n/a → n/a | 36.81 → 36.27 | 6.62 → 6.72 | 897.52 → 555.69 |
| sync | 1048577 | 1 | request/s | 388.50 → 385.72 | n/a → n/a | 2.77 → 2.81 | 5.29 → 5.29 | 1622.05 → 546.38 |
| sync | 1048577 | 16 | request/s | 453.61 → 451.99 | n/a → n/a | 36.47 → 36.70 | 6.67 → 6.78 | 1448.17 → 553.89 |

</details>

<details><summary>threshold-512kib-retry / http-attempt-1 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| async | 524287 | 1 | accepted message/s | 23.16 → 23.41 | 16.36 → 17.01 | 70.29 → 65.16 | 86.71 → 88.03 | 566.44 → 562.44 |
| async | 524287 | 16 | accepted message/s | 27.79 → 27.54 | 22.39 → 21.33 | 900.05 → 870.81 | 63.63 → 66.56 | 1227.28 → 1130.88 |
| async | 524288 | 1 | accepted message/s | 23.88 → 23.57 | 16.04 → 15.74 | 67.25 → 70.79 | 77.96 → 84.43 | 1043.12 → 868.95 |
| async | 524288 | 16 | accepted message/s | 28.75 → 28.04 | 21.72 → 22.60 | 1032.57 → 964.60 | 61.18 → 64.34 | 1200.14 → 1258.05 |
| async | 524289 | 1 | accepted message/s | 23.37 → 48.88 | 16.13 → 33.49 | 67.56 → 24.07 | 77.37 → 34.19 | 1023.66 → 1210.27 |
| async | 524289 | 16 | accepted message/s | 25.86 → 365.62 | 19.07 → 244.74 | 999.12 → 57.12 | 69.53 → 17.05 | 1203.61 → 981.05 |
| async | 1048576 | 1 | accepted message/s | 12.98 → 42.06 | 8.72 → 31.04 | 122.44 → 27.42 | 157.62 → 46.86 | 1096.45 → 664.89 |
| async | 1048576 | 16 | accepted message/s | 14.54 → 278.24 | 11.01 → 211.69 | 1656.16 → 77.97 | 121.92 → 21.13 | 1721.00 → 654.39 |
| async | 1048577 | 1 | accepted message/s | 40.44 → 42.13 | 29.93 → 28.85 | 29.46 → 27.90 | 48.84 → 48.27 | 1614.47 → 594.95 |
| async | 1048577 | 16 | accepted message/s | 253.48 → 278.60 | 170.72 → 232.25 | 97.17 → 78.28 | 22.85 → 19.97 | 1373.02 → 588.09 |
| async-burst | 4096 | 64 | accepted message/s | 2125.61 → 1936.42 | 581.71 → 604.28 | 55.08 → 53.52 | 1.38 → 1.51 | 1015.95 → 612.31 |
| async-paced | 4096 | 1 | accepted message/s | 8.63 → 9.00 | 7.94 → 8.00 | 19.91 → 14.97 | 48.51 → 49.99 | 1019.56 → 581.73 |
| direct | 524287 | 1 | request/s | 1723.06 → 1745.75 | n/a → n/a | 0.75 → 0.74 | 0.04 → 0.04 | 316.92 → 313.50 |
| direct | 524287 | 16 | request/s | 3510.88 → 3560.79 | n/a → n/a | 4.82 → 4.80 | 0.02 → 0.02 | 569.88 → 579.44 |
| direct | 524288 | 1 | request/s | 1615.06 → 1671.92 | n/a → n/a | 0.88 → 0.78 | 0.05 → 0.05 | 1227.30 → 1130.88 |
| direct | 524288 | 16 | request/s | 3584.22 → 3553.43 | n/a → n/a | 4.76 → 4.80 | 0.02 → 0.02 | 762.55 → 757.58 |
| direct | 524289 | 1 | request/s | 1628.88 → 1634.89 | n/a → n/a | 0.86 → 0.83 | 0.05 → 0.05 | 1198.53 → 1258.06 |
| direct | 524289 | 16 | request/s | 3560.41 → 3596.34 | n/a → n/a | 4.78 → 4.75 | 0.02 → 0.02 | 831.66 → 1088.80 |
| direct | 1048576 | 1 | request/s | 529.62 → 710.01 | n/a → n/a | 2.21 → 1.96 | 0.16 → 0.13 | 1203.78 → 660.39 |
| direct | 1048576 | 16 | request/s | 1866.73 → 1849.14 | n/a → n/a | 9.14 → 9.23 | 0.05 → 0.05 | 899.02 → 642.50 |
| direct | 1048577 | 1 | request/s | 570.77 → 562.08 | n/a → n/a | 2.13 → 2.15 | 0.14 → 0.16 | 1655.56 → 596.77 |
| direct | 1048577 | 16 | request/s | 1897.74 → 1898.96 | n/a → n/a | 9.07 → 9.11 | 0.04 → 0.05 | 1481.88 → 582.36 |
| sync | 524287 | 1 | request/s | 708.59 → 704.31 | n/a → n/a | 1.55 → 1.56 | 2.80 → 2.91 | 328.70 → 343.77 |
| sync | 524287 | 16 | request/s | 871.10 → 864.86 | n/a → n/a | 19.22 → 19.33 | 3.42 → 3.48 | 565.70 → 573.80 |
| sync | 524288 | 1 | request/s | 707.60 → 705.77 | n/a → n/a | 1.55 → 1.55 | 2.93 → 2.87 | 1232.98 → 1141.80 |
| sync | 524288 | 16 | request/s | 870.46 → 862.85 | n/a → n/a | 19.16 → 19.28 | 3.51 → 3.54 | 760.12 → 756.42 |
| sync | 524289 | 1 | request/s | 709.02 → 704.73 | n/a → n/a | 1.55 → 1.55 | 2.87 → 2.89 | 1209.36 → 1260.34 |
| sync | 524289 | 16 | request/s | 869.88 → 869.05 | n/a → n/a | 19.09 → 19.11 | 3.48 → 3.48 | 843.02 → 1106.70 |
| sync | 1048576 | 1 | request/s | 387.20 → 386.12 | n/a → n/a | 2.77 → 2.80 | 5.21 → 5.21 | 1137.66 → 664.53 |
| sync | 1048576 | 16 | request/s | 453.12 → 451.76 | n/a → n/a | 36.68 → 36.64 | 6.77 → 6.66 | 897.66 → 640.22 |
| sync | 1048577 | 1 | request/s | 388.75 → 385.39 | n/a → n/a | 2.76 → 2.80 | 5.25 → 5.26 | 1662.17 → 593.28 |
| sync | 1048577 | 16 | request/s | 453.46 → 449.62 | n/a → n/a | 36.61 → 36.74 | 6.59 → 6.75 | 1481.83 → 582.08 |

</details>

<details><summary>threshold-512kib-retry / http-attempt-2 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| async | 524287 | 1 | accepted message/s | 22.89 → 22.57 | 16.63 → 16.40 | 70.46 → 69.34 | 89.48 → 89.53 | 572.27 → 559.75 |
| async | 524287 | 16 | accepted message/s | 27.93 → 27.26 | 20.91 → 20.30 | 868.97 → 867.56 | 65.35 → 65.17 | 1193.08 → 1232.84 |
| async | 524288 | 1 | accepted message/s | 23.12 → 23.73 | 16.39 → 16.80 | 68.63 → 68.49 | 83.81 → 81.95 | 995.56 → 1035.70 |
| async | 524288 | 16 | accepted message/s | 27.67 → 26.36 | 21.40 → 21.99 | 949.91 → 994.07 | 63.30 → 69.98 | 1200.09 → 1299.84 |
| async | 524289 | 1 | accepted message/s | 22.61 → 48.17 | 15.57 → 33.04 | 67.01 → 24.72 | 99.02 → 33.61 | 1012.20 → 1221.39 |
| async | 524289 | 16 | accepted message/s | 26.46 → 378.28 | 20.78 → 252.85 | 871.38 → 57.92 | 75.68 → 16.97 | 1228.86 → 933.91 |
| async | 1048576 | 1 | accepted message/s | 12.38 → 44.79 | 9.17 → 35.63 | 124.59 → 27.18 | 156.59 → 45.45 | 1063.00 → 683.16 |
| async | 1048576 | 16 | accepted message/s | 13.03 → 286.34 | 9.53 → 191.74 | 1592.56 → 75.30 | 141.63 → 21.51 | 1606.61 → 671.47 |
| async | 1048577 | 1 | accepted message/s | 40.78 → 41.61 | 33.88 → 31.43 | 29.27 → 28.33 | 47.21 → 47.88 | 1523.72 → 638.94 |
| async | 1048577 | 16 | accepted message/s | 288.24 → 282.70 | 198.35 → 208.94 | 74.74 → 77.75 | 22.06 → 21.40 | 1362.95 → 579.59 |
| async-burst | 4096 | 64 | accepted message/s | 2190.63 → 2154.28 | 541.78 → 627.16 | 52.08 → 49.44 | 1.45 → 1.47 | 1098.98 → 607.91 |
| async-paced | 4096 | 1 | accepted message/s | 9.00 → 8.76 | 8.18 → 7.97 | 14.70 → 17.97 | 48.79 → 51.21 | 1098.06 → 581.66 |
| direct | 524287 | 1 | request/s | 1725.73 → 1654.51 | n/a → n/a | 0.73 → 0.74 | 0.04 → 0.05 | 314.59 → 315.12 |
| direct | 524287 | 16 | request/s | 3526.71 → 3541.14 | n/a → n/a | 4.80 → 4.81 | 0.02 → 0.02 | 562.39 → 564.03 |
| direct | 524288 | 1 | request/s | 1650.44 → 1635.71 | n/a → n/a | 0.79 → 0.85 | 0.05 → 0.05 | 1193.36 → 1234.38 |
| direct | 524288 | 16 | request/s | 3573.53 → 3528.64 | n/a → n/a | 4.79 → 4.81 | 0.02 → 0.02 | 723.45 → 754.42 |
| direct | 524289 | 1 | request/s | 1642.05 → 1573.89 | n/a → n/a | 0.79 → 0.97 | 0.05 → 0.05 | 1194.48 → 1280.91 |
| direct | 524289 | 16 | request/s | 3559.44 → 3591.72 | n/a → n/a | 4.77 → 4.74 | 0.02 → 0.02 | 769.23 → 1049.11 |
| direct | 1048576 | 1 | request/s | 522.96 → 552.98 | n/a → n/a | 2.24 → 2.17 | 0.19 → 0.17 | 1162.33 → 704.44 |
| direct | 1048576 | 16 | request/s | 1847.68 → 1889.50 | n/a → n/a | 9.22 → 9.11 | 0.05 → 0.04 | 909.16 → 667.36 |
| direct | 1048577 | 1 | request/s | 556.74 → 545.99 | n/a → n/a | 2.06 → 2.16 | 0.13 → 0.16 | 1562.94 → 623.84 |
| direct | 1048577 | 16 | request/s | 1898.46 → 1892.58 | n/a → n/a | 9.03 → 9.08 | 0.04 → 0.05 | 1389.55 → 566.77 |
| sync | 524287 | 1 | request/s | 707.82 → 705.41 | n/a → n/a | 1.55 → 1.56 | 2.91 → 2.87 | 340.17 → 341.22 |
| sync | 524287 | 16 | request/s | 865.02 → 862.05 | n/a → n/a | 19.28 → 19.22 | 3.48 → 3.43 | 555.33 → 559.38 |
| sync | 524288 | 1 | request/s | 706.88 → 708.91 | n/a → n/a | 1.55 → 1.55 | 2.90 → 2.90 | 1197.20 → 1174.86 |
| sync | 524288 | 16 | request/s | 869.28 → 865.77 | n/a → n/a | 19.05 → 19.31 | 3.55 → 3.48 | 721.08 → 750.97 |
| sync | 524289 | 1 | request/s | 711.10 → 704.44 | n/a → n/a | 1.54 → 1.56 | 2.88 → 2.90 | 1210.27 → 1283.50 |
| sync | 524289 | 16 | request/s | 878.10 → 867.33 | n/a → n/a | 19.01 → 19.12 | 3.51 → 3.43 | 766.47 → 1083.59 |
| sync | 1048576 | 1 | request/s | 389.72 → 385.54 | n/a → n/a | 2.78 → 2.80 | 5.25 → 5.26 | 1160.02 → 671.59 |
| sync | 1048576 | 16 | request/s | 446.80 → 450.54 | n/a → n/a | 36.99 → 36.82 | 6.68 → 6.59 | 934.53 → 678.97 |
| sync | 1048577 | 1 | request/s | 388.02 → 385.06 | n/a → n/a | 2.77 → 2.78 | 5.25 → 5.27 | 1579.62 → 629.70 |
| sync | 1048577 | 16 | request/s | 453.98 → 446.75 | n/a → n/a | 36.55 → 37.11 | 6.65 → 6.64 | 1388.02 → 567.03 |

</details>

<details><summary>wal-64kib-retry / http-attempt-1 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| async | 262144 | 1 | accepted message/s | 37.32 → 63.79 | 27.14 → 44.94 | 41.26 → 22.00 | 52.14 → 34.15 | 560.55 → 573.55 |
| async | 262144 | 16 | accepted message/s | 52.23 → 161.84 | 40.22 → 115.45 | 505.81 → 157.24 | 42.27 → 20.50 | 935.48 → 1063.00 |
| async | 1048576 | 1 | accepted message/s | 12.43 → 31.15 | 8.83 → 21.38 | 122.78 → 46.01 | 158.08 → 88.67 | 832.70 → 946.09 |
| async | 1048576 | 16 | accepted message/s | 13.98 → 43.78 | 10.40 → 31.80 | 1572.44 → 479.52 | 139.22 → 66.49 | 1568.84 → 2070.06 |
| async | 1048577 | 1 | accepted message/s | 43.11 → 42.80 | 27.93 → 29.34 | 28.07 → 28.26 | 48.37 → 47.98 | 1287.97 → 1684.09 |
| async | 1048577 | 16 | accepted message/s | 276.75 → 292.60 | 210.52 → 201.45 | 90.19 → 76.69 | 20.92 → 22.58 | 1164.59 → 1417.44 |
| async | 4194304 | 1 | accepted message/s | 37.20 → 36.92 | 27.55 → 23.89 | 31.02 → 30.97 | 96.67 → 99.69 | 1009.34 → 1217.95 |
| async | 4194304 | 16 | accepted message/s | 96.77 → 96.33 | 66.05 → 64.67 | 208.56 → 212.16 | 65.32 → 68.12 | 809.31 → 1222.80 |
| async-burst | 4096 | 64 | accepted message/s | 2026.20 → 3088.36 | 616.73 → 635.34 | 52.18 → 39.14 | 1.41 → 1.26 | 796.81 → 967.36 |
| async-paced | 4096 | 1 | accepted message/s | 8.82 → 9.04 | 8.10 → 8.29 | 17.09 → 14.00 | 48.69 → 49.44 | 766.64 → 1018.62 |
| direct | 262144 | 1 | request/s | 2756.23 → 2717.16 | n/a → n/a | 0.43 → 0.43 | 0.03 → 0.03 | 313.61 → 316.52 |
| direct | 262144 | 16 | request/s | 6101.86 → 6142.90 | n/a → n/a | 2.75 → 2.74 | 0.01 → 0.01 | 558.03 → 531.55 |
| direct | 1048576 | 1 | request/s | 530.33 → 603.00 | n/a → n/a | 2.24 → 1.99 | 0.16 → 0.11 | 863.47 → 1036.11 |
| direct | 1048576 | 16 | request/s | 1857.52 → 1890.83 | n/a → n/a | 9.22 → 9.07 | 0.04 → 0.04 | 814.86 → 840.98 |
| direct | 1048577 | 1 | request/s | 734.58 → 581.46 | n/a → n/a | 1.95 → 1.98 | 0.11 → 0.12 | 1523.03 → 2070.19 |
| direct | 1048577 | 16 | request/s | 1886.06 → 1886.92 | n/a → n/a | 9.13 → 9.09 | 0.04 → 0.04 | 1248.53 → 1602.12 |
| direct | 4194304 | 1 | request/s | 221.76 → 215.81 | n/a → n/a | 7.43 → 7.57 | 0.38 → 0.41 | 1010.23 → 1200.95 |
| direct | 4194304 | 16 | request/s | 468.03 → 464.58 | n/a → n/a | 37.40 → 37.64 | 0.18 → 0.18 | 869.19 → 1219.77 |
| sync | 262144 | 1 | request/s | 1206.49 → 1198.35 | n/a → n/a | 0.93 → 0.94 | 1.62 → 1.65 | 344.17 → 346.17 |
| sync | 262144 | 16 | request/s | 1608.97 → 1609.38 | n/a → n/a | 10.40 → 10.37 | 1.90 → 1.86 | 556.62 → 528.98 |
| sync | 1048576 | 1 | request/s | 389.02 → 388.23 | n/a → n/a | 2.78 → 2.78 | 5.23 → 5.27 | 869.84 → 1044.80 |
| sync | 1048576 | 16 | request/s | 448.37 → 450.90 | n/a → n/a | 36.76 → 36.72 | 6.61 → 6.63 | 813.73 → 839.95 |
| sync | 1048577 | 1 | request/s | 386.63 → 388.37 | n/a → n/a | 2.79 → 2.77 | 5.25 → 5.30 | 1529.88 → 1973.97 |
| sync | 1048577 | 16 | request/s | 451.74 → 451.56 | n/a → n/a | 36.75 → 36.58 | 6.57 → 6.63 | 1247.44 → 1628.12 |
| sync | 4194304 | 1 | request/s | 105.24 → 105.75 | n/a → n/a | 10.22 → 10.18 | 19.18 → 19.41 | 1015.00 → 1202.27 |
| sync | 4194304 | 16 | request/s | 113.06 → 112.70 | n/a → n/a | 149.92 → 149.89 | 25.16 → 25.57 | 868.33 → 1219.31 |

</details>

<details><summary>wal-64kib-retry / http-attempt-2 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| async | 262144 | 1 | accepted message/s | 37.40 → 51.11 | 27.17 → 36.27 | 39.90 → 39.26 | 50.46 → 33.35 | 532.67 → 522.84 |
| async | 262144 | 16 | accepted message/s | 50.98 → 152.35 | 40.34 → 107.89 | 463.13 → 172.02 | 41.40 → 20.05 | 908.50 → 1024.45 |
| async | 1048576 | 1 | accepted message/s | 12.74 → 27.50 | 8.76 → 18.93 | 121.10 → 51.17 | 154.20 → 96.12 | 810.45 → 899.81 |
| async | 1048576 | 16 | accepted message/s | 13.69 → 36.80 | 10.12 → 27.47 | 1509.66 → 609.60 | 134.51 → 66.94 | 1501.05 → 2221.28 |
| async | 1048577 | 1 | accepted message/s | 41.54 → 26.58 | 27.72 → 18.24 | 28.83 → 201.43 | 49.49 → 54.40 | 1528.31 → 1916.91 |
| async | 1048577 | 16 | accepted message/s | 285.71 → 283.85 | 197.25 → 185.17 | 77.66 → 80.03 | 22.16 → 22.40 | 1263.67 → 1757.81 |
| async | 4194304 | 1 | accepted message/s | 35.77 → 35.89 | 24.51 → 23.32 | 30.54 → 31.42 | 98.86 → 97.83 | 1018.33 → 1238.30 |
| async | 4194304 | 16 | accepted message/s | 92.06 → 97.17 | 62.59 → 63.63 | 244.60 → 216.65 | 66.90 → 67.59 | 926.53 → 1252.39 |
| async-burst | 4096 | 64 | accepted message/s | 2124.81 → 3015.87 | 682.78 → 583.97 | 51.29 → 34.82 | 1.40 → 1.28 | 960.02 → 1258.98 |
| async-paced | 4096 | 1 | accepted message/s | 8.94 → 9.07 | 8.29 → 8.24 | 14.88 → 12.38 | 40.73 → 35.26 | 929.30 → 1255.97 |
| direct | 262144 | 1 | request/s | 2754.50 → 2607.10 | n/a → n/a | 0.43 → 0.47 | 0.03 → 0.04 | 311.12 → 314.91 |
| direct | 262144 | 16 | request/s | 6076.96 → 6105.63 | n/a → n/a | 2.77 → 2.79 | 0.01 → 0.01 | 550.70 → 556.22 |
| direct | 1048576 | 1 | request/s | 560.48 → 711.74 | n/a → n/a | 2.19 → 1.80 | 0.15 → 0.13 | 852.33 → 997.23 |
| direct | 1048576 | 16 | request/s | 1851.89 → 1888.10 | n/a → n/a | 9.21 → 9.09 | 0.04 → 0.04 | 804.36 → 784.80 |
| direct | 1048577 | 1 | request/s | 575.46 → 751.60 | n/a → n/a | 2.10 → 1.82 | 0.13 → 0.14 | 1501.27 → 2216.44 |
| direct | 1048577 | 16 | request/s | 1852.78 → 1861.50 | n/a → n/a | 9.15 → 9.17 | 0.04 → 0.04 | 1427.23 → 1923.88 |
| direct | 4194304 | 1 | request/s | 229.03 → 226.51 | n/a → n/a | 7.05 → 6.97 | 0.38 → 0.44 | 1012.73 → 1234.92 |
| direct | 4194304 | 16 | request/s | 461.53 → 465.20 | n/a → n/a | 37.68 → 37.45 | 0.18 → 0.21 | 985.41 → 1227.50 |
| sync | 262144 | 1 | request/s | 1196.59 → 1175.26 | n/a → n/a | 0.94 → 0.98 | 1.62 → 1.62 | 339.88 → 340.06 |
| sync | 262144 | 16 | request/s | 1614.35 → 1654.08 | n/a → n/a | 10.37 → 10.43 | 1.89 → 1.81 | 548.39 → 554.48 |
| sync | 1048576 | 1 | request/s | 384.65 → 385.23 | n/a → n/a | 2.81 → 2.85 | 5.26 → 5.18 | 852.83 → 1013.03 |
| sync | 1048576 | 16 | request/s | 445.82 → 463.84 | n/a → n/a | 37.05 → 36.30 | 6.68 → 6.45 | 815.98 → 783.91 |
| sync | 1048577 | 1 | request/s | 382.95 → 387.35 | n/a → n/a | 2.81 → 2.82 | 5.25 → 5.11 | 1503.00 → 2065.70 |
| sync | 1048577 | 16 | request/s | 453.60 → 456.19 | n/a → n/a | 36.73 → 36.42 | 6.62 → 6.78 | 1425.75 → 1923.22 |
| sync | 4194304 | 1 | request/s | 104.69 → 107.84 | n/a → n/a | 10.25 → 9.99 | 19.38 → 18.92 | 1013.69 → 1233.62 |
| sync | 4194304 | 16 | request/s | 112.66 → 115.10 | n/a → n/a | 147.80 → 147.29 | 25.42 → 25.11 | 981.03 → 1227.45 |

</details>


## Candidate observations

Throughput and CPU are medians across the listed repetitions. RAM is the highest sampled cluster working set among those repetitions during the load window. For Streams and Jobs, accepted throughput covers submission and completed throughput covers submission and drain. The table CPU and RAM values cover submission. Drain resources remain in the raw reports.

| Profile | Scenario | Bytes | C | Runs | Median rate | Unit | Completed/s incl. drain | CPU ms/op | Peak MiB |
|---|---|---:|---:|---:|---:|---|---:|---:|---:|
| data-rerun | decrement | n/a | 1 | 2 | 39.68 | operation/s | n/a | 8.38 | 501.91 |
| data-rerun | decrement | n/a | 16 | 2 | 107.46 | operation/s | n/a | 2.33 | 507.81 |
| data-rerun | delete | 4096 | 1 | 2 | 206.06 | operation/s | n/a | 4.31 | 501.28 |
| data-rerun | delete | 4096 | 16 | 2 | 189.25 | operation/s | n/a | 1.71 | 508.41 |
| data-rerun | get | 4096 | 1 | 2 | 4635.59 | operation/s | n/a | 0.56 | 510.38 |
| data-rerun | get | 4096 | 16 | 2 | 19815.14 | operation/s | n/a | 0.33 | 498.42 |
| data-rerun | hashdelete | 4096 | 1 | 2 | 213.35 | operation/s | n/a | 4.32 | 430.30 |
| data-rerun | hashdelete | 4096 | 16 | 2 | 168.10 | operation/s | n/a | 1.74 | 492.73 |
| data-rerun | hashget | 4096 | 1 | 2 | 4985.17 | operation/s | n/a | 0.54 | 619.56 |
| data-rerun | hashget | 4096 | 16 | 2 | 19437.91 | operation/s | n/a | 0.32 | 462.00 |
| data-rerun | hashset | 4096 | 1 | 2 | 40.05 | operation/s | n/a | 8.69 | 513.73 |
| data-rerun | hashset | 4096 | 16 | 2 | 126.61 | operation/s | n/a | 2.68 | 589.31 |
| data-rerun | increment | n/a | 1 | 2 | 37.61 | operation/s | n/a | 7.81 | 500.88 |
| data-rerun | increment | n/a | 16 | 2 | 104.34 | operation/s | n/a | 2.38 | 501.67 |
| data-rerun | set | 4096 | 1 | 2 | 23.61 | operation/s | n/a | 13.94 | 462.42 |
| data-rerun | set | 4096 | 16 | 2 | 142.55 | operation/s | n/a | 2.93 | 496.91 |
| data-rerun | slimdata-set | 4096 | 1 | 2 | 12.62 | operation/s | n/a | 14.60 | 366.89 |
| data-rerun | slimdata-set | 4096 | 16 | 2 | 159.64 | operation/s | n/a | 1.92 | 433.44 |
| files-bounded-files | files | 262144 | 1 | 2 | 71.98 | operation/s | n/a | 23.28 | 333.55 |
| files-bounded-files | files | 262144 | 4 | 2 | 48.94 | operation/s | n/a | 18.76 | 378.77 |
| files-bounded-files | files | 1048576 | 1 | 2 | 41.97 | operation/s | n/a | 41.58 | 383.80 |
| files-bounded-files | files | 1048576 | 4 | 2 | 68.29 | operation/s | n/a | 33.05 | 393.06 |
| files-bounded-files | files | 4194304 | 1 | 2 | 20.48 | operation/s | n/a | 101.60 | 396.28 |
| files-bounded-files | files | 4194304 | 4 | 2 | 56.53 | operation/s | n/a | 88.37 | 397.09 |
| files-bounded-files-download | files-download | 262144 | 1 | 2 | 591.12 | operation/s | n/a | 2.13 | 333.72 |
| files-bounded-files-download | files-download | 262144 | 4 | 2 | 1143.44 | operation/s | n/a | 2.30 | 359.08 |
| files-bounded-files-download | files-download | 1048576 | 1 | 2 | 239.12 | operation/s | n/a | 5.61 | 364.03 |
| files-bounded-files-download | files-download | 1048576 | 4 | 2 | 466.57 | operation/s | n/a | 5.98 | 361.30 |
| files-bounded-files-download | files-download | 4194304 | 1 | 2 | 85.25 | operation/s | n/a | 17.08 | 364.75 |
| files-bounded-files-download | files-download | 4194304 | 4 | 2 | 136.50 | operation/s | n/a | 18.24 | 366.81 |
| files-bounded-files-unique | files-unique | 262144 | 1 | 2 | 140.44 | operation/s | n/a | 17.89 | 330.75 |
| files-bounded-files-unique | files-unique | 262144 | 4 | 2 | 56.21 | operation/s | n/a | 16.56 | 373.42 |
| files-bounded-files-unique | files-unique | 1048576 | 1 | 2 | 63.08 | operation/s | n/a | 34.29 | 380.16 |
| files-bounded-files-unique | files-unique | 1048576 | 4 | 2 | 98.79 | operation/s | n/a | 25.87 | 389.77 |
| files-bounded-files-unique | files-unique | 4194304 | 1 | 2 | 30.99 | operation/s | n/a | 89.03 | 393.36 |
| files-bounded-files-unique | files-unique | 4194304 | 4 | 2 | 102.04 | operation/s | n/a | 59.39 | 390.06 |
| files-bounded-files-upload | files-upload | 262144 | 1 | 2 | 105.33 | operation/s | n/a | 17.86 | 346.66 |
| files-bounded-files-upload | files-upload | 262144 | 4 | 2 | 44.98 | operation/s | n/a | 17.54 | 373.45 |
| files-bounded-files-upload | files-upload | 1048576 | 1 | 2 | 86.26 | operation/s | n/a | 31.84 | 380.88 |
| files-bounded-files-upload | files-upload | 1048576 | 4 | 2 | 42.65 | operation/s | n/a | 29.18 | 386.11 |
| files-bounded-files-upload | files-upload | 4194304 | 1 | 2 | 48.15 | operation/s | n/a | 78.90 | 389.14 |
| files-bounded-files-upload | files-upload | 4194304 | 4 | 2 | 89.86 | operation/s | n/a | 69.43 | 389.66 |
| files-retry | files-upload | 262144 | 1 | 2 | 106.45 | operation/s | n/a | 16.21 | 344.77 |
| jobs-rerun | jobs/http/fanout-1 | n/a | 1 | 2 | 131.27 | accepted job/s | 13.03 | 4.96 | 348.52 |
| jobs-rerun | jobs/http/fanout-1 | n/a | 16 | 2 | 1246.31 | accepted job/s | 13.09 | 0.96 | 352.75 |
| streams-rerun | streams/http/fanout-1 | 4096 | 1 | 2 | 134.28 | accepted message/s | 4.00 | 6.10 | 382.86 |
| streams-rerun | streams/http/fanout-1 | 4096 | 16 | 2 | 796.35 | accepted message/s | 4.01 | 1.93 | 442.55 |
| streams-rerun | streams/http/fanout-2 | 4096 | 1 | 2 | 98.96 | accepted message/s | 8.02 | 6.75 | 471.08 |
| streams-rerun | streams/http/fanout-2 | 4096 | 16 | 2 | 1072.85 | accepted message/s | 8.02 | 1.88 | 478.30 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 4096 | 1 | 2 | 80.74 | accepted message/s | 38.57 | 9.00 | 400.81 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 4096 | 16 | 2 | 379.11 | accepted message/s | 41.03 | 2.32 | 429.84 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 262144 | 1 | 2 | 31.16 | accepted message/s | 20.21 | 53.94 | 643.31 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 262144 | 16 | 2 | 40.31 | accepted message/s | 20.24 | 40.73 | 1224.50 |
| websocket-rerun | ws-async/python/fanout-1 | 4096 | 1 | 2 | 124.31 | accepted message/s | 52.65 | 8.12 | 956.08 |
| websocket-rerun | ws-async/python/fanout-1 | 4096 | 16 | 2 | 945.74 | accepted message/s | 40.80 | 2.38 | 925.06 |
| websocket-rerun | ws-async/python/fanout-1 | 262144 | 1 | 2 | 37.35 | accepted message/s | 20.21 | 59.11 | 987.89 |
| websocket-rerun | ws-async/python/fanout-1 | 262144 | 16 | 2 | 54.18 | accepted message/s | 40.56 | 41.07 | 1273.84 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 4096 | 1 | 2 | 192.03 | accepted message/s | 118.85 | 2.68 | 1187.81 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 4096 | 16 | 2 | 626.04 | accepted message/s | 120.31 | 2.08 | 1208.97 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 262144 | 1 | 2 | 134.79 | accepted message/s | 119.15 | 14.93 | 1206.12 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 262144 | 16 | 2 | 487.55 | accepted message/s | 117.43 | 12.13 | 1142.78 |
| websocket-rerun | ws-events/python/fanout-2 | 4096 | 1 | 2 | 1218.02 | accepted message/s | 1719.99 | 2.14 | 1187.39 |
| websocket-rerun | ws-events/python/fanout-2 | 4096 | 16 | 2 | 3062.87 | accepted message/s | 1374.57 | 1.47 | 1147.72 |
| websocket-rerun | ws-events/python/fanout-2 | 262144 | 1 | 2 | 198.49 | accepted message/s | 365.39 | 17.17 | 1147.94 |
| websocket-rerun | ws-events/python/fanout-2 | 262144 | 16 | 2 | 780.17 | accepted message/s | 801.28 | 10.13 | 1658.00 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 4096 | 1 | 2 | 110.03 | request/s | n/a | 4.21 | 340.88 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 4096 | 16 | 2 | 118.68 | request/s | n/a | 4.12 | 353.19 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 262144 | 1 | 2 | 108.32 | request/s | n/a | 10.76 | 355.94 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 262144 | 16 | 2 | 121.74 | request/s | n/a | 10.77 | 407.34 |
| websocket-rerun | ws-sync/python/fanout-1 | 4096 | 1 | 2 | 832.77 | request/s | n/a | 2.78 | 972.81 |
| websocket-rerun | ws-sync/python/fanout-1 | 4096 | 16 | 2 | 1524.99 | request/s | n/a | 2.61 | 986.42 |
| websocket-rerun | ws-sync/python/fanout-1 | 262144 | 1 | 2 | 390.78 | request/s | n/a | 11.14 | 986.48 |
| websocket-rerun | ws-sync/python/fanout-1 | 262144 | 16 | 2 | 603.32 | request/s | n/a | 9.89 | 950.25 |

Full CSV, JSON, Markdown, resource samples and raw evidence remain in the local campaign directory. The final report includes completed comparisons and identifies profiles that did not finish.

## Queue and routing microbenchmarks

Status: **queued**.

## Run status

- **jobs-rerun**: OBSERVED.
- **websocket-rerun**: OBSERVED.
- **streams-rerun**: OBSERVED.
- **data-rerun**: OBSERVED.
- **http-reference-comparison**: INCONCLUSIVE (30 GiB disk reserve).
- **files-retry**: INCONCLUSIVE (30 GiB disk reserve).
- **memory-reference-comparison**: INCONCLUSIVE (30 GiB disk reserve).
- **threshold-512kib**: deferred (30 GiB disk reserve).
- **wal-64kib**: deferred (30 GiB disk reserve).
- **files-bounded-files-upload**: OBSERVED.
- **files-bounded-files-download**: OBSERVED.
- **files-bounded-files**: OBSERVED.
- **files-bounded-files-unique**: OBSERVED.
- **http-bounded-abba**: FAIL (30 GiB disk reserve).
- **http-bounded-abba-20**: FAIL.
- **memory-abba-replay**: FAIL.
- **threshold-512kib-bounded**: FAIL (20 GiB disk reserve).
- **wal-64kib-bounded**: deferred (20 GiB disk reserve).
- **threshold-512kib-retry**: FAIL.
- **wal-64kib-retry**: FAIL.
