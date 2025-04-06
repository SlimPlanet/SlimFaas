# SlimFaas Functions (Sync & Async)

SlimFaas offers **two main ways** to invoke functions: **synchronous** and **asynchronous** HTTP calls.
Below is an overview of each.

---

## 1. Synchronous Functions

Synchronous calls block until the underlying function pod handles the request and returns a response.

- **Route**:
  `GET/POST/PUT/... http://<slimfaas>/function/<functionName>/<path>`

- **Example**:
  GET http://localhost:30021/function/fibonacci1/hello/guillaume → 200 (OK) with response from fibonacci1

If your function has scaled to zero, SlimFaas automatically **wakes it up** and waits until at least one replica is ready (subject to internal timeouts).

---

## 2. Asynchronous Functions

Asynchronous calls return immediately (HTTP 202 or 201), while SlimFaas queues the request and processes it in the background.

- **Route**:
  `GET/POST/PUT/... http://<slimfaas>/async-function/<functionName>/<path>`

- **Example**:
  GET http://localhost:30021/async-function/fibonacci1/hello/guillaume → 202 (Accepted), handled in background
  synchronous mode also allows:

- **Limiting parallel requests** via annotations (e.g., `SlimFaas/NumberParallelRequest`).
- **Retry pattern** on timeouts or specific HTTP status codes.

---

## 3. Wake Function

You can explicitly “wake up” a function without invoking a specific route:

- **Route**:
  `GET http://<slimfaas>/wake-function/<functionName>`

- **Response**:
  `204 (No Content)`

This is handy if you want to ensure the function is running before real traffic arrives.

---

## 4. Listing Functions

SlimFaas exposes a route to check the readiness status of all registered functions:

- **Route**:
  `GET http://<slimfaas>/status-functions`

- **Response**:
  An array of objects with details like `NumberReady`, `numberRequested`, `PodType`, `Visibility`, etc.

```json
[
    {
      "NumberReady": 1,
      "numberRequested": 1,
      "PodType": "Deployment",
      "Visibility": "Public",
      "Name": "fibonacci1"
    },
    ...
]
```

## 5. Private vs. Public Functions

By default, functions are **Public** (accessible from anywhere). You can specify them as **Private**—restricting access to calls originating from within the same namespace.

```yaml
metadata:
    annotations:
        SlimFaas/DefaultVisibility: "Private"
        # or define paths:
        SlimFaas/UrlsPathStartWithVisibility: "Private:/mypath,Public:/otherpath"
        SlimFaas/DefaultTrusted: "Trusted"
```
This helps you control which services can call certain endpoints.

An **Untrusted** function will be considered as outside the namespace and will not be able to access Private actions.  By default, a function is **Trusted**.

```yaml
metadata:
    annotations:
        SlimFaas/DefaultTrusted: "Trusted" # Trusted or Untrusted
```

## 6. Function Configuration
   You can fine-tune sync/async HTTP timeouts, retries, and more using JSON config in `SlimFaas/Configuration`:

```json
{
  "DefaultSync": {
    "HttpTimeout": 120,
    "TimeoutRetries": [2,4,8],
    "HttpStatusRetries": [500,502,503]
  },
  "DefaultAsync": {
    "HttpTimeout": 120,
    "TimeoutRetries": [2,4,8],
    "HttpStatusRetries": [500,502,503]
  },
  "DefaultPublish": {
    "HttpTimeout": 120,
    "TimeoutRetries": [2,4,8],
    "HttpStatusRetries": [500,502,503]
  }
}
```
These settings let you define how aggressively to retry failing calls, which statuses to retry, and more.

---

## 7. The DependsOn Annotation

You can also add a `DependsOn` annotation to specify that your function should wait for certain other pods to be ready before it scales up from zero. For example:

```yaml
metadata:
    annotations:
        SlimFaas/Function: "true"
        SlimFaas/ReplicasMin: "0"
        SlimFaas/ReplicasAtStart: "1"
        # ...
        SlimFaas/DependsOn: "mysql,fibonacci2"
```
- **mysql** and **fibonacci2** are the names of other deployments/statefulsets in the same namespace.
- SlimFaas will not scale the current function (e.g., `fibonacci1`) unless all pods listed in `DependsOn` are in a ready state and meet their own minimum replicas.

This is useful in scenarios where your function must not start until a database or another dependent function is confirmed running.

## 8. Scheduling Function Wake-Up and Scale-Down
If you want your function to automatically wake at a specific time or change its scale-down timeout based on the time of day, use the SlimFaas/Schedule annotation with a JSON configuration. This feature is especially useful for workloads with predictable peak/off-peak hours.

Example Annotation
```yaml
metadata:
  annotations:
    SlimFaas/Schedule: >
      {
        "TimeZoneID": "Europe/Paris",
        "Default": {
          "WakeUp": ["07:00"],
          "ScaleDownTimeout": [
            { "Time": "07:00", "Value": 20 },
            { "Time": "21:00", "Value": 10 }
          ]
        }
      }
```

### Configuration Details
- `TimeZoneID`
Defines which IANA time zone to use (e.g., `"Europe/Paris"`).
You can see the full list of valid time zone IDs here: https://nodatime.org/TimeZones

- `WakeUp`
An array of times (`HH:mm`) at which the function should be woken up automatically.
For example, `"07:00"` means that each day at 07:00 local time, the function will scale to its `ReplicasAtStart` value (rather than remain at zero).

- `ScaleDownTimeout`
An array of objects containing:
    - `Time`: A local time string (e.g., `"07:00"`).
    - `Value`: The inactivity timeout (in seconds) that applies after this time.
      - For instance, `{"Time":"07:00","Value":20}` sets a 20-second inactivity timeout from 07:00 until another time checkpoint is reached.
      - `{"Time":"21:00","Value":10}` sets a 10-second inactivity timeout from 21:00 onward.

### How It Works
1. **At each specified time**, SlimFaas updates the function’s wake-up behavior or scale-down timeout in accordance with the schedule.
2. **Waking up** a function ensures at least one replica is running at that time.
3. **ScaleDownTimeout** adjusts how quickly the function is allowed to scale back to zero if there is no traffic.

### Example Use Case
- **07:00**: Wake up the function to be immediately available for peak morning traffic. The inactivity timeout becomes 20 seconds. If no traffic arrives for 20 seconds, the function could scale back to zero.
- **21:00**: Reduce the inactivity timeout to 10 seconds, allowing a quicker scale-down in the evening/off-peak period.

This scheduling feature helps you maintain availability during predictable high-demand periods while efficiently saving resources during low-demand times.
