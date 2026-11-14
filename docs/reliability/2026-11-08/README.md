# Dated reliability evidence

Read [the results and limitations](../../slimdata-reliability-results.md) before
interpreting a passing field or an empty missing-ID list.

- `local-campaigns.json`: original reports, historical pre-resume reports, exact
  parameters/runtime hashes, journal event counts, delivery counts and raw-file
  hashes for selected Windows campaigns. Resource ranges combine nodes/phases;
  they are not per-payload causal measurements.
- `linux-endurance.json`: reports and accounting from the three original CI ZIPs,
  exact revision, artifact URLs, archive hashes and unknown-outcome categories.
- `linux-endurance-612.json`: a second complete three-role Linux endurance matrix
  with DotNext 6.12.0, including original ZIP hashes and all outage/restart results.
  This revision predates the transactional snapshot and Local job fixes.
- `six-role-endurance-df03.json`: all six Linux managed / Windows Native roles
  passed every dedicated outage duration and retained full restart after the
  snapshot fix. Original ZIP hashes and reports identify the exact older runtime;
  the separate Windows quick failure remains recorded.
- `linux-local-faults.json`: reports/parameters and archive hashes for preserved
  WSL packet-drop and disk-full campaigns, including the latest failed repeat.
- `performance.md` / `performance.json`: completed and failed Native benchmark
  attempts, the corrected MemoryLab ABBA pair, raw file hashes, resource samples
  and actual Async delivery reconciliation. The initial misconfigured MemoryLab
  setup is excluded and retained.
- `native-comparison-1.json` / `native-comparison-4.json`: outputs of the existing
  normalized comparer; both fail guardrails. Optimization improvement targets
  are separate from this reliability change's non-regression requirement.
- `validation.json`: test totals and hashes of raw verification logs.
- [Final validation](validation-final.json): final runtime/harness test totals,
  source-equivalence check, documentation build and technical-report link checks,
  with the corresponding raw log hashes.
- `wal-strategy-profiles.json`: later Private/Shared/Private Native controls,
  including their original reports and failure/unknown accounting.
- `follower-long-and-lease.json`: retained follower volumes eventually pass a
  larger-budget diagnostic, preserving original bounded failures, plus baseline
  silent-follower burst probes. This does not pass the original 300-second gate.
- `dotnext-612-first-campaigns.json`: Native lease, retained-state forward replay
  and Shared/Private SDK controls before changing the packaged Windows default.
- `linux-file-offline-inspection.json`: original failed CI image read without
  writes; converged snapshot metadata and an intact originating copy of the
  unavailable file, with image hashes checked before and after inspection.
- `dependency-upgrade.json`: resolved direct/transitive package changes,
  distribution contents/hashes, unchanged System.* third-party notices, NuGet
  vulnerability audit and the exact revision validated by FOSSA.
- `windows-default-trial.json`: the withdrawn default experiment, same-binary
  Private/Shared HTTP controls and the failed complete Native SDK recovery.
- `job-snapshot-recovery.json`: retained snapshot repair (data verification
  succeeded but two old job receipts remain missing), both Native job failures
  and the passing managed job-recovery/API/restart campaign. It includes original
  and resumed parameter/runtime attribution and raw-file hashes.
- `native-7e-verification.json`: passing Native API/short-outage, complete official
  SDK and owner-timeout campaigns on `7e1f20a6`, plus Native replay of the original
  pre-repair archive. The latter still fails on two old job receipts, while its
  confirmed data passes verification. Large Stream latency remains a release gate.
- `journal-io-evidence.json`: the excluded cross-OS development-manifest setup
  error, two ordinary ENOSPC recoveries, a forced journal-write failure before the
  fix and the three-cycle after-fix recovery. The final run verifies a terminal
  job checkpoint across full supervisor restart and retains the same volume for
  the subsequent crashes/restart. Raw images and their archives remain local.
- `leader-event-evidence.json`: failed original and token-only leader-route
  controls, the passing managed leader-generation/owner-timeout regressions,
  exact runtime/harness attribution and the 1,996-test validation logs.
- `counter-archive-replay.json`: the original failed Native quorum report and
  archive hash, 25 converged observations after cold replay with its original
  executable, and before/after harness regression log hashes. This diagnostic
  does not change the failed endurance result.
- `final-native-build.json`: exact final Native source/binary attribution,
  successful compiler flags, failed-build log hashes and measured transient
  staging capacity. It also identifies the verified inactive-symbol archive;
  these build-environment results are separate from runtime recovery.
- [Final Native verification](native-a95-verification.json): complete API/short
  outages, official SDK, owner-timeout, leader-route and empty-PATH bundle results
  on the final runtime, with original parameters and log hashes.
- [Final six-role endurance](six-role-endurance-571.json): the entirely successful
  workflow, all eight original ZIP hashes and reports, including both quick OS
  jobs and every Linux/Native Windows outage role through 3,600 seconds.
  Unknown-ID arrays are summarized by operation/outcome with raw member hashes;
  complete IDs remain in the original archives. `compact-endurance-index.py`
  reproduces this summary without modifying an archive or campaign outcome.
  [The stronger counter audit](final-571-counter-audit.json) checks 66 observations
  against the original confirmed/unknown ledgers without changing any outcome.
- [Current performance](performance-current.md) / [raw data](performance-current.json):
  ten-second Native HTTP/scaling ABBA (two failed baselines, two complete
  candidates), fresh MemoryLab ABBA, phase resources and delivery reconciliation.
- [Targeted 2 MB / 2 MiB comparison](performance-2mb.md) /
  [raw data](performance-2mb.json): all four workloads pass; normalized guardrails
  differ between bracketing baselines. These results do not certify non-regression.
- [Replication traffic](replication-traffic.md): the first captures with packet
  drops remain recorded; the replacement ABBA passes all functional and zero-drop
  gates. [Linux replay/traffic evidence](linux-retained-replay-and-traffic.json)
  indexes both attempts, the successful original journal-image API/restart resume
  and the focused cold replay of the previously unavailable file.
  [Journal details](journal-resume-details.json) distinguish the permitted replay
  of one acknowledged job from missing work. Historical failed reports remain failed.

Large raw logs, journals and persistent volumes remain outside Git under
`artifacts/slimdata-lts/`, or inside the linked CI artifacts. A top-level file hash
index identifies retained raw logs; it is not a recursive integrity signature of
every WAL/file byte. CI ZIP SHA-256s identify the complete downloaded archives.
Original WSL images and run directories remain in `/root/.cache/slimdata-lts/`.

Export a local run without mutating its state:

```bash
python .bin/collect-local-chaos-evidence.py \
  --output artifacts/local-chaos/evidence.json \
  artifacts/local-chaos/quick artifacts/local-chaos/endurance-follower
```

Receipt duplicates are counted by `(message ID, function)` when a function name
is available. Legitimate independent-consumer fanout is therefore not counted as
duplicate execution. Counts from older journals with no function label retain
their original limitation. Acknowledgement races permit at-least-once replay.

## Reproduce the historical Windows performance setup

`scripts/` retains the experiment orchestration/summary sources, with subsequent
static-analysis cleanups that preserve their parameters and results. These
reuse the existing Local benchmark and MemoryLab executables; they are a dated
Windows setup, not a new portable benchmark driver. Restore them to their original
`artifacts/slimdata-lts/` location so their repository-root calculation is valid.
They expect the preserved `baseline-aot` / `terminal-aot` executables, the built
drivers, SDK under `artifacts/dotnet-10.0.403`, and Git Bash at its recorded path.
Runtime/driver hashes and all data-load commands appear in `performance.json`.

From the repository root in PowerShell:

```powershell
New-Item -ItemType Directory -Force artifacts/slimdata-lts | Out-Null
Copy-Item docs/reliability/2026-11-08/scripts/*.py artifacts/slimdata-lts/
python artifacts/slimdata-lts/run-paired-benchmarks.py `
  --output artifacts/slimdata-lts/fresh-http-comparison `
  --candidate-runtime artifacts/slimdata-lts/terminal-aot/SlimFaas.exe
```

Before running the copied `run-memory-lab-pair.py`, change its `OUTPUT` constant
to a fresh directory; the original intentionally refuses an existing output.
It declares the real `memory-function` expected by the unmodified MemoryLab
driver and reconciles distinct delivered element IDs and checksums. All four
runs retain their own persistent volumes. Run the two comparisons sequentially,
with builds and other workload campaigns stopped. The summary script rebuilds
this dated report from the complete retained dataset, including excluded and
failed attempts. For a fresh comparison, use the existing `SlimFaasBenchmark
compare` command and the new raw MemoryLab reports.

## Reproduce the final comparisons and retained-state diagnostics

The final experiment scripts are also stored in `scripts/`. Copy them to
`artifacts/slimdata-lts/` before execution. The Native driver scripts accept fresh
output paths; never reuse a failed run directory as a clean comparison. They
expect the baseline executable and final `final-a95-aot-minimal/SlimFaas.exe`,
the same built benchmark driver, SDK and Git Bash described above. Build inputs
first, then stop other campaigns/builds before measurements.

```powershell
Copy-Item docs/reliability/2026-11-08/scripts/*.py artifacts/slimdata-lts/
python artifacts/slimdata-lts/run-paired-native-a95.py `
  --output artifacts/slimdata-lts/fresh-full-http
python artifacts/slimdata-lts/run-paired-2mb-a95.py `
  --output artifacts/slimdata-lts/fresh-targeted-http
```

`run-memory-lab-a95-minimal.py` preserves the recorded MemoryLab configuration;
set its `OUTPUT` to a new path before running it. The summary scripts require the
complete recorded raw dataset at its original paths, including both failed full
baselines. `summarize-2mb-and-traffic.py` deliberately refuses existing outputs.
For new datasets use the existing benchmark comparer with their new raw reports.
GC totals sum generation-labelled counters and are not unique collection events.

Linux scripts record the owned WSL cache layout rather than installing it.
Prepare proper framework-dependent baseline/candidate publications, the chaos
harness/fixtures and retained archives at the recorded paths. Copy every required
input to the WSL ext4 filesystem **before** entering an isolated network namespace;
Windows-mounted 9p inputs can become unavailable there. Run as root in this
disposable namespace; `raft-traffic-probe.py` needs `CAP_NET_RAW` and
`CAP_NET_ADMIN`. Install it as `.bin/raft_traffic_probe.py`, run
`test-traffic-probe.py` with that directory on `PYTHONPATH`, then use
`run-traffic-comparison-4e80-buffered.sh` with a fresh output path. The observer
changes only its socket buffer, not a global sysctl. Capture bytes include
loopback framing/control/retransmits and cannot be interpreted as physical-wire
capacity.

`run-original-file-replay.sh` and `replay-original-file-image.py` preserve the
focused GET-only replay procedure. The journal-resume script instead resumes the
original owned image in place; preserve its pre-resume archive and parameter
files. Both diagnostic procedures retain their earlier failures and original
images. Inspect and adapt these dated paths before using a different host.

The counter audit reads original ZIPs without extracting or mutating state:

```powershell
python artifacts/slimdata-lts/audit-counter-archives.py `
  --output artifacts/slimdata-lts/fresh-counter-audit.json `
  (Get-ChildItem artifacts/slimdata-lts/ci-final-571-zips/*endurance*.zip).FullName
```
