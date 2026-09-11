---
mode: agent
description: Open a pull request with a Conventional Commits title that will become the squash commit, the full template filled in, and no publishing marker words.
---

# open-pr

Open [docs/sdlc/open-pr.md](../../docs/sdlc/open-pr.md) and follow it step by step; this prompt only summarises it. Project rules come from [AGENTS.md](../../AGENTS.md).

- Title `type(scope): imperative summary` with a lower-case scope (`slimfaas`, `slimdata`, `mcp`, `demo`, `doc`, `ci`, `sdlc`); `feat` = minor, `fix` = patch, `BREAKING` = major.
- Never put the words `release`, `alpha` or `beta` in the title or any branch commit unless a publish is intended.
- Body from `.github/pull_request_template.md`: summary, `Closes #N`, checks run, documentation and architecture sections, licenses, screenshots.
- `gh pr create --title "..." --body-file pr.md`, then `gh pr checks <number> --watch`.

Run `python .bin/check-before-pr.py` from the repository root before creating the pull request and fix what it reports.
