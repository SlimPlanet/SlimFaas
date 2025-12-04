# SlimFaas Kafka Connector [![Docker SlimFaas](https://img.shields.io/docker/pulls/axaguildev/slimfaas-kafka.svg?label=docker+pull+slimfaas-kafka)](https://hub.docker.com/r/axaguildev/slimfaas-kafka/builds) [![Docker Image Size](https://img.shields.io/docker/image-size/axaguildev/slimfaas-kafka?label=image+size+slimfaas-kafka)](https://hub.docker.com/r/axaguildev/slimfaas/builds) [![Docker Image Version](https://img.shields.io/docker/v/axaguildev/slimfaas-kafka?sort=semver&label=latest+version+slimfaas-kafka)](https://hub.docker.com/r/axaguildev/slimfaas-kafka/builds) [![Artifact Hub](https://img.shields.io/endpoint?url=https://artifacthub.io/badge/repository/slimfaas-kafka)](https://artifacthub.io/packages/search?repo=slimfaas-kafka)
SlimFaas-Kafka is a lightweight micro‑service designed to **monitor Kafka topics** and automatically **wake up SlimFaas functions** when messages arrive or when recent Kafka activity indicates the function should stay awake.
It enables full event‑driven autoscaling of SlimFaas functions based on Kafka queues — without consuming the messages itself.

---

## 🚀 Overview

SlimFaas-Kafka acts as a **sidecar orchestration component** that:

1. **Watches Kafka topics** for:
    - Pending messages (lag)
    - Recent consumption activity (progress in committed offsets)
2. **Decides when a SlimFaas function should be started or kept alive**
3. **Triggers SlimFaas wake‑up API** to scale functions from 0 → N pods

SlimFaasKafka **never consumes messages** from your topics.
It only queries:

- high watermark offsets (latest message index per partition)
- committed consumer group offsets (only if Kafka ACLs allow it)

This means your message streams remain **untouched** and your existing consumers operate normally.

---

## 🏗 Architecture

Below is the logical architecture:

```
Kafka Topics  →  SlimFaasKafka  →  SlimFaas Orchestrator  →  Function Pods
                    ↑   ↓                         ↑
       Metadata & Offsets   Wake‑up events        |
```

### Components

| Component | Description |
|----------|-------------|
| **SlimFaasKafka** | Monitors Kafka lag & activity; sends wake signals |
| **Kafka Broker** | Holds the messages and consumer groups |
| **SlimFaas** | Manages function replicas and performs the actual autoscaling |
| **Function Pods** | Stateless workers that process Kafka messages |

SlimFaasKafka integrates with SlimFaas by calling:

```
POST /wake-functions/{functionName}
```

Each time:
- pending messages ≥ threshold
- OR recent activity detected

---

## 🔄 How SlimFaasKafka decides to wake up a function

SlimFaasKafka evaluates each *binding* (topic + consumer group + function) by computing:

### 1. Pending messages
Based on:

```
pending = high_watermark - committed_offset
```

If AdminClient (ACL) doesn’t allow offset read, fallback uses:

```
pending = high_watermarks
```

*(less accurate but safe)*

### 2. Recent activity
SlimFaasKafka tracks changes in committed offsets.
If offsets increase, it means a consumer worked recently.

You can configure:

- **minimum delta** that qualifies as activity
- **activity keep‑alive window** to keep the function awake after processing messages

### 3. Cooldown
To avoid spamming SlimFaas, a wake‑up cooldown per (topic, group) is applied.

---

## 🧩 Bindings

A binding describes:

- which Kafka topic to monitor
- which consumer group represents the function’s consumption
- which SlimFaas function should be woken up

Example binding:

```json
{
  "Topic": "fibo-public",
  "ConsumerGroupId": "fibonacci-listener-group",
  "FunctionName": "fibonaccilistener",
  "MinPendingMessages": 1,
  "CooldownSeconds": 30,
  "ActivityKeepAliveSeconds": 60,
  "MinConsumedDeltaForActivity": 1
}
```

---

## ⚙️ Configuration (Environment Variables)

SlimFaasKafka uses three configuration sections:

---

### 1. **Kafka Settings**

| Env Var | Meaning |
|--------|---------|
| `Kafka__BootstrapServers` | e.g. `kafka:9092`. MUST NOT be `localhost` inside Docker. |
| `Kafka__ClientId` | Name sent to Kafka broker |
| `Kafka__CheckIntervalSeconds` | How often to check queues |
| `Kafka__KafkaTimeoutSeconds` | Query timeout |
| `Kafka__AllowAutoCreateTopics` | Allow auto topic creation |

---

### 2. **SlimFaas Settings**

| Env Var | Meaning |
|--------|---------|
| `SlimFaas__BaseUrl` | URL of the SlimFaas API inside Docker (e.g. `http://slimfaas:30021`) |
| `SlimFaas__WakeUpPathTemplate` | Wake endpoint, default: `/functions/{functionName}/wake` |
| `SlimFaas__HttpTimeoutSeconds` | HTTP timeout |

---

### 3. **SlimFaasKafka Bindings**

Each binding is configured through indexed env vars:

```
SlimFaasKafka__Bindings__0__Topic=fibo-public
SlimFaasKafka__Bindings__0__ConsumerGroupId=fibonacci-listener-group
SlimFaasKafka__Bindings__0__FunctionName=fibonaccilistener
SlimFaasKafka__Bindings__0__MinPendingMessages=1
SlimFaasKafka__Bindings__0__CooldownSeconds=30
SlimFaasKafka__Bindings__0__ActivityKeepAliveSeconds=60
SlimFaasKafka__Bindings__0__MinConsumedDeltaForActivity=1
```

You can add more bindings:

```
SlimFaasKafka__Bindings__1__Topic=orders
SlimFaasKafka__Bindings__1__ConsumerGroupId=orders-group
SlimFaasKafka__Bindings__1__FunctionName=processorders
...
```
Description:

| Environment variable                                         | Type    | Default | Description |
|-------------------------------------------------------------|---------|---------|-------------|
| `SlimFaasKafka__Bindings__0__Topic`                         | string  | –       | Name of the **Kafka topic** to watch. SlimFaasKafka will inspect this topic to detect pending messages and activity. |
| `SlimFaasKafka__Bindings__0__ConsumerGroupId`               | string  | –       | **Kafka consumer group** to monitor for this topic. SlimFaasKafka looks at this group’s committed offsets to estimate lag and recent activity. |
| `SlimFaasKafka__Bindings__0__FunctionName`                  | string  | –       | Name of the **SlimFaas function** to wake up when conditions are met (pending messages and/or recent activity). This is the value used in the SlimFaas wake-up API. |
| `SlimFaasKafka__Bindings__0__MinPendingMessages`            | int     | `1`     | Minimum number of **pending messages** (lag) required before triggering a wake-up based on backlog. If the lag is below this threshold, no wake-up is triggered for “pending” reason. |
| `SlimFaasKafka__Bindings__0__CooldownSeconds`               | int     | `30`    | **Cooldown** between two wake-ups for this binding (topic + group + function). As long as the last wake-up is more recent than this delay, no new wake-up will be sent. |
| `SlimFaasKafka__Bindings__0__ActivityKeepAliveSeconds`      | int     | `60`    | Duration during which a **recent consumption activity** (committed offsets increasing) keeps the function “warm”. If activity is detected within this window, SlimFaasKafka can trigger a wake-up even with low lag (reason = `activity`). |
| `SlimFaasKafka__Bindings__0__MinConsumedDeltaForActivity`   | int     | `1`     | Minimum **delta of committed messages** (sum of offsets) to consider that “real” activity happened for the keep-alive logic. If the delta is below this value, the activity is ignored for the `activity` wake-up reason. |


---

## 📡 Metrics Endpoint

SlimFaasKafka exposes Prometheus metrics at:

```
/metrics
```

Metrics include:

| Metric                                          | Meaning |
|-------------------------------------------------|---------|
| `slimfaaskafka_wakeups_total`                   | Number of wake-ups per binding |
| `slimfaaskafka_pending_messages`                | Pending messages per topic/group/function |
| `slimfaaskafka_last_activity_timestamp_seconds` | Last activity time |

---

## 📈 Using Kafka metrics with SlimFaas Autoscaler (HPA-style)

The metrics exported by `KafkaMonitoringWorker` can be used directly by the **SlimFaas PromQL-based autoscaler** (the HPA-like `SlimFaas/Scale` mechanism). Below are ready-to-use examples of PromQL queries that are compatible with the SlimFaas PromQL mini-evaluator and that you can plug into `SlimFaas/Scale.Triggers[].Query`.

> All examples assume that:
> - `slimfaaskafka_*` metrics are scraped by the SlimFaas metrics scraper,
> - the label `function` corresponds to the SlimFaas function name (usually `${app}` in your annotations).

### 1. Scale out based on Kafka pending messages (queue length)

Scale a function based on **pending messages** observed by SlimFaasKafka:

```json
annotations:
  SlimFaas/Scale: >
    {
      "ReplicaMax": 50,
      "Triggers": [
        {
          "MetricType": "Value",
          "MetricName": "kafka_pending_messages_max_30s",
          "Query": "max_over_time(slimfaaskafka_pending_messages{function=\"${app}\"}[30s])",
          "Threshold": 200
        }
      ]
    }
```

**How it works**

- PromQL: `max_over_time(slimfaaskafka_pending_messages{function="${app}"}[30s])`
    - Looks at the **maximum** pending messages for this function over the last 30 seconds.
- `MetricType = "Value"`:
    - The threshold is interpreted as the **total** value we’re willing to accept.
- `Threshold = 200`:
    - If the max pending messages in the last 30s is 400, the SlimFaas autoscaler will aim for roughly `2 × currentReplicas` (subject to policies and caps).
- This is ideal when you want to **react to bursty Kafka queues**.

### 2. Scale based on average pending messages (smooth queue pressure)

If you prefer a smoother signal, you can use `avg_over_time`-like behavior by combining `avg()` and `max_over_time()`.
Because the current mini-evaluator does not support `avg_over_time`, a simple alternative is to evaluate the **current** pending messages and treat spikes implicitly through your scale-up policies:

```json
annotations:
  SlimFaas/Scale: >
    {
      "ReplicaMax": 50,
      "Triggers": [
        {
          "MetricType": "Value",
          "MetricName": "kafka_pending_messages_instant",
          "Query": "sum(slimfaaskafka_pending_messages{function=\"${app}\"})",
          "Threshold": 100
        }
      ]
    }
```

**How it works**

- PromQL: `sum(slimfaaskafka_pending_messages{function="${app}"})`
    - Sums all pending messages across bindings for this function.
- `Threshold = 100`:
    - When the total pending messages grows above 100, the autoscaler increases the number of pods.

You can combine this with **scale-up policies** to react faster to sudden spikes:

```json
"Behavior": {
  "ScaleUp": {
    "StabilizationWindowSeconds": 0,
    "Policies": [
      { "Type": "Percent", "Value": 100, "PeriodSeconds": 15 },
      { "Type": "Pods",    "Value": 10,  "PeriodSeconds": 15 }
    ]
  }
}
```

### 3. Scale based on wake-up rate (wakeups per minute)

You can also use the **rate of wake-ups** as a signal that your Kafka queues are frequently triggering function activity.

```json
annotations:
  SlimFaas/Scale: >
    {
      "ReplicaMax": 20,
      "Triggers": [
        {
          "MetricType": "Value",
          "MetricName": "kafka_wakeups_per_minute",
          "Query": "sum(rate(slimfaaskafka_wakeups_total{function=\"${app}\"}[1m]))",
          "Threshold": 5
        }
      ]
    }
```

**How it works**

- PromQL: `sum(rate(slimfaaskafka_wakeups_total{function="${app}"}[1m]))`
    - Estimates how many wake-ups per second occurred for this function over the last minute.
    - Since it’s a rate, `5` means “around 5 wake-ups per second” (you can choose smaller values if needed).
- Use this when:
    - You see **frequent wake-up events** and want SlimFaas to keep more pods around to avoid cold starts.

> Note: using wake-up rate alone can be noisy. It usually works best **combined with pending messages** or other metrics.

### 4. Combined trigger: pending messages + wake-up rate

You can combine Kafka-queue pressure and wake-up frequency in a single `SlimFaas/Scale` config:

```json
annotations:
  SlimFaas/Scale: >
    {
      "ReplicaMax": 50,
      "Triggers": [
        {
          "MetricType": "Value",
          "MetricName": "kafka_pending_messages_max_30s",
          "Query": "max_over_time(slimfaaskafka_pending_messages{function=\"${app}\"}[30s])",
          "Threshold": 200
        },
        {
          "MetricType": "Value",
          "MetricName": "kafka_wakeups_per_minute",
          "Query": "sum(rate(slimfaaskafka_wakeups_total{function=\"${app}\"}[1m]))",
          "Threshold": 2
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
```

**Behavior**

- The autoscaler computes a desired replica count for **each trigger**.
- The final `desiredReplicas` is the **maximum** of:
    - the desired from pending messages,
    - the desired from wake-up rate.
- Scale-up is fast (aggressive policies), scale-down is more conservative (stabilization window + softer policies).

---

## 🐳 Running with Docker Compose

Minimal example:

```yaml
slimkafka:
    build:
        context: ./src/SlimFaasKafka
        dockerfile: Dockerfile
    environment:
        - Kafka__BootstrapServers=kafka:9092
        - Kafka__CheckIntervalSeconds=5
        - SlimFaas__BaseUrl=http://slimfaas:30021
        - SlimKafka__Bindings__0__Topic=fibo-public
        - SlimKafka__Bindings__0__ConsumerGroupId=fibonacci-listener-group
        - SlimKafka__Bindings__0__FunctionName=fibonaccilistener
        - SlimKafka__Bindings__0__MinPendingMessages=1
        - SlimKafka__Bindings__0__CooldownSeconds=30
        - SlimKafka__Bindings__0__ActivityKeepAliveSeconds=60
        - SlimKafka__Bindings__0__MinConsumedDeltaForActivity=1
    networks:
        - slimfaas-net
```

Make sure:

- SlimFaas and SlimFaasKafka are on the same Docker network
- Kafka’s `ADVERTISED_LISTENERS` does *not* include `localhost`
- SlimFaas is reachable at the internal hostname (`slimfaas:30021`)

---

## 🚦 Startup Sequence

1. Kafka starts
2. SlimFaas starts
3. SlimFaasKafka starts
4. SlimFaasKafka connects to Kafka
5. SlimFaasKafka monitors the configured topics
6. If messages arrive → SlimFaasKafka calls SlimFaas → SlimFaas scales function up

---


## ✔ Summary

SlimFaasKafka enables:

- **Zero‑to‑N autoscaling** for Kafka-driven functions
- Without consuming messages
- With monitoring of lag and recent activity
- Full Prometheus visibility
- Easy configuration through environment variables
- Works perfectly inside Docker or Kubernetes

It is the recommended companion service for **event-driven SlimFaas workloads**.
