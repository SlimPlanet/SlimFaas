# PR #506: integrated .NET 11 validation

Validation is in progress. This report separates the integrated runtime from the
completed historical .NET 10 campaigns. Neither performance non-regression nor
LTS certification is established by the integration checks below.

## Current source and checks (14 November)

Runtime source `1ecba4d13d29ae68909979e168ff5b53aae6fea1` merges the
Windows WAL geometry correction `eb7e1033` with resource collector correction
`a3e28adc`. Both contributions were preserved without rewriting branch history.
The [Windows report](windows-benchmark.md) explains the persisted chunk-size
marker, compatibility constraints and retained failed controls. Fresh macOS and
Linux WAL defaults are unchanged; existing unmarked Windows WALs are not migrated.

The complete Release solution passes **2,128 tests, zero failed or skipped** on
this merged source. Native macOS SlimFaas and standalone SlimData publish
successfully, as does the managed/trimmed Linux ARM64 runtime. Native API/recovery,
the live official Python client, 72 official .NET SDK payload cases and the
empty-PATH native bundle pass on the new SlimFaas executable. The stale-leader
route takes 8.983 seconds within its unchanged 15-second bound. All nine Linux
network/disk fault repetitions pass. A failed loop-device setup after the VM
reboot is retained separately; after restoring the prerequisite, that repetition
passes in a fresh evidence directory.

The renewed Kubernetes silent-follower control also passes, with 201.858 seconds
from crash to listener availability and another 0.437 seconds for ready/write.
The first interval remains an explicit startup limitation, separate from the
unchanged 15-second healthy-majority check after the listener opens.

The [current Windows/Linux reliability workflow](https://github.com/SlimPlanet/SlimFaas/actions/runs/43549137043),
[dashboard/documentation workflow](https://github.com/SlimPlanet/SlimFaas/actions/runs/43549137145)
and [main validation workflow](https://github.com/SlimPlanet/SlimFaas/actions/runs/43549137155)
all complete successfully, including Native publications and the full unit suite.
FOSSA License Compliance also passes. The separate SonarCloud quality gate still
fails with 38 open vulnerability findings and new-code Security E; a successful
scanner job does not clear this gate. No findings or rules were suppressed.

The [completed short controls](short-controls-net11-03.json) retain SDK case
counts, fault history and raw resource boundary samples. All six renewed long
roles now pass, including final reconciliation and original-volume restart.
The complete performance matrices remain in progress. The [current publication provenance](provenance-net11-03.json) records the
merged-source executable, image and driver hashes. The new comparative driver
is frozen from `a3e28adc`. Its identical managed DLL
runs on macOS and in both Linux tool images. Direct file hashes also confirm
identical native CLR, GC, JIT, OS release and libc between reference and candidate
Linux images. Each of the three current Kubernetes runtime pods has the expected
published executable/DLL hashes and occupies a distinct worker. Renewed long
endurance campaigns on these exact publications now pass; complete
performance comparisons remain in progress; the earlier completed endurance below remains evidence
for its recorded `a8e1b5fd` runtime, rather than a result for the newer source.

On documentation head `e1fc7d3f`, the renewed
[dashboard/documentation workflow](https://github.com/SlimPlanet/SlimFaas/actions/runs/43565904282),
[Windows/Linux reliability workflow](https://github.com/SlimPlanet/SlimFaas/actions/runs/43565904173)
and [main validation workflow](https://github.com/SlimPlanet/SlimFaas/actions/runs/43565904276)
also pass, as does FOSSA. The separate external security gate remains open.

## Current-source native HTTP observations

The [two complete ABBA attempts](native-http-net11-03.json) have an overall
**INCONCLUSIVE** verdict. Each has an incomplete reference: the first attempt's
initial reference observes 1,349 of 1,352 accepted IDs for 2,097,152-byte bodies,
concurrency 16, repetition 1; the repeat's final reference observes 827 of 831
for the same body size, concurrency 4, repetition 2. Both stop at the unchanged
180-second drain limit, with zero observed duplicates. These bounded observations
do not establish permanent loss. All original logs, samples and persisted state
remain available. The one permitted complete repeat has been used.

All four candidate runs complete their 216 HTTP measurements, six focused
paced/burst measurements and 200-message scaling scenario. They record zero
failed requests, missing IDs or duplicate deliveries. All 78 async resource
measurements per run have positive CPU, RSS and Raft evidence, with three-node
initial, final-process and final-diagnostics boundaries.

Complete reference/candidate comparisons nevertheless retain **FAIL** verdicts
for several tail-latency and throughput guardrails. In the repeat, 262,144-byte
async requests at concurrency 16 show throughput reductions of 12.08% and
14.33%, and HTTP p99 increases of 44.47% and 26.18%, against the initial reference.
Resource guardrails and scaling pass in every complete comparison. No gain is
claimed, and no threshold, drain bound or durability check has been relaxed.

Both raw sync aggregates and normalized overhead appear in the linked data.
For example, the repeat's 1,048,576-byte, concurrency-one reference has a slower
direct target: its normalized rate ratio fails while actual proxy throughput
changes from 378.73 to 369.47 requests/second, a 2.44% reduction. This sensitivity
is reported alongside the failed normalized check; it does not waive that check.
An authorized inactive-cache cleanup overlaps the first attempt's third run;
the evidence records that interval. Compiler monitoring observes no external
compilation. The VS Code restart did not restart the campaign or its workloads.
Native MemoryLab and microbenchmark results follow below; container comparisons
remain in progress.

## Current-source native MemoryLab observations

The [complete Set/mixed comparisons](native-memory-net11-03.json) retain three
**PASS** blocks: Set at concurrency 12 and 48, and mixed at concurrency 12.
Every bracket comparison passes their throughput, p99 and RSS thresholds. All
warmups and 120-second loads pass workload checks, independent async receipt
reconciliation and validation on all three nodes.

Mixed at concurrency 48 is **FAIL**. Candidate ordinal two peaks at 208.84 MiB
and passes against both references. Candidate ordinal three peaks at 244 MiB,
versus reference peaks of 205.53 and 208.14 MiB: increases of **18.72% and
17.23%**, above the unchanged 15% limit. Its throughput is 2,694.65 operations
per second versus 2,676.12 and 2,667.55, and its p99 is 32.03 ms versus 35.82
and 47.69 ms. Throughput, p99 and integrity pass. Both references agree on each
candidate's verdict, so the contradictory-bracket repeat does not apply. The
reported sampled peak is retained, without substituting a mean or total RSS.

Disk protection prevents ordinal three from starting after the Mac drops below
20 GiB. Ordinal two has already completed successfully. Outside measurements,
closed build/package caches and 25 untagged non-campaign Podman images with no
running or retained container references are removed. Tagged campaign images,
installed SDKs, frozen publications, sources, sessions, persisted state and all
campaign evidence remain intact; previously archived build caches are no longer
restorable without rebuilding. Native executable/driver hashes are checked again.
Free space returns to 31.95 GiB on the Mac and 60.38 GiB inside the VM before
resuming the two remaining ordinals with the same configuration. The original
isolation segment retains 5,667 samples, zero external compilation observations
and an 8.020-second maximum sample gap. The cleanup interval and resumed segment
remain distinct in the evidence.

## Current-source managed microbenchmarks

The [complete statistical reports](micro-net11-03.json) and
[comparison CSV](micro-net11-03.csv) contain **74 cases in each of four ABBA
runs**, or 296 measured case executions. Every case has valid measured
statistics and allocation data; every run completes successfully with zero
external compilation observations. All runs use the same .NET 11.0.0 ARM64
Release host, SDK 11.0.100 and 13 identical benchmark source files, compiled
against their respective runtime sources. The mode is managed
`ShortRunInProcess`, with three measured iterations per case. Application
acceptance remains governed by the separate HTTP and MemoryLab results.

Representative observed means and allocations include improvements and costs:

| Case | Reference means | Candidate means | Allocated bytes/op: reference → candidate |
|---|---:|---:|---:|
| First ready pod ports, 20 functions | 74.76 / 74.83 ns | 28.32 / 29.25 ns | 176 → 0 |
| Function search, 20 functions | 51.24 / 51.74 ns | 25.48 / 25.47 ns | 24 → 0 |
| Job caller lookup, 200 entries | 13.61 / 12.90 µs | 7.58 / 7.88 µs | 48,608 → 18,048 |
| Retry queue, 200 entries | 429.88 / 396.70 ns | 673.66 / 675.77 ns | 1,656 → 80 |

These short-run estimates preserve all measured values and confidence intervals.
Lower allocation counts do not clear the observed mixed-workload RSS failure,
and local lookup improvements do not clear the failed HTTP tail guardrails.

To reproduce the common-source driver, use the
[project template](common-micro.csproj.template) from this report revision.
Build both drivers before measuring; keep the reference checkout's tracked
sources unchanged. Run from the candidate repository root with SDK 11.0.100,
and use a fresh path for the reference worktree:

```bash
SLIMFAAS_MICRO_ROOT="$PWD"
SLIMFAAS_REFERENCE_ROOT="$PWD/../SlimFaas-net11-micro-reference"
git worktree add --detach "$SLIMFAAS_REFERENCE_ROOT" f36084643a75d803051a6918d2950d1a4f674ada

for SLIMFAAS_VARIANT in before after; do
  SLIMFAAS_PROJECT="$SLIMFAAS_MICRO_ROOT/artifacts/common-micro/$SLIMFAAS_VARIANT/project"
  mkdir -p "$SLIMFAAS_PROJECT"
  cp docs/reliability/2026-11-13/common-micro.csproj.template "$SLIMFAAS_PROJECT/Common.csproj"
  SLIMFAAS_RUNTIME_ROOT="$SLIMFAAS_MICRO_ROOT"
  if [ "$SLIMFAAS_VARIANT" = before ]; then
    SLIMFAAS_RUNTIME_ROOT="$SLIMFAAS_REFERENCE_ROOT"
  fi
  dotnet build "$SLIMFAAS_PROJECT/Common.csproj" -c Release \
    -p:SkipClientAppBuild=true \
    -p:MicrobenchmarkSourceRoot="$SLIMFAAS_MICRO_ROOT" \
    -p:RuntimeSourceRoot="$SLIMFAAS_RUNTIME_ROOT" \
    -o "$SLIMFAAS_MICRO_ROOT/artifacts/common-micro/$SLIMFAAS_VARIANT/bin"
done

for SLIMFAAS_ORDINAL in 1 2 3 4; do
  case "$SLIMFAAS_ORDINAL" in
    1|4) SLIMFAAS_VARIANT=before ;;
    *) SLIMFAAS_VARIANT=after ;;
  esac
  dotnet "artifacts/common-micro/$SLIMFAAS_VARIANT/bin/SlimFaas.Benchmarks.dll" \
    --filter '*' --exporters json csv markdown \
    --artifacts "artifacts/common-micro/run-$SLIMFAAS_ORDINAL-$SLIMFAAS_VARIANT"
done
```

The candidate runtime for these observations is `1ecba4d1`; subsequent changes
cover reports, validation workflow triggers and Kubernetes shutdown orchestration, with unchanged runtime
products and benchmark source files. The JSON includes the 13 source hashes and
both frozen driver hashes appear in the current publication provenance. The
template has the same properties, compilation inputs and runtime references as
the generated project used for these measurements, with configurable paths.

## Kubernetes collection correction

The shared Kubernetes launcher previously returned from `stop` immediately
after scaling controllers to zero. Pods could still be terminating and flushing
their WALs when the VM export began. The matrix also ignored a nonzero stop
result. The [retained regression evidence](shutdown-regressions-net11-03.json)
reproduces both problems through the public CLI paths.

The launcher now scales only resources carrying its campaign label, then waits
for controller pods to disappear and other labelled writers to finish. Completed
Job pods remain. `controller/shutdown.json` records the pending pod UIDs and the
final quiescence verdict, with a separate 180-second collection deadline. A failed
stop prevents the archive and the next phase. Runtime publications, request
deadlines, comparison algorithms and workload configurations are unchanged.

Four regressions fail on the original code, and all **25 platform harness tests**
pass after the correction. They run in an observed gap after the active Podman
guest exits. Only the host launcher waits for this check; the guest workload and
its isolation monitor continue throughout the measurement. The launcher then
resumes automatically. The earlier six endurance originals already had an
explicit coordinator-level shutdown barrier before their hashes were collected.
The correction moves that guarantee into the shared command for upcoming
Kubernetes performance exports.

## Retained Podman isolation attempt

The [first Podman attempt](podman-isolation-net11-03.json) is excluded from
performance acceptance and retained as **INCONCLUSIVE**. Its two candidates each
complete 216 HTTP cases, six focused cases and 200-message scaling, with zero
failed requests, missing IDs or duplicates. Both retain positive CPU/RSS/Raft
evidence across all three nodes. The final candidate reaches four ready replicas
in 6.511 seconds and drains in 19.751 seconds.

Both bracketing references are incomplete at the unchanged 180-second drain:
the first observes 1,145/1,160 IDs for 4,194,304-byte bodies at concurrency 16,
repetition 2; the last observes 688/689 at concurrency 4, repetition 1. These
bounded observations do not establish permanent loss. Neither reference supplies
the mandatory complete comparison report.

The final reference also records one external Node `build-storybook` wrapper
in the isolation log. The original guard catches its child but misses the
wrapper's suffixed command. The reservation now matches these variants and
frontend test/coverage wrappers, continuously across environment transitions.
Shared editor, terminal and Codex roots remain active. The invalid observation
closes only after the current guest exits; original VM data and verified
archives remain. A fresh complete Podman attempt uses new output and campaign
identities; completed Native and microbenchmark work is preserved.

Between the two completed candidates and the final reference, discarding already
unallocated VM filesystem blocks returns about **6.32 GiB** to the Mac without
deleting any file. Free space reaches 29.99 GiB; the strict 30 GiB housekeeping
target remains false in its raw metadata. The 20 GiB startup threshold is not
crossed during that interval. The later isolation restart also performs discard
only after guest shutdown.

On harness/workflow head `bcb744ba`, the
[dashboard/documentation](https://github.com/SlimPlanet/SlimFaas/actions/runs/43619681420),
[Windows/Linux reliability](https://github.com/SlimPlanet/SlimFaas/actions/runs/43619681412)
and [main validation](https://github.com/SlimPlanet/SlimFaas/actions/runs/43619681423)
workflows pass, as does FOSSA. Of 41 check runs, the separate SonarCloud quality
gate remains failed: Security E, 38 open vulnerability findings and seven closed.
The successful scanner job remains distinct from that gate. No finding or rule
is suppressed.

## Current-source Podman HTTP observations (15 November)

The [fresh ABBA block](podman-http-net11-03.json), using new campaign IDs after
the invalid isolation attempt above, is **INCONCLUSIVE**. Both references stop
at the unchanged 180-second async drain: the initial reference observes
2,778/2,786 IDs for 2,097,152-byte bodies, concurrency 16, repetition 2; the final
reference observes 1,548/1,551 for 2,000,000-byte bodies, concurrency 16,
repetition 1. Both record zero duplicates. These bounded observations do not
establish permanent loss. Neither reference supplies a mandatory complete
comparison report; no complete pair or performance gain is claimed. Both
references yield the same inconclusive result, so the contradictory-bracket
repeat does not apply.

Both candidate runs complete all 216 HTTP cases, six focused paced/burst cases
and 200-message scaling with zero failed requests, missing IDs or duplicates.
Each retains 78 positive CPU/RSS/Raft measurements with initial, final-process
and final-diagnostics boundaries covering three distinct nodes. Four replicas
are ready in 5.361 and 5.379 seconds; queues drain in 18.619 and 19.904 seconds.
The linked data includes raw sync aggregates and normalized overhead, settings,
report/log hashes and verified archive metadata. Original per-request samples
and persisted state remain retained on the Mac and VM.

The frozen runtime, driver, images, resource limits and request deadlines are
unchanged. Between closed guest phases, filesystem discard returns already
unallocated VM blocks to the Mac; it removes no files or images and overlaps no
timed guest workload. Isolation monitoring continues through the matrix; its
final audit, Podman MemoryLab and Kubernetes performance results remain pending.

On report head `46f3bfb5`, the
[dashboard/documentation](https://github.com/SlimPlanet/SlimFaas/actions/runs/43631519374),
[Windows/Linux reliability](https://github.com/SlimPlanet/SlimFaas/actions/runs/43631519348)
and [main validation](https://github.com/SlimPlanet/SlimFaas/actions/runs/43631519293)
workflows pass, as does FOSSA. The separate SonarCloud security gate remains
failed; scanner completion does not establish gate acceptance.

## Current-source endurance

The [six renewed endurance reports](endurance-net11-03.json) pass on the frozen
`1ecba4d1` publications, including all six absence durations, successive crashes
and complete restart with the original volumes. All **92,281 confirmed
operations** reconcile without confirmed loss or corruption. Unknown client
outcomes remain separate. One extra successful delivery is recorded in the
Native quorum-fault campaign; the other five roles record none.

| Environment / role | Confirmed | Unknown | Hour-long absence: ready / full reconciliation |
|---|---:|---:|---:|
| Native follower | 23,153 | 25 | 1.033 / 51.976 s |
| Native leader | 23,071 | 30 | 0.733 / 51.839 s |
| Native quorum | 145 | 26,119 | 1.033 / 23.890 s |
| Kubernetes follower | 22,838 | 43 | 0.761 / 72.501 s |
| Kubernetes leader | 22,895 | 33 | 0.610 / 71.827 s |
| Kubernetes quorum | 179 | 8,950 | 3.781 / 26.070 s |

Kernel evidence covers 18 initial/restarted pod identities with a maximum
30.818-second sample gap. It records a **975.66 MiB sampled peak** against the
1 GiB runtime limit, **2,141 memory-limit hits**, **3,701 socket-throttling
events**, and zero observed OOM/kill events. This is measured memory pressure;
the sampled maximum is not a bound on every instantaneous peak.

The [complete correctness file index](correctness-raw-file-index-net11-03.json.gz)
and its [size/hash metadata](correctness-raw-file-index-net11-03-metadata.json)
cover the frozen Native and VM originals, including failed setup attempts.
This is an index of retained local evidence; it does not provide a download of
the original volumes or journals. The earlier source's index remains unchanged.

All correctness controllers are stopped, and the original persisted files are
hashed before the first timed comparison. Discarding already unallocated blocks
of the VM filesystem returns about 4.2 GiB to the Mac without removing any file,
image, WAL, snapshot or volume. The renewed Native ABBA measurements use the corrected common driver. Their
completed HTTP, MemoryLab and microbenchmark observations are reported above;
container conclusions remain pending.
Earlier failed or interrupted runs are retained.

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
| Full Release solution tests after the completion fix | 2,113 passed, zero failed/skipped |
| Official Python client | 37 passed |
| Full Release build, including dashboards | Passed, zero MSBuild warnings/errors; Vite chunk advisory retained |
| MemoryLab and collection probe builds | Passed |
| Common-source reference/candidate microbenchmark builds | Passed |
| Harness, evidence, comparer and bundle helper tests | Passed; platform checks expanded to 21 tests |
| Native AOT macOS reference, candidate, MCP and standalone SlimData | Published successfully |
| Documentation production build | 23 pages, 1,672 local links/assets, 401 search entries before this report |
| Initial integrated Native API/recovery campaign (`8cac1116`) | Passed, 187 confirmed operations, 12 unknown outcomes, zero missing |
| Corrected Native API/recovery campaign (`a8e1b5fd`) | Passed, 172 confirmed operations, 12 unknown outcomes, zero missing |
| Corrected Native stale-leader routing | 6.770 s, within the unchanged 15 s bound |
| Official Python SDK/API live campaign | Passed |
| Standalone native bundle with empty PATH | Passed, including actual functions and job |
| Official .NET SDK live payload checks | 72 passed |

Native publication retains the existing MemoryPack IL2104/IL3053,
ConfigurationManager IL2104 and Apple debug-map warnings. Successful publication
does not mean those upstream warning summaries disappeared. For runtime
`a8e1b5fd`, the Linux reliability job, unit-test job and FOSSA License Compliance
check pass. The Windows reliability job fails on a 1,048,576-byte chunked async
request at its unchanged 15-second bound, before its SDK steps. That operation
has an unknown client outcome; a later complete receipt has the matching SHA-256.
Previous Windows Stream timeouts remain separate failures. Full macOS and
Kubernetes endurance integrity checks now pass; comparative performance remains
pending.

The current [PR checks](https://github.com/SlimPlanet/SlimFaas/pull/506/checks)
also retain a failed SonarCloud Code Analysis quality gate: new-code Security E,
38 open vulnerability findings and seven closed findings. Reliability and
maintainability are A; the gate reports 100% new-code coverage and reviewed
hotspots. The open findings concern validation tooling, its HTTP/WS deployment
configuration and benchmark container configuration. No rule was disabled and
no finding was dismissed to obtain acceptance. The successful scanner job does
not mean the quality gate passed.

On documentation head `b1a309f4`, Linux reliability and FOSSA also pass. The
Windows run reaches SDK streaming before failing an async HTTP response
deadline; its complete log is retained. The traffic-animation job initially
reaches its twenty-minute CI budget during AOT compilation, before browser
tests start. An unchanged [single-job rerun](https://github.com/SlimPlanet/SlimFaas/actions/runs/43410209967/job/131857197981)
passes Native runtime/function publication and animations on three nodes.
No test or deadline was changed for that replay.

On documentation head `20517328`, Linux reliability, Native publications,
dashboards, documentation, three-node animations and FOSSA pass. The Windows
quick campaign fails its unchanged fifteen-second async request deadline on a
1,048,576-byte Content-Length request; that client outcome remains unknown.
The Windows coverage job first fails a single-attempt write after re-election in
`RaftMembershipRecoveryTests`. Its unchanged replay passes that test but fails
`RaftClusterTests.MessageExchange`: the local follower still shows an available
queue item immediately after its completion callback replicated on the leader.
Both failed logs are retained. The fixture now applies the same follower read
barrier already used for hash assertions before checking the local queue count.
This changes the test's synchronization, with no runtime, request or test-timeout
change. On test/documentation head `a8b5a34e`, the
[Windows coverage replay](https://github.com/SlimPlanet/SlimFaas/actions/runs/43463306234/job/132015701630)
passes all 2,113 tests, with zero failures or skips, and the scanner completes
successfully. This verifies the fixture correction; the external security
quality gate remains separate. The Windows quick campaign on that same head
passes its async payload checks before a Stream mutation returns an unknown
HTTP outcome. That failure and the earlier async failures remain retained.

A host reboot interrupted a later macOS comparison before the full ABBA block
completed. Its partial observations remain excluded from performance acceptance.
The dedicated toolchain has been restored outside temporary storage, and frozen
publication hashes have been verified again. A subsequent attempt encountered
host suspension and was also retained without a comparison verdict. Renewed
campaigns hold a process-lifetime macOS sleep-prevention assertion. Compiler
tasks and the dedicated C# project services are suspended during measurements;
the editor, shared terminal and assistant services remain active. Raw observations
from interrupted isolation attempts remain retained separately from complete
runtime health controls.

## Resource collection correction

A later isolated native ABBA block completed its first reference and both
candidate runs: each has 216 HTTP measurements, six paced/burst cases and scaling,
with zero failed requests, missing IDs or extra deliveries. The final reference
stopped at 1,217 of 1,231 observed IDs after the unchanged 180-second drain for
2,097,152-byte bodies, concurrency 16, repetition 3. Its original data remains;
this observation does not establish permanent loss.

The comparator additionally rejected all complete reports because subsecond
bursts lacked positive CPU/Raft resource evidence. The resource monitor started
without awaiting its initial sample, stopped without a final sample and read
diagnostics gauges updated every five seconds. These observations remain
**INCONCLUSIVE**; the automatically started repeat was interrupted to correct
instrumentation before collecting further acceptance data.

A deterministic short-burst regression fails against the original collector.
The corrected collector awaits an all-node diagnostics baseline, uses live CPU
and RSS through load/drain, and waits for fresh all-node Raft counters afterward.
The exporter wait is excluded from CPU/RSS accounting and timed load; raw
boundary snapshots are retained. Three focused regressions and the full
**2,116-test Release suite** pass with zero failures or skips before the Windows
correction is integrated. The collector change itself does not alter runtime
sources or publications. The subsequent merged-source suite passes 2,128 tests;
its new runtime publications receive separate campaigns as described above.
Renewed performance acceptance requires fresh measurements using identical
corrected drivers for both revisions.

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

Integration artifacts use distinct `net11-01` names below `artifacts/pr506/`;
the completion correction and its comparison drivers use `net11-02`. Current
merged-source publications and the corrected macro driver use `net11-03`; the
current Linux tool image is `after-tools-net11-04`. Configuration
files include both source SHAs and the SDK. Publications, container image
inspection, raw receipts, original persisted state and failed attempts are
retained. The [frozen source, executable, image and driver
provenance](provenance.json) identifies the new artifacts before timed work.
Raw-result indexes and final per-environment verdicts require the renewed
campaigns to complete. Raw host/VM data is retained locally; this link does not
provide a public download of those original volumes or journals.

The unchanged .NET 11 reference passes Native API setup and Kubernetes API setup
using the same sidecar-port convention as the performance fixture. A separate
control with a declared sidecar port reproduces its historical listener-binding
failure; that Job was suspended using its exact UID and its state/logs are
retained. Performance fixtures already omit that declaration for both revisions,
as documented in the [benchmark guide](../../benchmarking.md). Direct hashes of
the Linux CLR, GC, JIT, OS release and libc files match between both images.

## Heavy-body screening and completion regressions

The integrated `8cac1116` Native AOT screening completed nine measured async
runs (2,000,000 / 2,097,152 / 4,194,304 bytes, concurrency 16, three repetitions).
All 9,722 accepted IDs were observed within the unchanged bounds, with zero
request failures and **16 extra deliveries** in the second 2,000,000-byte run.
All six paced/burst cases completed; scaling reached four ready replicas in
7.288 s and drained in 21.245 s. The screen's verdict is **FAIL**. Host monitoring
recorded zero concurrent compiler/test observations.

A retained 20-second Python receipt-target diagnostic then observed 2,110 of
2,112 accepted IDs after 180 seconds, with no duplicates. Transport failures and
all missing IDs remain archived. A second, 60-second diagnostic uses HTTP/1.1
keepalive and a 512-connection accept backlog, with the same 16 clients,
2,000,000-byte body, dispatch capacity 256 and 180-second drain. It observes all
6,075 accepted IDs, **14 extra full-body deliveries** and four partial-body
observations during canceled uploads. The strict diagnostic fails; those four
partial observations remain separate from SHA-256-verified complete deliveries.
Diagnostic-target rates are not performance comparisons with the .NET target.

Duplicate receipts carry the same request and queue IDs, moving from attempt 0
to attempt 1 approximately 124 seconds later. Logs record a Raft leader change
and canceled in-flight sends. The worker checked leadership and cleared its
tracking generation before draining already completed responses; its legacy
batch path also discarded completed callbacks before persistence succeeded.
A stale completion could additionally remove a newer local dispatch with the
same queue ID before checking its generation.

Three deterministic regressions fail against the original implementation and
pass with the correction. They cover completion persistence across leadership
loss, callback persistence failure without another application dispatch, and
stale-generation capacity ownership. The implementation drains known responses
first, retains uncommitted callback batches and their capacity, and releases only
committed offloads. In-flight cancellation on leadership loss is still exercised
by its existing regression. All 19 focused worker/offload tests and the full
2,113-test Release suite pass, with zero skipped tests; the documentation site
build passes again.

The exact 60-second diagnostic replay against corrected Native AOT runtime
`a8e1b5fd5aeed535676dc3860d3adacc9159226a` observes **5,940 accepted IDs and
5,940 verified deliveries**, with zero missing IDs, extra deliveries, body
mismatches, HTTP errors or sampling errors. Its executable SHA-256 is
`a4c8ceacd7bc4c7c7c52c938f661b32808d179abe6d4f292744f9b7daf901fc3`.
This passes the reproduction; final comparative performance remains pending.

The deterministic control can be reproduced in a separate worktree with SDK
11.0.100. The first test run is expected to fail the three new regressions:

```bash
git worktree add --detach ../SlimFaas-completion-repro a8e1b5fd5aeed535676dc3860d3adacc9159226a
cd ../SlimFaas-completion-repro
git restore --source=134868f8 -- src/SlimFaas/Workers/SlimQueuesWorker.cs
dotnet test tests/SlimFaas.Tests/SlimFaas.Tests.csproj -c Release \
  -p:SkipClientAppBuild=true --filter FullyQualifiedName~AsyncQueueWorkerTests
```

Restore the corrected worker and rerun the same tests; they must now pass:

```bash
git restore --source=a8e1b5fd5aeed535676dc3860d3adacc9159226a -- src/SlimFaas/Workers/SlimQueuesWorker.cs
dotnet test tests/SlimFaas.Tests/SlimFaas.Tests.csproj -c Release \
  -p:SkipClientAppBuild=true --filter FullyQualifiedName~AsyncQueueWorkerTests
```

A separate rolling upgrade from the final .NET 10 reliability runtime to this
.NET 11 runtime passes: all three nodes reconcile 187 keys after rolling
replacement, follower suspension, quorum interruption and complete restart using
the original volumes. Both sides use command protocol SLDC/5. This does not
establish rolling compatibility from the older SLDC/4 protocol.

## Corrected .NET 11 endurance

All three Native AOT roles on `a8e1b5fd` pass the complete
30/60/300/900/1,800/3,600-second absence sequence, successive crashes and a full
restart using the original volumes. All **45,987 confirmed operations** are
reconciled, with zero confirmed loss or checksum corruption. Unknown client
outcomes remain separate.

| Native role | Confirmed | Unknown | Extra successful deliveries | Ready after 1 h absence | Full reconciliation |
|---|---:|---:|---:|---:|---:|
| Follower | 22,954 | 29 | 3 | 2.295 s | 86.825 s |
| Leader | 22,888 | 37 | 1 | 0.433 s | 82.390 s |
| Quorum loss | 145 | 26,028 | 0 | 0.420 s | 31.538 s |

The four extra deliveries belong to explicit fault phases: follower absences of
900, 1,800 and 3,600 seconds, and the 900-second leader absence. They remain
counted under the permitted at-least-once fault behavior. Largest per-kind
confirmed-write spacings are 19.521 s for follower and 19.829 s for leader;
these observations include the mixed traffic cadence and remain distinct from
the dedicated fifteen-second leader-route check.

The [Native endurance evidence](native-endurance.json) retains every absence
result and availability observation. Original volumes, receipts and client
ledgers remain on the validation host. A passed endurance integrity check does
not establish comparative performance or LTS guarantees.

All three Kubernetes roles also pass the same absence sequence, successive
crashes and original-volume restart. All **45,443 confirmed operations** are
reconciled with zero confirmed loss, checksum corruption or extra successful
deliveries. Both environments together reconcile **91,430 confirmed operations**.

| Kubernetes role | Confirmed | Unknown | Extra successful deliveries | Ready after 1 h absence | Full reconciliation |
|---|---:|---:|---:|---:|---:|
| Follower | 22,606 | 45 | 0 | 0.752 s | 57.213 s |
| Leader | 22,667 | 30 | 0 | 1.050 s | 61.004 s |
| Quorum loss | 170 | 8,918 | 0 | 2.965 s | 21.132 s |

The [Kubernetes endurance evidence](kubernetes-endurance.json) includes all six
absences and observations for eighteen pod/UID groups. The largest periodic
memory sample is 965.77 MiB against a 1 GiB hard limit; this is not a strict peak
bound. There are 1,543 memory-limit hits and 1,989 socket-throttling events, with
zero observed OOM events or OOM kills and no unplanned container restarts. These
results establish the observed integrity outcome under pressure, not an absence
of memory pressure.

Native and Kubernetes endurance workloads ran concurrently in separate campaign
resources. Their recovery observations are correctness evidence; isolated
comparative timing runs follow separately. All campaign controllers are now
stopped and original persisted files were hashed after shutdown. The
[correctness evidence manifest](correctness-evidence.json) identifies retained
runs and hashes the [complete raw-result index](correctness-raw-index.json.gz).
The index is lossless gzip-compressed JSON listing original files and their
SHA-256 values; original data volumes remain local.

The separate Kubernetes silent-follower protocol control passes its ready/write
check in 0.729 s after the listener becomes available. Restart-to-listener takes
201.645 s. That startup interval remains a limitation and is not covered by a
claim that the whole restart completes within fifteen seconds.

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
