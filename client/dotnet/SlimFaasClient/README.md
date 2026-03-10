# SlimFaasClient

C# library to connect Jobs or virtual functions to **SlimFaas** via WebSocket.
Lets any process receive async requests and publish/subscribe events without exposing an HTTP port.

[![NuGet](https://img.shields.io/nuget/v/SlimFaasClient.svg)](https://www.nuget.org/packages/SlimFaasClient)

## Installation

```bash
dotnet add package SlimFaasClient
```

## Quick start

```csharp
using SlimFaasClient;

var config = new SlimFaasClientConfig
{
    FunctionName = "my-job",
    SubscribeEvents = [new SubscribeEventConfig { Name = "order-created" }],
    DefaultVisibility = FunctionVisibility.Public,
    NumberParallelRequest = 5,
};

await using var client = new SlimFaasClient.SlimFaasClient(
    new Uri("ws://slimfaas:5003/ws"), config);

// Async request handler — return the HTTP status code SlimFaas should record
client.OnAsyncRequest = async req =>
{
    Console.WriteLine($"Request: {req.Method} {req.Path}{req.Query}");
    // Body is available as a byte[]? decoded from base64
    return 200;
};

// Publish/subscribe event handler
client.OnPublishEvent = async evt =>
    Console.WriteLine($"Event: {evt.EventName}");

// Sync streaming handler (HTTP-over-WebSocket)
client.OnSyncRequest = async req =>
{
    await req.Response.StartAsync(200, new() { ["Content-Type"] = ["text/plain"] });
    await req.Response.WriteAsync(System.Text.Encoding.UTF8.GetBytes("Hello"));
    await req.Response.CompleteAsync();
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await client.RunForeverAsync(cts.Token);
```

## Full configuration

| Property | Kubernetes annotation | Description |
|---|---|---|
| `FunctionName` | Deployment name | Unique name — must **not** match an existing K8s Deployment |
| `DependsOn` | `SlimFaas/DependsOn` | Functions this one depends on |
| `SubscribeEvents` | `SlimFaas/SubscribeEvents` | Events to subscribe to (each with optional visibility override) |
| `DefaultVisibility` | `SlimFaas/DefaultVisibility` | `FunctionVisibility.Public` or `FunctionVisibility.Private` |
| `PathsStartWithVisibility` | `SlimFaas/PathsStartWithVisibility` | Visibility per path prefix |
| `Configuration` | `SlimFaas/Configuration` | Free-form JSON configuration |
| `ReplicasStartAsSoonAsOneFunctionRetrieveARequest` | `SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest` | Global scale trigger |
| `NumberParallelRequest` | `SlimFaas/NumberParallelRequest` | Max concurrent requests across all replicas |
| `NumberParallelRequestPerPod` | `SlimFaas/NumberParallelRequestPerPod` | Max concurrent requests per replica |
| `DefaultTrust` | `SlimFaas/DefaultTrust` | `FunctionTrust.Trusted` or `FunctionTrust.Untrusted` |

## Long-running requests (status 202)

Return `202` from the handler to acknowledge the request without completing it yet,
then call `SendCallbackAsync` when the work is done:

```csharp
client.OnAsyncRequest = async req =>
{
    _ = Task.Run(async () =>
    {
        await LongProcessAsync(req);
        await client.SendCallbackAsync(req.ElementId, 200);
    });
    return 202; // "I'll handle it — will call back"
};
```

## Dependency injection

```csharp
// Register in your DI container (e.g. ASP.NET Core)
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SlimFaasClient.SlimFaasClient>>();
    var config = new SlimFaasClientConfig { FunctionName = "my-job" };
    return new SlimFaasClient.SlimFaasClient(
        new Uri("ws://slimfaas:5003/ws"), config, logger: logger);
});

// Access other services inside handlers via a scoped service provider
client.OnAsyncRequest = async req =>
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<IMyDatabase>();
    await db.SaveAsync(req.Body);
    return 200;
};
```

## Important rules

1. `FunctionName` must **not** match an existing Kubernetes Deployment name.
   SlimFaas will reject the registration with a `SlimFaasRegistrationException`.

2. All clients sharing the same `FunctionName` must use the **exact same configuration**.
   Mismatches are rejected on connection.

## Running the tests

```bash
dotnet test
```
