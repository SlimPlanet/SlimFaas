# Reproduce the source49 Files catch-up failure

The [archive](file-admission-fault-reproducer-49.tar.gz), SHA-256
`d4d53cdfba4c3e057d709d3989426ea29be0b604b4c6ce3c64be952e58fa7a7a`,
contains the actual source49 profile. It includes both passing async controls and
the failing Files control. Do not substitute a corrected binary and attribute
its result to source49.

Use the dedicated rootless Podman kind cluster described in the
[campaign guide](../../slimdata-reliability.md). The kubeconfig must be
`artifacts/pr506/kubernetes/kubeconfig`, context `kind-slimfaas-pr506`, and every
kind node must mount `/var/home/core/slimfaas-pr506` at `/evidence`. The scripts
always pass this kubeconfig/context explicitly. Never use a production cluster.
Have at least 20 GiB free on the Mac and VM and stop competing campaigns/builds.
Preserve every old namespace, volume, quarantine directory, journal and result.

Extract a copy of the immutable profile:

```bash
mkdir -p artifacts/pr506/profiles
tar -xzf docs/reliability/2026-11-15/file-admission-fault-reproducer-49.tar.gz \
  -C artifacts/pr506/profiles
profile=artifacts/pr506/profiles/file-admission-fault-net11-27
```

The original image is local to the validation machine. On another machine,
build from the recorded source, using new tags:

```bash
source_sha=49e9801f25e4ef13c4b9dfeb9e9cf4a5ece107aa
source_dir=artifacts/pr506/repro-sources/file-admission49
mkdir -p artifacts/pr506/repro-sources
git worktree add --detach "$source_dir" "$source_sha"
runtime_image=localhost/slimfaas-pr506:file-admission49-repro-runtime
tools_image=localhost/slimfaas-pr506:file-admission49-repro-tools
podman build --label "org.opencontainers.image.revision=$source_sha" \
  -f "$source_dir/Dockerfile" -t "$runtime_image" "$source_dir"
podman build --build-arg "RUNTIME_IMAGE=$runtime_image" \
  --label "org.opencontainers.image.revision=$source_sha" \
  -f "$source_dir/benchmarks/platform/Dockerfile" -t "$tools_image" "$source_dir"
export KIND_EXPERIMENTAL_PROVIDER=podman
kind load docker-image --name slimfaas-pr506 "$tools_image"
```

Record the actual rebuilt hashes in the extracted copy. Rebuilt hashes are not
assumed to match the frozen image:

```bash
python3 - "$profile" "$tools_image" <<'PY'
from pathlib import Path
import json, subprocess, sys
root, image = Path(sys.argv[1]), sys.argv[2]
variants = json.loads((root / 'variants.json').read_text())
metadata = json.loads(subprocess.check_output(['podman', 'image', 'inspect', image], text=True))[0]
assert metadata['Config']['Labels']['org.opencontainers.image.revision'] == variants['after']['source']
paths = ['/app/SlimFaas', '/app/SlimFaas.dll', '/app/SlimData.dll']
raw = subprocess.check_output(['podman', 'run', '--rm', '--entrypoint', 'sha256sum', image, *paths], text=True)
hashes = {line.split()[1]: line.split()[0] for line in raw.splitlines()}
variants['after'].update(image=image, image_config_sha256=metadata['Id'],
    hashes=dict(zip(['runtime', 'slimfaas_dll', 'slimdata_dll'], [hashes[p] for p in paths])))
(root / 'variants.json').write_text(json.dumps(variants, indent=2) + '\n')
PY
```

Run one fresh namespace at a time; choose another suffix for every replay:

```bash
python3 "$profile/run-one.py" \
  --namespace slimfaas-pr506-fa27-repro-files-follower-01 \
  --chunk-size 65536 --variant after --offload-threshold 524288 \
  --fault-case volume-loss --ingress-role follower --kind file \
  --required-copies 2 --probes endurance
```

For the async controls use `--kind async` and a fresh namespace; test follower
and leader ingress separately. Each fault sends one 1 MiB request. Normal probes
are checked by separate healthy controls; the endurance probe profile allows
the intentional suspension. A source49 Files failure depends on peer order:
the unit regression fixes that ordering explicitly and preserves the original
failure. A passing replay does not erase the failed original.

The controller captures current/previous logs by pod UID/restart before stopping
owned writers and hashing the VM tree. The quarantined ingress content is never
restored. Read `report.json`, both log audits and `vm-file-index.json`; a valid
receipt alone does not establish an error-free engine. All workers share one VM
filesystem, so this tests logical volume-content loss and not independent-host
hardware failure. Source49 results and all raw hashes are in the
[application evidence](file-admission-application-evidence-01.json).
