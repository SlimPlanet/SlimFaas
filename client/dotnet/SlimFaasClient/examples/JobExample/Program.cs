// Exemple d'utilisation de SlimFaasClient en C#.
// Ce programme montre comment connecter un Job .NET à SlimFaas via WebSocket.

using Microsoft.Extensions.Logging;
using SlimFaasClient;
using System.Text.Json;

// --- Logging ---
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
var logger = loggerFactory.CreateLogger<SlimFaasClient.SlimFaasClient>();

// --- Configuration ---
var config = new SlimFaasClientConfig
{
    // Nom du job (ne doit PAS être le nom d'un Deployment Kubernetes existant)
    FunctionName = "my-dotnet-job",

    // SlimFaas/SubscribeEvents : écoute ces évènements publish-event
    SubscribeEvents = ["order-created", "order-updated"],

    // SlimFaas/DefaultVisibility
    DefaultVisibility = "Public",

    // SlimFaas/NumberParallelRequest
    NumberParallelRequest = 5,

    // SlimFaas/DefaultTrust
    DefaultTrust = "Trusted",
};

var options = new SlimFaasClientOptions
{
    ReconnectDelay = 5.0,
    PingInterval = 30.0,
};

// --- Client ---
var uri = new Uri("ws://localhost:5003/ws");
Console.WriteLine($"Connecting to SlimFaas at {uri} with function '{config.FunctionName}'…");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await using var client = new SlimFaasClient.SlimFaasClient(uri, config, options, logger);

// --- Handler pour les requêtes asynchrones ---
client.OnAsyncRequest = async req =>
{
    Console.WriteLine($"[AsyncRequest] {req.Method} {req.Path}{req.Query} | elementId={req.ElementId}");
    Console.WriteLine($"  Headers: {req.Headers.Count}");
    Console.WriteLine($"  Body: {req.Body?.Length ?? 0} bytes");

    if (req.Body != null)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(req.Body);
            var doc = JsonDocument.Parse(text);
            Console.WriteLine($"  Payload: {doc.RootElement}");
        }
        catch { /* pas du JSON */ }
    }

    // Traitement simulé
    await Task.Delay(100);
    return 200; // Succès
};

// --- Handler pour les évènements publish/subscribe ---
client.OnPublishEvent = async evt =>
{
    Console.WriteLine($"[PublishEvent] '{evt.EventName}' | {evt.Method} {evt.Path}");
    Console.WriteLine($"  Body: {evt.Body?.Length ?? 0} bytes");
    await Task.CompletedTask;
};

Console.WriteLine("Client ready. Waiting for messages… (Ctrl+C to stop)");

try
{
    await client.RunForeverAsync(cts.Token);
}
catch (SlimFaasRegistrationException ex)
{
    Console.Error.WriteLine($"Registration failed: {ex.Message}");
    Environment.Exit(1);
}

Console.WriteLine("Disconnected cleanly.");

