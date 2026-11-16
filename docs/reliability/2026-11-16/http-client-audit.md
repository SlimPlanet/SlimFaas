# HTTP connection ownership and replacement

PR #467 is integrated separately as `29daa097` (upstream `fda21a1f`).
`IHttpClientFactory.SetHandlerLifetime` does not replace the handler already held
by the singleton `SendClient` or the cached batch client. Connection age must be
bounded in their `SocketsHttpHandler` itself. The function pool retains PR #467's
five-minute lifetime. Batch, Files and cached cluster-peer pools use two minutes.
No application retry, request deadline or HTTP version policy changes here.

| Client / owner | Destinations | Connection lifetime / idle / connect | Exchange policy |
| --- | --- | --- | --- |
| Functions / singleton `SendClient` | Selected pod IP, or configured DNS/base URL | 5 min / default 1 min / default infinite | Existing `HttpClient` 100 s default and configured request cancellation; redirects and opt-in internal TLS retained |
| Command batch / cached `_batchHttpClient` | Current leader URI, IP or DNS | 2 min / default 1 min / default infinite | HTTP/1.1 configured; existing client timeout covers headers and body, canceled when leader/consensus epoch ends; existing batch identity retained |
| Files / factory client per peer pull | Raft members' file URLs, IP or DNS | 2 min / default 1 min / default infinite | Infinite client timeout; existing transfer deadline, bounded HEAD probe, peer fallback and explicit redirect handling retained |
| WebSocket peers / cached HTTP client and owned handshake invoker | Current registered owner peer | 2 min / default 1 min / default infinite | HTTP client timeout stays infinite; existing signed lease, operation cancellation and upgraded streams retained |
| Activity sync / cached client | Current peer endpoints | 2 min / default 1 min / default infinite | Existing three-second poll deadline and bounded cursor retry; internal proxy bypass, strict TLS |
| Raft / `RaftClientHandlerFactory` supplied as `IHttpMessageHandlerFactory` | Member URI | Existing 5 min / 30 s / 2 s defaults, configurable | Existing request deadlines, socket NoDelay, abortive close on cancellation and certificate policy retained |
| Named `RaftClient` registration | Member URI | Existing 5 min / 2 min / 2 s | HTTP/1.1 exact; distinct from the handler factory used by the Raft transport |
| Protocol compatibility and member announcement / factory per call | Member URI | Existing 2 min / 30 s / 2 s | HTTP/1.1 exact, four connections/server, infinite client timeout with caller deadlines |
| Scaling diagnostics / factory per operation | Current leader | Factory's existing two-minute handler rotation | Existing timeout/cancellation, no redirects, no system proxy |
| Metrics scrape / factory per scrape | Configured metrics endpoints | Factory's existing two-minute handler rotation | Existing scrape cancellation |
| Process control and native readiness | Stable local supervisor / loopback | Existing cached client or short-lived probe client | No changing cluster DNS destination; unchanged |
| Docker/Podman API | Unix socket or configured engine URI | Existing owned engine transport | Kept separate from function/cluster transport; unchanged |
| Kubernetes SDK | Configured API endpoint | SDK-owned transport/watch lifecycle | Watch/list recovery is audited separately; unchanged by pool changes |

Pool expiry retires a connection when it becomes reusable; it does not cancel an
active response or upgrade. The loopback regression uses real HTTP/1.1 TCP
connections, holds a response body open beyond the test pool age, then checks the
next connection ID. The legacy unbounded handler reuses the same connection;
function, batch and peer factories replace it. Production two/five-minute values
are asserted before shortening only the test clock. Files' actual DI registration
is checked for finite pool age and unchanged infinite client/connect timeouts,
one-minute idle timeout, proxy bypass and explicit redirects.

Validation: 43 function/send/peer/activity tests and 16 SlimData/Files/WAL tests
pass on macOS ARM64. Logs and unique TRX files are retained under
`artifacts/pr506/{logs,tests}/http-client-audit-*`. The first build exposed two
remaining WebSocket uses of the old factory name; these now use the audited peer
factory. No NuGet dependency or lockfile changed.

These are targeted functional checks. Actual five-minute DNS/pod replacement,
long Files transfers, negotiated HTTP versions, CPU/socket/reconnection cost and
final-source non-regression remain to be measured. An explicit
`HttpRequestMessage` can retain HTTP/1.1 even when its client's default is 2.0;
the campaign must record the actual response version. The existing function
client's 100-second default can also bound response headers for a configuration
above 100 seconds; this patch does not silently relax that existing limit.

Connection recycling cannot repair a stale selected pod address. See the
[snapshot](proxy-snapshot-regression.md) and
[terminating-pod](terminating-pod-regression.md) regressions. The source189 healthy
async `Not port or IP available`, Raft errors and duplicate deliveries remain
open. This audit is not an exactly-once or non-regression verdict.
