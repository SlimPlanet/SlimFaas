---
name: slimfaas-local-release
description: Validate the SlimFaas native local CLI, process orchestration, Docker and Podman demos, dedicated Kubernetes deployments, and standalone Native AOT demo bundles.
---

# Validate local runtime and release bundles

## Native local mode

The local CLI and manifest orchestration live in `src/SlimFaas/Local/`.
Validate the actual manifest with the .NET SDK declared in `global.json`.
The following paths are relative to the SlimFaas launch profile.

```bash
dotnet run --project src/SlimFaas -- local validate -f ../../slimfaas.local.yaml
dotnet run --project src/SlimFaas -- local up -f ../../slimfaas.local.yaml
```

With the demo running, exercise readiness and a function in another terminal.

```bash
curl --fail http://127.0.0.1:30020/status-functions
curl --fail http://127.0.0.1:30020/function/fibonacci1/hello/local
```

Use repeatable `-f` overlays such as `slimfaas.local.dev.yaml` and
`slimfaas.local.debug.yaml`. The latter exercises `debugUrl` routing to an IDE
process. Validate shutdown and persisted state when changing process lifecycle,
data or recovery paths. Stop the supervisor after the smoke test and verify
that the owned node and function processes exit.

## Deployment demos

Use the repository root as the build context for each product Dockerfile.
Keep `demo/deployment-*.yml`, `docker-compose.yml` and
`slimfaas.local*.yaml` aligned with code changes affecting ports,
annotations, environment variables, images and sample workloads.

```bash
docker build -t slimfaas:latest .
docker compose up -d
./run-podman-compose.sh up -d
```

For Kubernetes validation, select the intended kubeconfig and context
explicitly and use a dedicated test namespace. Verify readiness and behavior
on multiple pods for scaling, routing and consensus changes.

## Standalone bundle

Use the host RID. Packaging needs .NET 11, Node 26 and Python 3.11 or newer.
The native release workflow builds and exercises all five supported RIDs.

```bash
python3 .bin/package-local-demo.py --rid osx-arm64 --version local-check
python3 .bin/test-local-bundle.py artifacts/local-demo/SlimFaas-Local-osx-arm64.zip
python3 .bin/test-local-bundle-helpers.py
python3 .bin/test-install-local-demo.py
```

Preserve the bundled manifest overlay, guided tour and Bruno fixtures. The
smoke test runs the native runtime and packaged workloads with an empty
`PATH`. Readiness can retry temporary startup failures within its deadline.
Function and job mutations run once. Cleanup may accept an absent job after
observing successful completion. Verify the supervisor's exit code and use
a bounded retry when releasing extracted files still held by a stopping node
on Windows.
