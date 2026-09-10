# OpenTelemetry Integration

SlimFaas provides built-in support for OpenTelemetry, enabling comprehensive observability of your serverless functions through distributed tracing, metrics, and logs.

---

## 🚀 Features

* **Automatic instrumentation** of ASP.NET Core and HTTP client calls
* **OTLP export** to any compatible backend (Jaeger, Tempo, Prometheus, etc.)
* **Traces, metrics, and logs** correlation
* **Console exporter** for local debugging
* **Minimal configuration** via `appsettings.json` or environment variables
* **Production-ready** with configurable endpoints and service names

---

## 📖 Configuration Parameters

> Configuration can be provided through `appsettings.json` or environment variables.

| Parameter | Required    | Type         | Purpose                                                                                   | Notes                                                | Example |
|-----------|-------------|--------------|-------------------------------------------------------------------------------------------|------------------------------------------------------|---------|
| `Enable` | ✅ Yes       |  Boolean     | Enable/disable OpenTelemetry instrumentation                                              | Set to `false` to completely disable telemetry       | `true` |
| `ServiceName` | Optional    | String       | Name of the service for tracing                                                           | Helps identify traces in your observability platform | `"SlimFaas"` |
| `Endpoint` | Conditional | String (URL) | OTLP exporter endpoint (gRPC)                                                             | Required if `Enable` is `true`                       | `"http://localhost:4317"` |
| `EnableConsoleExporter` | Optional    | Boolean      | Export to console for debugging                                                           | Useful for local development                         | `false` |
| `ExcludedUrls`        |  Optional   | string array | List of URL path prefixes to exclude from tracing |                | `["/health", "/metrics"]` |

**Configuration Priority (default behavior):**
1. The `OpenTelemetry` configuration section, including `OpenTelemetry__*` environment overrides of `appsettings.json`
2. Environment variables `OTEL_SERVICE_NAME` and `OTEL_EXPORTER_OTLP_ENDPOINT` (fallback if configuration values are not specified)
3. If `Enable` is `true` and no `Endpoint` is found in either configuration or environment variables, the OpenTelemetry default value will be used.

---

## 📦 Quick Start

### appsettings.json Configuration

```json
{
  "OpenTelemetry": {
    "Enable": true,
    "ServiceName": "SlimFaas",
    "Endpoint": "http://localhost:4317",
    "EnableConsoleExporter": false,
    "ExcludedUrls": ["/health", "/metrics", "/swagger"]
  }
}
```

### Environment Variables

```bat
:: Windows Command Prompt
set OpenTelemetry__Enable=true
set OpenTelemetry__ServiceName=SlimFaas
set OpenTelemetry__Endpoint=http://localhost:4317
set OpenTelemetry__EnableConsoleExporter=false
set OpenTelemetry__ExcludedUrls__0=/health
set OpenTelemetry__ExcludedUrls__1=/metrics

```

```bash
# Linux/macOS
export OpenTelemetry__Enable=true
export OpenTelemetry__ServiceName=SlimFaas
export OpenTelemetry__Endpoint=http://localhost:4317
export OpenTelemetry__EnableConsoleExporter=false
export OpenTelemetry__ExcludedUrls__0=/health
export OpenTelemetry__ExcludedUrls__1=/metrics
```

---

## 🔌 What is Collected

### Traces

SlimFaas automatically instruments:
- ✅ **HTTP requests** via ASP.NET Core instrumentation. URLs specified in `ExcludedUrls` are filtered from tracing based on **case-insensitive path prefix matching**. For example, `/health` will exclude `/health`, `/health/live`, `/health/ready`, etc.
- Empty or missing `ExcludedUrls` configuration will use the default values `["/health", "/metrics"]`
- ✅ **HTTP client calls** to functions and external services

### Logs

- ✅ **Application logs** exported via OTLP
- **Note**: URL filtering does **not** apply to logs; all logs are collected regardless of `ExcludedUrls`

### Metrics

Exported metrics include:
- ✅ **ASP.NET Core metrics**: request duration, request count, etc.
- ✅ **HTTP client metrics**: outbound request duration and count
- **Note**: URL filtering does **not** apply to metrics; all metrics are collected regardless of `ExcludedUrls`

---

## 🐛 Debugging with Console Exporter

For local development and debugging, enable the console exporter:

```json
{
  "OpenTelemetry": {
    "Enable": true,
    "ServiceName": "SlimFaas",
    "Endpoint": "http://localhost:4317",
    "EnableConsoleExporter": true,
    "ExcludedUrls": ["/health", "/metrics", "/swagger"]
  }
}
```

This will output telemetry data directly to the console alongside the OTLP export.

---

**Enjoy distributed tracing with SlimFaas!** 🚀


## External autoscaling sources

Existing `slimfaas_autoscaler_*` and `slimfaas_metrics_scrape_*` names and labels remain
unchanged. External sources additionally expose:

| Metric | Meaning |
|---|---|
| `slimfaas_scaler_source_available` | Last external scrape succeeded (1/0) |
| `slimfaas_scaler_source_last_success_unixtime` | Last successful scrape timestamp |
| `slimfaas_scaler_source_scrapes_total` | Scrape outcomes, with a bounded `state` label |
| `slimfaas_scaler_trigger_valid` | Latest trigger evaluation is usable (1/0), including freshness |
| `slimfaas_scaler_trigger_state` | One active state: Valid, Unavailable, Timeout, InvalidMetric, Stale or Misconfigured |
| `slimfaas_scaler_trigger_value` | Latest usable trigger value |
| `slimfaas_scaler_trigger_desired_replicas` | Recommendation before aggregate policies |

Source metrics carry `function`, `source`, `provider`; trigger metrics also carry `metric`.
Source/trigger series are removed when configuration removes them; a former leader marks
its sources unavailable. URLs and PromQL queries are never metric labels. An invalid trigger publishes value zero
alongside `valid=0`; zero in that diagnostic gauge is not a scaling instruction.
Use the existing function desired/current/ready replica gauges for final decisions.

See [configuration and failure behavior](autoscaling.md#external-metrics-and-opt-in-wake-up).


## Interactive scaling diagnostics

Open **Live Stream → Scaling** to inspect real trigger values, source health, policy/stabilization constraints and replica application outcomes. The view reads structured decisions from the leader; it does not scrape Prometheus supervision metrics to reconstruct decisions. Existing metric names and labels are preserved.

Playground previews emit no production scaling telemetry and do not extend real decision histories. Their results and capture time are returned directly to the browser. The bounded live journal is for recent troubleshooting rather than persistent audit storage; use your existing metrics/logs pipeline for longer retention. See [Scaling diagnostics and playground](user-interface.md#scaling-diagnostics-and-playground).
