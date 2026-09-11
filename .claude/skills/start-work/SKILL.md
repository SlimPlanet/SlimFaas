---
name: start-work
description: Create the git worktree and branch for a task, run the first build and the tests of the touched area to establish a green baseline.
---

# start-work

Read `docs/sdlc/start-work.md` and follow it step by step; this skill only summarises it. Project rules come from `AGENTS.md` (imported by `CLAUDE.md`).

- Maintainers: `git worktree add .claude/worktrees/<slug> -b claude/<slug> origin/main`; the Copilot coding agent uses its own `copilot/*` branch.
- First build: `dotnet build src/SlimFaas/SlimFaas.csproj -p:SkipClientAppBuild=true`, then the test project of the area you will touch.
- Record a failing baseline in the pull request instead of fixing it silently.
- Commit with `type(scope): summary`; never write `release`, `alpha` or `beta` in a commit message unless publishing is intended.

Use `$ARGUMENTS` as the slug when given (`/start-work my-change`). Create the worktree with the built-in worktree support or the git command above, keeping the `claude/<slug>` branch name.
