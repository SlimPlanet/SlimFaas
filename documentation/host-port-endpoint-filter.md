# HostPortEndpointFilter

## Description

`HostPortEndpointFilter` est un filtre d'endpoint pour ASP.NET Core Minimal API qui vérifie si le port de la requête HTTP correspond aux ports SlimFaas configurés.

## Fonctionnement

Le filtre compare le port de connexion locale et le port de l'hôte de la requête avec la liste des ports SlimFaas configurés. Si aucun port ne correspond, la requête est rejetée avec un code de statut 404 (NotFound).

## Utilisation

### Application du filtre

Le filtre est appliqué aux endpoints en utilisant la méthode `.AddEndpointFilter<HostPortEndpointFilter>()` :

```csharp
app.MapPost("/publish-event/{eventName}/{**functionPath}", PublishEvent)
    .WithName("PublishEvent")
    .Produces(204)
    .Produces(404)
    .DisableAntiforgery()
    .AddEndpointFilter<HostPortEndpointFilter>();
```

### Injection de dépendances

Le filtre utilise l'injection de dépendances pour obtenir l'instance de `ISlimFaasPorts` :

```csharp
public class HostPortEndpointFilter : IEndpointFilter
{
    private readonly ISlimFaasPorts? _slimFaasPorts;

    public HostPortEndpointFilter(ISlimFaasPorts? slimFaasPorts = null)
    {
        _slimFaasPorts = slimFaasPorts;
    }

    // ...
}
```

### Vérification des ports

Le filtre utilise la méthode `HostPort.IsSamePort()` pour vérifier si les ports correspondent :

```csharp
if (!HostPort.IsSamePort(
    [httpContext.Connection.LocalPort, httpContext.Request.Host.Port ?? 0],
    _slimFaasPorts?.Ports.ToArray() ?? []))
{
    return Results.NotFound();
}
```

## Avantages

1. **Sécurité** : Empêche les requêtes sur des ports non autorisés
2. **Réutilisable** : Peut être appliqué à plusieurs endpoints
3. **Maintenable** : La logique de vérification des ports est centralisée
4. **Compatible AOT** : Compatible avec la compilation Native AOT de .NET

## Endpoints utilisant ce filtre

- `/publish-event/{eventName}/{**functionPath}`
- `/publish-event/{eventName}`

## Remarques

- Si `ISlimFaasPorts` est `null` ou si la liste des ports est vide, le filtre retourne NotFound
- Le filtre est résolu automatiquement via l'injection de dépendances ASP.NET Core
- Compatible avec .NET 10 et la compilation AOT

