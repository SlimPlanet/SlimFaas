<!--
Thanks for contributing to SlimFaas. The full lifecycle is described in docs/sdlc/README.md.
Humans, GitHub Copilot and Claude Code follow the same checklist. Delete sections that do not apply.
-->

## Summary

<!-- What changes and why. Link the plan issue when there is one. -->

Closes #

## Pull request title

The PR title becomes the squash commit on `main` and drives versioning (see `docs/sdlc/open-pr.md` and `docs/sdlc/release.md`).

- [ ] Title follows `type(scope): imperative summary` (Conventional Commits: `feat` = minor, `fix` = patch, `BREAKING` in title or body = major)
- [ ] Title and commit messages do **not** contain the words `release`, `alpha` or `beta` unless this merge is meant to publish a version

## Checks run locally

<!-- Tick what you ran. See docs/sdlc/pre-pr-checklist.md for the exact commands. -->

- [ ] `dotnet test`
- [ ] `dotnet publish -c Release` (when `src/SlimFaas`, `src/SlimData` or `src/SlimFaasMcp` changed, AOT must still compile)
- [ ] Dashboard (`npm test`, `npm run build`), Planet Saver (`pnpm run coverage`) or client tests when those packages changed
- [ ] Documentation site: `(cd src/SlimFaasSite && pnpm install --frozen-lockfile && pnpm build)`
- [ ] Native local chain (`slimfaas local validate` / `local up`) when runtime, configuration, routing, data, queue, job or UI behaviour changed
- [ ] Benchmarks (`.bin/slimdata-benchmark.sh`, `.bin/slimdata-batch-modes-benchmark.sh`, `.bin/memory-lab.sh`) when SlimData, data, queue, metrics or high-throughput paths changed
- [ ] `python .bin/check-agent-rules.py` and `python .bin/check-architecture-docs.py` pass

## Documentation checklist

<!-- Mirrors the "Documentation Checklist" in AGENTS.md. -->

- [ ] User-facing behaviour changed → `README.md` (major feature), `docs/get-started.md` or `docs/how-it-works.md` (configuration)
- [ ] API endpoints changed → `docs/functions.md`, `docs/jobs.md` or `docs/data-*.md`; scaling changed → `docs/autoscaling.md`
- [ ] Demo-facing behaviour changed → `demo/deployment-*.yml`, `docker-compose.yml`, `slimfaas.local*.yaml`
- [ ] New public documentation page registered in `src/SlimFaasSite/src/lib/documentation-catalog.ts`
- [ ] Documentation examples were executed

## Architecture documentation

<!-- Required for any change to components, workers, endpoints, flows, state machines, persistence, Raft or cross-project contracts. See docs/architecture/README.md. -->

- [ ] Architecture page updated: `docs/…`
- [ ] Mermaid diagram added or refreshed (real type names, `Source:` line): …
- [ ] Or: no architectural change; `Last verified` bumped after re-reading the page because …

## Dependencies and licenses

- [ ] No dependency or lockfile change, **or** the FOSSA License Compliance check is green
- Temporary license-related version holds and their reason (required by `AGENTS.md`): none

## UI changes

<!-- Screenshots or recordings. Confirm BEM class names and the #0000ff brand token. -->
