---
mode: agent
description: Update or create the architecture page mapped to changed code, with Mermaid diagrams naming real types, Source and Last verified lines; use when check-architecture-docs.py fails or after changing components, flows, states or contracts.
---

# document-architecture

Open [docs/sdlc/document-architecture.md](../../docs/sdlc/document-architecture.md) and follow it step by step; this prompt only summarises it. Project rules come from [AGENTS.md](../../AGENTS.md).

- Find the page in the map table of `docs/architecture/README.md`; `python .bin/check-architecture-docs.py` prints what it expects.
- Choose the diagram type: `flowchart` for components, `sequenceDiagram` for flows, `stateDiagram-v2` for lifecycles; nodes use the real type names; add a `Source:` line under each diagram.
- No page yet: copy `docs/architecture/TEMPLATE.md` to `docs/architecture/<area>.md` and add a map row.
- Bump `Last verified: YYYY-MM-DD against <short SHA>` and run the checker again.

Read the changed code and the existing page before editing; keep unrelated diagrams untouched.
