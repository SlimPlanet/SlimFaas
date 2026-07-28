# Native local development mode

`slimfaas local` starts functions, Jobs, auxiliary development processes, and
one to three real SlimFaas/Raft nodes directly as operating-system processes.
It is intended for development: processes share the host network and no CPU,
memory, or security isolation is applied.

The existing `Local` orchestrator remains available for deterministic tests.
The CLI uses the separate internal `Process` orchestrator and a single
loopback-only, token-authenticated supervisor.

## Start a project

Copy [the complete example](../slimfaas.local.example.yaml) to
`slimfaas.local.yaml`, then run:

```bash
slimfaas local validate
slimfaas local up
```

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
dotnet run --project src/SlimFaas -- local validate
dotnet run --project src/SlimFaas -- local up
```

`local up` stays in the foreground, prefixes and aggregates child output, and
writes the same output below `state.directory/logs`. `Ctrl+C` calls configured
function shutdown hooks, then terminates every child process tree.

## Overlays and local secrets

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

See [the development overlay](../slimfaas.local.dev.example.yaml) and
[the env-file example](../.env.local.example).

Use `--clean` to discard persistent local state:

```bash
slimfaas local up --clean
```

For safety, SlimFaas only deletes a directory containing its
`.slimfaas-local.json` ownership marker. Changing `cluster.nodes` for an
existing persistent state also requires `--clean`.

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
`DeploymentInformation`, autoscaling, readiness, routing, and the SlimFaas
control API. Commands run without a shell. On Windows, executable wrappers such
as `npm.cmd` and `.bat` files are resolved through `ComSpec`; the same
`["npm", "run", "dev"]` command therefore remains portable.

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

For example, a one-off asset generator can be declared as:

```yaml
processes:
  generate-assets:
    command: ["npm", "run", "generate"]
    workingDirectory: src/FibonacciReact
    restartPolicy: never
```

## Jobs and gateway

Job commands run without a shell. Arguments from `/job/{name}` are appended to
the configured command; a request cannot replace that executable through
`Image`. CPU and memory fields remain accepted for API compatibility but are
not enforced in process mode.

The normal distributed queue, leader election, dependencies, parallelism, and
schedules decide when a Job is created. The supervisor makes creation
idempotent by `jobFullName`, tracks exit status and `backoffLimit`, and applies
the configured TTL.

`cluster.gatewayPort` is a stable TCP entry point. Each new connection is sent
round-robin to a node whose `/ready` endpoint succeeds. Interrupted
connections are never replayed; a later connection selects from the current
ready set.

The YAML is intentionally static during a run. Detached mode, supervisor high
availability, resource enforcement, and network isolation are outside this
development-mode scope.
