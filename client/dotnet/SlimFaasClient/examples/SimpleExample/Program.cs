// Ultra-simple SlimFaasClient example in C#.
//
// Connects to SlimFaas via WebSocket, subscribes to "my-event"
// and prints every async request / event / sync request received.
//
// Run:
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

// Async request → print and return 200
client.OnAsyncRequest = async req =>
{
    Console.WriteLine($"[Async] {req.Method} {req.Path} — {req.Body?.Length ?? 0} bytes");
    await Task.CompletedTask;
    return 200;
};

// Publish/subscribe event → print
client.OnPublishEvent = async evt =>
{
    Console.WriteLine($"[Event] {evt.EventName} — {evt.Body?.Length ?? 0} bytes");
    await Task.CompletedTask;
};

// Sync request → respond with JSON "ok"
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

