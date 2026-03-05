
# SlimFaasClient

Librairie C# pour connecter des Jobs ou fonctions virtuelles à **SlimFaas** via WebSocket. Permet de recevoir des requêtes asynchrones et des évènements publish/subscribe sans exposer de port HTTP.

## Installation

```bash
dotnet add package SlimFaasClient
```

## Utilisation

```csharp
using SlimFaasClient;

var config = new SlimFaasClientConfig
{
    FunctionName = "my-job",
    SubscribeEvents = ["order-created"],
    DefaultVisibility = "Public",
    NumberParallelRequest = 5,
};

var options = new SlimFaasClientOptions
{
    ReconnectDelay = 5.0,
    PingInterval = 30.0,
};

await using var client = new SlimFaasClient(
    new Uri("ws://slimfaas:5003/ws"),
    config,
    options,
    logger);

client.OnAsyncRequest = async req =>
{
    Console.WriteLine($"Request: {req.Method} {req.Path}{req.Query}");
    // Corps disponible dans req.Body (byte[]? décodé depuis base64)
    // ...
    return 200; // Code HTTP à renvoyer à SlimFaas
};

client.OnPublishEvent = async evt =>
{
    Console.WriteLine($"Event: {evt.EventName}");
    // ...
};

await client.RunForeverAsync(CancellationToken.None);
```

## Configuration complète

| Propriété | Annotation Kubernetes | Description |
|---|---|---|
| `FunctionName` | nom du Deployment | Nom unique (ne doit pas être un Deployment K8s existant) |
| `DependsOn` | `SlimFaas/DependsOn` | Fonctions dont celle-ci dépend |
| `SubscribeEvents` | `SlimFaas/SubscribeEvents` | Évènements auxquels s'abonner |
| `DefaultVisibility` | `SlimFaas/DefaultVisibility` | `"Public"` ou `"Private"` |
| `PathsStartWithVisibility` | `SlimFaas/PathsStartWithVisibility` | Visibilité par préfixe |
| `Configuration` | `SlimFaas/Configuration` | Config JSON libre |
| `ReplicasStartAsSoonAsOneFunctionRetrieveARequest` | `SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest` | Scale global |
| `NumberParallelRequest` | `SlimFaas/NumberParallelRequest` | Max requêtes parallèles |
| `NumberParallelRequestPerPod` | `SlimFaas/NumberParallelRequestPerPod` | Max par replica |
| `DefaultTrust` | `SlimFaas/DefaultTrust` | `"Trusted"` ou `"Untrusted"` |

## Traitement long (status 202)

```csharp
client.OnAsyncRequest = async req =>
{
    _ = Task.Run(async () =>
    {
        await LongProcessAsync(req);
        await client.SendCallbackAsync(req.ElementId, 200);
    });
    return 202; // "Je m'en occupe"
};
```

## Règles

1. Le `FunctionName` ne doit pas correspondre à un Deployment Kubernetes existant.
2. Tous les clients avec le même `FunctionName` doivent avoir la **même configuration** (sinon `SlimFaasRegistrationException`).

## Tests

```bash
dotnet test
```

