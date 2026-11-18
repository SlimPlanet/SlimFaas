# Observed Raft-listener TCP traffic

Real three-node Linux Local, proper framework-dependent publications, identical SDK/fixture and original ABBA order. Each run publishes 24 real HTTP Async messages at concurrency four: six exact body sizes repeated with Content-Length and chunked encoding, totaling **38,670,848 application bytes**. Every accepted body reaches the real function with its checksum verified.

AF_PACKET captures IPv4 TCP on loopback, using only PACKET_OUTGOING to avoid counting each loopback copy twice. Selected ports are the three Raft listeners. Counts include internal HTTP framing, control traffic, snapshots and retransmissions. They are observed TCP payload bytes, not physical-wire bandwidth or maximum steady-state replication capacity. Duration includes startup, the workload, shutdown and the capture drain.

## Original capture attempts

All four first workloads pass, but baseline 01 loses 315 capture packets and candidate 03 loses 113. Those byte measurements are excluded. A new complete ABBA uses a 128 MiB forced socket buffer (kernel-reported 256 MiB). Its exact-byte/other-port self-test passes and **all four workloads and all four zero-drop capture gates pass**.

| Run | Seconds | TCP payload bytes | Observed MiB/s | Kernel drops |
|---|---:|---:|---:|---:|
| 1-baseline | 18.940 | 88,811,666 | 4.472 | 0 |
| 2-candidate | 18.620 | 88,848,171 | 4.551 | 0 |
| 3-candidate | 22.160 | 93,213,825 | 4.012 | 0 |
| 4-baseline | 22.010 | 88,840,127 | 3.849 | 0 |

Per-second counters, to/from-listener byte counts, runtime/harness hashes, client ledgers, receipt reconciliation, logical WAL sizes and all failed first captures are indexed in [linux-retained-replay-and-traffic.json](linux-retained-replay-and-traffic.json). Raw original volumes remain in the owned WSL cache. WAL logical file sizes and snapshot gauges are separate from captured network bytes; neither proves allocated physical disk growth.
