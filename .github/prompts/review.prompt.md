---
mode: agent
description: Review a SlimFaas pull request against AGENTS.md: AOT safety, BEM, tests, documentation, architecture diagrams, licenses, title and publishing markers; run it locally and give a verdict.
---

# review

Open [docs/sdlc/review.md](../../docs/sdlc/review.md) and follow it step by step; this prompt only summarises it. Project rules come from [AGENTS.md](../../AGENTS.md).

- Check scope and title (`type(scope): summary`, type matches the change, no marker words unless intended).
- Check AOT safety, multi-pod behaviour, benchmarks for performance paths, BEM for UI.
- Check tests (names, success and failure paths, no delays), docs (catalog, demos, clients) and the architecture page with its Mermaid diagrams and `Last verified:` line.
- `gh pr checkout <number>`, build, `dotnet test`, `python .bin/check-architecture-docs.py --base origin/main`; then `gh pr review --approve` or `--request-changes`.

Report findings ordered by severity with file and line; post the review only after the user confirms.
