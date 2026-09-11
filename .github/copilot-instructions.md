# GitHub Copilot instructions for SlimFaas

Read [`AGENTS.md`](../AGENTS.md) first: it is the single source of truth for project rules and is not duplicated here. This file only points to it and to the shared development lifecycle.

## Non-negotiables (details in AGENTS.md)

1. **AOT first.** `src/SlimFaas`, `src/SlimData` and `src/SlimFaasMcp` publish with `PublishAot=true`: no reflection lookups, source-generated JSON contexts, `MemoryPackable` SlimData commands, AOT-capable packages only.
2. **BEM** for every web UI style (`block__element--modifier`), brand blue `#0000ff` through the central token.
3. **CNCF/FOSSA license policy** for every dependency or lockfile change; record temporary version holds in the pull request.
4. **Documentation ships with the code**: user-facing behaviour, configuration and API changes update `docs/*.md`; public pages are registered in `src/SlimFaasSite/src/lib/documentation-catalog.ts`.
5. **Architecture ships with the code**: a change in a mapped code area updates its architecture page with Mermaid diagrams, real type names, `Source:` and `Last verified:` lines (`docs/architecture/README.md`); `python .bin/check-architecture-docs.py` must pass.
6. **Tests**: xUnit + Moq, `Subject_Behavior[_WhenCondition]` names, no external services, no hardcoded delays; `dotnet test` before a pull request.
7. **Pull request titles are squash commits**: `type(scope): summary`; never use the words `release`, `alpha` or `beta` in titles or commit messages unless a publish is intended (CI publishes on those substrings and the squash commit concatenates every branch commit message).

## Path-scoped instructions

`.github/instructions/*.instructions.md` apply automatically by file path (AOT services, SlimData, tests, web UI, docs and demos, clients, architecture docs). Their bodies mirror `.claude/rules/` for Claude Code; `python .bin/check-agent-rules.py` keeps them identical.

## Workflows

The lifecycle is documented in [`docs/sdlc/README.md`](../docs/sdlc/README.md). Each workflow is available as a prompt: `/plan-issue`, `/start-work`, `/document-architecture`, `/pre-pr-checklist`, `/open-pr`, `/release`, `/review` (`.github/prompts/`). The coding agent works on its own `copilot/*` branch and can skip the worktree step of `/start-work`.

Fill in [`.github/pull_request_template.md`](pull_request_template.md) completely, including the architecture documentation section.
