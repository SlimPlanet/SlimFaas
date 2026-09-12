# Start work on a task

Goal: an isolated branch with a green baseline before the first change.

## Prerequisites

From `AGENTS.md` (Running the Project): .NET SDK `10.0.103` or later (`global.json`), Node.js 24, pnpm `10.14.0`, Python 3.10+ with `uv` for the Python client, Docker or Podman only for containerised demos.

## Branch and workspace

| Who | Where | Branch name |
|---|---|---|
| Maintainers with Claude Code | git worktree under `.claude/worktrees/<slug>` (ignored by git) | `claude/<slug>` |
| GitHub Copilot coding agent | branch created by the agent | `copilot/<generated>` |
| Other assistants | branch in the main repository | `<tool>/<slug>` (for example `codex/<slug>`) |
| External contributors | fork | any descriptive name |

Create a worktree from the current `main`:

```bash
git fetch origin
git worktree add .claude/worktrees/<slug> -b claude/<slug> origin/main
cd .claude/worktrees/<slug>
```

Claude Code can do the same with its built-in worktree support; keep the `claude/<slug>` branch name so the history stays consistent. The Copilot coding agent already runs on its own branch and skips this step.

## First build and baseline

```bash
# Fast backend-only SlimFaas build (skip embedded dashboard ClientApp)
dotnet build src/SlimFaas/SlimFaas.csproj -p:SkipClientAppBuild=true

# Tests of the area you are about to touch
dotnet test tests/SlimFaas.Tests/SlimFaas.Tests.csproj
dotnet test tests/SlimData.Tests/SlimData.Tests.csproj
dotnet test tests/SlimFaasMcp.Tests/SlimFaasMcp.Tests.csproj
dotnet test tests/SlimFaasKafka.Tests/SlimFaasKafka.Tests.csproj
```

For UI work: `(cd src/SlimFaas/ClientApp && npm ci --ignore-scripts && npm test)`. For the site: `(cd src/SlimFaasSite && pnpm install --frozen-lockfile && pnpm build)`. For clients: see `AGENTS.md` – Running Tests.

Record the baseline: if a test is already failing on `origin/main`, say so in the pull request instead of fixing it silently in an unrelated change.

## While working

- Read the architecture page mapped to the code area before changing it ([`docs/architecture/README.md`](../architecture/README.md)); update it as you go ([`document-architecture.md`](document-architecture.md)).
- Commit with Conventional Commit messages (`type(scope): summary`). Never write the words `release`, `alpha` or `beta` in a commit message unless you intend to publish ([`release.md`](release.md)).
- Keep demos and manifests in sync with user-facing changes (`demo/deployment-*.yml`, `docker-compose.yml`, `slimfaas.local*.yaml`).

## Cleanup after merge

```bash
git worktree remove .claude/worktrees/<slug>
git branch -d claude/<slug>
```

## Related

- `AGENTS.md` – Running the Project, Unit Tests
- Next: [`pre-pr-checklist.md`](pre-pr-checklist.md)
- Claude Code skill `/start-work`, GitHub Copilot prompt `/start-work`
