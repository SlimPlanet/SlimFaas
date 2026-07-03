# Agent Guidelines for SlimFaas Development

## 🎯 Overview

This document provides essential guidelines for AI agents (like GitHub Copilot) working on the **SlimFaas** and **SlimData** projects. It covers compilation strategies, execution commands, testing procedures, and documentation requirements.

---

## 📦 Core Technologies

### SlimFaas & SlimData: AOT Compilation

**Both SlimFaas and SlimData are compiled using Ahead-of-Time (AOT) compilation**, a .NET feature that compiles IL code directly to native machine code at build time.

#### Why AOT?

- **Slim Footprint**: Reduced binary size and memory usage
- **Faster Startup**: No JIT compilation overhead
- **Production Ready**: Ideal for containerized FaaS workloads
- **Cold-start Optimization**: Functions wake up instantly from scale-to-zero

#### AOT Configuration

Both projects have `<PublishAot>true</PublishAot>` in their `.csproj` files:

- **SlimFaas** (`src/SlimFaas/SlimFaas.csproj`):
  - Target Framework: `.NET 10.0`
  - Full trimming enabled
  - Symbols stripped
  - Unsafe blocks allowed for performance

- **SlimData** (`src/SlimData/SlimData.csproj`):
  - Target Framework: `.NET 10.0`
  - Optimization preference: Size
  - Full trimming enabled

#### Important AOT Considerations

- **Reflection Limitations**: Minimize runtime reflection; use code generation where needed
- **Type Safety**: Ensure all types used in serialization are discoverable at compile time
- **Native Dependencies**: Be careful with P/Invoke calls; verify they work across platforms
- **Dependencies**: Only use NuGet packages with AOT support (e.g., `KubernetesClient.Aot`, `MemoryPack`, `prometheus-net`)

---

## 🚀 Running the Project

### Prerequisites

- **.NET SDK**: Version `10.0.103` or later (see `global.json`)
- **Node.js**: Version `24` or later (for UI/dashboard builds)
- **Docker** or **Podman** (optional, for containerized deployments)

### Building

```bash
# Build the entire solution
dotnet build

# Build with AOT compilation (creates native executable)
dotnet publish -c Release

# Build a specific project
dotnet build src/SlimFaas/SlimFaas.csproj
```

### Running Locally

```bash
# Run SlimFaas in development mode
dotnet run --project src/SlimFaas/SlimFaas.csproj

# Run with Kubernetes integration (requires k8s cluster)
dotnet run --project src/SlimFaas/SlimFaas.csproj

# Run examples
dotnet run --project src/Fibonacci/Fibonacci.csproj
dotnet run --project demo/
```

### Docker

```bash
# Build Docker image
docker build -t slimfaas:latest .

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

#### 2. **Documentation Folder** (`./documentation/`)
These files are **automatically published to the website** (https://slimfaas.dev):

- **`get-started.md`** – Deployment instructions
  - Add new deployment methods if available
  - Update prerequisites when SDK versions change
  - Include new environment variables

- **`autoscaling.md`** – Scaling mechanisms
  - Document new PromQL trigger types
  - Explain scale-up/scale-down policies
  - Add examples of advanced scaling scenarios

- **`functions.md`** – Function definition and calling
  - Describe sync/async invocation changes
  - Document new function annotations
  - Explain timeout and retry behaviors

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
- **Images & Diagrams** – Store in `documentation/` folder; reference with full GitHub URL

### Documentation Site Build

The documentation site is **automatically built and deployed** via the `SiteBuild` workflow:

- Trigger: Updates to `documentation/` or `README.md`
- Build Tool: Node.js + pnpm (see `.github/workflows/SiteBuild.yml`)
- Deployment: Published to https://slimfaas.dev
- No manual intervention needed; changes appear automatically

### Documentation Checklist

Before committing code changes:

- [ ] Did I modify user-facing behavior? → Update `README.md` if it's a major feature
- [ ] Did I add/change configuration options? → Update `get-started.md` or `how-it-works.md`
- [ ] Did I modify API endpoints? → Update `functions.md`, `jobs.md`, or `data-*.md`
- [ ] Did I change scaling behavior? → Update `autoscaling.md`
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

2. **SiteBuild.yml** – Documentation site:
   - Builds Markdown documentation to static site
   - Deploys to GitHub Pages or hosting provider

3. **Docker.yml** – Container builds:
   - Creates multi-platform Docker images
   - Publishes to Docker Hub

### Running Locally Before Commit

```bash
# Run SonarCloud analysis (requires setup)
dotnet sonarscanner begin ...
dotnet build
dotnet sonarscanner end ...

# Run unit tests with coverage
dotnet test --collect "Code Coverage;Format=cobertura"

# Build UI components
cd src/SlimFaasPlanetSaver
pnpm i --frozen-lockfile
pnpm run coverage
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

4. **Consider Performance**
   - SlimFaas is designed for **slim footprint and fast execution**
   - Avoid large allocations; use pooling/streaming where possible
   - Profile impact on memory and startup time

5. **Kubernetes-First Mindset**
   - Test with proper Kubernetes API interactions
   - Use annotations for configuration
   - Consider multi-pod scenarios (leader election, state sync)

### File Organization

```
SlimFaas/
├── src/
│   ├── SlimFaas/              # Main FaaS runtime (AOT-compiled)
│   ├── SlimData/              # Embedded Raft-based KV store (AOT-compiled)
│   ├── SlimFaasKafka/         # Kafka connector
│   ├── SlimFaasMcp/           # MCP runtime
│   └── examples/              # Demo applications
├── tests/
│   ├── SlimFaas.Tests/
│   ├── SlimData.Tests/
│   └── ...
├── documentation/             # Markdown docs → published to web
├── README.md                   # Project overview
├── agent.md                    # This file
└── global.json                 # .NET SDK version
```

---

## 📋 Summary

| Aspect | Details |
|--------|---------|
| **Compilation** | AOT-first; both SlimFaas & SlimData use `.NET 10.0` with `PublishAot: true` |
| **Build** | `dotnet build` or `dotnet publish -c Release` for native executable |
| **Test** | `dotnet test --collect "Code Coverage;Format=cobertura"` |
| **Run** | `dotnet run --project src/SlimFaas/` or Docker Compose |
| **Docs** | Update `README.md` + `documentation/*.md`; auto-published to website |
| **AOT Tips** | Avoid reflection, use MemoryPack, test with `dotnet publish` |

---

## 📞 Questions?

- **Architecture**: See `documentation/how-it-works.md`
- **Deployment**: See `documentation/get-started.md`
- **Scaling**: See `documentation/autoscaling.md`
- **Contributing**: See `CONTRIBUTING.md`

---

**Last Updated**: 2026-07-03
**Target Audience**: AI Agents, Contributors, Maintainers
