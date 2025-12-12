![SlimFaas.png](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/SlimFaas.png?raw=true)

# SlimFaas: The Slimmest, Simplest & Autoscaling-First Function-as-a-Service

[![Continuous Integration](https://github.com/SlimPlanet/SlimFaas/actions/workflows/main.yml/badge.svg)](https://github.com/SlimPlanet/SlimFaas/actions/workflows/main.yml)
[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=alert_status)](https://sonarcloud.io/dashboard?id=SlimPlanet_SlimFaas)
[![Reliability](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=reliability_rating)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=reliability_rating)
[![Security](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=security_rating)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=security_rating)
[![Code Coverage](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=coverage)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=Coverage)
[![Docker SlimFaas](https://img.shields.io/docker/pulls/axaguildev/slimfaas.svg?label=docker+pull+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds)
[![Docker Image Size](https://img.shields.io/docker/image-size/axaguildev/slimfaas?label=image+size+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds)
[![Docker Image Version](https://img.shields.io/docker/v/axaguildev/slimfaas?sort=semver&label=latest+version+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds)
[![FOSSA Status](https://app.fossa.com/api/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas.svg?type=shield&issueType=license)](https://app.fossa.com/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas?ref=badge_shield&issueType=license)
[![FOSSA Status](https://app.fossa.com/api/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas.svg?type=shield&issueType=security)](https://app.fossa.com/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas?ref=badge_shield&issueType=security)

SlimFaas is a lightweight, plug-and-play Function-as-a-Service (FaaS) platform for Kubernetes (and Docker-Compose / Podman-Compose).
It’s designed to be **fast**, **simple**, and **extremely slim** — with a very opinionated, **autoscaling-first** design:
- `0 → N` wake-up from HTTP history & schedules,
- `0 → N` wake-up from **Kafka lag** via the companion **SlimFaas Kafka** service,
- `N → M` scaling powered by PromQL,
- internal metrics store, debug endpoints, and scale-to-zero out of the box.

> **Looking for MCP integration?**
> Check out **[SlimFaas MCP](https://slimfaas.dev/mcp)** — the companion runtime that converts *any* OpenAPI definition into MCP-ready tools on the fly.

---

## Why Use SlimFaas?

### 🚀 Autoscaling that actually understands your traffic

SlimFaas puts autoscaling at the center of the design:

- **Scale-to-zero & cold-start control**
    - Scale down to `0` after inactivity with configurable timeouts.
    - Wake up from `0 → N` based on real HTTP history and/or time-based schedules.
    - Wake up from `0 → N` based on **Kafka topic activity**: SlimFaas Kafka watches consumer group lag and calls the wake-up API whenever there are pending messages or recent consumption.
    - Control initial capacity via `ReplicasAtStart` to avoid thundering herds.

- **Two-phase scaling model**
    - **`0 → N`**: driven by HTTP history, schedules, dependencies **and Kafka lag (via SlimFaas Kafka)** to bring functions online only when needed.
    - **`N → M`**: driven by Prometheus metrics and a built-in PromQL mini-evaluator.
    - The metrics-based autoscaler only runs once at least one pod exists — no “metrics from the void”.

- **PromQL-driven autoscaler (no external HPA required)**
    - Write PromQL-style triggers like:
        - `sum(rate(http_server_requests_seconds_count{...}[1m]))`
        - `max_over_time(slimfaas_function_queue_ready_items{function="my-func"}[30s])`
        - `histogram_quantile(0.95, sum by (le) (rate(http_server_requests_seconds_bucket{...}[1m])))`
    - Targets can be interpreted as **“per pod”** (`AverageValue`) or **global** (`Value`).
    - Scale-up/scale-down policies and stabilization windows inspired by HPA/KEDA.

- **Integrated metrics scraper (no Prometheus mandatory in the hot path)**
    - SlimFaas scrapes **only** the HTTP metrics endpoints of pods with Prometheus annotations.
    - It stores **only the metrics you actually request** in your triggers or debug queries.
    - A single SlimFaas node is responsible for scraping and writing to the shared store; all nodes read from it.

- **Debug-friendly autoscaling**
    - `POST /debug/promql/eval` – evaluate any PromQL expression against the internal store.
    - `GET /debug/store` – see what metrics SlimFaas is scraping and how much data it keeps.
    - Designed so you can reason about “what SlimFaas sees” when it makes scaling decisions.

- **FinOps-friendly by design**
    - 30-minute metrics retention window for predictable memory usage.
    - Native scale-to-zero + schedules to keep non-critical workloads cold outside of business hours.
    - Super slim runtime so your control plane does not become the new bottleneck.

### 🧵 Synchronous and Asynchronous Functions

- Simple HTTP endpoints for both **sync** and **async** calls.
- Async mode:
    - Limit parallelism per function.
    - Configure retries and backoff strategies.
    - Drive scaling decisions based on queue metrics.

### ⏱ Jobs

- Run one-off, batch, and scheduled (cron) jobs via HTTP.
- Control:
    - concurrency,
    - visibility (public/private),
    - and lifecycles.

### 🔐 Private/Public Functions and Jobs

- Expose only what you need:
    - Internal functions stay private and are accessible only from within the cluster or trusted pods.
    - Public functions can be routed via Ingress / API Gateways.

### 📣 Publish/Subscribe Internal Events

- Synchronously broadcast events to **every replica** of selected functions.
- No external event bus required — perfect for simple fan-out, cache invalidation, or configuration refresh scenarios.

### 🧠 “Mind Changer” (Status & Wake-up API)

- A built-in REST API to:
    - inspect the current state of your functions,
    - wake them up on demand,
    - integrate health/usage information into your own dashboards.

### 🔌 Plug and Play

- Deploy SlimFaas as a standard pod/StatefulSet with minimal configuration.
- Onboard existing workloads by **adding annotations** to your Deployments:
    - let SlimFaas manage their replicas and autoscaling,
    - keep your existing containers and tooling.

### ⚡ Slim & Fast

- Written in .NET with a focus on:
    - low CPU and memory footprint,
    - AOT-friendly code paths,
    - minimal dependencies.

![slim-faas-ram-cpu.png](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/slim-faas-ram-cpu.png?raw=true)

---

## Ready to Get Started?

Dive into the documentation:

- [Get Started](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/get-started.md) – Learn how to deploy SlimFaas on Kubernetes or Docker Compose.
- [Autoscaling](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/autoscaling.md) – Deep-dive into `0 → N` / `N → M` autoscaling, PromQL triggers, metrics scraping, and debug endpoints.
  *(See the `documentation/autoscaling` guide in this repo.)*
- [Kafka Connector](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/kafka.md) – Use Kafka topic lag to wake functions from `0 → N` and keep workers alive while messages are still flowing.
- [Functions](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/functions.md) – See how to call functions synchronously or asynchronously.
- [Events](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/events.md) – Explore how to use internal synchronous publish/subscribe events.
- [Jobs](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/jobs.md) – Learn how to define and run one-off jobs.
- [How It Works](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/how-it-works.md) – Dive into SlimFaas’s architecture and design.
- [Planet Saver](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/planet-saver.md) – See how to start and monitor replicas from a JavaScript frontend.
- [MCP](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/mcp.md) – Discover how to convert *any* OpenAPI definition into MCP-ready tools on the fly.
- [OpenTelemetry](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/opentelemetry.md) – Enable distributed tracing, metrics, and logs with OpenTelemetry integration.

We hope SlimFaas helps you **cut costs**, **simplify autoscaling**, and **keep your serverless workloads slim**.

---

### Community & Governance

- **CNCF Project**
  SlimFaas is proud to be part of the [Cloud Native Computing Foundation (CNCF) landscape](https://landscape.cncf.io).

  <img alt="CNCF logo" src="https://www.cncf.io/wp-content/uploads/2022/07/cncf-stacked-color-bg.svg" width="200"/>

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

![AXA.png](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/adopters_logo/AXA.png?raw=true)

---

Add your logo via a pull request:

- Logo must be in PNG format, `100 px width, and 100 px height`.
- Add your logo to the `documentation/adopters` folder.
