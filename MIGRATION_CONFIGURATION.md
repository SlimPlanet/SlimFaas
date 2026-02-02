# Breaking Change: Migration from Environment Variables to appsettings.json

## Overview

SlimFaas has been refactored to use strongly-typed configuration through `appsettings.json` instead of environment variables. This provides better type safety, validation, and follows .NET best practices.

## Configuration Migration Guide

### SlimFaas Configuration

**Old Environment Variables** → **New appsettings.json Section**

```json
{
  "SlimFaas": {
    "AllowUnsecureSsl": false,
    "JobsConfiguration": null,
    "CorsAllowOrigin": "*",
    "BaseSlimDataUrl": "http://{pod_name}.{service_name}.{namespace}.svc:3262",
    "BaseFunctionUrl": "http://{pod_ip}:{pod_port}",
    "BaseFunctionPodUrl": "http://{pod_ip}:{pod_port}",
    "Namespace": "default",
    "Orchestrator": "Kubernetes",
    "MockKubernetesFunctions": null,
    "Hostname": "slimfaas-1",
    "Ports": [],
    "PodScaledUpByDefaultWhenInfrastructureHasNeverCalled": false
  }
}
```

| Old Environment Variable | New Config Path | Default Value |
|-------------------------|-----------------|---------------|
| `SLIMFAAS_ALLOW_UNSECURE_SSL` | `SlimFaas:AllowUnsecureSsl` | `false` |
| `SLIMFAAS_JOBS_CONFIGURATION` | `SlimFaas:JobsConfiguration` | `null` |
| `SLIMFAAS_CORS_ALLOW_ORIGIN` | `SlimFaas:CorsAllowOrigin` | `"*"` |
| `BASE_SLIMDATA_URL` | `SlimFaas:BaseSlimDataUrl` | `"http://{pod_name}.{service_name}.{namespace}.svc:3262"` |
| `BASE_FUNCTION_URL` | `SlimFaas:BaseFunctionUrl` | `"http://{pod_ip}:{pod_port}"` |
| `BASE_FUNCTION_POD_URL` | `SlimFaas:BaseFunctionPodUrl` | `"http://{pod_ip}:{pod_port}"` |
| `NAMESPACE` | `SlimFaas:Namespace` | `"default"` |
| `SLIMFAAS_ORCHESTRATOR` | `SlimFaas:Orchestrator` | `"Kubernetes"` |
| `MOCK_KUBERNETES_FUNCTIONS` | `SlimFaas:MockKubernetesFunctions` | `null` |
| `HOSTNAME` | `SlimFaas:Hostname` | `"slimfaas-1"` |
| `POD_SCALED_UP_BY_DEFAULT_WHEN_INFRASTRUCTURE_HAS_NEVER_CALLED` | `SlimFaas:PodScaledUpByDefaultWhenInfrastructureHasNeverCalled` | `false` |

### SlimData Configuration

```json
{
  "SlimData": {
    "Directory": null,
    "Configuration": null,
    "AllowColdStart": false
  }
}
```

| Old Environment Variable | New Config Path | Default Value |
|-------------------------|-----------------|---------------|
| `SLIMDATA_DIRECTORY` | `SlimData:Directory` | `null` (uses temp directory) |
| `SLIMDATA_CONFIGURATION` | `SlimData:Configuration` | `null` |
| Cold start (in config) | `SlimData:AllowColdStart` | `false` |

### Workers Configuration

```json
{
  "Workers": {
    "DelayMilliseconds": 10,
    "JobsDelayMilliseconds": 1000,
    "ReplicasSynchronizationDelayMilliseconds": 3000,
    "HistorySynchronizationDelayMilliseconds": 500,
    "ScaleReplicasDelayMilliseconds": 1000,
    "HealthDelayMilliseconds": 1000,
    "HealthDelayToExitSeconds": 60,
    "HealthDelayToStartHealthCheckSeconds": 20
  }
}
```

| Old Environment Variable | New Config Path | Default Value |
|-------------------------|-----------------|---------------|
| `SLIM_WORKER_DELAY_MILLISECONDS` | `Workers:DelayMilliseconds` | `10` |
| `SLIM_JOBS_WORKER_DELAY_MILLISECONDS` | `Workers:JobsDelayMilliseconds` | `1000` |
| `REPLICAS_SYNCHRONISATION_WORKER_DELAY_MILLISECONDS` | `Workers:ReplicasSynchronizationDelayMilliseconds` | `3000` |
| `HISTORY_SYNCHRONISATION_WORKER_DELAY_MILLISECONDS` | `Workers:HistorySynchronizationDelayMilliseconds` | `500` |
| `SCALE_REPLICAS_WORKER_DELAY_MILLISECONDS` | `Workers:ScaleReplicasDelayMilliseconds` | `1000` |
| `HEALTH_WORKER_DELAY_MILLISECONDS` | `Workers:HealthDelayMilliseconds` | `1000` |
| `HEALTH_WORKER_DELAY_TO_EXIT_SECONDS` | `Workers:HealthDelayToExitSeconds` | `60` |
| `HEALTH_WORKER_DELAY_TO_START_HEALTH_CHECK_SECONDS` | `Workers:HealthDelayToStartHealthCheckSeconds` | `20` |

## Migration Steps

### 1. Update Your appsettings.json

Add the new configuration sections to your `appsettings.json` file with your desired values.

### 2. Update Kubernetes Deployments

If you're using environment variables in your Kubernetes deployments, you can either:

**Option A: Use ConfigMap**
```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: slimfaas-config
  namespace: default
data:
  appsettings.json: |
    {
      "SlimFaas": {
        "Namespace": "production",
        "CorsAllowOrigin": "https://myapp.com"
      }
    }
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: slimfaas
spec:
  template:
    spec:
      containers:
      - name: slimfaas
        volumeMounts:
        - name: config
          mountPath: /app/appsettings.Production.json
          subPath: appsettings.json
      volumes:
      - name: config
        configMap:
          name: slimfaas-config
```

**Option B: Use Environment Variables for Configuration Override**

.NET still supports environment variable override using the format:
```yaml
env:
- name: SlimFaas__Namespace
  value: "production"
- name: SlimFaas__CorsAllowOrigin
  value: "https://myapp.com"
- name: Workers__DelayMilliseconds
  value: "20"
```

Note the double underscore `__` to separate sections and properties.

### 3. Update Docker Compose

If using Docker Compose:

```yaml
services:
  slimfaas:
    image: slimfaas:latest
    environment:
      - SlimFaas__Orchestrator=Docker
      - SlimFaas__Namespace=default
      - Workers__DelayMilliseconds=10
    volumes:
      - ./appsettings.Production.json:/app/appsettings.Production.json:ro
```

## Benefits

1. **Type Safety**: Configuration is now strongly typed with validation
2. **Better IDE Support**: IntelliSense and code completion work properly
3. **Easier Testing**: Configuration can be easily mocked and tested
4. **Standard .NET Practices**: Follows Microsoft's recommended configuration patterns
5. **AOT Compatibility**: Better support for Native AOT compilation
6. **Validation**: Configuration is validated at startup

## Backward Compatibility

⚠️ **This is a breaking change**. The old environment variables are no longer supported. You must migrate to the new configuration format.

## Need Help?

If you encounter any issues during migration, please open an issue on GitHub with:
- Your previous environment variable configuration
- Your new appsettings.json configuration
- Any error messages you're seeing
