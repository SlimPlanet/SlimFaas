# Bounded Kubernetes producer-receipt reproduction

This historical reproducer compares runtime `1ecba4d1` with `766ada9b`, using the
same measured workload driver and observer. The self-contained controller uses
an extracted host-isolation utility and additionally rejects managed-OOM messages
in current runtime logs. It is a diagnostic, not the formal performance matrix.
Every pass sends 200 × 1 MiB at concurrency 16 and retains the full 180-second
post-send observation. Its fsynced per-ID journals affect workload cost.

## Prerequisites and fresh resources

Use the dedicated four-node kind cluster described in
[the benchmark guide](../../../benchmarking.md). Its context must be
`kind-slimfaas-pr506`, kubeconfig `artifacts/pr506/kubernetes/kubeconfig`, and all
nodes must mount VM-native `/var/home/core/slimfaas-pr506` at `/evidence`.
Configure rootless Podman/cgroup v2 prerequisites before cluster creation.
These scripts never select the default Kubernetes context.

Have at least 20 GiB free on both the Mac and VM. Stop competing builds, load tests
and fault campaigns. Retain every old namespace, volume and observation; use
fresh campaign names for every replay. A pre-existing namespace or VM state root
is rejected before the diagnostic starts.

## Build both images from their sources

Use fresh local tags; do not overwrite the frozen validation images.
All Dockerfiles use the repository root as context. ARM64 images are managed,
trimmed .NET 11; these results must not be compared with macOS Native AOT.

```bash
export KIND_EXPERIMENTAL_PROVIDER=podman
mkdir -p artifacts/pr506/repro-sources
git worktree add --detach artifacts/pr506/repro-sources/before 1ecba4d13d29ae68909979e168ff5b53aae6fea1
git worktree add --detach artifacts/pr506/repro-sources/after 766ada9b9b92b5ad520c27c43b4ba770bce45e33
for variant in before after; do
  source_dir="artifacts/pr506/repro-sources/$variant"
  source_sha=$(git -C "$source_dir" rev-parse HEAD)
  runtime_image="localhost/slimfaas-pr506:receipt-repro-$variant-runtime"
  tools_image="localhost/slimfaas-pr506:receipt-repro-$variant-tools"
  podman build --label "org.opencontainers.image.revision=$source_sha"     -f "$source_dir/Dockerfile" -t "$runtime_image" "$source_dir"
  podman build --build-arg "RUNTIME_IMAGE=$runtime_image"     -f "$source_dir/benchmarks/platform/Dockerfile" -t "$tools_image" "$source_dir"
  kind load docker-image --name slimfaas-pr506 "$tools_image"
done
```

The checked-in `variants.json` records the original images and executable hashes.
For source rebuilds, record their actual hashes rather than claiming identity
with an earlier binary. This diagnostic uses the Python workload; the separately
built benchmark DLLs are not part of its measurement.

## Copy and configure the reproducible workload

```bash
profile=artifacts/pr506/profiles/producer-receipts-repro
mkdir -p "$profile"
cp docs/reliability/2026-11-15/producer-receipts-reproducer/*.py "$profile/"
cp docs/reliability/2026-11-15/producer-receipts-reproducer/variants.json "$profile/"
python3 - "$profile" <<'PYCONFIG'
import json, subprocess, sys
from pathlib import Path
root = Path(sys.argv[1])
variants = json.loads((root / 'variants.json').read_text())
for variant in ['before', 'after']:
    image = f'localhost/slimfaas-pr506:receipt-repro-{variant}-tools'
    paths = ['/app/SlimFaas', '/app/SlimFaas.dll', '/app/SlimData.dll']
    raw = subprocess.check_output(['podman', 'run', '--rm', '--entrypoint',
                                   'sha256sum', image, *paths], text=True)
    hashes = {line.split()[1]: line.split()[0] for line in raw.splitlines()}
    metadata = json.loads(subprocess.check_output(['podman', 'image', 'inspect', image], text=True))[0]
    assert metadata['Config']['Labels']['org.opencontainers.image.revision'] == variants[variant]['source']
    variants[variant]['image'] = image
    variants[variant]['image_config_sha256'] = metadata['Id']
    variants[variant]['hashes'] = dict(zip(['runtime', 'slimfaas_dll', 'slimdata_dll'],
                                         [hashes[path] for path in paths]))
(root / 'variants.json').write_text(json.dumps(variants, indent=2) + '\n')
# Choose another fresh, short prefix for every later execution.
for name in ['run-one.py', 'run-abba.py']:
    path = root / name
    path.write_text(path.read_text().replace('slimfaas-pr506-ps07-',
                                            'slimfaas-pr506-repro01-'))
PYCONFIG
python3 "$profile/test-observer.py"
# No compilation or other campaigns while this command is running.
python3 "$profile/run-abba.py"
```

The default geometry is 4 KiB. A separate 64 KiB diagnostic must use a fresh
profile directory and namespace prefix, set `chunk=65536` in its copied
`run-abba.py`, and update the scope text accordingly. Never rewrite a marker in
an existing WAL. Both variants use the same explicit geometry, one-shot fresh
bootstrap and normal probes. After the first verified fresh pod-zero start,
restarts never force cold bootstrap.

## Interpret and retain every result

The coordinator's exit zero means the diagnostic block completed, not that
all integrity checks passed. Inspect each phase's `passed` field in `abba.json`,
its `report.json`, `managed-oom-audit.json`, sampling errors, container restart
counts and function receipts. An unavailable sampler remains an error and does
not shorten the observation window. Log-occurrence counts are not counts of
distinct exceptions. Previous container incarnations can be absent from current
logs, so restart counts must also remain zero for healthy acceptance.

Outputs include manifests, source/image/executable provenance, per-ID receipts,
accepted versus unknown operation ledgers, shutdown proofs and hashes of retained
VM state. All writers stop before export. Missing at 180 seconds is a bounded
failure, not a proof of permanent loss; recovery must use retained state or
verified copies. Do not obtain a pass by discarding state, increasing deadlines
or ignoring duplicates.
