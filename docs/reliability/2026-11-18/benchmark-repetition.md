# Independent repeat of the bounded macOS benchmark campaign

Updated 2026-11-18 07:25 CET. Campaign state: **final**.

Product candidate: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`. The .NET 11 reference is `f36084643a75d803051a6918d2950d1a4f674ada`.
Both products use macOS ARM64 Native AOT. Driver source: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`, SDK 11.0.100.

The [previous campaign](supervisor-repair-benchmarks.md) measured the same frozen binaries. Every profile in this repeat starts with fresh state.
The host reached the shutdown margin during the HTTP confirmation. A reference run in that interrupted attempt recorded 368 of 383 accepted messages within the 180-second delivery window. The incomplete attempt and its reference failure are retained separately. The resumed comparison uses the completed first block and repeats the entire confirmation block.

The disk reserve is 20 GiB, with a 1 GiB margin for stopping active workloads. Timed work is scheduled to stop by 08:45 CET.

This rerun uses fresh state and the repaired supervisor. Omitted arguments run the manifest command without additions. A job whose preparation fails follows its retry policy while other jobs continue.
The WebSocket async runner checks refreshed queue totals on all nodes after receipt reconciliation. File scenarios have an explicit byte budget.
The bounded HTTP comparison uses 5-second load windows, 1-second warmups, two repetitions, concurrency 1/16, and five payload sizes from 4 KiB through 4 MiB. It supplies a shorter ABBA comparison. Quiescent offload files are transparently compressed between roles, with their bytes and SHA-256 preserved.

| Profile | Evidence status | Rows | Observed | Failed | Incomplete |
|---|---|---:|---:|---:|---:|
| data-rerun | OBSERVED | 36 | 36 | 0 | 0 |
| files-bounded-files | OBSERVED | 12 | 12 | 0 | 0 |
| files-bounded-files-download | OBSERVED | 12 | 12 | 0 | 0 |
| files-bounded-files-unique | OBSERVED | 12 | 12 | 0 | 0 |
| files-bounded-files-upload | OBSERVED | 12 | 12 | 0 | 0 |
| http-bounded-abba-20 | FAIL | 448 | 448 | 0 | 0 |
| jobs-rerun | OBSERVED | 4 | 4 | 0 | 0 |
| memory-abba-replay | INCONCLUSIVE | 0 | 0 | 0 | 0 |
| memory-abba-replay-resumed | FAIL | 20 | 20 | 0 | 0 |
| streams-rerun | OBSERVED | 8 | 8 | 0 | 0 |
| threshold-512kib-retry | INCONCLUSIVE | 0 | 0 | 0 | 0 |
| threshold-512kib-retry-resumed | FAIL | 128 | 128 | 0 | 0 |
| wal-64kib-retry-resumed | INCONCLUSIVE | 78 | 78 | 0 | 0 |
| websocket-rerun | OBSERVED | 48 | 48 | 0 | 0 |

An observation means that the measured run supplied the expected integrity and resource evidence. Comparative acceptance comes from the ABBA verdicts.

## Receiver reconciliation

| Profile | Requests | Expected receipts | Completions | Missing | Duplicates | Corrupt |
|---|---:|---:|---:|---:|---:|---:|
| jobs-rerun | 800 | 800 | 800 | 0 | 0 | 0 |
| streams-rerun | 1600 | 2400 | 2400 | 0 | 0 | 0 |
| websocket-rerun | 9600 | 12800 | 12800 | 0 | 0 | 0 |

## Comparisons

- **memory-abba-replay-resumed / set-c12**: FAIL. 2 ABBA block(s), pair verdicts PASS, PASS, FAIL, PASS, PASS, FAIL, PASS, PASS.
  - throughput (2 pair comparisons).
- **memory-abba-replay-resumed / set-c48**: PASS. 1 ABBA block(s), pair verdicts PASS, PASS, PASS, PASS.
- **memory-abba-replay-resumed / mixed-c12**: PASS. 1 ABBA block(s), pair verdicts PASS, PASS, PASS, PASS.
- **memory-abba-replay-resumed / mixed-c48**: PASS. 1 ABBA block(s), pair verdicts PASS, PASS, PASS, PASS.

A and B refer to the first and second variants, respectively. For code comparisons, A is the reference and B is the candidate. For settings comparisons, A uses the current settings and B uses the measured change. Each table includes matched cases with evidence across all four roles of the attempt. The displayed throughput, p95 and CPU are medians across repetitions. RAM is the highest sampled cluster working set. The verdicts above use the comparison criteria for the corresponding profile.

<details><summary>http-bounded-abba-20 / http-attempt-1 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| async | 4096 | 1 | accepted message/s | 93.23 → 90.47 | 72.85 → 72.20 | 13.70 → 13.83 | 11.42 → 10.85 | 402.16 → 390.92 |
| async | 4096 | 16 | accepted message/s | 1092.96 → 1085.92 | 857.56 → 851.79 | 20.42 → 19.87 | 1.82 → 1.77 | 495.25 → 497.80 |
| async | 262144 | 1 | accepted message/s | 38.46 → 37.54 | 30.20 → 29.47 | 38.52 → 39.08 | 48.46 → 47.68 | 620.75 → 583.66 |
| async | 262144 | 16 | accepted message/s | 55.57 → 54.37 | 44.03 → 45.01 | 463.50 → 507.83 | 36.54 → 37.22 | 1639.23 → 986.73 |
| async | 1048576 | 1 | accepted message/s | 12.56 → 12.43 | 10.55 → 9.64 | 124.52 → 126.86 | 154.60 → 163.25 | 1616.39 → 831.44 |
| async | 1048576 | 16 | accepted message/s | 14.29 → 14.20 | 12.21 → 11.84 | 1546.50 → 1547.70 | 131.38 → 130.82 | 3317.12 → 1820.94 |
| async | 1048577 | 1 | accepted message/s | 44.76 → 41.85 | 38.09 → 35.61 | 26.31 → 27.79 | 42.13 → 45.63 | 3157.53 → 1752.00 |
| async | 1048577 | 16 | accepted message/s | 286.16 → 281.19 | 240.63 → 234.58 | 74.82 → 74.17 | 18.04 → 20.21 | 3087.81 → 1132.89 |
| async | 4194304 | 1 | accepted message/s | 36.31 → 35.63 | 30.70 → 29.27 | 30.23 → 30.56 | 93.07 → 97.18 | 2001.05 → 625.89 |
| async | 4194304 | 16 | accepted message/s | 95.68 → 96.50 | 79.30 → 79.24 | 214.32 → 215.09 | 63.00 → 64.66 | 1695.31 → 537.83 |
| async-burst | 4096 | 64 | accepted message/s | 2167.32 → 2075.08 | 1209.02 → 532.73 | 46.70 → 52.16 | 1.17 → 1.50 | 1455.34 → 604.44 |
| async-paced | 4096 | 1 | accepted message/s | 8.93 → 9.07 | 8.27 → 8.22 | 16.06 → 14.10 | 47.09 → 47.60 | 1443.33 → 582.05 |
| direct | 4096 | 1 | request/s | 10152.08 → 10265.30 | n/a → n/a | 0.11 → 0.11 | 0.01 → 0.01 | 389.56 → 382.06 |
| direct | 4096 | 16 | request/s | 23169.90 → 23180.41 | n/a → n/a | 0.74 → 0.74 | 0.00 → 0.00 | 489.59 → 485.62 |
| direct | 262144 | 1 | request/s | 2706.47 → 2820.69 | n/a → n/a | 0.44 → 0.42 | 0.03 → 0.03 | 604.80 → 572.00 |
| direct | 262144 | 16 | request/s | 6053.41 → 6055.87 | n/a → n/a | 2.81 → 2.79 | 0.01 → 0.01 | 1419.92 → 875.88 |
| direct | 1048576 | 1 | request/s | 806.93 → 611.39 | n/a → n/a | 1.71 → 2.08 | 0.10 → 0.13 | 1624.08 → 847.20 |
| direct | 1048576 | 16 | request/s | 1828.03 → 1850.19 | n/a → n/a | 9.32 → 9.24 | 0.05 → 0.05 | 2850.03 → 1781.75 |
| direct | 1048577 | 1 | request/s | 574.30 → 554.90 | n/a → n/a | 2.08 → 2.09 | 0.14 → 0.14 | 3118.69 → 1741.31 |
| direct | 1048577 | 16 | request/s | 1847.58 → 1872.74 | n/a → n/a | 9.21 → 9.21 | 0.05 → 0.05 | 3086.77 → 1354.41 |
| direct | 4194304 | 1 | request/s | 225.76 → 228.95 | n/a → n/a | 7.08 → 7.02 | 0.39 → 0.39 | 1997.45 → 634.94 |
| direct | 4194304 | 16 | request/s | 462.38 → 468.33 | n/a → n/a | 38.08 → 36.75 | 0.20 → 0.24 | 1649.27 → 528.09 |
| sync | 4096 | 1 | request/s | 3052.39 → 3053.92 | n/a → n/a | 0.36 → 0.36 | 0.52 → 0.52 | 389.12 → 383.25 |
| sync | 4096 | 16 | request/s | 7760.60 → 7821.72 | n/a → n/a | 2.38 → 2.37 | 0.47 → 0.46 | 497.36 → 504.77 |
| sync | 262144 | 1 | request/s | 1178.68 → 1194.47 | n/a → n/a | 0.95 → 0.93 | 1.67 → 1.64 | 605.09 → 568.75 |
| sync | 262144 | 16 | request/s | 1612.27 → 1619.23 | n/a → n/a | 10.42 → 10.42 | 1.90 → 1.90 | 1495.23 → 944.05 |
| sync | 1048576 | 1 | request/s | 386.83 → 389.22 | n/a → n/a | 2.78 → 2.78 | 5.29 → 5.29 | 1634.14 → 811.16 |
| sync | 1048576 | 16 | request/s | 449.45 → 453.95 | n/a → n/a | 37.04 → 36.63 | 6.74 → 6.65 | 2865.19 → 1779.73 |
| sync | 1048577 | 1 | request/s | 386.31 → 386.66 | n/a → n/a | 2.79 → 2.79 | 5.20 → 5.16 | 3125.42 → 1745.86 |
| sync | 1048577 | 16 | request/s | 450.84 → 453.81 | n/a → n/a | 36.83 → 36.70 | 6.77 → 6.69 | 3087.70 → 1355.27 |
| sync | 4194304 | 1 | request/s | 105.60 → 105.16 | n/a → n/a | 10.20 → 10.22 | 19.50 → 19.21 | 1997.48 → 637.75 |
| sync | 4194304 | 16 | request/s | 113.29 → 113.33 | n/a → n/a | 151.59 → 147.16 | 25.58 → 25.50 | 1653.11 → 528.31 |

</details>

<details><summary>memory-abba-replay-resumed / mixed-c12-attempt-1 (PASS)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| slimdata-mixed | n/a | 12 | operation/s | 438.37 → 441.61 | n/a → n/a | 92.19 → 90.94 | 1.68 → 1.71 | 416.42 → 447.38 |

</details>

<details><summary>memory-abba-replay-resumed / mixed-c48-attempt-1 (PASS)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| slimdata-mixed | n/a | 48 | operation/s | 2665.47 → 2592.87 | n/a → n/a | 26.10 → 26.21 | 0.85 → 0.83 | 576.56 → 595.88 |

</details>

<details><summary>memory-abba-replay-resumed / set-c12-attempt-1 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| slimdata-set | 4096 | 12 | operation/s | 103.06 → 105.33 | n/a → n/a | 249.39 → 249.36 | 3.72 → 3.58 | 497.14 → 502.92 |

</details>

<details><summary>memory-abba-replay-resumed / set-c12-attempt-2 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| slimdata-set | 4096 | 12 | operation/s | 114.88 → 114.15 | n/a → n/a | 244.32 → 247.72 | 3.57 → 3.49 | 503.64 → 499.50 |

</details>

<details><summary>memory-abba-replay-resumed / set-c48-attempt-1 (PASS)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| slimdata-set | 4096 | 48 | operation/s | 1035.30 → 1046.75 | n/a → n/a | 95.89 → 91.82 | 1.15 → 1.07 | 491.72 → 507.75 |

</details>

<details><summary>threshold-512kib-retry-resumed / http-attempt-1 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| async | 524287 | 1 | accepted message/s | 23.46 → 24.19 | 16.58 → 17.08 | 66.71 → 64.78 | 92.10 → 88.32 | 579.14 → 576.62 |
| async | 524287 | 16 | accepted message/s | 29.38 → 29.56 | 22.80 → 23.49 | 863.46 → 858.07 | 65.81 → 65.04 | 1255.83 → 1194.73 |
| async | 524288 | 1 | accepted message/s | 23.45 → 23.56 | 16.10 → 16.21 | 69.21 → 66.89 | 87.68 → 80.74 | 1003.12 → 968.39 |
| async | 524288 | 16 | accepted message/s | 29.54 → 30.58 | 22.98 → 23.71 | 885.99 → 886.49 | 65.37 → 64.18 | 1250.98 → 1242.77 |
| async | 524289 | 1 | accepted message/s | 24.30 → 50.20 | 16.75 → 33.51 | 68.17 → 21.44 | 84.42 → 32.82 | 1042.06 → 1125.72 |
| async | 524289 | 16 | accepted message/s | 27.14 → 379.18 | 22.00 → 260.89 | 937.87 → 55.99 | 73.83 → 17.00 | 1255.39 → 876.27 |
| async | 1048576 | 1 | accepted message/s | 13.07 → 43.67 | 8.49 → 29.15 | 121.69 → 26.48 | 163.56 → 49.17 | 1122.41 → 691.34 |
| async | 1048576 | 16 | accepted message/s | 15.42 → 291.54 | 11.59 → 189.66 | 1577.79 → 72.51 | 127.43 → 23.10 | 1642.66 → 561.52 |
| async | 1048577 | 1 | accepted message/s | 43.02 → 42.16 | 35.71 → 34.98 | 27.31 → 27.95 | 46.28 → 48.63 | 1501.27 → 554.95 |
| async | 1048577 | 16 | accepted message/s | 290.37 → 280.32 | 220.63 → 192.93 | 72.37 → 77.35 | 21.27 → 23.15 | 1276.20 → 542.17 |
| async-burst | 4096 | 64 | accepted message/s | 2264.55 → 2274.64 | 548.13 → 595.02 | 43.88 → 46.29 | 1.37 → 1.56 | 1064.16 → 578.69 |
| async-paced | 4096 | 1 | accepted message/s | 8.96 → 9.12 | 8.23 → 8.28 | 14.50 → 13.41 | 40.18 → 38.33 | 1057.23 → 542.34 |
| direct | 524287 | 1 | request/s | 1571.31 → 1438.56 | n/a → n/a | 0.97 → 1.10 | 0.05 → 0.06 | 316.03 → 315.33 |
| direct | 524287 | 16 | request/s | 3549.34 → 3545.53 | n/a → n/a | 4.81 → 4.81 | 0.02 → 0.02 | 600.44 → 569.69 |
| direct | 524288 | 1 | request/s | 1614.80 → 1601.86 | n/a → n/a | 0.83 → 0.81 | 0.05 → 0.06 | 1230.62 → 1194.83 |
| direct | 524288 | 16 | request/s | 3534.82 → 3586.73 | n/a → n/a | 4.81 → 4.77 | 0.02 → 0.02 | 789.50 → 749.89 |
| direct | 524289 | 1 | request/s | 1604.42 → 1588.93 | n/a → n/a | 0.87 → 0.90 | 0.05 → 0.06 | 1250.64 → 1242.78 |
| direct | 524289 | 16 | request/s | 3573.75 → 3565.26 | n/a → n/a | 4.79 → 4.79 | 0.02 → 0.03 | 802.05 → 1027.41 |
| direct | 1048576 | 1 | request/s | 527.33 → 552.05 | n/a → n/a | 2.23 → 2.18 | 0.17 → 0.16 | 1256.08 → 746.56 |
| direct | 1048576 | 16 | request/s | 1841.84 → 1847.02 | n/a → n/a | 9.20 → 9.21 | 0.05 → 0.05 | 908.14 → 612.38 |
| direct | 1048577 | 1 | request/s | 565.32 → 695.67 | n/a → n/a | 2.14 → 2.04 | 0.13 → 0.13 | 1589.83 → 524.66 |
| direct | 1048577 | 16 | request/s | 1868.98 → 1876.52 | n/a → n/a | 9.11 → 9.10 | 0.05 → 0.05 | 1409.78 → 537.62 |
| sync | 524287 | 1 | request/s | 702.73 → 697.75 | n/a → n/a | 1.56 → 1.59 | 2.93 → 2.84 | 344.62 → 343.75 |
| sync | 524287 | 16 | request/s | 864.81 → 872.35 | n/a → n/a | 19.42 → 19.14 | 3.54 → 3.58 | 596.14 → 566.20 |
| sync | 524288 | 1 | request/s | 702.52 → 697.88 | n/a → n/a | 1.56 → 1.58 | 2.92 → 2.94 | 1232.34 → 1207.06 |
| sync | 524288 | 16 | request/s | 860.07 → 870.16 | n/a → n/a | 19.34 → 19.14 | 3.56 → 3.58 | 788.16 → 747.77 |
| sync | 524289 | 1 | request/s | 698.66 → 696.67 | n/a → n/a | 1.57 → 1.58 | 2.91 → 2.95 | 1259.30 → 1159.53 |
| sync | 524289 | 16 | request/s | 862.99 → 883.39 | n/a → n/a | 19.44 → 18.91 | 3.57 → 3.57 | 798.56 → 1096.91 |
| sync | 1048576 | 1 | request/s | 383.75 → 386.89 | n/a → n/a | 2.84 → 2.81 | 5.26 → 5.37 | 1257.66 → 746.62 |
| sync | 1048576 | 16 | request/s | 451.48 → 452.97 | n/a → n/a | 36.84 → 36.58 | 6.75 → 6.84 | 907.45 → 611.84 |
| sync | 1048577 | 1 | request/s | 385.54 → 386.20 | n/a → n/a | 2.81 → 2.81 | 5.38 → 5.31 | 1596.53 → 535.86 |
| sync | 1048577 | 16 | request/s | 459.53 → 451.76 | n/a → n/a | 36.43 → 36.73 | 6.76 → 6.79 | 1485.05 → 537.56 |

</details>


## Candidate observations

Throughput and CPU are medians across the listed repetitions. RAM is the highest sampled cluster working set among those repetitions during the load window. For Streams and Jobs, accepted throughput covers submission and completed throughput covers submission and drain. The table CPU and RAM values cover submission. Drain resources remain in the raw reports.

| Profile | Scenario | Bytes | C | Runs | Median rate | Unit | Completed/s incl. drain | CPU ms/op | Peak MiB |
|---|---|---:|---:|---:|---:|---|---:|---:|---:|
| data-rerun | decrement | n/a | 1 | 2 | 39.37 | operation/s | n/a | 8.63 | 498.02 |
| data-rerun | decrement | n/a | 16 | 2 | 119.77 | operation/s | n/a | 2.13 | 502.88 |
| data-rerun | delete | 4096 | 1 | 2 | 212.27 | operation/s | n/a | 4.73 | 503.02 |
| data-rerun | delete | 4096 | 16 | 2 | 129.52 | operation/s | n/a | 2.06 | 492.94 |
| data-rerun | get | 4096 | 1 | 2 | 4813.39 | operation/s | n/a | 0.55 | 452.92 |
| data-rerun | get | 4096 | 16 | 2 | 19991.84 | operation/s | n/a | 0.33 | 485.30 |
| data-rerun | hashdelete | 4096 | 1 | 2 | 218.22 | operation/s | n/a | 4.46 | 415.66 |
| data-rerun | hashdelete | 4096 | 16 | 2 | 158.37 | operation/s | n/a | 1.85 | 473.05 |
| data-rerun | hashget | 4096 | 1 | 2 | 4924.99 | operation/s | n/a | 0.53 | 587.12 |
| data-rerun | hashget | 4096 | 16 | 2 | 19633.99 | operation/s | n/a | 0.31 | 456.59 |
| data-rerun | hashset | 4096 | 1 | 2 | 40.43 | operation/s | n/a | 8.74 | 503.42 |
| data-rerun | hashset | 4096 | 16 | 2 | 98.23 | operation/s | n/a | 3.12 | 531.50 |
| data-rerun | increment | n/a | 1 | 2 | 39.88 | operation/s | n/a | 8.42 | 474.44 |
| data-rerun | increment | n/a | 16 | 2 | 105.50 | operation/s | n/a | 2.24 | 486.45 |
| data-rerun | set | 4096 | 1 | 2 | 34.41 | operation/s | n/a | 11.38 | 473.94 |
| data-rerun | set | 4096 | 16 | 2 | 140.08 | operation/s | n/a | 3.43 | 496.80 |
| data-rerun | slimdata-set | 4096 | 1 | 2 | 12.88 | operation/s | n/a | 16.69 | 358.52 |
| data-rerun | slimdata-set | 4096 | 16 | 2 | 112.42 | operation/s | n/a | 2.92 | 447.27 |
| files-bounded-files | files | 262144 | 1 | 2 | 71.10 | operation/s | n/a | 21.86 | 337.91 |
| files-bounded-files | files | 262144 | 4 | 2 | 49.90 | operation/s | n/a | 18.45 | 378.23 |
| files-bounded-files | files | 1048576 | 1 | 2 | 39.07 | operation/s | n/a | 46.29 | 388.94 |
| files-bounded-files | files | 1048576 | 4 | 2 | 132.96 | operation/s | n/a | 30.99 | 397.94 |
| files-bounded-files | files | 4194304 | 1 | 2 | 21.37 | operation/s | n/a | 100.82 | 401.73 |
| files-bounded-files | files | 4194304 | 4 | 2 | 44.26 | operation/s | n/a | 92.33 | 401.47 |
| files-bounded-files-download | files-download | 262144 | 1 | 2 | 534.73 | operation/s | n/a | 2.91 | 336.72 |
| files-bounded-files-download | files-download | 262144 | 4 | 2 | 1132.54 | operation/s | n/a | 2.36 | 356.34 |
| files-bounded-files-download | files-download | 1048576 | 1 | 2 | 267.66 | operation/s | n/a | 5.31 | 360.69 |
| files-bounded-files-download | files-download | 1048576 | 4 | 2 | 350.01 | operation/s | n/a | 5.64 | 361.70 |
| files-bounded-files-download | files-download | 4194304 | 1 | 2 | 75.68 | operation/s | n/a | 16.97 | 363.58 |
| files-bounded-files-download | files-download | 4194304 | 4 | 2 | 140.52 | operation/s | n/a | 17.97 | 364.19 |
| files-bounded-files-unique | files-unique | 262144 | 1 | 2 | 137.42 | operation/s | n/a | 15.97 | 330.61 |
| files-bounded-files-unique | files-unique | 262144 | 4 | 2 | 55.57 | operation/s | n/a | 15.95 | 375.00 |
| files-bounded-files-unique | files-unique | 1048576 | 1 | 2 | 53.43 | operation/s | n/a | 34.87 | 379.23 |
| files-bounded-files-unique | files-unique | 1048576 | 4 | 2 | 125.29 | operation/s | n/a | 25.03 | 384.44 |
| files-bounded-files-unique | files-unique | 4194304 | 1 | 2 | 26.91 | operation/s | n/a | 84.74 | 387.47 |
| files-bounded-files-unique | files-unique | 4194304 | 4 | 2 | 105.77 | operation/s | n/a | 56.15 | 388.66 |
| files-bounded-files-upload | files-upload | 262144 | 1 | 2 | 105.64 | operation/s | n/a | 16.03 | 345.84 |
| files-bounded-files-upload | files-upload | 262144 | 4 | 2 | 39.95 | operation/s | n/a | 17.52 | 382.08 |
| files-bounded-files-upload | files-upload | 1048576 | 1 | 2 | 89.70 | operation/s | n/a | 31.65 | 385.80 |
| files-bounded-files-upload | files-upload | 1048576 | 4 | 2 | 43.08 | operation/s | n/a | 30.05 | 396.59 |
| files-bounded-files-upload | files-upload | 4194304 | 1 | 2 | 41.77 | operation/s | n/a | 80.41 | 398.56 |
| files-bounded-files-upload | files-upload | 4194304 | 4 | 2 | 94.89 | operation/s | n/a | 64.15 | 399.81 |
| jobs-rerun | jobs/http/fanout-1 | n/a | 1 | 2 | 140.98 | accepted job/s | 13.15 | 5.29 | 338.91 |
| jobs-rerun | jobs/http/fanout-1 | n/a | 16 | 2 | 1403.81 | accepted job/s | 13.16 | 1.00 | 341.44 |
| streams-rerun | streams/http/fanout-1 | 4096 | 1 | 2 | 122.96 | accepted message/s | 4.01 | 7.62 | 378.91 |
| streams-rerun | streams/http/fanout-1 | 4096 | 16 | 2 | 606.54 | accepted message/s | 4.00 | 2.03 | 443.02 |
| streams-rerun | streams/http/fanout-2 | 4096 | 1 | 2 | 94.94 | accepted message/s | 7.99 | 9.18 | 467.08 |
| streams-rerun | streams/http/fanout-2 | 4096 | 16 | 2 | 570.47 | accepted message/s | 7.99 | 2.10 | 485.27 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 4096 | 1 | 2 | 94.83 | accepted message/s | 37.03 | 7.52 | 401.88 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 4096 | 16 | 2 | 450.29 | accepted message/s | 40.89 | 1.80 | 428.92 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 262144 | 1 | 2 | 34.29 | accepted message/s | 20.12 | 44.44 | 538.75 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 262144 | 16 | 2 | 45.86 | accepted message/s | 20.18 | 34.20 | 868.25 |
| websocket-rerun | ws-async/python/fanout-1 | 4096 | 1 | 2 | 114.15 | accepted message/s | 41.04 | 8.38 | 936.48 |
| websocket-rerun | ws-async/python/fanout-1 | 4096 | 16 | 2 | 1004.53 | accepted message/s | 40.83 | 1.63 | 917.94 |
| websocket-rerun | ws-async/python/fanout-1 | 262144 | 1 | 2 | 42.46 | accepted message/s | 20.10 | 50.22 | 967.78 |
| websocket-rerun | ws-async/python/fanout-1 | 262144 | 16 | 2 | 60.58 | accepted message/s | 41.77 | 38.41 | 1108.94 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 4096 | 1 | 2 | 222.05 | accepted message/s | 125.28 | 2.54 | 862.78 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 4096 | 16 | 2 | 688.43 | accepted message/s | 128.02 | 1.94 | 885.69 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 262144 | 1 | 2 | 127.35 | accepted message/s | 127.89 | 12.83 | 885.67 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 262144 | 16 | 2 | 436.58 | accepted message/s | 127.97 | 11.42 | 850.42 |
| websocket-rerun | ws-events/python/fanout-2 | 4096 | 1 | 2 | 1307.98 | accepted message/s | 1798.63 | 2.10 | 1038.88 |
| websocket-rerun | ws-events/python/fanout-2 | 4096 | 16 | 2 | 3026.69 | accepted message/s | 1362.94 | 1.59 | 982.02 |
| websocket-rerun | ws-events/python/fanout-2 | 262144 | 1 | 2 | 204.67 | accepted message/s | 374.83 | 17.39 | 900.48 |
| websocket-rerun | ws-events/python/fanout-2 | 262144 | 16 | 2 | 814.51 | accepted message/s | 817.22 | 10.08 | 1368.23 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 4096 | 1 | 2 | 121.48 | request/s | n/a | 3.97 | 333.31 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 4096 | 16 | 2 | 128.71 | request/s | n/a | 4.15 | 339.31 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 262144 | 1 | 2 | 113.73 | request/s | n/a | 11.51 | 349.28 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 262144 | 16 | 2 | 129.20 | request/s | n/a | 11.31 | 408.27 |
| websocket-rerun | ws-sync/python/fanout-1 | 4096 | 1 | 2 | 781.26 | request/s | n/a | 2.85 | 891.94 |
| websocket-rerun | ws-sync/python/fanout-1 | 4096 | 16 | 2 | 1422.17 | request/s | n/a | 2.65 | 913.81 |
| websocket-rerun | ws-sync/python/fanout-1 | 262144 | 1 | 2 | 410.40 | request/s | n/a | 10.91 | 913.98 |
| websocket-rerun | ws-sync/python/fanout-1 | 262144 | 16 | 2 | 609.77 | request/s | n/a | 9.88 | 925.80 |

Full CSV, JSON, Markdown, resource samples and raw evidence remain in the local campaign directory. The final report includes completed comparisons and identifies profiles that did not finish.

## Queue and routing microbenchmarks

Status: **deferred**.

Less than 30 minutes or insufficient disk space.

## Run status

- **jobs-rerun**: OBSERVED.
- **websocket-rerun**: OBSERVED.
- **streams-rerun**: OBSERVED.
- **data-rerun**: OBSERVED.
- **files-bounded-files-upload**: OBSERVED.
- **files-bounded-files-download**: OBSERVED.
- **files-bounded-files**: OBSERVED.
- **files-bounded-files-unique**: OBSERVED.
- **http-bounded-abba-20**: FAIL (20 GiB disk reserve).
- **memory-abba-replay**: INCONCLUSIVE (20 GiB disk reserve).
- **threshold-512kib-retry**: INCONCLUSIVE (20 GiB disk reserve).
- **wal-64kib-retry**: deferred (20 GiB disk reserve).
- **http-bounded-abba-20-resumed**: FAIL (20 GiB disk reserve).
- **memory-abba-replay-resumed**: FAIL.
- **threshold-512kib-retry-resumed**: FAIL (20 GiB disk reserve).
- **wal-64kib-retry-resumed**: INCONCLUSIVE (20 GiB disk reserve).
