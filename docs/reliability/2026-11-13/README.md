# PR #506: integrated .NET 11 validation

Validation is in progress. This report separates the integrated runtime from the
completed historical .NET 10 campaigns. Neither performance non-regression nor
LTS certification is established by the integration checks below.

## Integrated sources

| Input | Source | Integration commit |
|---|---|---|
| PR #506 before integration | `b110b33fe680be27d16d197547b5b95f5513a215` | Existing reliability work |
| PR #504, collections and Windows file sharing | `e1d54ec455c42897046d8ea6674d78fa0148f5a6` | `1d84170c` |
| PR #514, .NET 11 and dependency refresh | `f36084643a75d803051a6918d2950d1a4f674ada` | `3643d7d4` |
| Integrated validation tooling | `8cac1116ad5022fb5e48374e557fd9689003be97` | .NET 11 paths and endpoint regression |

PR #504 was merged first, followed by PR #514, into
`codex/slimdata-lts-reliability`. The other checkout and the historical
`496e8739e5f7b4326f64520fe4caa4e0e23f8d06` reference retain their tracked sources.
The integration preserves the durable WAL acknowledgment/flush gate, local apply
read barrier, bounded Raft protocol probe, cancelled-upload socket cleanup,
WebSocket frame integrity and process/controller ownership protections. Windows
file readers retain replacement/deletion sharing while metadata still uses the
reliability branch's defensive decoder.

The merged MemoryLab supports custom function names and container node URLs
together. A real loopback HTTP regression checks the encoded route, unique
message IDs and SHA-256 headers. The local benchmark accepts both
`BENCHMARK_RUNTIME` and PR #504's `BENCHMARK_RUNTIME_PATH` executable override.

## Integration checks

Toolchain: SDK 11.0.100, Node 26.13.0, pnpm 12.18.0. Analyzer and dependency audit
policies remain enabled. Existing dependency/license holds from PR #514 remain.

| Check | Observed result |
|---|---|
| Full Release solution tests | 2,110 passed, zero failed/skipped |
| Official Python client | 37 passed |
| Full Release build, including dashboards | Passed, zero MSBuild warnings/errors; Vite chunk advisory retained |
| MemoryLab and collection probe builds | Passed |
| Common-source reference/candidate microbenchmark builds | Passed |
| Harness, evidence, comparer and bundle helper tests | Passed; platform checks expanded to 21 tests |
| Native AOT macOS reference, candidate and MCP | Published successfully |
| Documentation production build | 23 pages, 1,672 local links/assets, 401 search entries before this report |
| Native short API/recovery campaign | Passed, 187 confirmed operations, 12 unknown outcomes, zero missing |
| Native stale-leader routing | 6.813 s, within the unchanged 15 s bound |
| Official Python SDK/API live campaign | Passed |
| Standalone native bundle with empty PATH | Passed, including actual functions and job |
| Official .NET SDK live payload checks | 72 passed |

Native publication retains the existing MemoryPack IL2104/IL3053,
ConfigurationManager IL2104 and Apple debug-map warnings. Successful publication
does not mean those upstream warning summaries disappeared. The Windows CI
WebSocket campaign again times out reading a Stream; its failure remains open.
Linux, macOS, Kubernetes, FOSSA and Sonar gates require their own final evidence.

## Renewed measurement protocol

The .NET 11 reference is the unchanged PR #514 source `f3608464`; the candidate
contains the collection and reliability changes. Both use SDK 11.0.100. Native
AOT macOS and managed/trimmed Linux ARM64 remain separate comparisons. Kubernetes
uses the same source-built images as Podman and an isolated kubeconfig.

The [benchmark guide](../../benchmarking.md) defines the unchanged HTTP and
MemoryLab matrices, ABBA order, one complete repeat for contradictory references,
resource guardrails and required integrity checks. Common microbenchmark source
files compile directly against each revision's project references; this requires
no tracked edit to the reference checkout. Heavy-body screening is diagnostic and
does not replace the complete matrix or its 180-second async drain bound.

New artifacts use distinct `net11-01` names below `artifacts/pr506/`. Configuration
files include both source SHAs and the SDK. Publications, container image
inspection, raw receipts, original persisted state and failed attempts are
retained. Final image/executable hashes, raw-result indexes and per-environment
verdicts will be added after the renewed campaigns complete.

## Preserved .NET 10 findings

The final pre-integration runtime was
`5f0ae2e4bb808f2d6fa9c8c755646cd146dc1367`, compiled with SDK 10.0.403.
All six final native/Kubernetes endurance roles completed the 30, 60, 300, 900,
1,800 and 3,600-second absence matrix and original-volume restart with zero
confirmed loss or corruption. Unknown outcomes were kept distinct from confirmed
operations. This evidence does not validate the new .NET 11 runtime.

Performance acceptance was not established: HTTP references failed the required
async observation bound; candidate completed runs retained extra deliveries
under sustained heavy load. Native mixed/concurrency-48 failed the throughput
and p99 guardrails in a repeated block. Other contradictory or incomplete
comparisons remain inconclusive. A Podman mixed validation timeout is retained.
These failures are not waived because durability or allocations improved.

The third .NET 10 Kubernetes attempt was interrupted explicitly to integrate the
two PRs. Its driver Job was suspended using its exact UID, owned controllers were
scaled down, and complete partial evidence was exported. Original volumes remain.
Earlier Kubernetes runs affected by another project's compilation remain marked
as unsuitable for isolated performance conclusions. The user's temporary host
reservation was released when those measurements stopped.

See the [historical reliability report](../../slimdata-reliability-results.md)
and [PR #504 performance report](../../performance-pr504-followup.md). Previous
failures, unsupported guarantees and the open reliability follow-up remain open.
