# Pooled file-verification reads

The source511 durability-cost screen exposed extra allocation and CPU in file
verification. Separate managed macOS microprobes compare read-buffer choices,
then framework SHA256 streaming with pooled-block streaming. Each row checks
every SHA-256, warms 32 hashes and measures 128 hashes against a warm file cache.
Six sizes from 64 bytes through 4 MiB run at concurrency 1 and 16. This isolates
hashing and stream-buffer overhead; it includes no repository page-cache advice,
persistence barriers, generator or application traffic.

The first probe measures roughly 132 KiB allocated per framework hash with a
128 KiB FileStream buffer. Disabling that buffer reduces allocation but worsens
large-file CPU substantially. The second uses the same 128 KiB FileStream buffer
and compares framework–pooled–pooled–framework, reading into a reusable 128 KiB
scratch buffer and clearing it on return. Representative C16 observations are:

| Body size | Framework allocation/hash | Pooled allocation/hash | Framework CPU ms/hash | Pooled CPU ms/hash |
| --- | ---: | ---: | ---: | ---: |
| 64 KiB | 132,639–132,667 B | 981–983 B | 0.1814–0.1836 | 0.1137–0.1174 |
| 1 MiB | 132,464–132,651 B | 983–3,032 B | 0.9183–0.9445 | 0.6093–0.6168 |
| 4 MiB | 132,588–132,745 B | 983 B | 2.8849–2.9465 | 2.0316–2.0535 |

These are finite microprobe observations, not an application throughput, p99,
RSS or durable-copy cost verdict. Pool growth and scheduling are included in
process allocation/CPU counters; all original rows and both enclosing references
remain available in the [probe results](file-verification-probes.json).
The [reproducible archive](file-verification-probes.tar.gz) contains exact project
sources, commands, logs and results; its [index](file-verification-probes-index.json)
records their hashes.

The runtime now uses the same pooled-block pattern for availability verification
and explicit persistence receipts. Full-body hash/length checks and file/directory
flush barriers remain in place. The helper owns its buffer and hash state while
the caller owns the stream; cancellation and read failures propagate. It handles
partial nonseekable reads and never hashes an unread buffer tail. WAL geometry,
offload threshold, packet formats and defaults are independent of this change.

All **2,406 .NET tests pass with zero failures/skips**, including 117 targeted
tests. Eleven new cases cover empty/fragmented input, boundaries around 128 KiB,
1/4 MiB bodies, caller ownership, pre-cancellation, cancellation at EOF and read
failure. Existing corrupted/truncated-body, copy-receipt, Range and persistence
controls stay green. Full UI builds, analyzers and audits remain active; 41
platform tests and the documentation build pass. Dependencies are unchanged.
See the [exact tested source and results](pooled-file-verification-evidence.json)
and [closed raw index](pooled-file-verification-index.json.gz).

New-source AOT publication, container fault controls and application comparisons
are still required before attributing an application improvement. The
[source511 cost failures](persisted-copy-validation-511.md) remain attached to
their frozen binaries. No LTS or performance acceptance is implied.
