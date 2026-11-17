# Benchmark tooling implementation — 17 November 2026

This follow-up starts from PR #506 commit `12cad732`. It adds the feature matrix,
shared resource collection, retained failure verdicts, offline publication
artifacts and isolated optimization candidates. It does not change adopted
runtime defaults or supply new performance measurements.

## Delivered tooling

- One C# cluster collector for HTTP and MemoryLab: all-node process identity,
  CPU boundaries, sampled peak cluster RSS, raw samples and explicit windows.
- Separate HTTP acceptance/load and completion/drain clocks, including paced
  and burst scenarios. Missing evidence cannot override known candidate failures.
- Isolated data operations and verified Files; bounded unique uploads and
  post-validation cleanup through the API of only the current run's file IDs.
- Bounded C# Streams, actual Jobs and official .NET/Python WebSocket scenarios,
  independent durable receiver receipts, integrity reconciliation and drain checks.
- Versioned offline JSON/CSV/Markdown campaign export, per-repetition data,
  dispersion summaries, raw hashes/provenance and comparison verdicts.
- Serial ABBA blocks for macOS, Podman and dedicated kind. Resume checks frozen
  configuration, source/driver identities and immutable image digests. Failed
  attempts remain visible; new feature performance is descriptive (`OBSERVED`).
- Nine offline-generated offload/WAL configurations, changing one factor at a
  time on fresh volumes, including boundary payloads and reference controls.

The public [benchmark guide](../../benchmarking.md) gives commands, coverage,
units, output contracts, prerequisites and resume limitations. Generate and
review real campaign results before publishing measured rows to that page.

## Isolated runtime candidates

Each branch below starts from tooling commit `7d79b66c`. Each changes one runtime
selection method, with unchanged predicates, ordering and result representation.
Use the **latest common tooling branch** to build the frozen driver for every
reference/candidate role; do not rebuild a different driver from each candidate.

| Branch | Commit | Runtime change |
|---|---|---|
| `codex/queue-running-linq` | `1926661f` | Running selection with .NET LINQ |
| `codex/queue-running-loop` | `8c2d400c` | Running selection with a direct loop |
| `codex/queue-retry-linq` | `43871fda` | Retry selection with .NET LINQ |
| `codex/queue-retry-loop` | `24dce40f` | Retry selection with a direct loop |

The default ZLinq implementation remains the reference. The benchmark-only
`QueuePipelineBenchmarks` compares all three strategies, with routing benchmarks
as controls. These are hypotheses, not adopted performance improvements.

## Validation and limits

The complete .NET solution passed **2,488 tests** with
`SlimFaas.Tests.PerfRegression` and `KubernetesSyncBenchmarkShould` explicitly
excluded. The focused benchmark unit suite passed **70 tests**. Each of the four
runtime candidates separately passed all **469 SlimData tests**.

The platform controller passed **58 Python tests**, including bounded fake-server
fixtures for data operations, corrupt file rejection, WAL fencing and resume.
The Sonar boundary checks passed 7 tests, routing fixtures passed 12 tests, and
legacy reporting checks passed 6 tests with 2 Windows-only tests skipped.
The documentation suite passed 11 tests; the site built 23 pages and checked
1,695 local links/assets. Builds retained warnings-as-errors and analyzers.

Local raw validation logs are retained under
`artifacts/pr506/feature-implementation-20261117/`. Test fixtures contain synthetic
values and are not measurements for the website.

**No performance benchmark, BenchmarkDotNet run, platform workload campaign or
endurance run was launched.** Full live macOS/Podman/Kubernetes feature runs,
resource comparisons and selection of best defaults remain for the explicitly
manual campaign. Existing WALs and previous benchmark evidence were retained.
The original PR #506 reliability/merge assessment is not superseded by these
unit-test results; this tooling work is isolated in a separate draft PR.
