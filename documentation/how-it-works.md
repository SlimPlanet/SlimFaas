# How SlimFaas Works (Architecture)

Under the hood, SlimFaas is an **HTTP proxy** that intercepts requests for your functions, jobs, or events.
It handles scaling, routing, and state management.

---

## 1. Core Concepts

1. **SlimFaas Pod**
   Runs as a **Deployment** or **StatefulSet** (commonly 3 replicas in production). Each pod has internal workers:
   - **SlimWorker**: Handles async call processing.
   - **SlimDataSynchronizationWorker**: Manages the embedded database cluster (Raft-based).
   - **HistorySynchronizationWorker**: Syncs request history and logs.
   - **ReplicasSynchronizationWorker**: Keeps track of your function pods’ replicas and statuses in Kubernetes.
   - **ReplicasScaleWorker**: If the SlimFaas pod is leader, it scales up/down your function pods.

2. **SlimData**
   A built-in key-value store based on [Raft](https://raft.github.io/), provided by .NET’s [dotNext](https://github.com/dotnet/dotNext).
   This database is crucial for consistent state among SlimFaas pods.

3. **Annotations**
   Add or remove SlimFaas **annotations** on your pods/Deployments to control scaling, concurrency, visibility, and timeouts.

4. **Public vs. Private**
   Restricts who can access a function/job (any external caller vs. same-namespace or trusted pods).

---

## 2. Request Flow

### Synchronous HTTP Calls

1. **Client → SlimFaas**
   `GET /function/<functionName>/...`
2. **SlimFaas** ensures the target function is scaled up and ready.
3. **SlimFaas → Function**
   Wait for the function’s response.
4. **SlimFaas** returns the function’s response to the client.

![sync_http_call.PNG](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/sync_http_call.PNG?raw=true)

### Asynchronous HTTP Calls

1. **Client → SlimFaas**
   `GET /async-function/<functionName>/...` (returns immediately with `202 Accepted`).
2. **SlimFaas** enqueues the request in SlimData.
3. **SlimWorker** processes requests in the background, respecting concurrency limits.
4. **Function** handles each request. SlimFaas logs outcomes in SlimData.

![async_http_call.PNG](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/async_http_call.PNG?raw=true)

### Publish/Subscribe (Events)

1. **Client → SlimFaas**
   `POST /publish-event/<eventName>` with JSON payload.
2. SlimFaas synchronously broadcasts the payload to each subscribed function’s replicas.
3. Each replica processes the event and responds individually to SlimFaas.

![publish_sync_call.png](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/publish_sync_call.png?raw=true)

---

## 3. Scaling Logic

- **Scale to 0** after a defined inactivity (`SlimFaas/TimeoutSecondBeforeSetReplicasMin`).
- **Scale from 0 to 1+** when a new request arrives or `wake-function` is called.
- **Optional**: Use standard K8s Horizontal Pod Autoscalers or KEDA if you need more advanced scaling triggers.

---

## 4. CPU-Aware Rate Limiting

SlimFaas includes built-in **load shedding** to protect your cluster during traffic spikes by automatically rejecting requests when CPU usage exceeds configurable thresholds.

### Key Features

- **Hysteresis support**: Prevents rapid toggling between limited and normal states with separate high/low thresholds.
- **Port-specific**: Applies to all SlimFaas ports **except** the SlimData internal port (used for cluster coordination).
- **Path exclusions**: Configurable list of paths to exclude (e.g., health checks, metrics endpoints).
- **Native AOT compatible**: Minimal performance overhead.

### How It Works

1. **Monitoring**: A background service continuously samples CPU usage at a configurable interval.
2. **Activation**: When CPU exceeds the `CpuHighThreshold`, the middleware starts rejecting requests with `429 Too Many Requests`.
3. **Deactivation**: When CPU drops below the `CpuLowThreshold`, normal processing resumes.
4. **Exemptions**: The SlimData port (used for internal cluster communication) is always exempt from rate limiting.

### Configuration

Add the following to your `appsettings.json`:

```json
{
  "SlimFaas": {
    "RateLimiting": {
      "Enabled": true,
      "CpuHighThreshold": 80.0,
      "CpuLowThreshold": 60.0,
      "SampleIntervalMs": 1000,
      "RetryAfterSeconds": 30,
      "ExcludedPaths": [
        "/health",
        "/ready",
        "/metrics"
      ]
    }
  }
}
```

**Parameters:**

- `Enabled` (bool): Enable or disable CPU rate limiting.
- `CpuHighThreshold` (double, 0-100): CPU percentage that triggers rate limiting.
- `CpuLowThreshold` (double, 0-100): CPU percentage that stops rate limiting (must be < CpuHighThreshold).
- `SampleIntervalMs` (int, ≥100): How often to sample CPU usage (milliseconds).
- `RetryAfterSeconds` (int?, optional): Value for the `Retry-After` header in 429 responses.
- `ExcludedPaths` (string[]): List of paths that bypass rate limiting (e.g., health checks).

**Validation:**

- `CpuLowThreshold < CpuHighThreshold`
- Both thresholds must be between 0 and 100
- `SampleIntervalMs` must be ≥ 100

### Port Exemption

The CPU rate limiting middleware automatically exempts the **SlimData port** (configured via `publicEndPoint` in your SlimData configuration). This ensures that:

- Internal cluster coordination is never throttled
- Raft consensus and state synchronization continue uninterrupted
- Only external/public traffic on other ports is subject to rate limiting

This design keeps your control plane healthy even under extreme load.

---

## 5. Build & Technology Stack

SlimFaas is developed in **.NET**, chosen for its:
- High performance in web APIs. SlimFaas is compile in Ahead Of Time (AOT) mode which produce a **native** application.
- Excellent concurrency model.
- Constant improvements in speed and memory usage.
- Compact container images.

---

That’s the architecture in a nutshell! SlimFaas ensures your functions and jobs scale efficiently while
remaining lightweight and easy to set up.
