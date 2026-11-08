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

## Reproduce the recorded Windows performance setup

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
