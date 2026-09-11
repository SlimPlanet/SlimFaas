---
paths:
  - "src/SlimFaas/WebSocket/**"
  - "client/**"
  - "docs/clients.md"
---

# WebSocket runtime and official clients stay in sync

- The runtime WebSocket code lives in `src/SlimFaas/WebSocket/`; the official clients in `client/dotnet/SlimFaasClient/` and `client/python/slimfaas-client/` must implement the same protocol.
- Any change to registration payloads, callbacks or sync streaming updates `docs/clients.md`, both client READMEs and the client tests in the same pull request.
- `FunctionName` / `function_name` must not match an existing Kubernetes Deployment; clients sharing a name must use identical configuration.
- The .NET client targets `net8.0;net9.0;net10.0`; keep public API changes backward compatible or flag them as `BREAKING`.
- Tests: `dotnet test client/dotnet/SlimFaasClient/tests/SlimFaasClient.Tests/SlimFaasClient.Tests.csproj` and `(cd client/python/slimfaas-client && uv sync --extra dev && uv run pytest)`.
- NuGet and PyPI packages are published manually with `workflow_dispatch`; see `docs/sdlc/release.md`.
