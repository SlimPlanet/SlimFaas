# Migration des tests : SlimProxyMiddleware → Endpoints Minimal API

## 📋 Vue d'ensemble

Les tests ont été migrés du middleware `SlimProxyMiddleware` vers les nouveaux **Endpoints Minimal API**.

---

## 📁 Fichiers

| Ancien | Nouveau | Statut |
|--------|---------|--------|
| `SlimProxyMiddlewareTests.cs` | `EndpointsTests.cs` | ✅ Migré |
| `SlimProxyMiddlewareTimeoutTests.cs` | À migrer | ⏳ En attente |

---

## 🔄 Changements principaux

### 1. Configuration de l'hôte de test

#### ❌ Avant (Middleware)
```csharp
.Configure(app => {
    app.UseMiddleware<SlimProxyMiddleware>();
});
```

#### ✅ Après (Endpoints)
```csharp
.Configure(app =>
{
    app.UseRouting();
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapEventEndpoints();        // Pour /publish-event
        endpoints.MapSyncFunctionEndpoints(); // Pour /function
        endpoints.MapAsyncFunctionEndpoints();// Pour /async-function
        endpoints.MapStatusEndpoints();       // Pour /status et /wake
    });
});
```

### 2. Services requis

Il faut ajouter le service de routing :

```csharp
.ConfigureServices(services =>
{
    // ...existing code...
    services.AddRouting(); // ← Nouveau
})
```

---

## 🧪 Tests migrés

### 1. Tests de publication d'événements

**Nom de test** : `CallPublishInSyncModeAndReturnOk` → `CallPublishEventEndpointAndReturnOk`

**Endpoint testé** : `/publish-event/{eventName}/{**functionPath}`

**Changements** :
- Utilise `MapEventEndpoints()` au lieu du middleware
- Le verbe HTTP doit être `POST` (pas GET)

```csharp
// Avant
HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

// Après
HttpResponseMessage response = await host.GetTestClient().PostAsync($"http://localhost:5000{path}", null);
```

### 2. Tests de fonctions synchrones

**Nom de test** : `CallFunctionInSyncModeAndReturnOk` → `CallSyncFunctionEndpointAndReturnOk`

**Endpoint testé** : `/function/{functionName}/{**functionPath}`

**Changements** :
- Utilise `MapSyncFunctionEndpoints()`
- Pas de changement de verbe HTTP (toujours GET)

### 3. Tests de fonctions asynchrones

**Nom de test** : `CallFunctionInAsyncSyncModeAndReturnOk` → `CallAsyncFunctionEndpointAndReturnOk`

**Endpoint testé** : `/async-function/{functionName}/{**functionPath}`

**Changements** :
- Utilise `MapAsyncFunctionEndpoints()`
- Retourne toujours `202 Accepted`

### 4. Tests de réveil de fonction

**Nom de test** : `JustWakeFunctionAndReturnOk` → `WakeFunctionEndpointAndReturnOk`

**Endpoint testé** : `/wake-function/{functionName}`

**Changements** :
- Utilise `MapStatusEndpoints()`
- Le verbe HTTP doit être `POST` (pas GET)

```csharp
// Avant
HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

// Après
HttpResponseMessage response = await host.GetTestClient().PostAsync($"http://localhost:5000{path}", null);
```

### 5. Tests de statut de fonction

**Nom de test** : `GetStatusFunctionAndReturnOk` → `GetStatusFunctionEndpointAndReturnOk`

**Endpoints testés** :
- `/status-function/{functionName}` (GET)
- `/status-functions` (GET)

**Changements** :
- Utilise `MapStatusEndpoints()`
- Format JSON mis à jour (camelCase au lieu de PascalCase)

```json
// Avant
{"NumberReady":1,"NumberRequested":0,"PodType":"Deployment","Visibility":"Public","Name":"fibonacci"}

// Après
{"numberReady":1,"numberRequested":0,"podType":"Deployment","visibility":"Public","functionName":"fibonacci"}
```

---

## 📊 Matrice de migration

| Test original | Test migré | Endpoint | Verbe | Changements |
|---------------|------------|----------|-------|-------------|
| `CallPublishInSyncModeAndReturnOk` | `CallPublishEventEndpointAndReturnOk` | `/publish-event/{event}/{**path}` | POST | ✅ Verbe changé |
| `CallFunctionInSyncModeAndReturnOk` | `CallSyncFunctionEndpointAndReturnOk` | `/function/{name}/{**path}` | GET | ✅ Aucun |
| `CallFunctionInAsyncSyncModeAndReturnOk` | `CallAsyncFunctionEndpointAndReturnOk` | `/async-function/{name}/{**path}` | GET | ✅ Aucun |
| `JustWakeFunctionAndReturnOk` | `WakeFunctionEndpointAndReturnOk` | `/wake-function/{name}` | POST | ✅ Verbe changé |
| `GetStatusFunctionAndReturnOk` | `GetStatusFunctionEndpointAndpointAndReturnOk` | `/status-*` | GET | ✅ Format JSON |

---

## 🔍 Différences clés

### Format JSON des réponses

Les endpoints Minimal API utilisent des **JSON Source Generators** qui produisent du JSON en **camelCase** par défaut.

| Propriété (Middleware) | Propriété (Endpoints) |
|------------------------|----------------------|
| `NumberReady` | `numberReady` |
| `NumberRequested` | `numberRequested` |
| `PodType` | `podType` |
| `Visibility` | `visibility` |
| `Name` | `functionName` |

### Verbes HTTP

| Endpoint | Middleware | Endpoints |
|----------|-----------|-----------|
| `/publish-event/*` | GET | **POST** |
| `/wake-function/*` | GET | **POST** |
| `/function/*` | GET | GET |
| `/async-function/*` | GET | GET |
| `/status-*` | GET | GET |

---

## ✅ Avantages de la migration

| Avantage | Description |
|----------|-------------|
| **Performance** | Les endpoints sont plus rapides que le middleware |
| **Typage fort** | Les routes sont fortement typées |
| **AOT** | Compatible avec .NET Native AOT |
| **Testabilité** | Plus facile à tester individuellement |
| **Séparation** | Chaque endpoint a sa propre logique |

---

## 🚀 Pour exécuter les nouveaux tests

```bash
# Tous les nouveaux tests
dotnet test tests/SlimFaas.Tests/SlimFaas.Tests.csproj \
  --filter "FullyQualifiedName~EndpointsTests"

# Test spécifique
dotnet test tests/SlimFaas.Tests/SlimFaas.Tests.csproj \
  --filter "FullyQualifiedName~CallPublishEventEndpointAndReturnOk"
```

---

## 📝 Classes helper partagées

Les classes suivantes sont utilisées par les deux fichiers de tests :

- ✅ `MemoryReplicasService`
- ✅ `MemoryReplicas2ReplicasService`
- ✅ `MemorySlimFaasQueue`
- ✅ `SendClientMock`
- ✅ `SlimFaasPortsMock`
- ✅ `SendData` (record)

---

## ⚠️ Points d'attention

### 1. Verbes HTTP
Les endpoints `/publish-event` et `/wake-function` utilisent maintenant **POST** au lieu de GET.

### 2. Format JSON
Les réponses JSON sont en **camelCase** à cause des JSON Source Generators.

### 3. Routing
Il faut ajouter `services.AddRouting()` dans la configuration des services.

### 4. Endpoints séparés
Chaque type d'endpoint doit être mappé explicitement :
```csharp
endpoints.MapEventEndpoints();
endpoints.MapSyncFunctionEndpoints();
endpoints.MapAsyncFunctionEndpoints();
endpoints.MapStatusEndpoints();
```

---

## 🎯 Prochaines étapes

1. ✅ Tests Endpoints créés
2. ⏳ Migrer `SlimProxyMiddlewareTimeoutTests.cs`
3. ⏳ Supprimer `SlimProxyMiddleware.cs` si plus utilisé
4. ⏳ Mettre à jour la documentation

---

## 🎉 Résultat

Les tests ont été **migrés avec succès** des middleware vers les endpoints Minimal API !

- ✅ **5 tests migrés**
- ✅ **Compilation réussie**
- ✅ **Prêt pour exécution**
- ✅ **Compatible AOT**

