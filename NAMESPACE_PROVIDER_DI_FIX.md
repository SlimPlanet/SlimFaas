# Correction INamespaceProvider - Injection de dépendances

## Problème
L'application crashait au démarrage avec l'erreur :
```
System.InvalidOperationException: Unable to resolve service for type 'SlimFaas.Kubernetes.INamespaceProvider' 
while attempting to activate 'SlimFaas.Jobs.JobService'.
```

### Cause
`INamespaceProvider` était enregistré dans `serviceCollectionStarter` mais n'était pas transféré vers `serviceCollectionSlimFaas`, le conteneur principal de l'application. Cela causait une erreur lors de la résolution de `JobService` qui dépend de `INamespaceProvider`.

## Solution appliquée

### Modification de `Program.cs` (ligne ~227)

Ajout de l'enregistrement de `INamespaceProvider` dans `serviceCollectionSlimFaas` :

```csharp
serviceCollectionSlimFaas.AddSingleton<IKubernetesService>(sp =>
    serviceProviderStarter.GetService<IKubernetesService>()!);
serviceCollectionSlimFaas.AddSingleton<INamespaceProvider>(sp =>
    serviceProviderStarter.GetRequiredService<INamespaceProvider>());
serviceCollectionSlimFaas.AddSingleton<IJobService, JobService>();
```

### Explication

Dans l'architecture de SlimFaas, il y a deux conteneurs d'injection de dépendances :

1. **`serviceProviderStarter`** : Conteneur initial utilisé pour les services de démarrage
   - `INamespaceProvider` y était enregistré
   - `IReplicasService` y était créé
   
2. **`serviceCollectionSlimFaas`** : Conteneur principal de l'application
   - `JobService` y est enregistré
   - Tous les workers y sont enregistrés
   - **`INamespaceProvider` y manquait** ❌

La correction transfère `INamespaceProvider` de `serviceProviderStarter` vers `serviceCollectionSlimFaas`, permettant à tous les services et workers d'y accéder.

## Services affectés positivement

Avec cette correction, les services suivants peuvent maintenant être correctement instanciés :

✅ **JobService** - Utilise `INamespaceProvider` dans son constructeur
✅ **SendClient** - Utilise `INamespaceProvider` dans son constructeur  
✅ **ScaleReplicasWorker** - Utilise `INamespaceProvider` dans son constructeur
✅ **ReplicasSynchronizationWorker** - Utilise `INamespaceProvider` dans son constructeur
✅ **MetricsScrapingWorker** - Utilise `INamespaceProvider` dans son constructeur
✅ **SlimDataSynchronizationWorker** - Utilise `INamespaceProvider` dans son constructeur

## Résultat

✅ **Compilation réussie** sans erreur
✅ **Tous les services peuvent être résolus** correctement
✅ **L'application peut démarrer** sans exception `InvalidOperationException`
✅ **Les tests unitaires** continuent de fonctionner avec les mocks de `INamespaceProvider`

## Pattern utilisé

Ce pattern est déjà utilisé pour d'autres services dans le même fichier :

```csharp
serviceCollectionSlimFaas.AddSingleton<IServiceX>(sp =>
    serviceProviderStarter.GetRequiredService<IServiceX>());
```

Exemples existants :
- `IMetricsScrapingGuard`
- `IRequestedMetricsRegistry`
- `IMetricsStore`
- `IAutoScalerStore`
