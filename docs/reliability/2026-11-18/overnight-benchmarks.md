# Overnight macOS benchmark campaign

Updated 2026-11-17 19:54 CET. Campaign state: **initial results and recovery**.

Product candidate: `f2f80124af9b88d0ad4bbd94653372cf393ad1b5`. The historical .NET 11 reference is `f36084643a75d803051a6918d2950d1a4f674ada`.
Both products use macOS ARM64 Native AOT. The driver runs on .NET 11.0.100.

The first campaign stopped at its legacy disk threshold. Eight Streams observations and 50 earlier data observations were retained.
The initial Jobs harness serialized the argument field with the wrong case. The local supervisor crashed while starting a Job without an argument list, so the first WebSocket stage could not register.
Subsequent measurements use the corrected Jobs request, a 30 GiB reserve, and explicit budgets for file workloads.

| Profile | Evidence status | Rows | Observed | Failed | Incomplete |
|---|---|---:|---:|---:|---:|
| data-observations | INCONCLUSIVE | 50 | 50 | 0 | 0 |
| feature-observations | INCONCLUSIVE | 8 | 8 | 0 | 0 |

An observation means that the measured run supplied the expected integrity and resource evidence. Comparative acceptance comes from the ABBA verdicts.

## Comparisons

ABBA results are pending.

## Candidate observations

Throughput and CPU are medians across the listed repetitions. RAM is the highest sampled cluster working set among those repetitions. Each row retains its payload and concurrency.

| Profile | Scenario | Bytes | C | Runs | Median rate | Unit | CPU ms/op | Peak MiB |
|---|---|---:|---:|---:|---:|---|---:|---:|
| data-observations | decrement | n/a | 1 | 2 | 40.18 | operation/s | 7.94 | 492.55 |
| data-observations | decrement | n/a | 16 | 2 | 107.94 | operation/s | 2.40 | 503.53 |
| data-observations | delete | 4096 | 1 | 2 | 238.14 | operation/s | 3.92 | 481.30 |
| data-observations | delete | 4096 | 16 | 2 | 138.91 | operation/s | 1.83 | 515.94 |
| data-observations | files-upload | 4096 | 1 | 2 | 33.97 | operation/s | 24.25 | 511.75 |
| data-observations | files-upload | 4096 | 16 | 2 | 109.24 | operation/s | 14.52 | 513.58 |
| data-observations | files-upload | 262144 | 1 | 2 | 24.90 | operation/s | 30.52 | 535.61 |
| data-observations | files-upload | 262144 | 16 | 2 | 118.89 | operation/s | 20.04 | 549.86 |
| data-observations | files-upload | 1048576 | 1 | 2 | 21.34 | operation/s | 48.69 | 549.45 |
| data-observations | files-upload | 1048576 | 16 | 2 | 55.85 | operation/s | 47.03 | 587.58 |
| data-observations | files-upload | 4194304 | 1 | 2 | 15.82 | operation/s | 96.46 | 573.42 |
| data-observations | get | 4096 | 1 | 2 | 4877.18 | operation/s | 0.54 | 460.22 |
| data-observations | get | 4096 | 16 | 2 | 19487.04 | operation/s | 0.32 | 488.28 |
| data-observations | hashdelete | 4096 | 1 | 2 | 218.57 | operation/s | 3.77 | 420.75 |
| data-observations | hashdelete | 4096 | 16 | 2 | 143.43 | operation/s | 1.96 | 478.91 |
| data-observations | hashget | 4096 | 1 | 2 | 5039.35 | operation/s | 0.51 | 577.84 |
| data-observations | hashget | 4096 | 16 | 2 | 19421.39 | operation/s | 0.32 | 444.08 |
| data-observations | hashset | 4096 | 1 | 2 | 37.70 | operation/s | 8.81 | 521.17 |
| data-observations | hashset | 4096 | 16 | 2 | 113.87 | operation/s | 2.76 | 574.45 |
| data-observations | increment | n/a | 1 | 2 | 36.92 | operation/s | 8.76 | 482.95 |
| data-observations | increment | n/a | 16 | 2 | 162.78 | operation/s | 1.55 | 488.11 |
| data-observations | set | 4096 | 1 | 2 | 27.63 | operation/s | 13.07 | 465.41 |
| data-observations | set | 4096 | 16 | 2 | 129.02 | operation/s | 3.68 | 478.31 |
| data-observations | slimdata-set | 4096 | 1 | 2 | 20.59 | operation/s | 13.23 | 361.61 |
| data-observations | slimdata-set | 4096 | 16 | 2 | 102.29 | operation/s | 3.30 | 430.41 |
| feature-observations | streams/http/fanout-1 | 4096 | 1 | 2 | 127.98 | accepted message/s | 8.02 | 368.52 |
| feature-observations | streams/http/fanout-1 | 4096 | 16 | 2 | 751.28 | accepted message/s | 2.10 | 433.19 |
| feature-observations | streams/http/fanout-2 | 4096 | 1 | 2 | 87.41 | accepted message/s | 9.59 | 456.56 |
| feature-observations | streams/http/fanout-2 | 4096 | 16 | 2 | 622.55 | accepted message/s | 1.98 | 471.06 |

Full CSV, JSON, Markdown, resource samples and raw evidence remain in the local campaign directory. The final report includes completed comparisons and identifies profiles that did not finish.
