<div align="center">
  <img src="https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/SlimFaas.png?raw=true" alt="SlimFaas" />
</div>

<div align="center">
  <h1>
    <a href="https://slimfaas.dev">Go to SlimFaas website</a>
  </h1>
</div>

# SlimFaas: The Slimmest, Simplest & Autoscaling-First Function-as-a-Service

[![Continuous Integration](https://github.com/SlimPlanet/SlimFaas/actions/workflows/main.yml/badge.svg)](https://github.com/SlimPlanet/SlimFaas/actions/workflows/main.yml)
[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=alert_status)](https://sonarcloud.io/dashboard?id=SlimPlanet_SlimFaas)
[![Reliability](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=reliability_rating)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=reliability_rating)
[![Security](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=security_rating)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=security_rating)
[![Code Coverage](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=coverage)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=Coverage)
[![Docker SlimFaas](https://img.shields.io/docker/pulls/axaguildev/slimfaas.svg?label=docker+pull+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds)
[![Docker Image Size](https://img.shields.io/docker/image-size/axaguildev/slimfaas?label=image+size+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds)
[![Docker Image Version](https://img.shields.io/docker/v/axaguildev/slimfaas?sort=semver&label=latest+version+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds)
[![Artifact Hub](https://img.shields.io/endpoint?url=https://artifacthub.io/badge/repository/slimfaas)](https://artifacthub.io/packages/search?repo=slimfaas)
[![FOSSA Status](https://app.fossa.com/api/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas.svg?type=shield&issueType=license)](https://app.fossa.com/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas?ref=badge_shield&issueType=license)
[![FOSSA Status](https://app.fossa.com/api/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas.svg?type=shield&issueType=security)](https://app.fossa.com/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas?ref=badge_shield&issueType=security)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/SlimPlanet/SlimFaas/badge)](https://scorecard.dev/viewer/?uri=github.com/SlimPlanet/SlimFaas)
[![OpenSSF Best Practices](https://www.bestpractices.dev/projects/10016/badge)](https://www.bestpractices.dev/projects/10016)

SlimFaas is a lightweight, plug-and-play Function-as-a-Service (FaaS) platform for Kubernetes (and Docker-Compose / Podman-Compose).
It’s designed to be **fast**, **simple**, and **extremely slim** — with a very opinionated, **autoscaling-first** design:
- `0 → N` wake-up from HTTP history & schedules,
- `0 → N` wake-up from **Kafka lag** via the companion **SlimFaas Kafka** service,
- `N → M` scaling powered by PromQL,
- internal metrics store, debug endpoints, and scale-to-zero out of the box.
- temporary **Data Binary** endpoints to ingest and stage files (from tiny to very large) with TTL-friendly storage — perfect for caching & agentic workflows.

> **Looking for MCP integration?**
> Check out **[SlimFaas MCP](https://slimfaas.dev/mcp)** — the companion runtime that converts *any* OpenAPI definition into MCP-ready tools on the fly.

---

## Why Use SlimFaas?

### 🚀 Autoscaling that actually understands your traffic

- **Scale-to-zero & wake-up**
    - Scale down to `0` after inactivity with configurable timeouts.
    - Wake up from `0 → N` based on real HTTP traffic and/or cron-like schedules.
    - Wake up from `0 → N` based on **Kafka topic activity**, using **SlimFaas Kafka** to monitor consumer lag and call the SlimFaas wake-up API.
    - Control initial capacity with `ReplicasAtStart` to reduce cold-start impact.

- **Two-phase scaling model**
    - **`0 → N`**: driven by HTTP history, schedules, **and Kafka lag (SlimFaas Kafka)** to bring functions online only when they’re needed.
    - **`N → M`**: driven by a built-in PromQL mini-evaluator on top of an internal metrics store.
    - Metrics-based autoscaling only runs when at least one pod exists — no reliance on non-existent metrics.

- **PromQL-driven autoscaler**
    - Express scaling rules with PromQL-style queries, for example:
        - `sum(rate(http_server_requests_seconds_count{namespace="...",job="..."}[1m]))`
        - `max_over_time(slimfaas_function_queue_ready_items{function="my-func"}[30s])`
        - `histogram_quantile(0.95, sum by (le) ( rate(http_server_requests_seconds_bucket{...}[1m]) ))`
    - Choose whether thresholds are **per pod** (`AverageValue`) or **global** (`Value`).
    - Configure scale-up/scale-down policies and stabilization windows inspired by HPA/KEDA.

- **Integrated metrics scraping**
    - SlimFaas scrapes only the Prometheus-style HTTP metrics endpoints of pods with `prometheus.io/scrape: "true"`.
    - It stores only the **metric keys that are requested** in autoscaling triggers or debug queries.
    - A single designated node scrapes and persists metrics; all other nodes read from the same store.

- **Debug-friendly**
    - `POST /debug/promql/eval` – evaluate a PromQL expression against the internal store and see the scalar result.
    - `GET /debug/store` – inspect what metrics are being scraped, how many series exist, and retention size.
    - Designed so you can easily answer: *“What does SlimFaas see when it decides to scale?”*

- **FinOps-minded**
    - 30-minute metrics retention window for predictable memory usage.
    - Native scale-to-zero and schedules to keep non-critical workloads cold when they’re not needed.
    - Slim control-plane footprint to avoid burning resources in your autoscaling logic itself.

### 🧵 Synchronous and Asynchronous Functions

- Simple HTTP endpoints for both **sync** and **async** calls.
- Async mode:
    - Limit the number of concurrent requests per function.
    - Configure retry behaviors and backoff strategies.
    - Drive autoscaling decisions from queue metrics.

### ⏱ Jobs

- Run **one-off**, **batch**, and **scheduled (cron)** jobs via HTTP calls.
- Configure:
    - concurrency,
    - visibility (public/private),
    - retry behavior.

### 🔐 Private/Public Functions and Jobs

- Mark functions as **public** or **private**:
    - Private: only accessible from within the cluster or from trusted pods.
    - Public: fronted by Ingress / API Gateways as usual.

### 📣 Publish/Subscribe Internal Events

- Synchronously send events to **every replica** of selected functions.
- No additional event bus required — ideal for cluster-local fan-out, cache invalidation, configuration refresh, etc.

### 📁 Data & Files (real-time ingestion + ephemeral caching)

SlimFaas includes **Data Files** endpoints to **stream, store, and serve temporary files** — from tiny payloads to *very large* binaries.
Ideal for **agentic workflows** and **real-time ingestion**: upload once, get an `id`, then let tools/functions consume it when they’re ready.

- Stream-first uploads (without buffering in memory or disk)
- **Agentic-ready** attachments & multi-step flows
- **Ephemeral caching** for intermediate artifacts
- **TTL-based** lifecycle (auto-expiration)

### 🧠 “Mind Changer” (Status & Wake-up API)

- Built-in REST APIs to:
    - monitor function and replica status,
    - wake functions up on demand,
    - integrate autoscaling state into your own tools/dashboards.

### 🔌 Plug and Play

- Deploy SlimFaas as a standard pod/StatefulSet with minimal configuration.
- Onboard existing workloads simply by adding annotations:
    - let SlimFaas manage their scaling without rewriting your applications.

### ⚡ Slim & Fast

- Written in .NET with:
    - focus on performance and low memory footprint,
    - AOT-friendly design,
    - minimal dependency surface.

<div align="center">
  <img src="https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/slim-faas-ram-cpu.png?raw=true" alt="SlimFaas CPU RAM" />
</div>

---

## Ready to Get Started?

Check out:

- [Get Started](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/get-started.md) – Learn how to deploy SlimFaas on Kubernetes or Docker Compose.
- Scaling
  - [Autoscaling](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/autoscaling.md) – Deep-dive into `0 → N` / `N → M` autoscaling, PromQL triggers, metrics scraping, and debug endpoints.
  - [Kafka Connector](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/kafka.md) – Use Kafka topic lag to wake functions from `0 → N` and keep workers alive while messages are still flowing.
  - [Planet Saver](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/planet-saver.md) – See how to start and monitor replicas from a JavaScript frontend.
- Functions & Workloads
  - [Functions](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/functions.md) – See how to call functions synchronously or asynchronously.
  - [Events](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/events.md) – Explore how to use internal synchronous publish/subscribe events.
  - [Jobs](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/jobs.md) – Learn how to define and run one-off jobs.
  - [OpenTelemetry](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/opentelemetry.md) – Enable distributed tracing, metrics, and logs with OpenTelemetry integration.
- Data & Files
  - [Data Files](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/data-files.md) - Understand how to ingest, store, and serve temporary binary artifacts.
- [How It Works](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/how-it-works.md) – Dive into SlimFaas’s architecture and design.
- [MCP](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/mcp.md) – Discover how to convert *any* OpenAPI definition into MCP-ready tools on the fly.

We hope SlimFaas helps you **simplify autoscaling**, **reduce costs**, and **keep your serverless workloads slim**.

---

### Community & Governance

- **CNCF Project**
  SlimFaas is proud to be part of the [Cloud Native Computing Foundation (CNCF) landscape](https://landscape.cncf.io).

  <div align="center">
    <img alt="CNCF logo" src="https://www.cncf.io/wp-content/uploads/2022/07/cncf-stacked-color-bg.svg" width="200"/>
  </div>

- **Community Meeting**
  Join us through our [Community Meeting Calendar](https://calendar.google.com/calendar/embed?src=be1dd72d18650490580a7d5d96a45a6eebe0fc4c9fe8adce630754cbb6121cca%40group.calendar.google.com&ctz=Europe%2FParis)
    - [ICS file](https://calendar.google.com/calendar/ical/be1dd72d18650490580a7d5d96a45a6eebe0fc4c9fe8adce630754cbb6121cca%40group.calendar.google.com/public/basic.ics)

- **Slack Channel**
  Join our channel on the [CNCF Slack](https://cloud-native.slack.com/archives/C08CRC77VDE) to connect with other SlimFaas users.

- **Code of Conduct**
  SlimFaas follows the [CNCF Code of Conduct](https://github.com/cncf/foundation/blob/main/code-of-conduct.md).

Enjoy SlimFaas!

---

## Adopters

List of organizations using this project in production or at stages of testing.

<div align="center">
  <img src="https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/adopters_logo/AXA.png?raw=true" alt="AXA" />
</div>

---

Add your logo via a pull request:

- Logo must be at PNG format, `100 px width, and 100 px height`.
- Add your logo to the `documentation/adopters` folder.
