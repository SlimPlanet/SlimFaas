---
name: plan-issue
description: Plan a multi-phase or cross-project change as a GitHub issue with phases, documentation impact and verification, before writing code.
---

# plan-issue

Read `docs/sdlc/plan-issue.md` and follow it step by step; this skill only summarises it. Project rules come from `AGENTS.md` (imported by `CLAUDE.md`).

- Read `AGENTS.md` and the architecture pages mapped to the code areas involved before proposing a design.
- Split the work into phases that each fit one reviewable pull request and leave `main` releasable.
- For each phase, name the `docs/*.md` pages, demo manifests and architecture pages to update, and how it will be verified.
- Create the issue with the *Implementation plan* form and keep it current as pull requests merge.

Draft the plan in the conversation first; create the issue with `gh issue create --title "plan: ..." --label enhancement --body-file <file>` only after the user confirms.
