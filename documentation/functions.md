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
   By default, functions are **Public** (accessible from anywhere). You can specify them as **Private**—restricting access to calls originating from within the same namespace or from “Trusted” pods—by using annotations:


```yaml
metadata:
    annotations:
        SlimFaas/DefaultVisibility: "Private"
        # or define paths:
        SlimFaas/UrlsPathStartWithVisibility: "Private:/mypath,Public:/otherpath"
```
This helps you control which services can call certain endpoints.

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
