# ⚠️ Breaking Changes: Environment Variables Migration to appsettings.json

## Overview

All environment variables have been migrated to the strongly-typed .NET options system (`IOptions<T>`).
Old environment variables are **no longer supported**.

---

## 📊 Migration Table

### SlimFaas Configuration

| Old Environment Variable | New Configuration (appsettings.json) | New Env. Variable (.NET format) | Type | Default |
|-----------------------------------|-------------------------------------------|---------------------------------------|------|--------|
| `SLIMFAAS_ALLOW_UNSECURE_SSL` | `SlimFaas:AllowUnsecureSsl` | `SlimFaas__AllowUnsecureSsl` | `bool` | `false` |
| `SLIMFAAS_JOBS_CONFIGURATION` | `SlimFaas:JobsConfiguration` | `SlimFaas__JobsConfiguration` | `string?` | `null` |
| `SLIMFAAS_CORS_ALLOW_ORIGIN` | `SlimFaas:CorsAllowOrigin` | `SlimFaas__CorsAllowOrigin` | `string` | `"*"` |
| `BASE_SLIMDATA_URL` | `SlimFaas:BaseSlimDataUrl` | `SlimFaas__BaseSlimDataUrl` | `string` | `"http://{pod_name}.{service_name}.{namespace}.svc:3262"` |
| `BASE_FUNCTION_URL` | `SlimFaas:BaseFunctionUrl` | `SlimFaas__BaseFunctionUrl` | `string` | `"http://{pod_ip}:{pod_port}"` |
| `BASE_FUNCTION_POD_URL` | `SlimFaas:BaseFunctionPodUrl` | `SlimFaas__BaseFunctionPodUrl` | `string` | `"http://{pod_ip}:{pod_port}"` |
| `NAMESPACE` | `SlimFaas:Namespace` | `SlimFaas__Namespace` | `string` | `"default"` |
| `SLIMFAAS_ORCHESTRATOR` | `SlimFaas:Orchestrator` | `SlimFaas__Orchestrator` | `string` | `"Kubernetes"` |
| `MOCK_KUBERNETES_FUNCTIONS` | `SlimFaas:MockKubernetesFunctions` | `SlimFaas__MockKubernetesFunctions` | `string?` | `null` |
| `HOSTNAME` | `SlimFaas:Hostname` | `SlimFaas__Hostname` | `string` | `"slimfaas-1"` |
| `SLIMFAAS_PORTS` | `SlimFaas:Ports` | `SlimFaas__Ports__0`, `SlimFaas__Ports__1`, etc. | `int[]` | `[]` |
| `POD_SCALED_UP_BY_DEFAULT_WHEN_INFRASTRUCTURE_HAS_NEVER_CALLED` | `SlimFaas:PodScaledUpByDefaultWhenInfrastructureHasNeverCalled` | `SlimFaas__PodScaledUpByDefaultWhenInfrastructureHasNeverCalled` | `bool` | `false` |

### Workers Configuration

| Old Environment Variable | New Configuration (appsettings.json) | New Env. Variable (.NET format)              | Type | Default |
|-----------------------------------|-------------------------------------------|-----------------------------------------------------|------|--------|
| `SLIM_WORKER_DELAY_MILLISECONDS` | `Workers:DelayMilliseconds` | `Workers__QueuesDelayMilliseconds`                  | `int` | `10` |
| `SLIM_JOBS_WORKER_DELAY_MILLISECONDS` | `Workers:JobsDelayMilliseconds` | `Workers__JobsDelayMilliseconds`                    | `int` | `1000` |
| `REPLICAS_SYNCHRONISATION_WORKER_DELAY_MILLISECONDS` | `Workers:ReplicasSynchronizationDelayMilliseconds` | `Workers__ReplicasSynchronizationDelayMilliseconds` | `int` | `3000` |
| `HISTORY_SYNCHRONISATION_WORKER_DELAY_MILLISECONDS` | `Workers:HistorySynchronizationDelayMilliseconds` | `Workers__HistorySynchronizationDelayMilliseconds`  | `int` | `500` |
| `SCALE_REPLICAS_WORKER_DELAY_MILLISECONDS` | `Workers:ScaleReplicasDelayMilliseconds` | `Workers__ScaleReplicasDelayMilliseconds`           | `int` | `1000` |
| `HEALTH_WORKER_DELAY_MILLISECONDS` | `Workers:HealthDelayMilliseconds` | `Workers__HealthDelayMilliseconds`                  | `int` | `1000` |
| `HEALTH_WORKER_DELAY_TO_EXIT_SECONDS` | `Workers:HealthDelayToExitSeconds` | `Workers__HealthDelayToExitSeconds`                 | `int` | `60` |
| `HEALTH_WORKER_DELAY_TO_START_HEALTH_CHECK_SECONDS` | `Workers:HealthDelayToStartHealthCheckSeconds` | `Workers__HealthDelayToStartHealthCheckSeconds`     | `int` | `20` |

### SlimData Configuration

| Old Environment Variable | New Configuration (appsettings.json) | New Env. Variable (.NET format) | Type | Default |
|-----------------------------------|-------------------------------------------|---------------------------------------|------|--------|
| `SLIMDATA_DIRECTORY` | `SlimData:Directory` | `SlimData__Directory` | `string?` | `null` |
| `SLIMDATA_CONFIGURATION` | `SlimData:Configuration` | `SlimData__Configuration` | `string?` | `null` |
| *(implicit)* | `SlimData:AllowColdStart` | `SlimData__AllowColdStart` | `bool` | `false` |

### RaftClientHandler Configuration (new)

| Old Environment Variable | New Configuration (appsettings.json) | New Env. Variable (.NET format) | Type | Default |
|-----------------------------------|-------------------------------------------|---------------------------------------|------|--------|
| `SLIMDATA_SOCKETS_HTTP_HANDLER_TIMEOUT` | `RaftClientHandler:ConnectTimeoutMilliseconds` | `RaftClientHandler__ConnectTimeoutMilliseconds` | `int` | `2000` |
| *(new)* | `RaftClientHandler:PooledConnectionLifetimeMinutes` | `RaftClientHandler__PooledConnectionLifetimeMinutes` | `int` | `5` |
| *(new)* | `RaftClientHandler:PooledConnectionIdleTimeoutSeconds` | `RaftClientHandler__PooledConnectionIdleTimeoutSeconds` | `int` | `30` |
| *(new)* | `RaftClientHandler:MaxConnectionsPerServer` | `RaftClientHandler__MaxConnectionsPerServer` | `int` | `100` |

---

## 📝 Configuration Examples

### Before (docker-compose.yml / Kubernetes Deployment)

```yaml
environment:
  - SLIMFAAS_ALLOW_UNSECURE_SSL=false
  - SLIMFAAS_CORS_ALLOW_ORIGIN=*
  - BASE_SLIMDATA_URL=http://{pod_name}.{service_name}.{namespace}.svc:3262
  - BASE_FUNCTION_URL=http://{pod_ip}:{pod_port}
  - NAMESPACE=default
  - SLIMFAAS_ORCHESTRATOR=Kubernetes
  - SLIM_WORKER_DELAY_MILLISECONDS=10
  - SLIM_JOBS_WORKER_DELAY_MILLISECONDS=1000
  - REPLICAS_SYNCHRONISATION_WORKER_DELAY_MILLISECONDS=3000
  - HISTORY_SYNCHRONISATION_WORKER_DELAY_MILLISECONDS=500
  - SCALE_REPLICAS_WORKER_DELAY_MILLISECONDS=1000
  - HEALTH_WORKER_DELAY_MILLISECONDS=1000
  - HEALTH_WORKER_DELAY_TO_EXIT_SECONDS=60
  - HEALTH_WORKER_DELAY_TO_START_HEALTH_CHECK_SECONDS=20
  - POD_SCALED_UP_BY_DEFAULT_WHEN_INFRASTRUCTURE_HAS_NEVER_CALLED=false
  - SLIMDATA_DIRECTORY=/data
  - SLIMDATA_CONFIGURATION={"coldStart":"true"}
  - SLIMDATA_SOCKETS_HTTP_HANDLER_TIMEOUT=2000
```

### After - Option 1: appsettings.json (recommended)

```json
{
  "SlimFaas": {
    "AllowUnsecureSsl": false,
    "CorsAllowOrigin": "*",
    "BaseSlimDataUrl": "http://{pod_name}.{service_name}.{namespace}.svc:3262",
    "BaseFunctionUrl": "http://{pod_ip}:{pod_port}",
    "BaseFunctionPodUrl": "http://{pod_ip}:{pod_port}",
    "Namespace": "default",
    "Orchestrator": "Kubernetes",
    "Hostname": "slimfaas-1",
    "Ports": [],
    "PodScaledUpByDefaultWhenInfrastructureHasNeverCalled": false
  },
  "Workers": {
    "DelayMilliseconds": 10,
    "JobsDelayMilliseconds": 1000,
    "ReplicasSynchronizationDelayMilliseconds": 3000,
    "HistorySynchronizationDelayMilliseconds": 500,
    "ScaleReplicasDelayMilliseconds": 1000,
    "HealthDelayMilliseconds": 1000,
    "HealthDelayToExitSeconds": 60,
    "HealthDelayToStartHealthCheckSeconds": 20
  },
  "SlimData": {
    "Directory": "/data",
    "Configuration": "{\"coldStart\":\"true\"}",
    "AllowColdStart": false
  },
  "RaftClientHandler": {
    "ConnectTimeoutMilliseconds": 2000,
    "PooledConnectionLifetimeMinutes": 5,
    "PooledConnectionIdleTimeoutSeconds": 30,
    "MaxConnectionsPerServer": 100
  }
}
```

### After - Option 2: Environment Variables (.NET format)

```yaml
environment:
  # SlimFaas
  - SlimFaas__AllowUnsecureSsl=false
  - SlimFaas__CorsAllowOrigin=*
  - SlimFaas__BaseSlimDataUrl=http://{pod_name}.{service_name}.{namespace}.svc:3262
  - SlimFaas__BaseFunctionUrl=http://{pod_ip}:{pod_port}
  - SlimFaas__BaseFunctionPodUrl=http://{pod_ip}:{pod_port}
  - SlimFaas__Namespace=default
  - SlimFaas__Orchestrator=Kubernetes
  - SlimFaas__Hostname=slimfaas-1
  - SlimFaas__Ports__0=5000
  - SlimFaas__Ports__1=5001
  - SlimFaas__PodScaledUpByDefaultWhenInfrastructureHasNeverCalled=false

  # Workers
  - Workers__DelayMilliseconds=10
  - Workers__JobsDelayMilliseconds=1000
  - Workers__ReplicasSynchronizationDelayMilliseconds=3000
  - Workers__HistorySynchronizationDelayMilliseconds=500
  - Workers__ScaleReplicasDelayMilliseconds=1000
  - Workers__HealthDelayMilliseconds=1000
  - Workers__HealthDelayToExitSeconds=60
  - Workers__HealthDelayToStartHealthCheckSeconds=20

  # SlimData
  - SlimData__Directory=/data
  - SlimData__Configuration={"coldStart":"true"}
  - SlimData__AllowColdStart=false

  # RaftClientHandler
  - RaftClientHandler__ConnectTimeoutMilliseconds=2000
  - RaftClientHandler__PooledConnectionLifetimeMinutes=5
  - RaftClientHandler__PooledConnectionIdleTimeoutSeconds=30
  - RaftClientHandler__MaxConnectionsPerServer=100
```

---

## 🔧 Kubernetes ConfigMap/Secrets Migration

### Before

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: slimfaas-config
data:
  SLIMFAAS_ORCHESTRATOR: "Kubernetes"
  NAMESPACE: "default"
  BASE_SLIMDATA_URL: "http://{pod_name}.{service_name}.{namespace}.svc:3262"
  SLIM_WORKER_DELAY_MILLISECONDS: "10"
  SCALE_REPLICAS_WORKER_DELAY_MILLISECONDS: "1000"
  SLIMDATA_SOCKETS_HTTP_HANDLER_TIMEOUT: "2000"
```

### After

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: slimfaas-config
data:
  appsettings.json: |
    {
      "SlimFaas": {
        "Orchestrator": "Kubernetes",
        "Namespace": "default",
        "BaseSlimDataUrl": "http://{pod_name}.{service_name}.{namespace}.svc:3262"
      },
      "Workers": {
        "DelayMilliseconds": 10,
        "ScaleReplicasDelayMilliseconds": 1000
      },
      "RaftClientHandler": {
        "ConnectTimeoutMilliseconds": 2000
      }
    }
```

**OR** use the .NET format with double underscores:

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: slimfaas-config
data:
  SlimFaas__Orchestrator: "Kubernetes"
  SlimFaas__Namespace: "default"
  SlimFaas__BaseSlimDataUrl: "http://{pod_name}.{service_name}.{namespace}.svc:3262"
  Workers__DelayMilliseconds: "10"
  Workers__ScaleReplicasDelayMilliseconds: "1000"
  RaftClientHandler__ConnectTimeoutMilliseconds: "2000"
```

---

## 📂 Options Classes Structure

### SlimFaasOptions
**File**: `/src/SlimFaas/Options/SlimFaasOptions.cs`
**Section**: `"SlimFaas"`

| Property | Type | Default | Description |
|-----------|------|--------|-------------|
| `AllowUnsecureSsl` | `bool` | `false` | Allow unsecure SSL connections |
| `JobsConfiguration` | `string?` | `null` | Jobs configuration in JSON format |
| `CorsAllowOrigin` | `string` | `"*"` | Allowed CORS origins |
| `BaseSlimDataUrl` | `string` | `"http://{pod_name}.{service_name}.{namespace}.svc:3262"` | Base URL for SlimData |
| `BaseFunctionUrl` | `string` | `"http://{pod_ip}:{pod_port}"` | Base URL for functions |
| `BaseFunctionPodUrl` | `string` | `"http://{pod_ip}:{pod_port}"` | Base URL for function pods |
| `Namespace` | `string` | `"default"` | Kubernetes namespace |
| `Orchestrator` | `string` | `"Kubernetes"` | Orchestrator type (Kubernetes, Docker, Mock) |
| `MockKubernetesFunctions` | `string?` | `null` | Mocked functions (comma-separated) |
| `Hostname` | `string` | `"slimfaas-1"` | Pod hostname |
| `Ports` | `int[]` | `[]` | Listening ports |
| `PodScaledUpByDefaultWhenInfrastructureHasNeverCalled` | `bool` | `false` | Start pods by default |

### WorkersOptions
**File**: `/src/SlimFaas/Options/WorkersOptions.cs`
**Section**: `"Workers"`

| Property | Type | Default | Description |
|-----------|------|--------|-------------|
| `DelayMilliseconds` | `int` | `10` | Main worker delay (ms) |
| `JobsDelayMilliseconds` | `int` | `1000` | Jobs worker delay (ms) |
| `ReplicasSynchronizationDelayMilliseconds` | `int` | `3000` | Replicas synchronization delay (ms) |
| `HistorySynchronizationDelayMilliseconds` | `int` | `500` | History synchronization delay (ms) |
| `ScaleReplicasDelayMilliseconds` | `int` | `1000` | Replicas scaling delay (ms) |
| `HealthDelayMilliseconds` | `int` | `1000` | Health checks delay (ms) |
| `HealthDelayToExitSeconds` | `int` | `60` | Delay before exit after health issue (s) |
| `HealthDelayToStartHealthCheckSeconds` | `int` | `20` | Delay before starting health checks (s) |

### SlimDataOptions
**File**: `/src/SlimFaas/Options/SlimDataOptions.cs`
**Section**: `"SlimData"`

| Property | Type | Default | Description |
|-----------|------|--------|-------------|
| `Directory` | `string?` | `null` | Persistent storage directory |
| `Configuration` | `string?` | `null` | JSON configuration for SlimData |
| `AllowColdStart` | `bool` | `false` | Allow cold start |

### RaftClientHandlerOptions (new)
**File**: `/src/SlimData/Options/RaftClientHandlerOptions.cs`
**Section**: `"RaftClientHandler"`

| Property | Type | Default | Description |
|-----------|------|--------|-------------|
| `ConnectTimeoutMilliseconds` | `int` | `2000` | TCP+TLS connection timeout (ms) |
| `PooledConnectionLifetimeMinutes` | `int` | `5` | Pooled connection lifetime (min) |
| `PooledConnectionIdleTimeoutSeconds` | `int` | `30` | Connection idle timeout (s) |
| `MaxConnectionsPerServer` | `int` | `100` | Max connections per server |

---

## 🔄 Migration Examples

### Example 1: Simple configuration

**Before**:
```bash
export NAMESPACE=production
export SLIMFAAS_ORCHESTRATOR=Kubernetes
export SLIM_WORKER_DELAY_MILLISECONDS=50
```

**After**:
```bash
export SlimFaas__Namespace=production
export SlimFaas__Orchestrator=Kubernetes
export Workers__DelayMilliseconds=50
```

### Example 2: Configuration with array

**Before**:
```bash
export SLIMFAAS_PORTS=5000,5001,5002
```

**After**:
```bash
export SlimFaas__Ports__0=5000
export SlimFaas__Ports__1=5001
export SlimFaas__Ports__2=5002
```

**OR** in appsettings.json:
```json
{
  "SlimFaas": {
    "Ports": [5000, 5001, 5002]
  }
}
```

### Example 3: Complex JSON configuration

**Before**:
```bash
export SLIMDATA_CONFIGURATION='{"coldStart":"true","replica":3}'
```

**After**:
```bash
export SlimData__Configuration='{"coldStart":"true","replica":3}'
```

**OR** in appsettings.json:
```json
{
  "SlimData": {
    "Configuration": "{\"coldStart\":\"true\",\"replica\":3}"
  }
}
```

---

## 🎯 Advantages of the New Approach

### 1. **Type Safety**
```csharp
// BEFORE: Manual parsing with runtime error risk
int delay = int.Parse(Environment.GetEnvironmentVariable("DELAY") ?? "1000");

// AFTER: Type-safe at compile time
int delay = workersOptions.Value.DelayMilliseconds;
```

### 2. **Startup Validation**
```csharp
services.AddOptions<WorkersOptions>()
    .Bind(configuration.GetSection(WorkersOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();  // ⚠️ Fails at startup if config is invalid
```

### 3. **IntelliSense and documentation**
- Auto-completion in IDE
- XML comments on each property
- No need to search in code to find variable names

### 4. **Testability**
```csharp
// Create mock options for tests
var mockOptions = Microsoft.Extensions.Options.Options.Create(new WorkersOptions
{
    DelayMilliseconds = 100
});
```

### 5. **Hierarchical configuration**
- Better organization of configuration
- Logical sections grouped together
- Support for multiple sources (JSON, XML, YAML, etc.)

### 6. **Hot Reload** (if needed)
```csharp
// IOptions<T>: frozen value
// IOptionsSnapshot<T>: value per request
// IOptionsMonitor<T>: value with change notification
```

---

## 🚨 Important Notes

### ⚠️ Environment variable format

**Double underscore** (`__`) is used for hierarchy:

```bash
# Correct ✅
SlimFaas__Namespace=default

# Incorrect ❌
SlimFaas_Namespace=default
SLIMFAAS_NAMESPACE=default
```

### ⚠️ Arrays in environment variables

```bash
# Indexed format
SlimFaas__Ports__0=5000
SlimFaas__Ports__1=5001

# OR in appsettings.json
"Ports": [5000, 5001]
```

### ⚠️ Configuration priority

.NET applies configuration in this order (last one wins):

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. Environment variables
4. Command-line arguments

---

## 📚 Deleted Files

- ❌ `/src/SlimData/EnvironmentVariables.cs` - **DELETED** (obsolete)
- ⚠️ `/src/SlimFaas/EnvironmentVariables.cs` - **CAN BE DELETED** (no longer used in code, only constants remain for historical reference)

---

## ✅ Migration Checklist for Deployments

- [ ] Update `docker-compose.yml` files
- [ ] Update Kubernetes Deployments/StatefulSets
- [ ] Update ConfigMaps
- [ ] Update Secrets (if applicable)
- [ ] Update documentation
- [ ] Test in development environment
- [ ] Test in staging environment
- [ ] Deploy to production with rollback plan

---

## 🆘 Support

If you encounter issues during the migration:

1. Check that variable names follow the .NET format (`Section__Property`)
2. Check startup logs for configuration errors
3. Use `dotnet run --environment Development` to enable detailed logs
4. Refer to the official documentation: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/

---

**Refactoring Date**: February 2026
**Version**: .NET 10
**Status**: ✅ Production Ready
