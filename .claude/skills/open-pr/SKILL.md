---
name: open-pr
description: Open a pull request with a Conventional Commits title that will become the squash commit, the full template filled in, and no publishing marker words.
disable-model-invocation: true
---

# open-pr

Read `docs/sdlc/open-pr.md` and follow it step by step; this skill only summarises it. Project rules come from `AGENTS.md` (imported by `CLAUDE.md`).

- Title `type(scope): imperative summary` with a lower-case scope (`slimfaas`, `slimdata`, `mcp`, `demo`, `doc`, `ci`, `sdlc`); `feat` = minor, `fix` = patch, `BREAKING` = major.
- Never put the words `release`, `alpha` or `beta` in the title or any branch commit unless a publish is intended.
- Body from `.github/pull_request_template.md`: summary, `Closes #N`, checks run, documentation and architecture sections, licenses, screenshots.
- `gh pr create --title "..." --body-file pr.md`, then `gh pr checks <number> --watch`.

Write the body to a file in the scratchpad first and show it to the user. The PreToolUse hook runs `python .bin/check-before-pr.py` before `gh pr create`; fix what it reports. Ask before pushing.
