# Plan a change in an issue

Goal: turn a non-trivial request into an issue with phases that each fit in one reviewable pull request, before any code is written.

## When to plan

- The change touches more than one project (`src/SlimFaas`, `src/SlimData`, `src/SlimFaasMcp`, clients, dashboard, site) or more than one architecture page.
- It needs several pull requests, a migration, a new configuration surface, or a benchmark to justify it.
- A maintainer asked for a plan, or the request is ambiguous.

Small bug fixes and single-file changes do not need an issue plan: go to [`start-work.md`](start-work.md).

## Steps

1. **Understand the rules and the current design.** Read [`AGENTS.md`](../../AGENTS.md), [`docs/how-it-works.md`](../how-it-works.md) and the architecture pages mapped to the code areas involved ([`docs/architecture/README.md`](../architecture/README.md)). Search for similar existing code before proposing new abstractions.
2. **Check the constraints that shape the design**: AOT (no reflection, source-generated serializers), BEM for any UI, CNCF/FOSSA for any dependency, Kubernetes multi-pod behaviour, performance-sensitive paths that require benchmarks.
3. **Split into phases.** Each phase is one pull request that leaves `main` releasable: build green, tests green, documentation and diagrams updated. Order phases so that risky or foundational work goes first.
4. **Name the documentation impact per phase**: `docs/*.md` pages, demo manifests (`demo/deployment-*.yml`, `docker-compose.yml`, `slimfaas.local*.yaml`) and the architecture pages that will need new or refreshed Mermaid diagrams.
5. **Define verification per phase**: test projects to run, benchmarks (`.bin/slimdata-benchmark.sh`, `.bin/slimdata-batch-modes-benchmark.sh`, `.bin/memory-lab.sh`), the native local chain (`slimfaas local up`), manual checks in the dashboard.
6. **Create the issue** with the *Implementation plan* form (`.github/ISSUE_TEMPLATE/plan.yml`): Context, Phases as a checklist, Documentation and architecture pages impacted, Verification, Out of scope. With the GitHub CLI:

   ```bash
   gh issue create --title "plan: <summary>" --label enhancement --body-file plan.md
   ```

7. **Keep the issue current.** Each pull request references its phase (`Part of #N, phase 2`); tick the phase when the PR merges; note scope changes in the issue, not only in PR descriptions.

## Output

An issue whose body reads as the plan of record: a reviewer can tell from it what each pull request will contain and how it will be verified.

## Related

- `AGENTS.md` – Development Tips for Agents, Documentation Requirements
- [`docs/architecture/README.md`](../architecture/README.md) – which pages a phase must update
- Claude Code skill `/plan-issue`, GitHub Copilot prompt `/plan-issue`
