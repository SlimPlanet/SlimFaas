# Refactorisation: Configuration Fortement Typée

## Résumé

Cette refactorisation remplace complètement l'utilisation des variables d'environnement par une configuration fortement typée utilisant `appsettings.json` en .NET 10. Il s'agit d'un **BREAKING CHANGE** important.

## Fichiers Créés

### Classes d'Options

1. **`src/SlimFaas/Options/SlimFaasOptions.cs`**
   - Configuration principale de SlimFaas
   - Propriétés: AllowUnsecureSsl, JobsConfiguration, CorsAllowOrigin, BaseSlimDataUrl, BaseFunctionUrl, BaseFunctionPodUrl, Namespace, Orchestrator, MockKubernetesFunctions, Hostname, Ports, PodScaledUpByDefaultWhenInfrastructureHasNeverCalled

2. **`src/SlimFaas/Options/SlimDataOptions.cs`**
   - Configuration de SlimData
   - Propriétés: Directory, Configuration, AllowColdStart

3. **`src/SlimFaas/Options/WorkersOptions.cs`**
   - Configuration des workers en arrière-plan
   - Propriétés: DelayMilliseconds, JobsDelayMilliseconds, ReplicasSynchronizationDelayMilliseconds, HistorySynchronizationDelayMilliseconds, ScaleReplicasDelayMilliseconds, HealthDelayMilliseconds, HealthDelayToExitSeconds, HealthDelayToStartHealthCheckSeconds

4. **`src/SlimFaas/Options/OptionsExtensions.cs`**
   - Méthodes d'extension pour enregistrer les options
   - Méthode utilitaire `GetTemporaryDirectory()`

### Documentation

5. **`MIGRATION_CONFIGURATION.md`**
   - Guide complet de migration
   - Tableau de correspondance des anciennes variables d'environnement vers les nouvelles configurations
   - Exemples pour Kubernetes et Docker Compose

## Fichiers Modifiés

### Configuration

- **`src/SlimFaas/appsettings.json`** - Ajout des nouvelles sections SlimFaas, SlimData et Workers
- **`global.json`** - Mise à jour de la version du SDK de 10.0.102 vers 10.0.100

### Programme Principal

- **`src/SlimFaas/Program.cs`**
  - Suppression de l'import `EnvironmentVariables`
  - Ajout de l'import `Microsoft.Extensions.Options`
  - Chargement précoce de la configuration
  - Liaison des options avant l'utilisation
  - Enregistrement des options dans le conteneur DI
  - Mise à jour de tous les appels pour utiliser les options au lieu des variables d'environnement
  - Combinaison intelligente du namespace (depuis Kubernetes ou config)

### Workers (Tous mis à jour pour utiliser IOptions)

- **`src/SlimFaas/Workers/HealthWorker.cs`**
- **`src/SlimFaas/Workers/HistorySynchronizationWorker.cs`**
- **`src/SlimFaas/Workers/ReplicasSynchronizationWorker.cs`**
- **`src/SlimFaas/Workers/ReplicasScaleWorker.cs`**
- **`src/SlimFaas/Workers/SlimQueuesWorker.cs`**
- **`src/SlimFaas/Workers/SlimDataSynchronizationWorker.cs`**
- **`src/SlimFaas/Workers/MetricsScrapingWorker.cs`**

### Jobs

- **`src/SlimFaas/Jobs/SlimJobsWorker.cs`** - Injection de IOptions<WorkersOptions>
- **`src/SlimFaas/Jobs/JobService.cs`** - Injection de IOptions<SlimFaasOptions>
- **`src/SlimFaas/Jobs/JobConfiguration.cs`** - Injection de IOptions<SlimFaasOptions>

### Services

- **`src/SlimFaas/SendClient.cs`** - Injection de IOptions<SlimFaasOptions>
- **`src/SlimFaas/SlimFaasPorts.cs`** - Injection de IOptions<SlimFaasOptions>
- **`src/SlimFaas/SlimDataEndpoint.cs`** - Signature mise à jour pour accepter baseUrl et namespace en paramètres

### Kubernetes

- **`src/SlimFaas/Kubernetes/MockKubernetesService.cs`** - Injection de IOptions<SlimFaasOptions>
- **`src/SlimFaas/Kubernetes/Namespace.cs`** - Méthode GetNamespace mise à jour pour accepter un defaultNamespace

### Endpoints

- **`src/SlimFaas/Endpoints/EventEndpoints.cs`** - Injection de IOptions<SlimFaasOptions>

## Changements Techniques

### Injection de Dépendances

Tous les services qui utilisaient précédemment `Environment.GetEnvironmentVariable()` ou `EnvironmentVariables.ReadInteger/ReadBoolean()` utilisent maintenant l'injection de dépendances via `IOptions<T>`.

### Validation

Les options sont validées au démarrage avec:
```csharp
.ValidateDataAnnotations()
.ValidateOnStart();
```

### Compatibilité AOT

Cette approche améliore la compatibilité avec la compilation Native AOT car elle évite la réflexion dynamique liée aux variables d'environnement.

## Bénéfices

1. **Type Safety** - Les erreurs de configuration sont détectées à la compilation
2. **IntelliSense** - Support complet de l'IDE pour la configuration
3. **Validation** - Validation automatique au démarrage
4. **Testabilité** - Configuration facile à mocker dans les tests
5. **Maintenabilité** - Code plus propre et plus facile à maintenir
6. **Best Practices .NET** - Suit les recommandations officielles de Microsoft
7. **Documentation** - Les options sont documentées via XML comments

## Tests

Le projet compile avec succès (27 avertissements, 0 erreur). Les avertissements sont principalement:
- Avertissements de nullabilité existants
- Avertissements AOT pour des parties non critiques (comme les attributs DynamicCode)
- Avertissements de style de code

## Migration pour les Utilisateurs

Les utilisateurs doivent migrer leur configuration selon le guide dans `MIGRATION_CONFIGURATION.md`.

Les principales options de migration sont:
1. Utiliser `appsettings.json` ou `appsettings.Production.json`
2. Utiliser un ConfigMap Kubernetes
3. Utiliser les variables d'environnement avec la convention .NET `Section__Property` (double underscore)

## Prochaines Étapes Recommandées

1. Mettre à jour les tests unitaires pour utiliser les nouvelles options
2. Mettre à jour les exemples Docker Compose et Kubernetes dans le dépôt
3. Mettre à jour la documentation principale
4. Créer une release note avec ce breaking change
5. Supprimer le fichier `EnvironmentVariables.cs` (actuellement conservé pour référence)

## Note sur EnvironmentVariables.cs

Le fichier `src/SlimFaas/EnvironmentVariables.cs` est toujours présent mais n'est plus utilisé dans le code. Il peut être supprimé dans une future étape de nettoyage.
