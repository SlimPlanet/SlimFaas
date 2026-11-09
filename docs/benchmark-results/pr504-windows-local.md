# PR 504 Windows local runtime attempts

Every fixed planned attempt is included. Runtime-node CPU/RSS exclude the driver, target and supervisor. GC and I/O deltas cover the first-to-last sampled window. Windows process I/O includes cached and network transfers; it is not physical disk traffic. Failed file attempts have invalid rates because workers stop on their first error. Two repetitions give limited timing precision.

[Full summaries and manifests](pr504-windows-local.json) · [Original evidence, receipts, logs and sources](pr504-windows-evidence.zip)

[Error-to-delivery analysis with source](pr504-windows-async-errors.json) joins all 23 in-window body/metadata error lines to 22 distinct queue IDs, all observed at the target. The five file-not-found warnings in the final fixed variants occur after their target receipts. These warnings do not explain the 52 unobserved deliveries. All 8,859 broad quorum/leadership matches are Debug uncommitted-index messages; no explicit leader-change/consensus-failure line occurs in the observation windows. This does not prove continuous quorum or reproduce the original M4 interruption.

## Byte-verified files

| Campaign | Attempt | IDs | Result | Verified cycles | Cycles/s | Upload p95/p99 ms | Download p95/p99 ms | CPU ms/cycle | Peak RSS MiB | GC 0/1/2 | Sampled I/O MiB read/write |
|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| pr504-files-streams | round-1-baseline-aot | reused | FAIL | 418 | invalid | 393.65/480.32 | 318.38/518.31 | 74.84 | 224.5 | 61/56/53 | 249.1/252.7 |
| pr504-files-streams | round-1-baseline-aot | unique | pass | 589 | 28.86 | 493.52/645.15 | 433.65/585.65 | 60.83 | 242.6 | 55/52/52 | 372.9/374.4 |
| pr504-files-streams | round-1-baseline-jit | reused | FAIL | 582 | invalid | 273.39/341.86 | 179.09/365.02 | 109.64 | 414.8 | 106/101/101 | 393.4/396.8 |
| pr504-files-streams | round-1-baseline-jit | unique | pass | 709 | 34.43 | 524.10/738.99 | 341.84/638.02 | 73.34 | 438.6 | 100/93/93 | 488.1/489.4 |
| pr504-files-streams | round-1-candidate-aot | reused | FAIL | 498 | invalid | 283.93/382.06 | 189.10/311.52 | 52.80 | 225.5 | 73/69/67 | 330.2/333.7 |
| pr504-files-streams | round-1-candidate-aot | unique | pass | 704 | 34.16 | 524.58/609.72 | 346.53/477.77 | 49.80 | 231.3 | 88/83/83 | 458.8/461.8 |
| pr504-files-streams | round-1-candidate-jit | reused | FAIL | 485 | invalid | 341.83/391.07 | 203.33/318.83 | 123.36 | 428.8 | 90/86/86 | 327.5/331.0 |
| pr504-files-streams | round-1-candidate-jit | unique | pass | 762 | 36.99 | 488.17/579.89 | 311.01/582.34 | 65.45 | 444.3 | 109/103/103 | 514.6/515.0 |
| pr504-files-streams | round-2-baseline-aot | reused | FAIL | 289 | invalid | 386.46/432.08 | 346.79/710.59 | 70.72 | 229.9 | 37/33/30 | 158.7/162.4 |
| pr504-files-streams | round-2-baseline-aot | unique | pass | 581 | 28.42 | 470.97/575.73 | 434.16/560.46 | 59.76 | 240.9 | 55/52/52 | 365.6/366.1 |
| pr504-files-streams | round-2-baseline-jit | reused | FAIL | 565 | invalid | 547.74/646.99 | 519.60/1274.71 | 104.07 | 405.4 | 58/55/55 | 287.2/288.4 |
| pr504-files-streams | round-2-baseline-jit | unique | pass | 524 | 25.85 | 476.41/540.29 | 669.01/1354.66 | 92.11 | 409.4 | 66/60/60 | 339.9/339.7 |
| pr504-files-streams | round-2-candidate-aot | reused | FAIL | 388 | invalid | 304.08/497.18 | 287.26/473.71 | 74.02 | 232.0 | 49/45/42 | 226.8/230.4 |
| pr504-files-streams | round-2-candidate-aot | unique | pass | 531 | 25.86 | 503.59/619.98 | 479.82/669.02 | 62.12 | 246.1 | 50/46/46 | 328.3/329.7 |
| pr504-files-streams | round-2-candidate-jit | reused | FAIL | 518 | invalid | 441.74/513.29 | 439.02/1056.70 | 111.61 | 407.4 | 57/54/54 | 269.4/270.9 |
| pr504-files-streams | round-2-candidate-jit | unique | pass | 566 | 27.79 | 460.75/534.16 | 564.93/1545.64 | 93.50 | 410.0 | 65/59/59 | 338.5/341.6 |
| pr504-files-fixed | round-1-candidate-aot | reused | pass | 552 | 27.10 | 483.36/620.39 | 441.59/572.82 | 83.05 | 231.9 | 56/49/46 | 281.0/282.8 |
| pr504-files-fixed | round-1-candidate-aot | unique | pass | 555 | 27.17 | 528.04/616.48 | 482.16/600.66 | 61.32 | 234.6 | 52/49/49 | 340.4/340.9 |
| pr504-files-fixed | round-1-candidate-jit | reused | FAIL | 611 | invalid | 510.40/617.93 | 279.61/396.99 | 109.27 | 412.2 | 83/77/77 | 382.9/383.9 |
| pr504-files-fixed | round-1-candidate-jit | unique | pass | 593 | 29.05 | 543.00/663.80 | 424.64/694.08 | 102.18 | 455.3 | 63/58/58 | 401.9/401.5 |
| pr504-files-fixed | round-2-candidate-aot | reused | pass | 567 | 27.28 | 482.87/644.19 | 491.49/654.87 | 89.09 | 224.7 | 74/68/66 | 304.6/304.7 |
| pr504-files-fixed | round-2-candidate-aot | unique | pass | 563 | 27.64 | 442.96/544.92 | 547.10/740.72 | 63.78 | 231.0 | 71/67/67 | 347.6/347.8 |
| pr504-files-fixed | round-2-candidate-jit | reused | pass | 530 | 25.97 | 563.68/1036.43 | 533.62/985.86 | 124.94 | 390.8 | 74/70/70 | 286.0/289.4 |
| pr504-files-fixed | round-2-candidate-jit | unique | pass | 529 | 25.00 | 518.97/607.98 | 522.48/857.75 | 105.68 | 410.8 | 59/53/53 | 325.8/326.3 |
| pr504-files-final | round-1-candidate-aot | reused | pass | 728 | 35.63 | 483.87/569.93 | 313.04/428.25 | 62.99 | 227.7 | 87/81/78 | 414.8/416.5 |
| pr504-files-final | round-1-candidate-aot | unique | pass | 551 | 25.98 | 486.92/567.48 | 525.50/771.91 | 66.73 | 232.5 | 67/63/63 | 347.2/347.2 |
| pr504-files-final | round-1-candidate-jit | reused | pass | 658 | 32.40 | 534.15/590.72 | 297.94/390.65 | 105.39 | 436.8 | 70/66/66 | 383.7/386.2 |
| pr504-files-final | round-1-candidate-jit | unique | pass | 701 | 34.37 | 473.32/587.01 | 364.44/676.34 | 73.67 | 452.8 | 76/69/69 | 462.9/463.5 |
| pr504-files-final | round-2-candidate-aot | reused | pass | 545 | 26.66 | 528.44/629.05 | 480.29/593.36 | 86.67 | 226.5 | 62/58/55 | 279.4/282.9 |
| pr504-files-final | round-2-candidate-aot | unique | pass | 588 | 28.79 | 499.69/609.70 | 466.82/617.97 | 62.90 | 234.1 | 69/64/64 | 354.3/354.4 |
| pr504-files-final | round-2-candidate-jit | reused | pass | 479 | 17.77 | 706.51/1078.56 | 522.03/843.48 | 173.77 | 413.8 | 63/60/60 | 268.9/268.7 |
| pr504-files-final | round-2-candidate-jit | unique | pass | 553 | 26.63 | 477.70/557.90 | 657.73/2028.54 | 93.58 | 437.8 | 73/67/67 | 351.3/351.7 |

## Sets (4 KiB, 16 workers, 60 seconds)

| Campaign | Attempt | Completed | Failed | Op/s | p95/p99 ms | CPU ms/op | Peak RSS MiB | GC 0/1/2 | Sampled I/O MiB read/write |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| pr504-sets-streams | round-1-baseline-aot | 6393 | 0 | 105.58 | 320.81/535.74 | 9.66 | 440.9 | 47/15/12 | 74.5/81.2 |
| pr504-sets-streams | round-1-baseline-jit | 6726 | 0 | 112.01 | 323.16/447.47 | 16.71 | 634.6 | 51/17/13 | 75.6/82.3 |
| pr504-sets-streams | round-1-candidate-aot | 6493 | 0 | 107.39 | 333.21/370.01 | 8.89 | 440.8 | 52/18/12 | 76.1/82.8 |
| pr504-sets-streams | round-1-candidate-jit | 7181 | 0 | 119.56 | 321.99/434.50 | 16.19 | 653.1 | 50/19/14 | 83.2/89.6 |
| pr504-sets-streams | round-2-baseline-aot | 7194 | 0 | 119.57 | 316.54/551.83 | 9.65 | 437.8 | 56/20/16 | 84.7/91.4 |
| pr504-sets-streams | round-2-baseline-jit | 6652 | 0 | 110.34 | 319.47/383.75 | 16.14 | 643.2 | 50/18/14 | 76.2/82.9 |
| pr504-sets-streams | round-2-candidate-aot | 5019 | 0 | 83.62 | 312.72/589.66 | 10.86 | 396.9 | 34/6/3 | 46.8/53.1 |
| pr504-sets-streams | round-2-candidate-jit | 7151 | 0 | 119.11 | 309.29/458.94 | 14.97 | 639.9 | 55/21/15 | 82.1/88.8 |
| pr504-sets-headroom | round-1-baseline-aot | 7594 | 0 | 126.51 | 315.51/643.32 | 9.23 | 441.8 | 55/18/16 | 90.8/97.5 |
| pr504-sets-headroom | round-1-baseline-jit | 7065 | 0 | 117.43 | 308.74/470.45 | 14.82 | 658.9 | 50/21/15 | 83.4/90.1 |
| pr504-sets-headroom | round-1-candidate-aot | 6127 | 0 | 100.92 | 334.82/635.21 | 9.67 | 437.8 | 44/15/11 | 71.1/77.8 |
| pr504-sets-headroom | round-1-candidate-jit | 6922 | 0 | 115.33 | 297.64/471.25 | 15.60 | 625.2 | 52/20/16 | 78.6/85.3 |
| pr504-sets-headroom | round-2-baseline-aot | 6260 | 0 | 103.55 | 312.18/657.30 | 9.41 | 437.6 | 46/17/12 | 71.9/78.4 |
| pr504-sets-headroom | round-2-baseline-jit | 7475 | 0 | 124.54 | 296.62/435.19 | 15.04 | 642.5 | 58/25/18 | 87.5/93.8 |
| pr504-sets-headroom | round-2-candidate-aot | 5569 | 0 | 91.75 | 313.72/526.44 | 9.49 | 425.0 | 44/14/9 | 63.5/70.2 |
| pr504-sets-headroom | round-2-candidate-jit | 6631 | 0 | 110.31 | 319.56/457.38 | 15.54 | 621.0 | 49/16/13 | 72.9/79.6 |

## Streams (200 publications, 4 KiB, 16 publishers)

Append latency measures HTTP acceptance. Target arrival latency measures delivery from the sender timestamp; it includes the dispatcher backlog. Successful bursts require 200 unique target IDs, no duplicates and committed progress with zero lag/pending.

| Attempt | Unique/duplicates | Lag/pending | Publications/s | Append p95/p99 ms | Delivery p95/p99 ms | CPU ms/publication | Peak RSS MiB | GC 0/1/2 | Sampled I/O MiB read/write |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| round-1-baseline-aot | 200/0 | 0/0 | 3.979 | 118.95/132.21 | 46527.68/48430.02 | 66.33 | 392.1 | 3/0/0 | 2.0/2.1 |
| round-1-baseline-jit | 200/0 | 0/0 | 3.981 | 114.01/141.05 | 46561.19/48501.66 | 196.80 | 605.9 | 5/1/1 | 2.1/11.4 |
| round-1-candidate-aot | 200/0 | 0/0 | 3.987 | 105.31/120.68 | 46448.49/48407.47 | 63.52 | 377.1 | 4/2/1 | 2.6/2.7 |
| round-1-candidate-jit | 200/0 | 0/0 | 3.985 | 107.12/149.58 | 46543.24/48467.84 | 156.02 | 619.1 | 5/1/1 | 2.8/12.2 |
| round-2-baseline-aot | 200/0 | 0/0 | 3.992 | 101.99/111.57 | 46450.88/48399.42 | 64.69 | 366.5 | 4/2/1 | 2.8/2.9 |
| round-2-baseline-jit | 200/0 | 0/0 | 4.002 | 126.25/159.24 | 46123.21/48072.05 | 151.64 | 589.0 | 4/1/0 | 2.7/2.7 |
| round-2-candidate-aot | 200/0 | 0/0 | 3.996 | 90.73/104.91 | 46640.28/48584.52 | 81.95 | 377.8 | 5/1/1 | 2.8/12.1 |
| round-2-candidate-jit | 200/0 | 0/0 | 3.981 | 107.09/134.65 | 46588.93/48538.01 | 192.50 | 609.0 | 4/2/0 | 2.7/12.0 |

## Async body diagnostics (2 MiB, 16 clients)

Two fixed rounds, 20 seconds load, maximum 300 seconds delivery drain, no request retries. The Python target hashes and journals every body. These rates are not comparable to the .NET benchmark target or M4 campaign.

| Attempt | HTTP 202 | Observed | Missing | Duplicate IDs | HTTP errors | Body mismatches | Queue ID errors | Minimum sampled disk free MiB |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| round-1-baseline-aot | 884 | 884 | 0 | 0 | 0 | 0 | 0 | 3477.9 |
| round-1-baseline-jit | 629 | 628 | 1 | 0 | 0 | 0 | 0 | 4099.5 |
| round-1-candidate-aot | 948 | 948 | 0 | 0 | 0 | 0 | 0 | 3248.6 |
| round-1-candidate-fixed-aot | 889 | 888 | 1 | 0 | 0 | 0 | 0 | 3381.8 |
| round-1-candidate-fixed-jit | 616 | 613 | 3 | 0 | 0 | 0 | 0 | 4090.2 |
| round-1-candidate-jit | 698 | 683 | 15 | 0 | 0 | 0 | 0 | 3795.6 |
| round-2-baseline-aot | 660 | 660 | 0 | 0 | 0 | 0 | 0 | 3880.9 |
| round-2-baseline-jit | 614 | 603 | 11 | 0 | 0 | 0 | 0 | 3363.5 |
| round-2-candidate-aot | 846 | 845 | 1 | 0 | 0 | 0 | 0 | 3205.4 |
| round-2-candidate-fixed-aot | 904 | 902 | 2 | 0 | 0 | 0 | 0 | 3029.4 |
| round-2-candidate-fixed-jit | 557 | 552 | 5 | 0 | 0 | 0 | 0 | 3865.3 |
| round-2-candidate-jit | 740 | 727 | 13 | 0 | 0 | 0 | 0 | 2865.5 |
