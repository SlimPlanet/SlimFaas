# Endpoints Minimal API - SlimFaas

Ce dossier contient la transformation du middleware `SlimProxyMiddleware` en endpoints Minimal API compatibles .NET 10 et AOT.

## Structure des fichiers

### 1. **FunctionEndpointsHelpers.cs**
Contient les méthodes helpers partagées par tous les endpoints :
- `SearchFunction()` - Recherche d'une fonction par nom
- `GetFunctionVisibility()` - Détermine la visibilité d'une fonction
- `MessageComeFromNamespaceInternal()` - Vérifie si la requête vient d'un namespace interne
- `InitCustomRequest()` - Initialise une requête personnalisée
- `MapToFunctionStatus()` - Convertit une déployment information en status

### 2. **StatusEndpoints.cs**
Endpoints pour la gestion des statuts :
- `GET /status-functions` - Liste tous les statuts des fonctions
- `GET /status-function/{functionName}` - Récupère le statut d'une fonction
- `POST /wake-function/{functionName}` - Réveille une fonction

### 3. **JobEndpoints.cs**
Endpoints pour la gestion des jobs :
- `POST /job/{functionName}` - Crée un nouveau job
- `GET /job/{functionName}` - Liste les jobs d'une fonction
- `DELETE /job/{functionName}/{elementId}` - Supprime un job

### 4. **JobScheduleEndpoints.cs**
Endpoints pour la gestion des jobs planifiés :
- `POST /job-schedules/{functionName}` - Crée un job planifié
- `GET /job-schedules/{functionName}` - Liste les jobs planifiés
- `DELETE /job-schedules/{functionName}/{elementId}` - Supprime un job planifié

### 5. **SyncFunctionEndpoints.cs**
Endpoints pour l'exécution synchrone de fonctions :
- `GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS /function/{functionName}/{**functionPath}` - Exécute une fonction en mode synchrone

Fonctionnalités :
- Attend le démarrage des pods
- Gère les timeouts
- Copie les headers de réponse
- Mise à jour du last call

### 6. **AsyncFunctionEndpoints.cs**
Endpoints pour l'exécution asynchrone de fonctions :
- `GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS /async-function/{functionName}/{**functionPath}` - Enqueue une fonction en mode asynchrone
- `POST /async-function-callback/{functionName}/{elementId}/{status}` - Callback pour le résultat d'une fonction async

### 7. **EventEndpoints.cs**
Endpoints pour la publication d'événements :
- `POST /publish-event/{eventName}/{**functionPath}` - Publie un événement vers les fonctions abonnées

Fonctionnalités :
- Broadcast vers plusieurs pods/fonctions
- Gestion des erreurs individuelles
- Mise à jour du last call pendant l'exécution

### 8. **SlimFaasEndpointsExtensions.cs**
Extension method pour enregistrer tous les endpoints :
```csharp
app.MapSlimFaasEndpoints();
```

## Utilisation dans Program.cs

Remplacez :
```csharp
app.UseMiddleware<SlimProxyMiddleware>();
```

Par :
```csharp
app.MapSlimFaasEndpoints();
```

N'oubliez pas d'ajouter le using :
```csharp
using SlimFaas.Endpoints;
```

## Compatibilité AOT

Tous les endpoints sont compatibles AOT :
- Utilisation de `JsonSourceGenerationContext` pour la sérialisation
- Pas de réflexion dynamique
- Types explicites dans les signatures
- Utilisation de `Results` au lieu de types anonymes

## Tests

Les tests existants dans `SlimFaas.Tests` doivent être adaptés pour utiliser les nouveaux endpoints au lieu du middleware.

## Migration progressive

Le middleware `SlimProxyMiddleware` reste dans le code pour compatibilité, mais n'est plus utilisé. Il peut être supprimé après validation complète des nouveaux endpoints.

