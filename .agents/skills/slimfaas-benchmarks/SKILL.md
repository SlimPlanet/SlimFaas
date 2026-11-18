---
name: slimfaas-benchmarks
description: "Run, resume and evaluate SlimFaas HTTP, MemoryLab and recovery benchmarks on macOS, Podman and Kubernetes, preserving source attribution and raw evidence. Use for performance or endurance campaigns and their handoff between pull requests."
---

# SlimFaas benchmarks

Start with the repository [benchmark guide](../../../docs/benchmarking.md).
Use the [campaign rules](references/campaign-rules.md) for comparisons,
resource metrics and fault accounting. For PR #506 history, consult the
[dated handoff](../../../docs/reliability/2026-11-17/pr506-handoff.md) and
[latest published repeat](../../../docs/reliability/2026-11-18/benchmark-repetition.md).
Refresh the current checkout, processes and state before resuming.

## Resume before starting

Establish the worktree, branch, source SHA, driver/controller SHAs, SDK, runtime
mode and output roots. Inspect running campaign ownership, locks, state files,
latest journals and both host/VM free space. A live campaign must continue under
its existing owner; do not start a second controller or rerun a detached script
from its beginning. Check command, UID and process start identity before treating
a saved PID as an owner. Use live process and workload evidence over archived
checkpoints. Keep at least 20 GiB free as described in the campaign rules and
record the deadline and current phase in the heartbeat.

Prepare builds and tests before the timed block; freeze product, drivers and
configuration through it. Keep compilation, other benchmarks, cleanup and
forensic Trace logging outside performance measurements. A source or sampler
change creates a new attributable campaign; earlier passes stay attached to
their original sources. Do not pause unrelated work without current authority.

## Select the existing path

| Requested work | Repository entry point |
| --- | --- |
| HTTP load, target, microbenchmarks, comparison | `benchmarks/SlimFaas.Benchmarks`, commands `run`, `target`, `micro`, `compare` |
| One platform load or serial ABBA matrix | `.bin/run-platform-workload.py`, `.bin/run-platform-matrix.py` |
| Set/mixed memory workloads | `tools/SlimFaas.MemoryLab`, `.bin/memory-lab.sh` |
| Native fault/restart integrity | `.bin/test-slimdata-local-chaos.py` |
| Dedicated kind lifecycle and real Jobs | `.bin/run-kubernetes-campaign.py` |
| Pod replacement, direct IP, Service/headless DNS, per-ID traces | `.bin/run-kubernetes-routing.py` |
| Sonar benchmark boundary | `.bin/check-sonar-benchmark-boundary.py` and its tests |

Confirm options in the selected checkout's parser/help and guide. C# owns load,
latency, counters and request journals; Python owns environment/fault control and
independent receipt reconciliation; shell owns toolchain/setup. Extend these
paths instead of writing another load generator or duplicating orchestration.

For Kubernetes, explicitly select the campaign kubeconfig/context on every API
call. Verify campaign labels, pod UID and command before stopping a process.
Run generators/functions inside the cluster. Diagnostic port-forwarding is not
the measured network path. Keep normal-probe controls separate from the
endurance probe profile that permits long process suspensions.

For a short, authorized performance smoke, use the repository's controls.

```bash
WARMUP_SECONDS=2 DURATION_SECONDS=5 REPETITIONS=1 .bin/slimdata-benchmark.sh
BENCHMARK_PHASE=screening SCREENING_DURATION_SECONDS=10 SCREENING_WARMUP_SECONDS=5 .bin/slimdata-batch-modes-benchmark.sh
```

## Decide and deliver

Use `compare --criteria non-regression` for safety acceptance; the default
optimization mode intentionally requires gains. Report PASS, FAIL or
INCONCLUSIVE by source, environment, compilation mode and scenario. Preserve
both reference comparisons, raw sync and normalized results, every failed
attempt, unknown operation and log-coverage gap. Assess performance, log
coverage and LTS readiness from their own evidence.

For a reproduced product defect, retain the failing control, add a meaningful
regression, fix the cause, and replay affected checks before new measurements.
Follow repository analyzer, audit, AOT, SDK/demo and documentation requirements.
Keep benchmark Sonar exclusions targeted; keep benchmarks/tests in CI.

Before handing off, stop only owned writers at a phase boundary, verify
quiescence before indexing/exporting state, and record exact resume commands,
source/image/executable hashes, original result paths, remaining blockers and
any active owner. If the user asks to leave a campaign running, record its
owner and planned roles without claiming completion or releasing its resources.
Export the retained evidence with the driver's offline `report --campaign`
command. Record throughput units, CPU per operation and sampled peak RAM, including
the load and drain windows. When publication is requested, update the report
on the intended PR and verify the remote commit. Keep raw journals, original
state and interrupted attempts in the campaign evidence.
