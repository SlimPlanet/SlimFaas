# Review a pull request

Goal: a review that checks what CI cannot: design against `AGENTS.md`, documentation and diagrams, intent of the title, and the release marker.

## Checklist

**Scope and title**

- [ ] The PR does one thing; the title is `type(scope): summary` and its type matches the change (`feat` vs `fix` decides the version bump).
- [ ] No `release`, `alpha` or `beta` in the title, body or commits unless the author and a maintainer intend to publish ([`release.md`](release.md)).
- [ ] `Closes #N` or `Part of #N, phase k` is present when an issue exists; the plan issue is still accurate.

**Code**

- [ ] AOT safety in `src/SlimFaas`, `src/SlimData`, `src/SlimFaasMcp`: no reflection lookups, new JSON types registered in the `JsonSerializerContext` partials, SlimData commands `MemoryPackable`, only AOT-capable packages.
- [ ] Kubernetes multi-pod behaviour considered (leader election, state sync) where relevant.
- [ ] Performance-sensitive paths (SlimData, queues, batching, metrics cardinality, HTTP pooling) come with benchmark numbers.
- [ ] UI changes use BEM classes and the central brand colour token; screenshots are attached.

**Tests**

- [ ] New behaviour and bug fixes have tests; names follow `Subject_Behavior[_WhenCondition]`; success and failure paths covered.
- [ ] No hardcoded delays, no dependency on external services.

**Documentation**

- [ ] User-facing behaviour, configuration and API changes are documented in the right `docs/*.md` page; new public pages are registered in `src/SlimFaasSite/src/lib/documentation-catalog.ts`.
- [ ] Demo manifests (`demo/deployment-*.yml`, `docker-compose.yml`, `slimfaas.local*.yaml`) still match the change.
- [ ] Client changes update `docs/clients.md`, both client READMEs and the client tests.

**Architecture** ([`docs/architecture/README.md`](../architecture/README.md))

- [ ] The architecture page mapped to the changed code area is in the diff, or the author explained why the `Last verified:` bump is enough.
- [ ] Diagrams are Mermaid, name the real types in the diff, and are followed by a `Source:` line; the prose matches the code.
- [ ] A new page, when created, has a row in the map table.

**Dependencies**

- [ ] FOSSA License Compliance is green when a dependency or lockfile changed; temporary holds are recorded in the summary.

**CI**

- [ ] SonarCloud, Unit Tests, Dashboard, Documentation, tags dry run and Docker builds are green; the dry-run version is the one expected from the title.

## Running the change locally

```bash
gh pr checkout <number>
dotnet build src/SlimFaas/SlimFaas.csproj -p:SkipClientAppBuild=true
dotnet test
python .bin/check-architecture-docs.py --base origin/main
```

Add the gates of [`pre-pr-checklist.md`](pre-pr-checklist.md) that apply to the touched areas (AOT publish, dashboards, clients, native local chain, benchmarks).

## Verdict

```bash
gh pr review <number> --approve --body "..."
gh pr review <number> --request-changes --body "..."
```

Request changes for anything in the checklist above; prefer a comment for style preferences. Only maintainers merge; they set the final squash title and, when publishing, the `(release)` marker.

## Related

- `AGENTS.md` – Development Tips for Agents, Testing Best Practices, Documentation Checklist
- `GOVERNANCE.md` §2.2, §5
- Claude Code skill `/review`, GitHub Copilot prompt `/review`
