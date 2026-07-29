# SlimFaas Clients

SlimFaas clients let a process register itself as a **virtual function** through WebSocket instead of exposing an HTTP server.

This is useful for jobs, agents, desktop services, local workers, or any process that must receive SlimFaas requests without being deployed as a Kubernetes `Deployment`.

Client libraries:

- [.NET SlimFaasClient README](../client/dotnet/SlimFaasClient/README.md)
- [Python slimfaas-client README](../client/python/slimfaas-client/README.md)
- [NuGet package: SlimFaasClient](https://www.nuget.org/packages/SlimFaasClient)
- [PyPI package: slimfaas-client](https://pypi.org/project/slimfaas-client)

---

## 1. Connection Endpoint

Clients connect to SlimFaas with WebSocket:

```http
ws://<slimfaas>:<websocket-port>/ws
```

By default, the dedicated WebSocket port is:

```text
5003
```

The port is configured with:

```json
{
  "SlimFaas": {
    "WebSocketPort": 5003
  }
}
```

Or with the environment variable:

```bash
SlimFaas__WebSocketPort=5003
```

`0` disables the dedicated WebSocket port.

SlimFaas only accepts `/ws` on the configured WebSocket port. If a request reaches `/ws` on another port, SlimFaas returns `404`.

---

## 2. Registration Flow

After opening the WebSocket, the client sends a `Register` message:

```json
{
  "type": 0,
  "correlationId": "<id>",
  "payload": {
    "functionName": "my-virtual-function",
    "configuration": {
      "dependsOn": [],
      "subscribeEvents": [{ "name": "order-created" }],
      "defaultVisibility": "Public",
      "pathsStartWithVisibility": [],
      "configuration": "",
      "replicasStartAsSoonAsOneFunctionRetrieveARequest": false,
      "numberParallelRequest": 10,
      "numberParallelRequestPerPod": 10,
      "defaultTrust": "Trusted"
    }
  }
}
```

SlimFaas replies with `RegisterResponse`:

```json
{
  "type": 1,
  "correlationId": "<id>",
  "payload": {
    "success": true,
    "error": null,
    "connectionId": "<connection-id>"
  }
}
```

If registration fails, `success` is `false` and `error` contains the reason.

---

## 3. Virtual Functions

Each active WebSocket connection is represented inside SlimFaas as a **virtual pod**.

All connections registered with the same `functionName` form one virtual function:

- `PodType` is `WebSocket`.
- each WebSocket connection is exposed as a ready virtual pod.
- the virtual pod name is generated from the connection id, for example `ws-1a2b3c4d`.
- the virtual pod IP is the WebSocket connection id.
- `Replicas` equals the number of active connections.
- the virtual function appears in the status API and in the user interface like other functions.

This means that clients can be called through the normal SlimFaas routes:

```http
GET/POST/... /function/<functionName>/<path>
GET/POST/... /async-function/<functionName>/<path>
```

They can also receive publish/subscribe events when they declare `subscribeEvents`.

---

## 4. Registration Rules

### Function Name Must Be Unique

The `functionName` used by a WebSocket client must **not** match an existing Kubernetes SlimFaas function.

SlimFaas checks current Kubernetes functions during registration. If a deployment already exists with the same name, registration is refused with an error like:

```text
Function '<name>' is already declared as a Kubernetes deployment. WebSocket clients cannot use the same name as an existing Kubernetes function.
```

This prevents ambiguity between real Kubernetes pods and virtual WebSocket replicas.

### Same Name Requires Same Configuration

Multiple clients may register with the same `functionName`. This is how you scale a virtual function horizontally.

However, all clients with the same `functionName` must use the **exact same configuration**.

SlimFaas compares:

- `defaultVisibility`
- `defaultTrust`
- `numberParallelRequest`
- `numberParallelRequestPerPod`
- `replicasStartAsSoonAsOneFunctionRetrieveARequest`
- `configuration`
- `subscribeEvents`
- `dependsOn`
- `pathsStartWithVisibility`

If a second client connects with the same `functionName` but a different configuration, registration is refused with an error like:

```text
Function '<name>' already has registered clients with a different configuration. All WebSocket clients with the same function name must share the same configuration.
```

When the last client for a function disconnects, SlimFaas removes the stored configuration for that virtual function.

---

## 5. Load Balancing and Parallelism

When several clients are connected with the same `functionName`, SlimFaas selects a client with round-robin.

The selection respects `numberParallelRequestPerPod`:

- each WebSocket connection is treated like one pod.
- `ActiveRequests` is the number of pending async callbacks plus active sync streams for that connection.
- if a connection has reached `numberParallelRequestPerPod`, SlimFaas skips it.
- if all connections are saturated, SlimFaas has no available client and returns an error for that dispatch path.

For queued async requests, the worker also respects the global function limit:

```text
min(numberParallelRequest, connectedClients * numberParallelRequestPerPod)
```

---

## 6. Async Requests

Requests sent to:

```http
/async-function/<functionName>/<path>
```

are stored in the SlimFaas queue. For WebSocket virtual functions, `WebSocketQueuesWorker` dequeues items and sends them to a connected client using an `AsyncRequest` message.

The payload contains:

- `elementId`: queue item id.
- `method`: original HTTP method.
- `path`: original path.
- `query`: original query string.
- `headers`: original headers.
- `body`: request body encoded as base64.
- `isLastTry`: whether this is the final retry attempt.
- `tryNumber`: current try number.

The client handler returns an HTTP-like status code:

- `2xx` means success.
- `5xx` can trigger retries depending on the function configuration.
- `202` means the client accepted the work and will send the final callback later.

For normal statuses, the client library sends `AsyncCallback` automatically. For `202`, the client must call the callback API manually:

- .NET: `SendCallbackAsync(elementId, statusCode)`
- Python: `send_callback(element_id, status_code)`

If the WebSocket request times out server-side, SlimFaas returns `504` for that queue item. If the WebSocket disconnects while callbacks are pending, SlimFaas resolves them with `503`.

---

## 7. Synchronous HTTP-Over-WebSocket

Requests sent to:

```http
/function/<functionName>/<path>
```

can be forwarded to a WebSocket client using binary streaming frames.

SlimFaas sends:

- `SyncRequestStart`: method, path, query, and headers as JSON.
- `SyncRequestChunk`: raw request body chunks.
- `SyncRequestEnd`: end of request body.

The client sends back:

- `SyncResponseStart`: status code and response headers as JSON.
- `SyncResponseChunk`: raw response body chunks.
- `SyncResponseEnd`: end of response body.
- `SyncCancel`: cancellation if the stream must be aborted.

Binary frame format:

```text
[type: 1 byte][correlationId: 36 ASCII bytes][flags: 1 byte][payload length: 4 bytes big-endian][payload]
```

The `correlationId` links all frames for the same synchronous request/response stream.

---

## 8. Publish/Subscribe Events

Clients can subscribe to SlimFaas publish/subscribe events with `subscribeEvents`.

When SlimFaas publishes an event to a subscribed virtual function, it sends a `PublishEvent` message to every active WebSocket connection for that function.

The payload contains:

- `eventName`
- `method`
- `path`
- `query`
- `headers`
- `body` encoded as base64

Event visibility follows the same rules as Kubernetes functions:

- if an event has its own visibility, that value is used.
- otherwise it inherits `defaultVisibility`.

---

## 9. Keepalive and Disconnection

Clients may send `Ping` messages. SlimFaas replies with `Pong` using the same `correlationId`.

When a WebSocket disconnects:

- the connection is unregistered.
- pending async callbacks are completed with `503`.
- pending sync streams are cancelled.
- if it was the last connection for the function, the virtual function disappears from SlimFaas status.

Both official clients reconnect automatically.

---

## 10. Official Client Libraries

### .NET

- README: [client/dotnet/SlimFaasClient/README.md](../client/dotnet/SlimFaasClient/README.md)
- Package: [NuGet SlimFaasClient](https://www.nuget.org/packages/SlimFaasClient)
- Install:

```bash
dotnet add package SlimFaasClient
```

### Python

- README: [client/python/slimfaas-client/README.md](../client/python/slimfaas-client/README.md)
- Package: [PyPI slimfaas-client](https://pypi.org/project/slimfaas-client)
- Install:

```bash
uv add slimfaas-client
```
