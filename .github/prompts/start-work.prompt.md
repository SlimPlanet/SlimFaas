---
mode: agent
description: Create the git worktree and branch for a task, run the first build and the tests of the touched area to establish a green baseline.
---

# start-work

Open [docs/sdlc/start-work.md](../../docs/sdlc/start-work.md) and follow it step by step; this prompt only summarises it. Project rules come from [AGENTS.md](../../AGENTS.md).

- Maintainers: `git worktree add .claude/worktrees/<slug> -b claude/<slug> origin/main`; the Copilot coding agent uses its own `copilot/*` branch.
- First build: `dotnet build src/SlimFaas/SlimFaas.csproj -p:SkipClientAppBuild=true`, then the test project of the area you will touch.
- Record a failing baseline in the pull request instead of fixing it silently.
- Commit with `type(scope): summary`; never write `release`, `alpha` or `beta` in a commit message unless publishing is intended.

When running as the coding agent, skip the worktree step: you already are on a `copilot/*` branch.
