@AGENTS.md

# Claude Code notes for SlimFaas

`AGENTS.md` above is the single source of truth for project rules; do not duplicate it here.

- Path-scoped rules load from `.claude/rules/` when you read or edit matching files (AOT services, SlimData, tests, web UI, docs and demos, clients). `architecture-docs.md` always applies: code changes in a mapped area update the architecture page with Mermaid diagrams (`docs/architecture/README.md`).
- The development lifecycle is in `docs/sdlc/README.md`. Its workflows are skills: `/plan-issue`, `/start-work`, `/document-architecture`, `/pre-pr-checklist`, `/open-pr`, `/release`, `/review`. Read the linked `docs/sdlc/<name>.md` and follow it.
- Work in a git worktree under `.claude/worktrees/<slug>` (ignored by git) on branch `claude/<slug>`, created from `origin/main`.
- Commit messages follow `type(scope): summary` and never contain the words `release`, `alpha` or `beta` unless a publish is intended: the squash commit on `main` concatenates every branch commit message and CI publishes on those substrings.
- `.claude/settings.json` runs `python .bin/check-before-pr.py` before any `gh pr create`; fix what it reports instead of bypassing it. Ask before pushing or opening a pull request.
- Fill in `.github/pull_request_template.md` completely, including the architecture documentation section.
