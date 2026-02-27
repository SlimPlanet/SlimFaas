
using SlimFaasClient;

// ─────────────────────────────────────────────────────────────────────────────
// Mode détection
// ─────────────────────────────────────────────────────────────────────────────

const string wsModeFlag = "--ws-mode";

if (args.Length > 0 && args[0] == wsModeFlag)
{
    await RunWebSocketModeAsync(args[1..]);
}
else
{
    RunFibonacciMode(args);
}

// ─────────────────────────────────────────────────────────────────────────────
// Mode classique : calcul Fibonacci en ligne de commande
// ─────────────────────────────────────────────────────────────────────────────

static void RunFibonacciMode(string[] args)
{
    foreach (string arg in args)
    {
        Console.WriteLine($"Calculating Fibonacci for {arg}");
        int i = int.Parse(arg);
        var fibonacci = new Fibonacci();
        var result = fibonacci.Run(i);
        Console.WriteLine($"Fibonacci for {arg} is {result}");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Mode WebSocket : le batch se connecte à SlimFaas et attend des évènements
//
// Usage : FibonacciBatch --ws-mode [options]
//
//   --ws-url <url>          URL WebSocket de SlimFaas
//                           (défaut : env SLIMFAAS_WS_URL ou ws://slimfaas:5003/ws)
//   --function-name <name>  Nom de la fonction virtuelle
//                           (défaut : env SLIMFAAS_FUNCTION_NAME ou "fibonacci-batch")
//   --subscribe <event>     Évènement(s) à écouter (répétable)
//                           (défaut : env SLIMFAAS_SUBSCRIBE_EVENTS, séparés par virgule)
// ─────────────────────────────────────────────────────────────────────────────

static async Task RunWebSocketModeAsync(string[] remainingArgs)
{
    // ── Parsing des arguments ──────────────────────────────────────────────
    string wsUrl = Environment.GetEnvironmentVariable("SLIMFAAS_WS_URL")
                   ?? "ws://slimfaas:5003/ws";
    string functionName = Environment.GetEnvironmentVariable("SLIMFAAS_FUNCTION_NAME")
                          ?? "fibonacci-batch";
    var subscribeEvents = new List<string>();

    string? envEvents = Environment.GetEnvironmentVariable("SLIMFAAS_SUBSCRIBE_EVENTS");
    if (!string.IsNullOrWhiteSpace(envEvents))
    {
        subscribeEvents.AddRange(envEvents.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    for (int idx = 0; idx < remainingArgs.Length; idx++)
    {
        switch (remainingArgs[idx])
        {
            case "--ws-url" when idx + 1 < remainingArgs.Length:
                wsUrl = remainingArgs[++idx];
                break;
            case "--function-name" when idx + 1 < remainingArgs.Length:
                functionName = remainingArgs[++idx];
                break;
            case "--subscribe" when idx + 1 < remainingArgs.Length:
                subscribeEvents.Add(remainingArgs[++idx]);
                break;
        }
    }

    Console.WriteLine("╔══════════════════════════════════════════════════════╗");
    Console.WriteLine("║       FibonacciBatch — mode WebSocket SlimFaas       ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════╝");
    Console.WriteLine($"  SlimFaas URL    : {wsUrl}");
    Console.WriteLine($"  Fonction        : {functionName}");
    Console.WriteLine($"  SubscribeEvents : [{string.Join(", ", subscribeEvents)}]");
    Console.WriteLine();

    // ── Configuration du client SlimFaas ─────────────────────────────────
    var config = new SlimFaasClientConfig
    {
        FunctionName = functionName,
        SubscribeEvents = subscribeEvents,
        DefaultVisibility = "Public",
        NumberParallelRequest = 5,
        NumberParallelRequestPerPod = 5,
    };

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\n[WS] Interruption demandée — fermeture en cours…");
        cts.Cancel();
    };

    await using var client = new SlimFaasClient.SlimFaasClient(new Uri(wsUrl), config);

    // ── Callback : requête asynchrone ─────────────────────────────────────
    client.OnAsyncRequest = async req =>
    {
        Console.WriteLine($"[WS] ── Requête asynchrone reçue ──────────────────────");
        Console.WriteLine($"[WS]   ElementId : {req.ElementId}");
        Console.WriteLine($"[WS]   Méthode   : {req.Method}");
        Console.WriteLine($"[WS]   Chemin    : {req.Path}");
        if (!string.IsNullOrEmpty(req.Query))
            Console.WriteLine($"[WS]   Query     : {req.Query}");
        if (req.Body is { Length: > 0 })
        {
            var body = System.Text.Encoding.UTF8.GetString(req.Body);
            Console.WriteLine($"[WS]   Corps     : {body}");

            // Si le corps contient un nombre, on calcule Fibonacci
            if (int.TryParse(body.Trim(), out int n))
            {
                var fib = new Fibonacci();
                var result = fib.Run(n);
                Console.WriteLine($"[WS]   Fibonacci({n}) = {result}");
            }
        }
        Console.WriteLine($"[WS]   Essai     : {req.TryNumber} (dernier: {req.IsLastTry})");
        Console.WriteLine();
        await Task.CompletedTask;
        return 200;
    };

    // ── Callback : évènement publish/subscribe ────────────────────────────
    client.OnPublishEvent = async evt =>
    {
        Console.WriteLine($"[WS] ── Évènement publish/subscribe reçu ──────────────");
        Console.WriteLine($"[WS]   Événement : {evt.EventName}");
        Console.WriteLine($"[WS]   Méthode   : {evt.Method}");
        Console.WriteLine($"[WS]   Chemin    : {evt.Path}");
        if (!string.IsNullOrEmpty(evt.Query))
            Console.WriteLine($"[WS]   Query     : {evt.Query}");
        if (evt.Body is { Length: > 0 })
        {
            var body = System.Text.Encoding.UTF8.GetString(evt.Body);
            Console.WriteLine($"[WS]   Corps     : {body}");
        }
        Console.WriteLine();
        await Task.CompletedTask;
    };

    Console.WriteLine("[WS] Connexion à SlimFaas… (Ctrl+C pour quitter)");
    Console.WriteLine();

    try
    {
        await client.RunForeverAsync(cts.Token);
    }
    catch (SlimFaasRegistrationException ex)
    {
        Console.Error.WriteLine($"[WS] ERREUR d'enregistrement (fatale) : {ex.Message}");
        Environment.Exit(1);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[WS] Client arrêté proprement.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Algorithme Fibonacci
// ─────────────────────────────────────────────────────────────────────────────

internal class Fibonacci
{
    public int Run(int i)
    {
        if (i <= 2)
        {
            return 1;
        }

        return Run(i - 1) + Run(i - 2);
    }
}
