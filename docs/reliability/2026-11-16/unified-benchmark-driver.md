# Unified benchmark driver

`benchmarks/SlimFaas.Benchmarks` now provides `micro`, `target`, `run` and
`compare`. The previous HTTP project is removed from the solution. Scripts,
container tooling, test references and current documentation use the unified
executable. Historical source-pinned reports and binaries retain their names.

The driver is managed and untrimmed; measured runtimes are published separately.
The first framework-dependent publication reproduced `NETSDK1151`: during
reference framework discovery, the SDK removes `SelfContained` and infers an
autonomous executable from the referenced products' trimming settings. During
driver publication, explicit project-reference `PublishSelfContained` metadata
keeps those settings consistent. Ordinary builds retain the existing reference
graph. Compiler/analyzer/audit checks remain enabled. Both framework-dependent
and self-contained macOS ARM64 managed publications then succeed without web
asset collisions. Production AOT settings are unchanged.

Validation completed before the following routing/connection changes:

- Full .NET solution: 2,433 passed, zero failed/skipped; 45 targeted benchmark tests.
- Exactly the same 33 discovered microbenchmark methods as the frozen previous
  driver; legacy arguments, filters and `--extended` remain supported.
- Three deliberately short parameterized executions produce JSON/CSV exports.
  These executions check dispatch and dependencies, not usable performance.
- The published Kestrel target handles real requests and duplicate observations.
- The comparer preserves pass/fail/inconclusive exit codes 0/1/2, including its
  default optimization criteria.
- The same published driver runs three HTTP latency cases, 100 paced and 1,000
  burst async messages, and the 200-message/C32/four-replica scaling fixture
  against the retained source189 Native AOT runtime. Reported failures, missing
  deliveries and duplicates are zero. Four replicas are ready in 6.142 seconds;
  drain completes in 19.957 seconds. This is an execution smoke, not a comparison.
- Platform/provenance harness checks and the documentation build pass.

The first CLI smoke used a nonexistent filter and correctly detected its missing
exports; the corrected filter is `*RoutingQueryBenchmarks.FirstReadyPodPorts*`.
Failed publication/smoke attempts remain retained. The full solution console log
preserves every project result; a fixed TRX filename overwrote the individual
TRX outputs, so subsequent validation must use generated unique filenames.

[Evidence index and hashes](unified-benchmark-driver.json) ·
[Reproducible driver commands](../../benchmarking.md#run-the-benchmark).
The prior incomplete references, duplicate controls and healthy-traffic internal
errors remain open; this refactor does not establish non-regression or LTS status.


The two C# application drivers now compile the same percentile calculation and
opt-in request journal from `benchmarks/Common`. MemoryLab sync and async
operations share ID/hash/response validation without a second orchestration loop.
A real TCP smoke independently reconciles 17 sync completions with 17 fsynced
function receipts, correct 192 KiB hashes, observed HTTP/1.1 and the serving pod
UID. The first smoke exposed duplicate forwarding of an existing ID header;
the corrected handler preserves the caller's single value, covered by a
regression. Original failed smoke output remains retained. This is a harness
functional check, not a runtime performance or fault verdict.
[Smoke command, output identity and hashes](request-journal-smoke.json).


The first Kubernetes routing startup reaches three ready instances but exposes a
collector API mismatch before the C# load Job is created. The failing adapter also
skips shutdown when its final collection raises. Both failures are preserved;
existing owned-campaign helpers retain the logs and stop the fixture. A regression
injecting collector failure is red, then all six routing-adapter checks pass after
using the actual collector contract and independently attempting every cleanup
action. Incomplete owned Jobs are suspended, and Kubernetes supplies a bounded
active deadline. No application retry or runtime code changes.
[Adapter regression evidence](routing-adapter-cleanup-regression.json) ·
[Built/imported image identities and common driver hashes](routing-images.json).
