// Example usage of SlimFaasClient in C#.
// This program shows how to connect a .NET Job to SlimFaas via WebSocket.

using Microsoft.Extensions.Logging;
using SlimFaasClient;
using System.Text.Json;

// --- Logging ---
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
var logger = loggerFactory.CreateLogger<SlimFaasClient.SlimFaasClient>();

// --- Configuration ---
var config = new SlimFaasClientConfig
{
    // Job name (must NOT be the name of an existing Kubernetes Deployment)
    FunctionName = "my-dotnet-job",

    // SlimFaas/SubscribeEvents: listen to these publish-event events
    SubscribeEvents =
    [
        new SubscribeEventConfig { Name = "order-created" },
        new SubscribeEventConfig { Name = "order-updated" },
    ],

    // SlimFaas/DefaultVisibility
    DefaultVisibility = FunctionVisibility.Public,

    // SlimFaas/NumberParallelRequest
    NumberParallelRequest = 5,

    // SlimFaas/DefaultTrust
    DefaultTrust = FunctionTrust.Trusted,
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

// --- Async request handler ---
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
        catch { /* not JSON */ }
    }

    // Simulated processing
    await Task.Delay(100);
    return 200; // Success
};

// --- Publish/subscribe event handler ---
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

