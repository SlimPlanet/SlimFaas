---
paths:
  - "src/SlimData/**"
  - "src/SlimFaas/Data/**"
  - "tests/SlimData.Tests/**"
---

# SlimData: embedded Raft store

- SlimData is the embedded Raft (DotNext) store behind replicated queues, configuration and small values. Consider multi-node scenarios for every change: leader election, membership changes, catch-up, snapshots and WAL recovery.
- Commands and their payloads must stay `MemoryPackable`; a new or changed command needs serialization and replication tests in `tests/SlimData.Tests`.
- Changes to batching, queues, WAL, snapshots or `src/SlimFaas/Data/` require the benchmarks: `WARMUP_SECONDS=2 DURATION_SECONDS=5 REPETITIONS=1 .bin/slimdata-benchmark.sh` and `BENCHMARK_PHASE=screening SCREENING_DURATION_SECONDS=10 SCREENING_WARMUP_SECONDS=5 .bin/slimdata-batch-modes-benchmark.sh`. Paste before/after numbers in the pull request.
- Update `docs/how-it-works.md` (Data storage and replication), `docs/data-sets.md`, `docs/slimdata-unified-batching.md` or `docs/slimdata-batch-modes.md` when behaviour, consistency guarantees or recovery change.
- Tests use fake time providers, not wall-clock sleeps or timing assertions; cluster tests need explicit deadlines so a slow CI runner fails with a clear message.
