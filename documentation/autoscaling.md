# SlimFaas Autoscaling Guide

> End-to-end autoscaling for SlimFaas functions:
> **HTTP activity & schedules for `0 → N` + Prometheus / PromQL AutoScaler for `N → M`**

---

## Table of contents

1. [Overview](#overview)
2. [Core concepts](#core-concepts)
3. [Autoscaling architecture](#autoscaling-architecture)
4. [Configuring `0 → N` scale (HTTP history + schedule)](#configuring-0--n-scale-http-history--schedule)
5. [Configuring `N → M` scale (Prometheus AutoScaler)](#configuring-n--m-scale-prometheus-autoscaler)
6. [Decision algorithm details](#decision-algorithm-details)
7. [Configuration examples](#configuration-examples)
8. [Best practices](#best-practices)
9. [Observability & debugging](#observability--debugging)
10. [FAQ](#faq)

---

## Overview

SlimFaas combines **two complementary autoscaling systems**:

1. **`0 → N` scaling (wake-up from zero)**
   Driven by:
    - HTTP call history (in-memory + database),
    - optional **schedule configuration** (wake-up times, scale-down timeouts),
    - **function dependencies** (`DependsOn`).

   👉 This system is the **only one allowed to bring a function from `0` to `> 0` replicas**.

2. **`N → M` scaling (Prometheus AutoScaler)**
   Driven by:
    - a Prometheus metrics scraping worker,
    - a small PromQL evaluator,
    - an `AutoScaler` that computes the desired number of replicas based on metrics.

   👉 This system is used **only when the function already has at least one pod** (`replicas > 0`).

Both systems are orchestrated by:

- `ReplicasService.CheckScaleAsync(namespace)`
- `ScaleReplicasWorker`, a background worker that calls `CheckScaleAsync` periodically on the master node.

---

## Core concepts

In SlimFaas, each function is represented by a Kubernetes `Deployment` and a set of **annotations** that drive autoscaling.

### Key properties (conceptual)

For each function (deployment):

- **Current replicas**
  The current number of pods, as seen by SlimFaas.

- **`ReplicasMin`**
  Minimal number of pods allowed after scale-down. This can be `0` if you want **scale-to-zero**, or a positive value if you want to avoid full cold starts.

- **`ReplicasAtStart`**
  Number of pods to start when waking up a function from `0` (or from below minimum) via the `0 → N` system.

- **`TimeoutSecondBeforeSetReplicasMin`**
  Inactivity duration (based on HTTP history and schedule) before SlimFaas scales down the function to `ReplicasMin`.

- **`DependsOn`**
  List of other functions that this function depends on. SlimFaas can ensure dependencies are ready before waking up a function.

- **`Schedule`**
  Time-based configuration for:
    - **wake-up times** (treated as synthetic HTTP activity),
    - **time-dependent scale-down timeouts**.

- **`Scale`**
  Metrics-based autoscaling configuration, powered by Prometheus & PromQL, used for `N → M` scaling.

### How this is exposed in Kubernetes

Many of these properties are configured via **annotations** on your function `Deployment`, for example:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: fibonacci
  namespace: default
  annotations:
    SlimFaas/ReplicasMin: "0"
    SlimFaas/ReplicasAtStart: "1"
    SlimFaas/TimeoutSecondBeforeSetReplicasMin: "300"
    SlimFaas/DependsOn: '["another-func-a","another-func-b"]'
    SlimFaas/Schedule: >
      {
        "TimeZoneID": "Europe/Paris",
        "Default": {
          "WakeUp": [ "08:00", "13:30" ],
          "ScaleDownTimeout": [
            { "Time": "08:00", "Value": 1800 },
            { "Time": "19:00", "Value": 300 }
          ]
        }
      }
    SlimFaas/Scale: >
      {
        "ReplicaMax": 10,
        "Triggers": [],
        "Behavior": {}
      }
spec:
  replicas: 1
  # ...
```

> Note: when the `SlimFaas/Scale` annotation is not present (or is invalid), the Prometheus-based AutoScaler is simply disabled for that function.

---

## Autoscaling architecture

### High-level diagram

```mermaid
flowchart LR
    subgraph HTTP_history["HTTP history & Schedule (0→N)"]
        H[History HTTP (in-memory + DB)] --> R
        S[Schedule config] --> R
        D[DependsOn] --> R
    end

    subgraph Prometheus["Prometheus AutoScaler (N→M)"]
        MSW[MetricsScrapingWorker] --> MS[Metrics store]
        MS --> PQ[PromQL evaluator]
        PQ --> AS[AutoScaler]
        AS --> ASS[AutoScalerStore]
    end

    subgraph Core["Scaling orchestration"]
        R[ReplicasService.CheckScaleAsync]
        SW[ScaleReplicasWorker]
    end

    SW --> R
    R -->|scale 0→N / N→0| K8S[(Kubernetes API)]
    R -->|desiredReplicas N→M| K8S
```

- `ScaleReplicasWorker` periodically calls `CheckScaleAsync` if the node is the master.
- `ReplicasService.CheckScaleAsync`:
    1. Computes a **desired replica count** using the **HTTP/schedule based system** (`0 → N` / `N → 0`).
    2. If the function already has `replicas > 0` and a `SlimFaas/Scale` annotation, it invokes the **Prometheus AutoScaler** to adjust `N → M`.

---

## Configuring `0 → N` scale (HTTP history + schedule)

The `0 → N` system is responsible for:

- **Scaling down** to `ReplicasMin` after inactivity,
- **Waking up** functions to `ReplicasAtStart` based on:
    - HTTP activity,
    - schedule,
    - dependencies.

### Inactivity timeout → scale down to `ReplicasMin`

SlimFaas measures the time since the last HTTP activity (or synthetic activity from schedule/dependencies).

Key annotations:

```yaml
metadata:
  annotations:
    SlimFaas/ReplicasMin: "0"                         # minimum replicas after scale-down (can be 0)
    SlimFaas/ReplicasAtStart: "1"                     # replicas after wake-up from zero
    SlimFaas/TimeoutSecondBeforeSetReplicasMin: "300" # seconds of inactivity before scale-down
```

Behavior (simplified):

- SlimFaas computes a last activity timestamp for each function:
    - from HTTP history,
    - plus schedule wake-up events,
    - plus the dependencies’ activity if relevant.
- If:

  ```text
  lastActivity + TimeoutSecondBeforeSetReplicasMin < now
  ```

  then SlimFaas scales the function to **`ReplicasMin`**.

If `ReplicasMin` is `"0"`, this gives you **scale-to-zero**.

### Wake-up `0 → N` (from zero or below minimum)

There are two main situations where SlimFaas wakes a function up:

1. **From `0` to `ReplicasAtStart`**:
    - The function currently has `replicas == 0`.
    - There is recent activity (HTTP or schedule) for this function.
    - All `DependsOn` functions are ready enough (as determined by SlimFaas).
    - ⇒ SlimFaas scales the function **to `ReplicasAtStart`**.

2. **From below min to `ReplicasAtStart`**:
    - The function currently has `replicas < ReplicasMin`.
    - Dependencies are ready.
    - ⇒ SlimFaas scales the function up to `ReplicasAtStart`.

This ensures:

- **Only the `0 → N` system can wake a function from 0**,
- The function will start with a known initial capacity before the Prometheus AutoScaler adjusts it.

### Time-based schedule (wake-up & scale-down timeout)

The `SlimFaas/Schedule` annotation lets you:

- Declare **wake-up times** (treated as “calls”),
- Configure **different scale-down timeouts by time of day**.

Annotation example:

```yaml
metadata:
  annotations:
    SlimFaas/Schedule: >
      {
        "TimeZoneID": "Europe/Paris",
        "Default": {
          "WakeUp": [
            "08:30"
          ],
          "ScaleDownTimeout": [
            { "Time": "08:30", "Value": 3600 },
            { "Time": "18:30", "Value": 300 }
          ]
        }
      }
```

- `TimeZoneID`: IANA time zone ID.
- `WakeUp`: list of `HH:mm` times when the function is considered active (even without HTTP calls).
- `ScaleDownTimeout`: list of time-based overrides for the inactivity timeout (`Value` is in seconds).

This allows you, for example:

- Large timeout in business hours (keep pods warm),
- Short timeout at night (aggressive scale-down).

---

## Configuring `N → M` scale (Prometheus AutoScaler)

The Prometheus-based AutoScaler is activated by adding a `SlimFaas/Scale` annotation on the function’s Deployment. It **only runs when the function has at least one pod**.

### `SlimFaas/Scale` annotation structure (JSON)

The annotation is a JSON `ScaleConfig`:

```yaml
metadata:
  annotations:
    SlimFaas/Scale: >
      {
        "ReplicaMax": 20,
        "Triggers": [
          {
            "MetricType": "AverageValue",
            "MetricName": "rps_per_pod",
            "Query": "sum(rate(http_server_requests_seconds_count{namespace="${namespace}",job="${app}"}[1m]))",
            "Threshold": 50
          }
        ],
        "Behavior": {
          "ScaleUp": {
            "StabilizationWindowSeconds": 0,
            "Policies": [
              { "Type": "Percent", "Value": 100, "PeriodSeconds": 15 },
              { "Type": "Pods",    "Value": 4,   "PeriodSeconds": 15 }
            ]
          },
          "ScaleDown": {
            "StabilizationWindowSeconds": 300,
            "Policies": [
              { "Type": "Percent", "Value": 50, "PeriodSeconds": 30 }
            ]
          }
        }
      }
```

Main fields:

- `ReplicaMax`
  Maximum number of pods SlimFaas may ever scale this function to. `null` means no hard cap.

- `Triggers`
  List of metrics-based scaling rules.

  Each trigger has:

    - `MetricType`: `"AverageValue"` or `"Value"`.
        - `"AverageValue"`: threshold is interpreted as **target per pod**.
        - `"Value"`: threshold is interpreted as **target for the total sum**.
    - `MetricName`: human-readable metric name (for doc / logs).
    - `Query`: PromQL query that **must return a scalar** (single numeric value).
    - `Threshold`: target value for the metric (per pod or total, depending on `MetricType`).

- `Behavior`
  Configuration of scale-up / scale-down behavior:
    - `ScaleUp`: policies and stabilization for increasing replicas.
    - `ScaleDown`: policies and stabilization for decreasing replicas.

### Scaling formula (HPA/KEDA-style)

For each trigger, the AutoScaler computes:

```text
desiredReplicasTrigger = ceil(currentReplicas * (currentMetric / Threshold))
```

Then:

- If **multiple triggers** are configured:
    - The AutoScaler takes the **maximum** of all `desiredReplicasTrigger`.

- The result is then constrained by:
    - `ReplicasMin` and `ReplicaMax`,
    - scale-up / scale-down policies,
    - stabilization windows.

If all triggers are invalid (NaN, Inf, negative, invalid PromQL, etc.), the AutoScaler **keeps the current replica count**, only clamping it between `ReplicasMin` and `ReplicaMax`.

### Policies and stabilization

The `Behavior.ScaleUp.Policies` and `Behavior.ScaleDown.Policies` define how fast the scaler is allowed to change the number of pods:

```jsonc
"Behavior": {
  "ScaleUp": {
    "StabilizationWindowSeconds": 0,
    "Policies": [
      { "Type": "Percent", "Value": 100, "PeriodSeconds": 15 },
      { "Type": "Pods",    "Value": 4,   "PeriodSeconds": 15 }
    ]
  },
  "ScaleDown": {
    "StabilizationWindowSeconds": 300,
    "Policies": [
      { "Type": "Percent", "Value": 50, "PeriodSeconds": 30 }
    ]
  }
}
```

Conceptually:

- For **scale-up**:
    - Each policy defines a **maximum allowed increase** (either percentage or absolute pod count).
    - SlimFaas picks the **most aggressive** policy (maximum allowed increase).

- For **scale-down**:
    - Each policy defines a **maximum allowed decrease**.
    - SlimFaas picks the **most conservative** policy (smallest allowed decrease).

- `StabilizationWindowSeconds`:
    - For scale-down, a non-zero window makes SlimFaas look at the **max desired replicas** in the recent window to avoid flapping.
    - For scale-up, you can also use a stabilization window, but the default is usually `0`.

---

## Decision algorithm details

High-level logic for each periodic tick of `CheckScaleAsync`:

```text
for each function:

  1. Read current replicas and configuration (annotations).

  2. Compute "last activity" timestamp using:
     - HTTP history,
     - schedule wake-up times,
     - dependency activity.

  3. HTTP/schedule-based 0→N / N→0 system:

     - If inactivity is longer than the current timeout:
         desiredReplicas = ReplicasMin

     - Else if (replicas == 0 or replicas < ReplicasMin) and dependencies are ready:
         desiredReplicas = ReplicasAtStart

     - Else:
         desiredReplicas = currentReplicas

  4. Prometheus-based N→M system:

     - If there is a valid SlimFaas/Scale annotation
       AND currentReplicas > 0:

         desiredReplicas = AutoScaler(desiredReplicas, metrics...)

  5. If desiredReplicas != currentReplicas:
       call Kubernetes Scale (Deployment replicas) for this function.
```

Key points:

- The **HTTP/schedule system always runs first**, and is the only one that can propose a transition from `0` to `> 0`.
- The **Prometheus AutoScaler only runs if `currentReplicas > 0`**.
- The final decision is applied only if `desiredReplicas` differs from the current value.

---

## Configuration examples

### 1. Simple RPS-based autoscaling with scale-to-zero

This example:

- Scales based on **requests per second (RPS)**,
- Allows **scale-to-zero** after 5 minutes of inactivity,
- Starts with 1 pod when waking up.

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: fibonacci
  namespace: default
  annotations:
    SlimFaas/ReplicasMin: "0"
    SlimFaas/ReplicasAtStart: "1"
    SlimFaas/TimeoutSecondBeforeSetReplicasMin: "300"
    SlimFaas/Scale: >
      {
        "ReplicaMax": 10,
        "Triggers": [
          {
            "MetricType": "AverageValue",
            "MetricName": "rps_per_pod",
            "Query": "sum(rate(http_server_requests_seconds_count{namespace="${namespace}",job="${app}"}[1m]))",
            "Threshold": 20
          }
        ]
      }
spec:
  replicas: 1
  # ...
```

Interpretation:

- Target: **20 RPS per pod**.
- Scale-up/down formula:

  ```text
  desiredReplicas = ceil(currentReplicas * (currentRps / 20))
  ```

- After 5 minutes with no activity (HTTP or schedule), SlimFaas scales down to 0.

### 2. Combined trigger: RPS per pod + queue length

This example:

- Uses two triggers:
    - RPS per pod,
    - Total queue length for this function.
- Uses per-pod and total constraints.

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: my-queue-driven-func
  namespace: default
  annotations:
    SlimFaas/ReplicasMin: "1"
    SlimFaas/ReplicasAtStart: "2"
    SlimFaas/TimeoutSecondBeforeSetReplicasMin: "600"
    SlimFaas/Scale: >
      {
        "ReplicaMax": 50,
        "Triggers": [
          {
            "MetricType": "AverageValue",
            "MetricName": "rps_per_pod",
            "Query": "sum(rate(http_server_requests_seconds_count{namespace="${namespace}",job="${app}"}[1m]))",
            "Threshold": 30
          },
          {
            "MetricType": "Value",
            "MetricName": "queue_length",
            "Query": "sum(slimfaas_queue_length{function="${app}"})",
            "Threshold": 200
          }
        ],
        "Behavior": {
          "ScaleUp": {
            "StabilizationWindowSeconds": 0,
            "Policies": [
              { "Type": "Percent", "Value": 100, "PeriodSeconds": 15 },
              { "Type": "Pods",    "Value": 10,  "PeriodSeconds": 15 }
            ]
          },
          "ScaleDown": {
            "StabilizationWindowSeconds": 300,
            "Policies": [
              { "Type": "Percent", "Value": 50, "PeriodSeconds": 30 }
            ]
          }
        }
      }
spec:
  replicas: 2
  # ...
```

Behavior:

- For each trigger, the AutoScaler computes a desired replica count.
- The final `desiredReplicas` is the **maximum** of:
    - the desired from RPS trigger,
    - the desired from queue length trigger.

### 3. Business-hours warmup via Schedule

Keep pods warm during business hours, aggressive scale-down at night:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: business-func
  namespace: default
  annotations:
    SlimFaas/ReplicasMin: "1"
    SlimFaas/ReplicasAtStart: "2"
    SlimFaas/Schedule: >
      {
        "TimeZoneID": "Europe/Paris",
        "Default": {
          "WakeUp": [
            "08:30"
          ],
          "ScaleDownTimeout": [
            { "Time": "08:30", "Value": 3600 },
            { "Time": "18:30", "Value": 300 }
          ]
        }
      }
spec:
  replicas: 2
  # ...
```

- At 08:30 local time:
    - The function is “virtually called” and can be scaled up to `ReplicasAtStart`.
    - Inactivity timeout is 3600 seconds (1 hour).
- After 18:30:
    - Timeout is only 300 seconds (5 minutes), so pods can scale down quickly.

---

## Best practices

1. **Always set a meaningful `ReplicaMax`**
   Avoid unbounded scaling in case of incorrect thresholds or noisy metrics.

2. **Choose `ReplicasMin` carefully**
    - Use `0` only when you accept cold start latency.
    - Use `1` or more for critical, low-latency functions.

3. **Use reasonable PromQL windows**
    - Prefer metrics such as `rate(...[1m])` or `rate(...[5m])` rather than raw counters.
    - Avoid overly short windows that create noisy signals.

4. **Be conservative with scale-down**
    - Add a `ScaleDown` stabilization window.
    - Use smaller percent values (e.g., 50%) to avoid aggressive shrink.

5. **Never rely on Prometheus to wake from 0**
    - Prometheus metrics require pods to be running and scraped.
    - In SlimFaas, **only HTTP/schedule controls 0 → N**.

6. **Document each trigger**
    - Use `MetricName` for clear semantic names.
    - Keep the PromQL queries in team documentation or dashboards.

---

## Observability & debugging

To understand and debug autoscaling behavior:

1. **Logs from ReplicasService / ScaleReplicasWorker**
    - Debug logs show time left before scale-down.
    - Info logs show scaling decisions:

      ```text
      Scale up {Deployment} from {Current} to {Desired}
      Scale down {Deployment} from {Current} to {ReplicasMin}
      ```

2. **Logs from the AutoScaler / PromQL evaluator**
    - Warnings when:
        - PromQL queries fail,
        - Metric values are NaN or infinite,
        - Thresholds are invalid.

3. **Prometheus / Grafana dashboards**
    - Visualize:
        - The PromQL used in triggers.
        - The effective RPS, queue length, etc.
        - The number of replicas per function over time.

4. **Metrics for desired vs actual replicas**
    - Expose or log the desired replica count computed per tick for better visibility.

---

## FAQ

### Q1. Why does the AutoScaler never wake a function from 0?

By design:

- SlimFaas **only allows the HTTP/schedule system to wake functions from 0**.
- The Prometheus AutoScaler only adjusts **existing** capacity.

This avoids relying on metrics that cannot exist while no pod is running.

---

### Q2. What happens if all triggers return invalid metrics?

If all triggers in `SlimFaas/Scale` produce invalid values (NaN, Inf, negative, PromQL error):

- The AutoScaler ignores them.
- The replica count is left unchanged, except for:
    - enforcing `ReplicasMin`,
    - enforcing `ReplicaMax`.

---

### Q3. How do I enable scale-to-zero?

Set:

```yaml
annotations:
  SlimFaas/ReplicasMin: "0"
  SlimFaas/TimeoutSecondBeforeSetReplicasMin: "300"
```

After 300 seconds of inactivity (considering HTTP history, schedule, and dependencies), SlimFaas will scale the function down to 0.

---

### Q4. How do I temporarily disable the Prometheus AutoScaler for a function?

Simply remove the `SlimFaas/Scale` annotation from the Deployment (or set it to an empty config with no triggers):

```yaml
annotations:
  SlimFaas/Scale: >
    {
      "Triggers": []
    }
```

With no valid triggers, the `N → M` system will effectively be disabled; only `0 → N` / `N → 0` will remain active.

---

### Q5. Can I combine several triggers for one function?

Yes.

- Each trigger computes its own desired replica count.
- The AutoScaler chooses the **maximum** of all trigger outputs.
- This lets you, for example, scale based on:
    - both RPS and queue length,
    - or RPS and CPU usage, etc.

---
