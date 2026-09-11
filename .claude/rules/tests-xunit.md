---
paths:
  - "tests/**/*.cs"
  - "client/dotnet/**/tests/**/*.cs"
---

# Tests: xUnit and Moq

- xUnit with Moq; mock Kubernetes, HTTP and time; tests never depend on external services.
- Name tests `Subject_Behavior[_WhenCondition]` with underscores, for example `CheckBindingsAsync_WakesFunction_WhenPendingExceedsThresholdAndNoCooldown`.
- Cover success and failure paths; every bug fix ships with a regression test.
- No hardcoded delays or wall-clock assertions: use async/await, fake time providers and explicit deadlines.
- Never leave a failing test or a skip without a reason. Internals are reachable through `InternalsVisibleTo`; do not widen visibility for tests.
- Run `dotnet test tests/<Project>.Tests/<Project>.Tests.csproj` for the touched area, `dotnet test --filter "ClassName=YourTestClass" --verbosity detailed` for one class, and `dotnet test` before opening a pull request.
