# Agent Guidelines for SlimFaas Development

## 🎯 Overview

This document provides essential guidelines for AI agents (like GitHub Copilot) working on the **SlimFaas**, **SlimData**, **SlimFaasMcp**, and client packages. It covers compilation strategies, execution commands, testing procedures, and documentation requirements.

---

## 📦 Core Technologies

### SlimFaas, SlimData & SlimFaasMcp: AOT Compilation

**SlimFaas, SlimData, and SlimFaasMcp are compiled using Ahead-of-Time (AOT) compilation**, a .NET feature that compiles IL code directly to native machine code at build time.

#### Why AOT?

- **Slim Footprint**: Reduced binary size and memory usage
- **Faster Startup**: No JIT compilation overhead
- **Production Ready**: Ideal for containerized FaaS workloads
- **Cold-start Optimization**: Functions wake up instantly from scale-to-zero

#### AOT Configuration

The native .NET services below have `<PublishAot>true</PublishAot>` in their `.csproj` files:

- **SlimFaas** (`src/SlimFaas/SlimFaas.csproj`):
  - Target Framework: `.NET 10.0`
  - Full trimming enabled
  - Symbols stripped
  - Unsafe blocks allowed for performance

- **SlimData** (`src/SlimData/SlimData.csproj`):
  - Target Framework: `.NET 10.0`
  - Optimization preference: Size
  - Full trimming enabled

- **SlimFaasMcp** (`src/SlimFaasMcp/SlimFaasMcp.csproj`):
  - Target Framework: `.NET 10.0`
  - Full trimming enabled
  - Symbols stripped
  - Builds `ClientApp` with `npm ci` and `npm run build` before build/publish

#### Important AOT Considerations

- **Reflection Limitations**: Minimize runtime reflection; use code generation where needed
- **Type Safety**: Ensure all types used in serialization are discoverable at compile time
- **Native Dependencies**: Be careful with P/Invoke calls; verify they work across platforms
- **Dependencies**: Only use NuGet packages with AOT support (e.g., `KubernetesClient.Aot`, `MemoryPack`, `prometheus-net`)
- **Source-generated serialization**: Add new JSON payloads to existing `JsonSerializerContext` partials (for example `src/SlimFaasMcp/AppJsonContext.cs` or `src/SlimFaas/Local/ProcessControlContracts.cs`) and keep SlimData command payloads `MemoryPackable`.

---

## 🚀 Running the Project

### Prerequisites

- **.NET SDK**: Version `10.0.103` or later (see `global.json`)
- **Node.js**: Version `24` or later (for UI/dashboard builds)
- **pnpm**: Version `10.14.0` is used by CI for `src/SlimFaasSite` and `src/SlimFaasPlanetSaver`
- **Python/UV**: Python `>=3.10` with `uv` for `client/python/slimfaas-client`
- **Docker** or **Podman** (optional, for containerized deployments)

### Building

```bash
# Build the entire solution
dotnet build

# Fast backend-only SlimFaas build (skip embedded dashboard ClientApp)
dotnet build src/SlimFaas/SlimFaas.csproj -p:SkipClientAppBuild=true

# Build with AOT compilation (creates native executable)
dotnet publish -c Release

# Build a specific project
dotnet build src/SlimFaas/SlimFaas.csproj

# Build the documentation site
(cd src/SlimFaasSite && pnpm install --frozen-lockfile && pnpm build)
```

### Running Locally

```bash
# Run SlimFaas in development mode
dotnet run --project src/SlimFaas/SlimFaas.csproj

# Run with Kubernetes integration (requires k8s cluster)
dotnet run --project src/SlimFaas/SlimFaas.csproj

# Run examples
dotnet run --project src/Fibonacci/Fibonacci.csproj
dotnet run --project src/FibonacciBatch/FibonacciBatch.csproj

# Validate and run the native local demo (paths are relative to the src/SlimFaas launch profile)
dotnet run --project src/SlimFaas -- local validate -f ../../slimfaas.local.yaml
dotnet run --project src/SlimFaas -- local up -f ../../slimfaas.local.yaml
```

Use repeatable `-f` overlays such as `slimfaas.local.dev.yaml` or `slimfaas.local.debug.yaml`; `debugUrl` routes a function to an IDE process in native local mode.

### Docker

```bash
# Build Docker image
docker build -t slimfaas:latest .

# Run local Compose demo
docker-compose up

# Podman Compose on macOS
chmod +x ./run-podman-compose.sh
./run-podman-compose.sh up -d
```

---

## 🧪 Unit Tests

SlimFaas maintains comprehensive test coverage across multiple test projects.

### Test Projects

- **SlimFaas.Tests** (`tests/SlimFaas.Tests/`)
  - Core SlimFaas functionality: HTTP proxy, workers, scaling logic
  - Metrics, replicas synchronization, history
  - Data handling and endpoints

- **SlimData.Tests** (`tests/SlimData.Tests/`)
  - Raft-based cluster consensus
  - Key-value operations
  - File-based persistence
  - Command serialization

- **SlimFaasMcp.Tests** (`tests/SlimFaasMcp.Tests/`)
  - MCP (Model Context Protocol) integration tests

- **SlimFaasKafka.Tests** (`tests/SlimFaasKafka.Tests/`)
  - Kafka connector and lag monitoring

- **SlimFaasClient.Tests** (`client/dotnet/SlimFaasClient/tests/`)
  - .NET WebSocket client registration, async handling, sync streaming

- **Python slimfaas-client tests** (`client/python/slimfaas-client/tests/`)
  - Python WebSocket client behavior with `uv run pytest`

- **SlimFaasPlanetSaver tests** (`src/SlimFaasPlanetSaver/src/*.spec.jsx`)
  - JavaScript frontend helper behavior and coverage via Vitest

### Running Tests

```bash
# Run all unit tests
dotnet test

# Run with code coverage
dotnet test --collect "Code Coverage;Format=cobertura"

# Run specific test project
dotnet test tests/SlimFaas.Tests/SlimFaas.Tests.csproj

# Run specific test with verbose output
dotnet test --filter "ClassName=YourTestClass" --verbosity detailed

# Run .NET client tests
dotnet test client/dotnet/SlimFaasClient/tests/SlimFaasClient.Tests/SlimFaasClient.Tests.csproj

# Run Python client tests
(cd client/python/slimfaas-client && uv sync --extra dev && uv run pytest)

# Run SlimFaasPlanetSaver coverage (same package checked by CI)
(cd src/SlimFaasPlanetSaver && pnpm i --frozen-lockfile && pnpm run coverage)

# Watch mode (re-run on file changes)
dotnet watch test
```

### Code Coverage

Code coverage reports are generated during CI/CD and stored in `TestResults/` directories:

```bash
# View coverage (after test run)
open coveragereport/index.html
```

### Testing Best Practices

✅ **Always**:
- Write tests for new features or bug fixes
- Use meaningful test names (e.g., `WhenScalingUpWith_TenRequests_ShouldCreateNewReplicas()`)
- Mock external dependencies (Kubernetes API, HTTP calls)
- Test both success and failure paths
- Ensure tests are AOT-compatible (avoid reflection where possible)

❌ **Never**:
- Leave failing tests in the codebase
- Ignore test failures in CI/CD
- Write tests that depend on external services
- Use hardcoded delays instead of proper async/await patterns

---

## 📚 Documentation Requirements

### Golden Rule: Always Update Documentation

**Every code change that affects user-facing behavior, configuration, or architecture MUST be accompanied by documentation updates.**

### Documentation Files to Update

#### 1. **README.md** (Root)
Located at `/README.md`, this is the first impression:
- Update project description if scope changes
- Keep feature list current with new capabilities
- Update performance benchmarks if AOT compilation improves metrics
- Add/remove links to documentation sections as needed

#### 2. **Documentation Folder** (`./docs/`)
Public files registered in
`src/SlimFaasSite/src/lib/documentation-catalog.ts` are automatically
published to [slimfaas.dev](https://slimfaas.dev). Other files remain
technical references on GitHub.

- **`get-started.md`** – Deployment instructions
  - Add new deployment methods if available
  - Update prerequisites when SDK versions change
  - Include new environment variables

- **`native-local-mode.md`** – Native local CLI and process orchestration
  - Document `slimfaas local validate` / `slimfaas local up` behavior
  - Explain manifest overlays, `--env-file`, `--clean`, and `debugUrl`
  - Keep examples aligned with `slimfaas.local*.yaml`

- **`autoscaling.md`** – Scaling mechanisms
  - Document new PromQL trigger types
  - Explain scale-up/scale-down policies
  - Add examples of advanced scaling scenarios

- **`functions.md`** – Function definition and calling
  - Describe sync/async invocation changes
  - Document new function annotations
  - Explain timeout and retry behaviors

- **`clients.md`** – Official WebSocket clients
  - Document .NET and Python client registration/configuration changes
  - Keep sync streaming, async callbacks, and publish/subscribe examples current
  - Update `client/dotnet/SlimFaasClient/README.md` or `client/python/slimfaas-client/README.md` when package APIs change

- **`jobs.md`** – Job scheduling and execution
  - Document cron schedule syntax
  - Explain concurrency and retry configuration
  - Add job examples

- **`events.md`** – Internal publish/subscribe
  - Document event types and payloads
  - Show integration examples
  - Explain reliability guarantees

- **`data-files.md`** – Temporary binary artifact storage
  - Explain upload/download API
  - Document TTL and lifecycle
  - Show agentic workflow examples

- **`data-sets.md`** – Redis-like KV store
  - Document commands and consistency model
  - Explain replication and failover
  - Show use cases

- **`kafka.md`** – Kafka lag-based scaling
  - Document consumer lag monitoring
  - Explain wake-up from Kafka events
  - Provide integration examples

- **`planet-saver.md`** – JavaScript frontend wake-up helper
  - Document behavior modes such as `WakeUp`, `WakeUp+BlockUI`, and `None`
  - Keep examples aligned with `src/SlimFaasPlanetSaver/README.md`

- **`how-it-works.md`** – Architecture deep-dive
  - Document internal workers and components
  - Explain request flow for sync/async/jobs
  - Update diagrams if design changes

- **`opentelemetry.md`** – Observability
  - Document metrics, traces, and logs integration
  - Provide configuration examples
  - Explain sampling strategies

- **`user-interface.md`** – Built-in dashboard
  - Document UI features and navigation
  - Explain real-time message streaming
  - Add screenshots if UI changes

- **`mcp.md`** – Model Context Protocol
  - Document OpenAPI to MCP conversion
  - Explain tool generation
  - Provide integration examples

### Documentation Format & Style

- **Markdown (.md)** – Use standard GitHub-flavored Markdown
- **Code Examples** – Always include executable examples with language markers:
  ```bash
  # Shell commands
  dotnet test
  ```
  ```csharp
  // C# code
  var result = await function.InvokeAsync();
  ```
  ```json
  // JSON payloads
  {"function": "myFunc"}
  ```
- **Sections** – Use clear hierarchy: `# Title`, `## Section`, `### Subsection`
- **Cross-links** – Link to related documentation files
- **Images & Diagrams** – Store in `docs/` and use paths relative to the Markdown file

### Documentation Site Build

The documentation site is **automatically built and deployed** from `src/SlimFaasSite`:

- Sources: The checked-out `docs/` files and `README.md`
- Build Tool: Node.js + pnpm (see `.github/workflows/SiteBuild.yml` and the `deploy_website` job in `.github/workflows/main.yml`)
- Deployment: Published to https://slimfaas.dev
- No manual intervention needed; changes appear automatically
- Validate locally with `(cd src/SlimFaasSite && pnpm install --frozen-lockfile && pnpm build)`; a missing source in `src/SlimFaasSite/src/lib/documentation-catalog.ts` fails the build.

### Documentation Checklist

Before committing code changes:

- [ ] Did I modify user-facing behavior? → Update `README.md` if it's a major feature
- [ ] Did I add/change configuration options? → Update `get-started.md` or `how-it-works.md`
- [ ] Did I modify API endpoints? → Update `functions.md`, `jobs.md`, or `data-*.md`
- [ ] Did I change scaling behavior? → Update `autoscaling.md`
- [ ] Did I change demo-facing behavior? → Update `demo/deployment-*.yml`, `docker-compose.yml`, and `slimfaas.local*.yaml`
- [ ] Did I change data or performance-sensitive paths? → Run the relevant `.bin/*benchmark*.sh` or memory-lab command
- [ ] Are there new dependencies or AOT concerns? → Update `how-it-works.md`
- [ ] Did I test the documentation examples? → Ensure they still work
- [ ] Did I fix a bug that affects deployment? → Document the workaround or fix

---

## 🔄 CI/CD Pipeline

### Workflows

All workflows live in `.github/workflows/`:

1. **main.yml** – Primary CI/CD:
   - SonarCloud code quality analysis
   - Unit tests with code coverage
   - Automated versioning and tagging
   - Docker image builds and pushes
   - Native AOT release artifacts for SlimFaas and SlimFaasMcp
   - SlimFaasPlanetSaver npm publish and documentation site deployment on `main`

2. **SiteBuild.yml** – Reusable documentation site build:
   - Builds Markdown documentation to static site
   - Used by external-contributor checks; deployment happens in `main.yml`

3. **Docker.yml** – Container builds:
   - Creates multi-platform Docker images
   - Publishes to Docker Hub

4. **main-external-contrib.yml** – External contributor CI:
   - Defines unit tests, SlimFaasPlanetSaver coverage, Docker builds without push, and site build checks

5. **publish-dotnet-nuget.yml** – .NET client package:
   - Builds, tests, packs, and publishes `client/dotnet/SlimFaasClient`

6. **publish-python-pypi.yml** – Python client package:
   - Uses `uv`, builds, tests, and publishes `client/python/slimfaas-client`

7. **scorecard.yml** – Supply-chain security:
   - Runs OpenSSF Scorecard analysis and uploads SARIF results

### Running Locally Before Commit

```bash
# Run SonarCloud analysis (requires setup)
dotnet sonarscanner begin ...
dotnet build
dotnet sonarscanner end ...

# Run unit tests with coverage
dotnet test --collect "Code Coverage;Format=cobertura"

# Build UI components
(cd src/SlimFaasPlanetSaver && pnpm i --frozen-lockfile && pnpm run coverage)

# Build documentation site
(cd src/SlimFaasSite && pnpm install --frozen-lockfile && pnpm build)

# Validate the native local end-to-end demo before runtime/config changes are considered done
dotnet run --project src/SlimFaas -- local validate -f ../../slimfaas.local.yaml
dotnet run --project src/SlimFaas -- local up -f ../../slimfaas.local.yaml

# While local up is running, smoke-test the whole chain from another terminal
curl http://127.0.0.1:30020/status-functions
curl http://127.0.0.1:30020/function/fibonacci1/hello/local

# Quick performance smoke tests for data/performance-sensitive changes
WARMUP_SECONDS=2 DURATION_SECONDS=5 REPETITIONS=1 .bin/slimdata-benchmark.sh
BENCHMARK_PHASE=screening SCREENING_DURATION_SECONDS=10 SCREENING_WARMUP_SECONDS=5 .bin/slimdata-batch-modes-benchmark.sh
```

---

## 🛠️ Development Tips for Agents

### When Making Code Changes

1. **Preserve AOT Compatibility**
   - Avoid `Type.GetType()` and reflection-based lookups
   - Use dependency injection instead of service locators
   - Ensure serialization libraries support AOT (e.g., `MemoryPack`, not `Newtonsoft.Json`)

2. **Run Full Test Suite**
   - Always: `dotnet test`
   - Verify no regressions in existing tests

3. **Update Documentation in Parallel**
   - Don't create orphaned documentation
   - Link new docs from `README.md` or related files
   - Test documentation examples

4. **Keep Demos Working**
   - If code changes affect user-facing configuration, sample workloads, ports, images, or annotations, update the matching demo files: `demo/deployment-*.yml`, `docker-compose.yml`, and `slimfaas.local*.yaml`.
   - After runtime, configuration, function routing, data, queue, job, or UI changes, run the native local chain with `dotnet run --project src/SlimFaas -- local up -f ../../slimfaas.local.yaml` and verify `http://127.0.0.1:30020/status-functions` plus a demo function path such as `/function/fibonacci1/hello/local`.

5. **Consider Performance**
   - SlimFaas is designed for **slim footprint and fast execution**
   - Avoid large allocations; use pooling/streaming where possible
   - Profile impact on memory and startup time
   - For changes touching `src/SlimData/`, `src/SlimFaas/Data/`, batching, queues, metrics cardinality, HTTP client pooling, or high-throughput request paths, run the relevant performance tests: `.bin/slimdata-benchmark.sh`, `.bin/slimdata-batch-modes-benchmark.sh`, `.bin/memory-lab.sh`, or `dotnet run --project src/SlimFaasBenchmark/SlimFaasBenchmark.csproj`.

6. **Kubernetes-First Mindset**
   - Test with proper Kubernetes API interactions
   - Use annotations for configuration
   - Consider multi-pod scenarios (leader election, state sync)

7. **Keep WebSocket Clients and Runtime in Sync**
   - Runtime WebSocket code lives in `src/SlimFaas/WebSocket/`; official clients live in `client/dotnet/SlimFaasClient/` and `client/python/slimfaas-client/`.
   - If registration payloads, callbacks, or sync streaming change, update `docs/clients.md`, both client READMEs, and the relevant client tests.
   - `FunctionName` / `function_name` must not match an existing Kubernetes Deployment, and clients sharing the same name must use identical configuration.

8. **Validate Native Local Mode Changes**
   - CLI and manifest orchestration live under `src/SlimFaas/Local/`.
   - Validate with `dotnet run --project src/SlimFaas -- local validate -f ../../slimfaas.local.yaml` and use `slimfaas.local.debug.yaml` for `debugUrl` IDE routing scenarios.

### File Organization

```
SlimFaas/
├── src/
│   ├── SlimFaas/              # Main FaaS runtime (AOT-compiled)
│   ├── SlimData/              # Embedded Raft-based KV store (AOT-compiled)
│   ├── SlimFaasKafka/         # Kafka connector
│   ├── SlimFaasMcp/           # MCP runtime (AOT-compiled) + ClientApp
│   ├── SlimFaasSite/          # Next.js documentation site
│   ├── SlimFaasPlanetSaver/   # JS wake-up UX helper package
│   └── Fibonacci*/CalculatorApi/GmailMailerApi/... # Demo applications
├── client/
│   ├── dotnet/SlimFaasClient/ # .NET WebSocket client package + tests
│   └── python/slimfaas-client/ # Python WebSocket client package + tests
├── .bin/                       # Benchmark and release helper scripts
├── tests/
│   ├── SlimFaas.Tests/
│   ├── SlimData.Tests/
│   └── ...
├── tools/SlimFaas.MemoryLab/   # Memory/performance workload tool
├── docs/                      # Markdown docs and assets → published to web
├── demo/                      # Kubernetes/Compose deployment manifests
├── README.md                   # Project overview
├── AGENTS.md                   # This file
└── global.json                 # .NET SDK version
```

---

## 📋 Summary

| Aspect | Details |
|--------|---------|
| **Compilation** | AOT-first; SlimFaas, SlimData & SlimFaasMcp use `.NET 10.0` with `PublishAot: true` |
| **Build** | `dotnet build` or `dotnet publish -c Release` for native executable |
| **Test** | `dotnet test --collect "Code Coverage;Format=cobertura"`; run client/JS tests when those packages change |
| **Run** | `dotnet run --project src/SlimFaas/`, `slimfaas local up`, or Docker/Podman Compose |
| **Docs** | Update `README.md` + relevant `docs/*.md` registered in `DOCUMENTATION_CATALOG`; auto-published to website |
| **AOT Tips** | Avoid reflection, use MemoryPack, test with `dotnet publish` |

---

## 📞 Questions?

- **Architecture**: See `docs/how-it-works.md`
- **Deployment**: See `docs/get-started.md`
- **Local Mode**: See `docs/native-local-mode.md`
- **Scaling**: See `docs/autoscaling.md`
- **Clients**: See `docs/clients.md`
- **Contributing**: See `CONTRIBUTING.md`

---

**Last Updated**: 2026-07-31
**Target Audience**: AI Agents, Contributors, Maintainers
