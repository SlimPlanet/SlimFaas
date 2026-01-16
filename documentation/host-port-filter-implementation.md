# Implémentation du filtre HostPortEndpointFilter

## Vue d'ensemble

Le filtre `HostPortEndpointFilter` a été appliqué à **tous les endpoints** de SlimFaas pour garantir que seules les requêtes sur les ports configurés sont acceptées.

## Endpoints protégés

### 1. **AsyncFunctionEndpoints.cs** ✅
- `POST /async-function/{functionName}/{**functionPath}` - Endpoint avec wildcard
- `GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS /async-function/{functionName}` - Endpoint root
- `POST /async-function-callback/{functionName}/{elementId}/{status}` - Callback async

### 2. **EventEndpoints.cs** ✅
- `POST /publish-event/{eventName}/{**functionPath}` - Publication d'événement avec path
- `POST /publish-event/{eventName}` - Publication d'événement root

### 3. **JobEndpoints.cs** ✅
- `POST /job/{functionName}` - Création de job
- `GET /job/{functionName}` - Liste des jobs
- `DELETE /job/{functionName}/{elementId}` - Suppression de job
- `PUT|PATCH /job/{functionName}` - Méthodes bloquées (405)

### 4. **JobScheduleEndpoints.cs** ✅
- `POST /job-schedules/{functionName}` - Création de job planifié
- `GET /job-schedules/{functionName}` - Liste des jobs planifiés
- `DELETE /job-schedules/{functionName}/{elementId}` - Suppression de job planifié
- `PUT|PATCH /job-schedules/{functionName}` - Méthodes bloquées (405)

### 5. **StatusEndpoints.cs** ✅
- `GET /status-functions` - Liste tous les statuts
- `GET /status-function/{functionName}` - Statut d'une fonction
- `POST /wake-function/{functionName}` - Réveiller une fonction

### 6. **SyncFunctionEndpoints.cs** ✅
- `GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS /function/{functionName}/{**functionPath}` - Fonction sync avec path
- `GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS /function/{functionName}` - Fonction sync root

## Total des endpoints protégés

- **22 endpoints** protégés par le filtre `HostPortEndpointFilter`
- **6 fichiers** d'endpoints modifiés

## Comportement du filtre

Pour chaque requête :
1. Le filtre vérifie si le port de connexion locale OU le port de l'hôte correspond aux ports SlimFaas
2. Si aucun port ne correspond → `404 NotFound`
3. Si un port correspond → la requête continue son traitement normal

## Avantages de cette approche

1. **Sécurité centralisée** : Un seul filtre géré à un seul endroit
2. **Réutilisabilité** : Le filtre est appliqué via `.AddEndpointFilter<HostPortEndpointFilter>()`
3. **Maintenabilité** : Toute modification de la logique de vérification des ports se fait dans un seul fichier
4. **Performance** : Le filtre s'exécute avant le traitement de la requête
5. **Compatible AOT** : Compatible avec .NET 10 et la compilation Native AOT

## Injection de dépendances

Le filtre utilise l'injection de dépendances pour obtenir `ISlimFaasPorts` :

```csharp
public HostPortEndpointFilter(ISlimFaasPorts? slimFaasPorts = null)
{
    _slimFaasPorts = slimFaasPorts;
}
```

Cette dépendance est enregistrée dans `Program.cs` :

```csharp
serviceCollectionStarter.AddSingleton<ISlimFaasPorts, SlimFaasPorts>();
```

## Tests

Pour tester le filtre, vous pouvez :
1. Envoyer une requête sur un port non-SlimFaas → doit retourner `404`
2. Envoyer une requête sur un port SlimFaas → doit traiter la requête normalement

## Remarques

- Le middleware original dans `Program.cs` (lignes 500-510) peut maintenant être supprimé car la logique est déplacée dans le filtre
- Tous les endpoints SlimFaas sont maintenant protégés de manière uniforme
- Le filtre est résolu automatiquement via DI, aucune configuration supplémentaire n'est nécessaire

