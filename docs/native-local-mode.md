# Native local development mode

`slimfaas local` starts functions, Jobs, auxiliary development processes, and
one to three real SlimFaas/Raft nodes directly as operating-system processes.
It is intended for development: processes share the host network and no CPU,
memory, or security isolation is applied.

Docker and Kubernetes are not required. Install the runtimes used by the
commands in your manifest, such as the .NET SDK or Node.js, and ensure the
configured port ranges are available.

The existing `Local` orchestrator remains available for deterministic tests.
The CLI uses the separate internal `Process` orchestrator and a single
loopback-only, token-authenticated supervisor.

## Quick start

The repository contains a complete [`slimfaas.local.yaml`](../slimfaas.local.yaml)
demo. From a directory containing that manifest, run:

```bash
slimfaas local validate
slimfaas local up
```

When the cluster is ready, the console prints:

```text
Entrypoint: http://127.0.0.1:30020
```

Open that URL for the SlimFaas user interface, or use it as the base URL for
function, Job, event, and data requests. `30020` is the stable entrypoint in
the repository demo. Ports starting at `30021` are direct node ports and
should not be used as the application entrypoint in native local mode.

For example:

```bash
curl http://127.0.0.1:30020/status-functions
curl http://127.0.0.1:30020/function/fibonacci1/hello/local
```

## Manifests and overlays

Select another manifest with `-f` or `--file`. Repeat the option to apply
partial overlays from left to right:

```bash
slimfaas local validate \
  -f slimfaas.local.yaml \
  -f slimfaas.local.dev.yaml \
  --env-file .env.local

slimfaas local up \
  -f slimfaas.local.yaml \
  -f slimfaas.local.dev.yaml \
  --env-file .env.local
```

When no `-f` is present, only `slimfaas.local.yaml` is loaded. SlimFaas does
not discover an override or `.env` file automatically.

When using the framework-dependent build from this repository:

```bash
dotnet run --project src/SlimFaas -- local validate \
  -f ../../slimfaas.local.yaml
dotnet run --project src/SlimFaas -- local up \
  -f ../../slimfaas.local.yaml
```

The repository launch profile uses `src/SlimFaas` as its working directory,
which is why repository-root files use the `../../` prefix in these commands.

### Local secrets

YAML mappings are merged recursively, while scalars and sequences such as
`command`, `dependsOn`, and `schedules` are replaced by the last file that
defines them. An empty sequence clears an inherited sequence. Set a value to
`null` to remove an inherited field, annotation, environment variable,
function, Job, or auxiliary process:

```yaml
functions:
  fibonacci:
    environment:
      UNUSED_IN_DEV: null
    command: ["dotnet", "watch", "run", "--project", "src/Fibonacci"]
```

Only the effective merged document must be complete. Runtime paths,
`workingDirectory`, and `state.directory` are always relative to the first
YAML file.

`--env-file` is repeatable and accepts dotenv entries (`KEY=VALUE`), comments,
quoted values, and an optional `export` prefix. Physical multiline values are
not supported. Later env-files override earlier ones, and the environment of
the `slimfaas` process has the highest priority. Values are only used when the
effective YAML references them:

```yaml
functions:
  fibonacci:
    environment:
      DATABASE_PASSWORD: "${FIBONACCI_DATABASE_PASSWORD}"
```

The merged YAML and env-file contents are not written to the state directory
or logs. Keep `.env.local` and secret overlays out of source control. Prefer
function, Job, or auxiliary-process `environment` entries for secrets: a
secret interpolated into `command` can be visible through operating-system
process inspection.

See [the development overlay](../slimfaas.local.dev.yaml) and
[the env-file example](../.env.local.example).

Use `--clean` to discard persistent local state:

```bash
slimfaas local up --clean
```

For safety, SlimFaas only deletes a directory containing its
`.slimfaas-local.json` ownership marker. Changing `cluster.nodes` for an
existing persistent state, or opening an unsupported marker schema, requires
`--clean`.

## Kubernetes-compatible function metadata

Everything under `functions.<name>.annotations` uses the exact Kubernetes
annotation names and value formats. In particular, JSON strings for
`SlimFaas/Configuration`, `SlimFaas/Schedule`, and `SlimFaas/Scale` can be
copied from `spec.template.metadata.annotations`. `SlimFaas/Function: "true"`
is required.

Structural YAML keys are case-insensitive, so both `environment` and
`Environment` work. Documentation uses camel case.

`SlimFaas/Scale.ReplicaMax` limits scale-out. Without that annotation, the
maximum is `max(ReplicasMin, ReplicasAtStart)`.

At local cluster startup, managed functions begin at `ReplicasMin`.
`ReplicasAtStart` remains the target used when an HTTP request, an event, or
`/wake-function/{name}` wakes a function from zero.

## Route a function to an IDE debugger

Set `functions.<name>.debugUrl` in a local, unversioned overlay to route the
function to a process started by an IDE:

```yaml
# slimfaas.local.debug.yaml
functions:
  fibonacci1:
    debugUrl: "http://127.0.0.1:5051"
```

Start the debug target with a fixed binding. For ASP.NET Core, set this in the
IDE launch profile or environment:

```text
ASPNETCORE_URLS=http://127.0.0.1:5051
```

Then apply the overlay after the normal manifest:

```bash
dotnet run --project src/SlimFaas -- local up \
  -f ../../slimfaas.local.yaml \
  -f ../../slimfaas.local.debug.yaml
```

Without the second `-f`, the function returns to its normal `command`. The
overlay is read only at startup; switching a target while the cluster is
running is not supported.

`debugUrl` must be an absolute `http` URL with an explicit port. A base path is
allowed, for example `http://127.0.0.1:5051/my-function`; query strings and
fragments are rejected. The port must not conflict with the entrypoint, node
HTTP, or Raft ports. A loopback or `localhost` debug port is also reserved from
`processPorts` and auxiliary processes.

While debug routing is enabled, SlimFaas does not start, restart, shut down, or
scale the function command. It advertises one virtual
`<function-name>-debug` replica and probes `debugUrl + health.path`. The
function is `Starting` until the IDE process answers successfully, becomes
`Running`, and returns to `Starting` if the process stops. Probes continue
without a fallback to the normal command. Synchronous calls keep their normal
HTTP timeout behavior, while asynchronous calls remain queued until the
endpoint is ready.

The URL can also be supplied from the existing `.env.local` mechanism:

```yaml
functions:
  fibonacci1:
    debugUrl: "${FIBONACCI1_DEBUG_URL}"
```

```dotenv
FIBONACCI1_DEBUG_URL=http://127.0.0.1:5051
```

Pass `--env-file ../../.env.local` together with both manifests. The repository
ignores `slimfaas.local.debug.yaml` and `.env.local` so developer-specific
ports are not committed accidentally.

## Dynamic ports

`processPorts` is one global pool shared by all functions and auxiliary
processes using `port: auto`. At scale-out, SlimFaas selects the first free
port, verifies it immediately before launch, and keeps it for the process or
replica identity across crash restarts. Function allocations are persisted in
persistent mode and released at scale-down.

The following placeholders are expanded independently for every replica in
command arguments, environment values, annotations, health checks, and
shutdown hooks:

- `{port}`: the allocated TCP port;
- `{replica}`: the zero-based replica index.

SlimFaas always sets `PORT`, `SLIMFAAS_PORT`, and
`SLIMFAAS_REPLICA_INDEX`. If `ASPNETCORE_URLS` is absent, it defaults to
`http://127.0.0.1:{port}`. For an explicit ASP.NET Core binding:

```yaml
environment:
  ASPNETCORE_ENVIRONMENT: Development
  ASPNETCORE_URLS: "http://127.0.0.1:{port}"
```

The real port is advertised in `PodInformation.Ports` and substituted into
annotations such as `prometheus.io/port`. If the pool is exhausted, the
desired replica remains visible with `StartFailureReason=PortRangeExhausted`
and is retried after a port becomes free.

## Auxiliary development processes

Use `processes` to launch frontends, file watchers, emulators, and other tools
alongside the SlimFaas cluster:

```yaml
processes:
  fibonacci-front:
    command: ["npm", "run", "dev", "--", "--host", "127.0.0.1", "--port", "{port}"]
    workingDirectory: src/FibonacciReact
    environment:
      BROWSER: "none"
      VITE_SLIMFAAS_URL: "http://127.0.0.1:30020"
    port: auto
    restartPolicy: always
```

Each entry starts exactly one process and is deliberately absent from
`DeploymentInformation`, autoscaling, function readiness, routing, and the
public SlimFaas control API. Its running state can only participate in the
local dependency checks described below. Commands run without a shell. On
Windows, executable wrappers such as `npm.cmd` and `.bat` files are resolved
through `ComSpec`; the same `["npm", "run", "dev"]` command therefore remains
portable.

`workingDirectory` defaults to `.` and is relative to the first YAML file.
`environment` follows the same overlay, `${VARIABLE}`, and secret-handling
rules as functions. Names must use lowercase letters, digits, `.`, `_`, or
`-`, start with a letter or digit, and contain at most 63 characters.

`port` can be omitted, set to a fixed integer, or set to `auto`. An automatic
port comes from `processPorts`; a fixed port may be inside or outside that
range. Configured process ports are reserved before functions start. When a
port is configured, `{port}` is replaced in command arguments and environment
values and `PORT` is injected. Using `{port}` without configuring `port` is a
validation error.

`restartPolicy` is case-insensitive and supports:

- `always` (default): restart after any exit or launch failure;
- `onFailure`: restart only after a non-zero exit or launch failure;
- `never`: make one attempt, which is useful for one-off setup commands.

Retries use an exponential delay from 1 to 30 seconds, reset after 30 seconds
of stable execution. A process ending never terminates the cluster. During
shutdown, restarts are disabled and every remaining process tree is
terminated. Output is prefixed with `[process/<name>]` and also written to
`logs/process-<name>.log`.

### Local-only process dependencies

A function, Job, or scheduled Job can wait for an auxiliary process by using
`processes:<name>` in its normal `SlimFaas/DependsOn` annotation:

```yaml
functions:
  orders-api:
    annotations:
      SlimFaas/Function: "true"
      SlimFaas/DependsOn: "orders-database,processes:database-emulator"

processes:
  database-emulator:
    command: ["database-emulator", "--port", "{port}"]
    port: auto
    restartPolicy: always
```

In native local mode, `processes:database-emulator` is ready while that managed
operating-system process is running. A function waiting at zero is not scaled
up, and a Job remains queued, until all its process dependencies are running.
There is no health probe for auxiliary processes, and losing a dependency does
not scale down a function that is already running.

The referenced name must exist under `processes`; `slimfaas local validate`
rejects missing or empty process names. Docker and Kubernetes deliberately
ignore every `processes:` entry, so the same annotation can combine a deployed
dependency such as `orders-database` with its local development replacement.

For example, a one-off asset generator can be declared as:

```yaml
processes:
  generate-assets:
    command: ["npm", "run", "generate"]
    workingDirectory: src/FibonacciReact
    restartPolicy: never
```

## Jobs

Local Jobs use the same `SlimFaas/*` annotations as suspended Kubernetes
CronJobs. For example:

```yaml
jobs:
  fibonacci5:
    command: ["dotnet", "run", "--project", "FibonacciBatch.csproj", "--"]
    workingDirectory: src/FibonacciBatch
    annotations:
      SlimFaas/Job: "true"
      SlimFaas/DefaultVisibility: "Public"
      SlimFaas/NumberParallelJob: "1"
      SlimFaas/DependsOn: "fibonacci1,fibonacci2"
      SlimFaas/Schedules: '[{"Schedule":"*/2 * * * *","Args":["39"]}]'
    ttlSecondsAfterFinished: 60
    backoffLimit: 1
    restartPolicy: Never
```

`SlimFaas/Job: "true"` is required. `DefaultVisibility` defaults to `Private`,
`NumberParallelJob` defaults to `1`, `DependsOn` is comma-separated, and
`Schedules` uses the same JSON array format as the Kubernetes annotation.
The former local fields `parallelism`, `visibility`, `dependsOn`, and
`schedules` have been removed and are rejected rather than treated as aliases.

`SlimFaas/Function`, replica scaling, and event subscription annotations apply
to entries under `functions:`; Kubernetes Jobs are configured with
`SlimFaas/Job` and do not subscribe to function events.

Job commands run without a shell. Arguments from `/job/{name}` are appended to
the configured command; a request cannot replace that executable through
`Image`. CPU and memory fields remain accepted for API compatibility but are
not enforced in process mode.

The normal distributed queue, leader election, dependencies, parallelism, and
schedules decide when a Job is created. The supervisor makes creation
idempotent by `jobFullName`, tracks exit status and `backoffLimit`, and applies
the configured TTL.

## Entrypoint and load balancing

`cluster.entrypointPort` is the stable TCP entrypoint used by applications and
developers. Each new connection is sent round-robin to a node whose `/ready`
endpoint succeeds. `cluster.nodeHttpPortBase` is the first direct node HTTP
port; subsequent nodes use consecutive ports. Interrupted connections are
never replayed; a later connection selects from the current ready set.

## Logs and shutdown

`local up` stays in the foreground, prefixes and aggregates child output, and
writes the same output below `state.directory/logs`. Every emitted line keeps
its `[function/<name>/<replica>]`, `[job/<name>]`, `[process/<name>]`, or
`[node/slimfaas-<index>]` prefix.

SlimFaas node logs default to `Error` in native local mode so function, Job,
and auxiliary-process output remains prominent. Configure the node verbosity
independently when troubleshooting the local cluster:

```yaml
cluster:
  nodeLogLevel: Warning
```

Accepted values are `Trace`, `Debug`, `Information`, `Warning`, `Error`,
`Critical`, and `None`. The setting applies from the first node bootstrap log
and covers SlimFaas, SlimData, Raft, and ASP.NET Core. Function log levels
remain controlled by each function's `environment`, for example:

```yaml
functions:
  fibonacci1:
    environment:
      Logging__LogLevel__Default: Debug
```

Console OpenTelemetry exporters are disabled for the managed SlimFaas nodes;
functions retain their own telemetry configuration.

`Ctrl+C` and, on Unix, `SIGTERM` call configured function shutdown hooks, then
stop functions, Jobs, auxiliary processes, SlimFaas nodes, and their complete
descendant process trees. Forced termination such as `kill -9`, `SIGKILL`, or
`taskkill /F` does not let SlimFaas execute this cleanup and can leave child
processes running.

Processes targeted through `debugUrl` are launched by the IDE, are not managed
by SlimFaas, and are never stopped by it.

## Troubleshooting

- If `http://127.0.0.1:30021/` returns `404`, use the printed entrypoint,
  `http://127.0.0.1:30020/`, for the user interface and application traffic.
- If a function remains `Starting`, inspect its prefixed console output and
  `state.directory/logs`. For `debugUrl`, also check that the IDE process is
  listening on the configured address and that its health endpoint succeeds.
- Run `slimfaas local validate` with the same `-f` and `--env-file` options as
  `local up` to identify manifest, interpolation, and port-collision errors.
- Use `--clean` when an earlier state marker is incompatible with a changed
  node count or state schema.

The YAML is intentionally static during a run. Detached mode, supervisor high
availability, resource enforcement, and network isolation are outside this
development-mode scope.
