# Pre-pull-request checklist

Goal: nothing that CI or a reviewer would reject is left for them to find. Run the gates in order from the repository root; skip a gate only when its condition does not apply, and say so in the pull request.

Commands are the ones `AGENTS.md` and the CI workflows use.

## Gates

1. **Unit tests** (always)

   ```bash
   dotnet test
   # or, as CI does
   dotnet test --collect "Code Coverage;Format=cobertura"
   ```

2. **AOT publish** (when `src/SlimFaas`, `src/SlimData` or `src/SlimFaasMcp` changed)

   ```bash
   dotnet publish -c Release
   ```

   A trimming or reflection warning that did not exist before is a failure: fix it (source-generated serializers, no `Type.GetType()`).

3. **Dashboards and Planet Saver** (when `src/SlimFaas/ClientApp`, `src/SlimFaasMcp/ClientApp` or `src/SlimFaasPlanetSaver` changed)

   ```bash
   (cd src/SlimFaas/ClientApp && npm ci --ignore-scripts && npm test && npm run build && npm run build-storybook)
   (cd src/SlimFaasPlanetSaver && pnpm i --frozen-lockfile && pnpm run coverage)
   ```

4. **Clients** (when `src/SlimFaas/WebSocket`, `client/` or the registration protocol changed)

   ```bash
   dotnet test client/dotnet/SlimFaasClient/tests/SlimFaasClient.Tests/SlimFaasClient.Tests.csproj
   (cd client/python/slimfaas-client && uv sync --extra dev && uv run pytest)
   ```

5. **Documentation site** (when `docs/`, `README.md` or `src/SlimFaasSite` changed)

   ```bash
   (cd src/SlimFaasSite && pnpm install --frozen-lockfile && pnpm build)
   ```

   A new public page must be registered in `src/SlimFaasSite/src/lib/documentation-catalog.ts`; a missing source fails the build.

6. **Native local chain** (when runtime, configuration, function routing, data, queue, job or UI behaviour changed)

   ```bash
   dotnet run --project src/SlimFaas -- local validate -f ../../slimfaas.local.yaml
   dotnet run --project src/SlimFaas -- local up -f ../../slimfaas.local.yaml
   # from another terminal while local up is running
   curl http://127.0.0.1:30020/status-functions
   curl http://127.0.0.1:30020/function/fibonacci1/hello/local
   ```

7. **Benchmarks** (when `src/SlimData/`, `src/SlimFaas/Data/`, batching, queues, metrics cardinality, HTTP client pooling or high-throughput request paths changed)

   ```bash
   WARMUP_SECONDS=2 DURATION_SECONDS=5 REPETITIONS=1 .bin/slimdata-benchmark.sh
   BENCHMARK_PHASE=screening SCREENING_DURATION_SECONDS=10 SCREENING_WARMUP_SECONDS=5 .bin/slimdata-batch-modes-benchmark.sh
   .bin/memory-lab.sh
   dotnet run --project src/SlimFaasBenchmark/SlimFaasBenchmark.csproj
   ```

   Paste before/after numbers in the pull request.

8. **Documentation checklist** (always): go through the "Documentation Checklist" in `AGENTS.md` (README, `get-started.md`, `how-it-works.md`, `functions.md` / `jobs.md` / `data-*.md`, `autoscaling.md`, demo manifests, tested examples).

9. **Dependencies and licenses** (when a `.csproj`, `package.json`, `pyproject.toml` or any lockfile changed): the FOSSA License Compliance check must be green on the pull request; record every temporary license-related version hold and its reason in the PR summary (`AGENTS.md` – Dependency License Compliance).

10. **Rule mirror** (when `.claude/rules/` or `.github/instructions/` changed)

    ```bash
    python .bin/check-agent-rules.py
    ```

11. **Architecture pages** (always; it exits immediately when no mapped code changed)

    ```bash
    python .bin/check-architecture-docs.py
    ```

    On failure follow [`document-architecture.md`](document-architecture.md).

## Before you continue

- `git status` shows only intended files; no build output, no local secrets (`.env.local`, `slimfaas.local.secrets.yaml`).
- Commit messages follow `type(scope): summary` and contain none of the words `release`, `alpha`, `beta` unless publishing is intended.

## Related

- `AGENTS.md` – Running Locally Before Commit, Documentation Checklist
- Next: [`open-pr.md`](open-pr.md)
- Claude Code skill `/pre-pr-checklist`, GitHub Copilot prompt `/pre-pr-checklist`
