// Exemple ultra-simple de SlimFaasClient en C#.
//
// Se connecte à SlimFaas via WebSocket, écoute un évènement "my-event"
// et affiche chaque requête async / évènement / requête sync reçue.
//
// Lancement :
//   dotnet run

using System.Text;
using SlimFaasClient;

var config = new SlimFaasClientConfig
{
    FunctionName = "simple-dotnet",
    SubscribeEvents = [new SubscribeEventConfig { Name = "my-event" }],
};

await using var client = new SlimFaasClient.SlimFaasClient(
    new Uri("ws://localhost:5003/ws"), config);

// Requête asynchrone → affiche et retourne 200
client.OnAsyncRequest = async req =>
{
    Console.WriteLine($"[Async] {req.Method} {req.Path} — {req.Body?.Length ?? 0} bytes");
    await Task.CompletedTask;
    return 200;
};

// Évènement publish/subscribe → affiche
client.OnPublishEvent = async evt =>
{
    Console.WriteLine($"[Event] {evt.EventName} — {evt.Body?.Length ?? 0} bytes");
    await Task.CompletedTask;
};

// Requête synchrone → répond "OK" en JSON
client.OnSyncRequest = async req =>
{
    var body = Encoding.UTF8.GetBytes("""{"status":"ok"}""");
    await req.Response.StartAsync(200, new() { ["Content-Type"] = ["application/json"] });
    await req.Response.WriteAsync(body, 0, body.Length);
    await req.Response.CompleteAsync();
};

Console.WriteLine("Listening… (Ctrl+C to stop)");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await client.RunForeverAsync(cts.Token);

