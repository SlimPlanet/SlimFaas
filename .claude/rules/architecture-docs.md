# Architecture documentation (always applies)

- Any change to components, workers, endpoints, request/queue/job flows, state machines, persistence or Raft behaviour, cross-project contracts (WebSocket protocol, MemoryPack commands, JSON serializer contexts) or public configuration must update the architecture page mapped to that code area, in the same pull request. The code-to-page map is the table in `docs/architecture/README.md`.
- Architecture is described with Mermaid blocks: `flowchart` for components and dependencies, `sequenceDiagram` for flows, `stateDiagram-v2` for lifecycles. Diagram nodes use the real type names from the code, and every diagram is followed by a `Source:` line listing the code paths it covers.
- Every architecture page ends with `Last verified: YYYY-MM-DD against <short SHA>`. When a change in a mapped area does not alter the architecture (rename, pure refactor, bug fix), re-read the page and bump that line anyway.
- When no page covers the code you change, create `docs/architecture/<area>.md` from `docs/architecture/TEMPLATE.md` and add a row to the map.
- Run `python .bin/check-architecture-docs.py` before opening a pull request; when it fails, follow `docs/sdlc/document-architecture.md` (`/document-architecture`).
- Existing pages without diagrams are upgraded the first time their code area is touched; do not rewrite unrelated pages.
