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

### Après - Option 2 : Variables d'environnement (format .NET)

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

## 🔧 Migration Kubernetes ConfigMap/Secrets

### Avant

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

### Après

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

**OU** utiliser le format .NET avec double underscore :

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

## 📂 Structure des classes d'options

### SlimFaasOptions
**Fichier** : `/src/SlimFaas/Options/SlimFaasOptions.cs`
**Section** : `"SlimFaas"`

| Propriété | Type | Défaut | Description |
|-----------|------|--------|-------------|
| `AllowUnsecureSsl` | `bool` | `false` | Autoriser les connexions SSL non sécurisées |
| `JobsConfiguration` | `string?` | `null` | Configuration des jobs au format JSON |
| `CorsAllowOrigin` | `string` | `"*"` | Origines CORS autorisées |
| `BaseSlimDataUrl` | `string` | `"http://{pod_name}.{service_name}.{namespace}.svc:3262"` | URL de base pour SlimData |
| `BaseFunctionUrl` | `string` | `"http://{pod_ip}:{pod_port}"` | URL de base pour les fonctions |
| `BaseFunctionPodUrl` | `string` | `"http://{pod_ip}:{pod_port}"` | URL de base pour les pods de fonctions |
| `Namespace` | `string` | `"default"` | Namespace Kubernetes |
| `Orchestrator` | `string` | `"Kubernetes"` | Type d'orchestrateur (Kubernetes, Docker, Mock) |
| `MockKubernetesFunctions` | `string?` | `null` | Fonctions mockées (séparées par virgule) |
| `Hostname` | `string` | `"slimfaas-1"` | Nom d'hôte du pod |
| `Ports` | `int[]` | `[]` | Ports d'écoute |
| `PodScaledUpByDefaultWhenInfrastructureHasNeverCalled` | `bool` | `false` | Démarrer les pods par défaut |

### WorkersOptions
**Fichier** : `/src/SlimFaas/Options/WorkersOptions.cs`
**Section** : `"Workers"`

| Propriété | Type | Défaut | Description |
|-----------|------|--------|-------------|
| `DelayMilliseconds` | `int` | `10` | Délai principal du worker (ms) |
| `JobsDelayMilliseconds` | `int` | `1000` | Délai du worker de jobs (ms) |
| `ReplicasSynchronizationDelayMilliseconds` | `int` | `3000` | Délai de synchronisation des replicas (ms) |
| `HistorySynchronizationDelayMilliseconds` | `int` | `500` | Délai de synchronisation de l'historique (ms) |
| `ScaleReplicasDelayMilliseconds` | `int` | `1000` | Délai de scaling des replicas (ms) |
| `HealthDelayMilliseconds` | `int` | `1000` | Délai des health checks (ms) |
| `HealthDelayToExitSeconds` | `int` | `60` | Délai avant sortie après problème de santé (s) |
| `HealthDelayToStartHealthCheckSeconds` | `int` | `20` | Délai avant démarrage des health checks (s) |

### SlimDataOptions
**Fichier** : `/src/SlimFaas/Options/SlimDataOptions.cs`
**Section** : `"SlimData"`

| Propriété | Type | Défaut | Description |
|-----------|------|--------|-------------|
| `Directory` | `string?` | `null` | Répertoire de stockage persistant |
| `Configuration` | `string?` | `null` | Configuration JSON pour SlimData |
| `AllowColdStart` | `bool` | `false` | Autoriser le démarrage à froid |

### RaftClientHandlerOptions (nouveau)
**Fichier** : `/src/SlimData/Options/RaftClientHandlerOptions.cs`
**Section** : `"RaftClientHandler"`

| Propriété | Type | Défaut | Description |
|-----------|------|--------|-------------|
| `ConnectTimeoutMilliseconds` | `int` | `2000` | Timeout de connexion TCP+TLS (ms) |
| `PooledConnectionLifetimeMinutes` | `int` | `5` | Durée de vie des connexions poolées (min) |
| `PooledConnectionIdleTimeoutSeconds` | `int` | `30` | Timeout d'inactivité des connexions (s) |
| `MaxConnectionsPerServer` | `int` | `100` | Nombre max de connexions par serveur |

---

## 🔄 Exemples de migration

### Exemple 1 : Configuration simple

**Avant** :
```bash
export NAMESPACE=production
export SLIMFAAS_ORCHESTRATOR=Kubernetes
export SLIM_WORKER_DELAY_MILLISECONDS=50
```

**Après** :
```bash
export SlimFaas__Namespace=production
export SlimFaas__Orchestrator=Kubernetes
export Workers__DelayMilliseconds=50
```

### Exemple 2 : Configuration avec tableau

**Avant** :
```bash
export SLIMFAAS_PORTS=5000,5001,5002
```

**Après** :
```bash
export SlimFaas__Ports__0=5000
export SlimFaas__Ports__1=5001
export SlimFaas__Ports__2=5002
```

**OU** dans appsettings.json :
```json
{
  "SlimFaas": {
    "Ports": [5000, 5001, 5002]
  }
}
```

### Exemple 3 : Configuration JSON complexe

**Avant** :
```bash
export SLIMDATA_CONFIGURATION='{"coldStart":"true","replica":3}'
```

**Après** :
```bash
export SlimData__Configuration='{"coldStart":"true","replica":3}'
```

**OU** dans appsettings.json :
```json
{
  "SlimData": {
    "Configuration": "{\"coldStart\":\"true\",\"replica\":3}"
  }
}
```

---

## 🎯 Avantages de la nouvelle approche

### 1. **Type Safety**
```csharp
// AVANT : Parsing manuel avec risque d'erreur runtime
int delay = int.Parse(Environment.GetEnvironmentVariable("DELAY") ?? "1000");

// APRÈS : Type-safe à la compilation
int delay = workersOptions.Value.DelayMilliseconds;
```

### 2. **Validation au démarrage**
```csharp
services.AddOptions<WorkersOptions>()
    .Bind(configuration.GetSection(WorkersOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();  // ⚠️ Échoue au démarrage si la config est invalide
```

### 3. **IntelliSense et documentation**
- Autocomplétion dans l'IDE
- Commentaires XML sur chaque propriété
- Pas besoin de chercher dans le code pour trouver les noms des variables

### 4. **Testabilité**
```csharp
// Créer des options mock pour les tests
var mockOptions = Microsoft.Extensions.Options.Options.Create(new WorkersOptions
{
    DelayMilliseconds = 100
});
```

### 5. **Configuration hiérarchique**
- Meilleure organisation de la configuration
- Sections logiques regroupées
- Support de multiples sources (JSON, XML, YAML, etc.)

### 6. **Hot Reload** (si nécessaire)
```csharp
// IOptions<T> : valeur figée
// IOptionsSnapshot<T> : valeur par requête
// IOptionsMonitor<T> : valeur avec notification de changement
```

---

## 🚨 Notes importantes

### ⚠️ Format des variables d'environnement

**Double underscore** (`__`) est utilisé pour la hiérarchie :

```bash
# Correct ✅
SlimFaas__Namespace=default

# Incorrect ❌
SlimFaas_Namespace=default
SLIMFAAS_NAMESPACE=default
```

### ⚠️ Tableaux dans les variables d'environnement

```bash
# Format à indices
SlimFaas__Ports__0=5000
SlimFaas__Ports__1=5001

# OU dans appsettings.json
"Ports": [5000, 5001]
```

### ⚠️ Priorité de configuration

.NET applique la configuration dans cet ordre (le dernier gagne) :

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. Variables d'environnement
4. Arguments en ligne de commande

---

## 📚 Fichiers supprimés

- ❌ `/src/SlimData/EnvironmentVariables.cs` - **SUPPRIMÉ** (obsolète)
- ⚠️ `/src/SlimFaas/EnvironmentVariables.cs` - **PEUT ÊTRE SUPPRIMÉ** (plus utilisé dans le code, seulement les constantes restent pour référence historique)

---

## ✅ Checklist de migration pour les déploiements

- [ ] Mettre à jour les fichiers `docker-compose.yml`
- [ ] Mettre à jour les Kubernetes Deployments/StatefulSets
- [ ] Mettre à jour les ConfigMaps
- [ ] Mettre à jour les Secrets (si applicable)
- [ ] Mettre à jour la documentation
- [ ] Tester en environnement de développement
- [ ] Tester en environnement de staging
- [ ] Déployer en production avec rollback plan

---

## 🆘 Support

Si vous rencontrez des problèmes lors de la migration :

1. Vérifiez que les noms des variables suivent le format .NET (`Section__Property`)
2. Vérifiez les logs au démarrage pour les erreurs de configuration
3. Utilisez `dotnet run --environment Development` pour activer les logs détaillés
4. Consultez la documentation officielle : https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/

---

**Date de la refactorisation** : Février 2026
**Version** : .NET 10
**Status** : ✅ Production Ready
