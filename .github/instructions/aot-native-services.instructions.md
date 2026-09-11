---
applyTo: "src/SlimFaas/**/*.cs,src/SlimData/**/*.cs,src/SlimFaasMcp/**/*.cs,src/SlimFaas/*.csproj,src/SlimData/*.csproj,src/SlimFaasMcp/*.csproj"
---

<!-- Mirror of .claude/rules/aot-native-services.md, keep in sync (checked by .bin/check-agent-rules.py) -->
# AOT-compiled services: SlimFaas, SlimData, SlimFaasMcp

- These projects publish with `PublishAot=true` and full trimming on .NET 10. Code that only works with the JIT is a bug, even if `dotnet build` and the tests pass.
- Avoid `Type.GetType()`, reflection-based lookups and service locators; use dependency injection and source generation.
- Use only AOT-capable packages (`KubernetesClient.Aot`, `MemoryPack`, `prometheus-net`); never `Newtonsoft.Json`.
- Register new JSON payloads in the existing `JsonSerializerContext` partials (`src/SlimFaasMcp/AppJsonContext.cs`, `src/SlimFaas/Local/ProcessControlContracts.cs`) and keep SlimData command payloads `MemoryPackable`.
- Keep the footprint slim: avoid large allocations, prefer pooling and streaming, watch startup time; run the benchmarks listed in `AGENTS.md` for performance-sensitive paths.
- Fast inner loop: `dotnet build src/SlimFaas/SlimFaas.csproj -p:SkipClientAppBuild=true`. Verify AOT with `dotnet publish -c Release` before opening a pull request; a new trimming or reflection warning is a failure.
