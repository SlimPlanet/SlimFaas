
# SlimFaas Kafka Connector [![Docker SlimFaas](https://img.shields.io/docker/pulls/axaguildev/slimfaas-kafka.svg?label=docker+pull+slimfaas-kafka)](https://hub.docker.com/r/axaguildev/slimfaas-kafka/builds) [![Docker Image Size](https://img.shields.io/docker/image-size/axaguildev/slimfaas-kafka?label=image+size+slimfaas-kafka)](https://hub.docker.com/r/axaguildev/slimfaas/builds) [![Docker Image Version](https://img.shields.io/docker/v/axaguildev/slimfaas-kafka?sort=semver&label=latest+version+slimfaas-kafka)](https://hub.docker.com/r/axaguildev/slimfaas-kafka/builds)


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
