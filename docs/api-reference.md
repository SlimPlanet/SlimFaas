# SlimFaas API Reference

This inventory describes the routes registered by the SlimFaas runtime in this checkout. Use the [Guided Tour](guided-tour.md) for executable cURL examples and the [Bruno collection](https://slimfaas.dev/downloads/slimfaas-demo.zip) for ordered requests with assertions.

## Addresses and response conventions

Use `http://127.0.0.1:30020` for the native-local entrypoint, or `http://127.0.0.1:30021` for the tutorial's Kubernetes port-forward and Docker Compose. WebSockets use `/ws` on the entrypoint in native local mode and a dedicated port, `5003` by default, in the other orchestrators.

HTTP function, event, job and dashboard endpoints check the configured SlimFaas ports. Visibility and trust are evaluated by the relevant endpoint; there is no universal bearer-token authentication scheme supplied by these routes. Private functions return `404` to untrusted callers. Configure ingress and forwarded headers consistently with your deployment's access model. Native local mode shares the host network.

JSON property names below preserve the server's casing. In particular, generated data IDs are JSON strings for sets/hashsets, plain text for files, and `{"Id":"..."}` for jobs/schedules. A `202` means acceptance, not completion. CPU load shedding can produce `429` with `Retry-After`; storage capacity or availability can also produce `429`, `413` or `503` on data/queue paths.

## Dashboard, health and wake-up

See [tour steps 1–2](guided-tour.md#1-read-the-cluster-state).

| Method | Route | Contract / response |
|---|---|---|
| GET | `/` | Embedded HTML dashboard and its static assets. |
| GET | `/health` | Liveness: `200`, body `OK`. |
| GET | `/ready` | Raft readiness: `200 READY` or `503 NOT_READY`. |
| GET | `/status-functions` | `200`, array of function statuses. |
| GET | `/status-function/{functionName}` | `200`, one status; `404` if unknown. |
| POST | `/wake-function/{functionName}` | `204`, request a wake-up; `404` if unknown. Does not wait for readiness. |
| POST | `/wake-functions` | `204`, request wake-up for all discovered functions. |
| GET | `/status-data-stream` | SSE metadata inventory. `kind=sets/files`, `prefix`, exclusive `after`, `limit=1..500` (default 100). Emits keys, Unix-ms expiry and file sizes only. `404` for disallowed access/port, `400` for invalid queries, `429` for the shared SSE client limit. See [Data inventory](user-interface.md#data-inventory). |
| GET | `/status-functions-stream` | SSE snapshots and activity with opaque address tokens instead of replica/caller IPs (including the legacy `Pods[].Ip` field). `429` when the configured subscriber limit is reached. |
| GET | `/jobs/status` | `200`, job configurations and running-job status. |
| GET | `/status-jobs` | Alias of `/jobs/status`. |

The health middleware matches paths without restricting the HTTP method; use GET for probes. SSE event names are `state`, `activity` and `activity_batch`. `state` includes functions, jobs, queue lengths, nodes and front status. Keep connections open or impose a deliberate timeout when exploring with cURL. See [UI configuration](user-interface.md).

## Function calls and callbacks

See [synchronous calls](guided-tour.md#3-call-functions-synchronously) and [async calls](guided-tour.md#4-queue-work-retry-and-complete-callbacks).

| Methods | Route | Contract / response |
|---|---|---|
| GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS | `/function/{functionName}/{**functionPath}` | Proxy path, query, body and response to a function; wait for readiness. Unknown or inaccessible function: `404`. Application response otherwise. |
| GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS | `/function/{functionName}` | Same behavior, with an empty forwarded path. |
| GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS | `/async-function/{functionName}/{**functionPath}` | Persist request for later dispatch; `202`. Unknown or inaccessible function: `404`. |
| GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS | `/async-function/{functionName}` | Same behavior, with an empty forwarded path. |
| POST | `/async-function-callback/{functionName}/{elementId}/{status}` | Worker completion callback: `200`; invalid input `400`, unknown/inaccessible function `404`. |

The wildcard represents the application's path; it is not a literal part of the URL. The registered HTTP methods do not guarantee that the downstream application supports them. For example, the demo exposes GET `/hello/{name}` and POST `/fibonacci`; calling an unsupported application method can return `405`.

An async worker sends `SlimFaas-Element-Id` to the function. If the function returns `202`, it must later call the completion route with that ID. `success` (case-insensitive) maps to successful completion; other nonempty status strings map to failure. Use the documented `success` or `error` values. The callback does not publish a queryable result resource. See [Functions](functions.md) for headers, retry configuration and idempotency.

## Events

See [publishing an event](guided-tour.md#5-publish-an-event).

| Methods | Route | Contract / response |
|---|---|---|
| GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS | `/publish-event/{eventName}/{**functionPath}` | Forward the method, body and application path to eligible subscribers. `204`; `404` when no allowed subscriber is configured. |
| GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS | `/publish-event/{eventName}` | Same operation with an empty application path. |

HTTP delivery targets ready replicas. Sleeping replicas are skipped and the publication does not wake or durably queue work for them. A configured subscriber can exist while having no ready replicas, so `204` alone does not establish delivery. Per-target send failures are logged; the publisher does not receive each function's result. Connected WebSocket subscribers are handled through the WebSocket transport. See [Events](events.md).

## Jobs and schedules

See [jobs in the tour](guided-tour.md#6-run-jobs-and-manage-schedules).

| Method | Route | Contract / response |
|---|---|---|
| POST | `/job/{functionName}` | JSON job request, e.g. `{"Args":["10"]}`. `202 {"Id":"..."}`; invalid/disallowed input `400`. |
| GET | `/job/{functionName}` | `200`, execution list for the configuration. |
| DELETE | `/job/{functionName}/{elementId}` | `200` when deleted, `404` if absent, `400` for invalid/disallowed input. |
| PUT, PATCH | `/job/{functionName}` | Explicitly rejected: `405`. |
| POST | `/job-schedules/{functionName}` | JSON, e.g. `{"Schedule":"*/1 * * * *","Args":["10"]}`. `201 {"Id":"..."}`; invalid/disallowed input `400`. |
| GET | `/job-schedules/{functionName}` | `200`, dynamically stored schedules. |
| DELETE | `/job-schedules/{functionName}/{elementId}` | `204` when deleted, `404` if absent, `400` for invalid/disallowed input. |
| PUT, PATCH | `/job-schedules/{functionName}` | Explicitly rejected: `405`. |

Creation/deletion validate a configuration name of 3–30 lowercase letters, digits, underscores or hyphens (names are normalized to lowercase). Configuration visibility and image allowlists govern which jobs callers may request. Optional job fields include image, environment, dependencies, resources, restart policy, backoff and retention; see the full [Jobs reference](jobs.md). Schedule endpoints return `404` if the schedule service is unavailable. Capture the returned ID; a completed execution may disappear before a subsequent deletion.

### Reading after a write

The entrypoint can route consecutive requests to different nodes. On a newly started cluster, a follower can briefly return `404` for a just-created ID, or still show a just-deleted value. Retry the read with a deadline; do not blindly repeat writes, increments or job submissions. The terminal smoke test and Bruno read assertions allow up to ten seconds for visibility. Bruno keeps the original HTTP response visible while its assertion polls for the expected state. Persistent `404` responses still require checking the ID, TTL and access configuration.

## Data sets and counters

See [data exercises](guided-tour.md#7-store-values-counters-hashsets-and-files) and [Data Sets](data-sets.md).

| Method | Route | Contract / response |
|---|---|---|
| POST | `/data/sets` | Raw body; optional `id` and `ttl` query parameters. `200`, JSON string ID. |
| POST | `/data/sets/{id}` | Create/overwrite raw value; optional `ttl`. `200`, JSON string ID. |
| GET | `/data/sets` | `200`, array of IDs and expiration metadata. |
| GET | `/data/sets/{id}` | `200`, original bytes as `application/octet-stream`; `404` if absent/expired. |
| DELETE | `/data/sets/{id}` | `204`, remove value. |
| POST | `/data/sets/{id}/incr` | Add 1; `200`, new integer as text. |
| POST | `/data/sets/{id}/incrby` | Required integer `by` query parameter; `200`, new integer as text. |
| POST | `/data/sets/{id}/incrbyfloat` | Required decimal `by`; `200`, new decimal as text. |
| POST | `/data/sets/{id}/decr` | Subtract 1; `200`, new integer as text. |
| POST | `/data/sets/{id}/decrby` | Required integer `by`; `200`, new integer as text. |

IDs must contain 1–200 letters, digits, `.`, `_` or `-`. Bodies are limited to 1 MiB (`413` on overflow). Invalid IDs/parameters return `400`. TTL is milliseconds; numeric mutations accept an optional positive `ttl`, otherwise preserve the existing expiration. Missing or expired counters start at zero. Invalid stored numeric contents and overflow return `409` without changing the value.

Sets and files use `Data:DefaultVisibility` (`Data__DefaultVisibility` in the environment), which defaults to `Private`. An untrusted caller receives `404`. The tutorial explicitly uses public access for these APIs.

## Hashsets

| Method | Route | Contract / response |
|---|---|---|
| POST | `/data/hashsets` | Raw body, optional `id` and `ttl` query parameters. `200`, JSON string ID. |
| GET | `/data/hashsets` | `200`, array of IDs and expiration metadata. |
| GET | `/data/hashsets/{id}` | `200`, stored bytes; `404` if absent/expired. |
| DELETE | `/data/hashsets/{id}` | `204`, delete the whole stored hashset. |

The HTTP facade stores the body in one `value` field. It is not an arbitrary hash-field manipulation API. The same ID and 1 MiB body constraints apply. The current hashset route registrations do **not** attach `DataVisibilityEndpointFilter`; `Data__DefaultVisibility` does not control this route family. Review exposure at your gateway. The [tour](guided-tour.md#hashsets) demonstrates create/read/list/delete.

## Files

| Method | Route | Contract / response |
|---|---|---|
| POST | `/data/files` | Raw file body; optional `id`, `ttl` (milliseconds). `200`, plain text ID. |
| GET | `/data/files` | `200`, file metadata list; internal async-offload artifacts are excluded. |
| GET | `/data/files/{elementId}` | `200`, streamed content with stored media type/filename; `404` if missing, expired or unavailable. |
| DELETE | `/data/files/{elementId}` | `204`, remove metadata and attempt local file cleanup. |

Set `Content-Type` and optionally `Content-Disposition` when uploading. Send the raw file, not a multipart envelope. cURL `--data-binary @file` sends its length. Invalid/reserved IDs are rejected; downloads of reserved internal IDs return `404`. The [Files guide](data-files.md) explains disk storage, peer retrieval, upload concurrency limits and the distinction between temporary artifacts and durable application storage.

## Metrics and diagnostics

See [tour diagnostics](guided-tour.md#8-inspect-metrics-and-live-updates) and [Autoscaling](autoscaling.md#debug-http-endpoints).

| Method | Route | Contract / response |
|---|---|---|
| GET | `/metrics` | Prometheus exposition text. |
| POST | `/debug/promql/eval` | JSON with `Query`, optional `NowUnixSeconds` and `Deployment`. `200 {"value":2}` for the scalar query `1 + 1`; `400` for invalid/no-data/nonfinite result; evaluation failure can return `500`. |
| GET | `/debug/store` | `200`, requested metric names and sample-store counts. |

Evaluating a query registers referenced metrics for scraping. A query over a new series may need samples before it returns a finite result. These are diagnostic routes and do not apply the data or function visibility policies.

## WebSocket transport

`GET /ws` upgrades an HTTP connection to WebSocket. A non-upgrade request returns `400`; on the wrong configured port it returns `404`. Native local mode shares the entrypoint; Kubernetes exposes service port `5003`. The basic Compose tour does not publish that dedicated port.

Clients register function configuration, receive sync/async/event messages, and send response frames or acknowledgments. HTTP requests to virtual functions still use the normal function routes. Use the official [.NET and Python clients](clients.md), which implement registration and streaming. This protocol is kept in the collection's **Manual** reference rather than treated as a finite HTTP test.

## Internal interfaces

These routes are documented for architecture and operations. They are not introductory API calls and are excluded from the automated tour.

| Interface | Routes | Purpose and access |
|---|---|---|
| Peer UI activity | GET `/internal/activity-events` with `since={unix_ms}` | Recent node-local events. Checks an internal caller; otherwise `403`. |
| SlimData leader | GET `/SlimData/leader` | Leader discovery/redirect on the internal SlimData listener. |
| SlimData health | GET `/health` on the SlimData listener | Internal liveness; distinct from public Raft readiness. |
| SlimData protocol | GET `/SlimData/protocol` | Protocol compatibility information. |
| SlimData writes | POST `/SlimData/CommandBatch` | Serialized internal command batches; not a JSON application API. |
| Membership | POST `/SlimData/members/announce` | Node announcement and membership handling. |
| Peer file transfer | GET `/cluster/files/{id}` | Internal file retrieval, including range reads. |
| Local supervisor topology | GET `/v1/topology` | Optional `namespace` query; process topology snapshot. |
| Local supervisor scaling | PUT `/v1/functions/{name}/scale` | Process replica command. |
| Local supervisor configuration | GET `/v1/jobs/configuration` | Native job configuration. |
| Local supervisor jobs | GET, POST `/v1/jobs`; DELETE `/v1/jobs/{name}` | Inspect/create/delete managed native executions. |

Raft consensus additionally uses DotNext's protocol handler on the internal listener. `/SlimData/ListLength` appears in redirect configuration but is not registered as an application handler; do not treat it as a supported public command. Keep SlimData listeners private to the cluster. The native supervisor binds loopback and requires its per-session token header; let `slimfaas local` manage it.

## Application routes versus SlimFaas routes

`/hello`, `/fibonacci`, `/download`, `/error`, `/compute` and `/computeWithCallback` belong to the Fibonacci demo. They become accessible through `/function/{name}/...` or `/async-function/{name}/...`. Kafka producer `/send/{value}` similarly belongs to an optional example application. These are examples of forwarding, not additional SlimFaas API route families.
