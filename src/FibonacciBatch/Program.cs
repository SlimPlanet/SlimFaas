
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    var subscribeEvents = new List<SubscribeEventConfig>();

    string? envEvents = Environment.GetEnvironmentVariable("SLIMFAAS_SUBSCRIBE_EVENTS");
    if (!string.IsNullOrWhiteSpace(envEvents))
    {
        foreach (var token in envEvents.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                Enum.TryParse<FunctionVisibility>(parts[0], ignoreCase: true, out var vis))
            {
                subscribeEvents.Add(new SubscribeEventConfig { Name = parts[1], Visibility = vis });
            }
            else
            {
                subscribeEvents.Add(new SubscribeEventConfig { Name = token });
            }
        }
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
            {
                var token = remainingArgs[++idx];
                var parts = token.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 &&
                    Enum.TryParse<FunctionVisibility>(parts[0], ignoreCase: true, out var vis))
                {
                    subscribeEvents.Add(new SubscribeEventConfig { Name = parts[1], Visibility = vis });
                }
                else
                {
                    subscribeEvents.Add(new SubscribeEventConfig { Name = token });
                }
                break;
            }
        }
    }

    Console.WriteLine("╔══════════════════════════════════════════════════════╗");
    Console.WriteLine("║       FibonacciBatch — mode WebSocket SlimFaas       ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════╝");
    Console.WriteLine($"  SlimFaas URL    : {wsUrl}");
    Console.WriteLine($"  Fonction        : {functionName}");
    Console.WriteLine($"  SubscribeEvents : [{string.Join(", ", subscribeEvents.Select(e => e.Visibility != null ? $"{e.Visibility}:{e.Name}" : e.Name))}]");
    Console.WriteLine();

    // ── Configuration du client SlimFaas ─────────────────────────────────
    var config = new SlimFaasClientConfig
    {
        FunctionName = functionName,
        SubscribeEvents = subscribeEvents,
        DefaultVisibility = FunctionVisibility.Public,
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
            var body = Encoding.UTF8.GetString(req.Body);
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
            var body = Encoding.UTF8.GetString(evt.Body);
            Console.WriteLine($"[WS]   Corps     : {body}");
        }
        Console.WriteLine();
        await Task.CompletedTask;
    };

    // ── Callback : requête synchrone streamée ────────────────────────────
    client.OnSyncRequest = async req =>
    {
        Console.WriteLine($"[WS] ── Requête synchrone streamée reçue ──────────────");
        Console.WriteLine($"[WS]   CorrelationId : {req.CorrelationId}");
        Console.WriteLine($"[WS]   Méthode       : {req.Method}");
        Console.WriteLine($"[WS]   Chemin        : {req.Path}");
        if (!string.IsNullOrEmpty(req.Query))
            Console.WriteLine($"[WS]   Query         : {req.Query}");

        // ── Affichage des headers ────────────────────────────────────────
        if (req.Headers.Count > 0)
        {
            Console.WriteLine($"[WS]   Headers ({req.Headers.Count}) :");
            foreach (var (key, values) in req.Headers)
                Console.WriteLine($"[WS]     {key}: {string.Join(", ", values)}");
        }

        // ── Lecture complète du body depuis le stream ────────────────────
        using var bodyStream = new MemoryStream();
        await req.Body.CopyToAsync(bodyStream);
        if (bodyStream.Length > 0)
            Console.WriteLine($"[WS]   Body reçu — {bodyStream.Length} octet(s)");

        // ── Parse et affichage JSON du body ──────────────────────────────
        string responseJson;
        int httpStatus = 200;

        bodyStream.Position = 0;
        var rawBody = Encoding.UTF8.GetString(bodyStream.ToArray());

        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            Console.WriteLine($"[WS]   Corps brut     : {rawBody}");
            try
            {
                var parsed = JsonNode.Parse(rawBody);
                var prettyBody = parsed?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? rawBody;
                Console.WriteLine($"[WS]   Corps (JSON)  :\n{prettyBody}");

                // Calcul Fibonacci si le JSON contient un champ "n" ou si c'est un entier direct
                int? fibInput = null;
                if (parsed is JsonValue val && val.TryGetValue(out int directInt))
                    fibInput = directInt;
                else if (parsed is JsonObject obj)
                {
                    foreach (var candidate in new[] { "n", "value", "input", "number" })
                    {
                        if (obj.TryGetPropertyValue(candidate, out var node) && node is JsonValue jv && jv.TryGetValue(out int v))
                        {
                            fibInput = v;
                            break;
                        }
                    }
                }

                // Construction de la réponse JSON
                var resultObj = new JsonObject { ["request"] = JsonNode.Parse(rawBody) };
                if (fibInput.HasValue && fibInput.Value >= 0 && fibInput.Value <= 40)
                {
                    var fib = new Fibonacci();
                    var fibResult = fib.Run(fibInput.Value);
                    resultObj["fibonacci"] = JsonValue.Create(fibResult);
                    resultObj["input"] = JsonValue.Create(fibInput.Value);
                    Console.WriteLine($"[WS]   Fibonacci({fibInput.Value}) = {fibResult}");
                }
                resultObj["correlationId"] = req.CorrelationId;
                resultObj["status"] = "processed";

                responseJson = resultObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException)
            {
                // Corps non-JSON : on le renvoie tel quel enveloppé
                Console.WriteLine($"[WS]   (Corps non-JSON — renvoi brut)");
                var fallback = new JsonObject
                {
                    ["correlationId"] = req.CorrelationId,
                    ["rawBody"] = rawBody,
                    ["status"] = "processed",
                };
                responseJson = fallback.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
        }
        else
        {
            Console.WriteLine($"[WS]   (Corps vide)");
            var empty = new JsonObject
            {
                ["correlationId"] = req.CorrelationId,
                ["status"] = "processed",
            };
            responseJson = empty.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        Console.WriteLine($"[WS]   Réponse JSON  :\n{responseJson}");
        Console.WriteLine();

        // ── Envoi de la réponse streamée vers SlimFaas ───────────────────
        var responseBytes = Encoding.UTF8.GetBytes(responseJson);

        await req.Response.StartAsync(httpStatus, new Dictionary<string, string[]>
        {
            ["Content-Type"] = ["application/json; charset=utf-8"],
            ["Content-Length"] = [$"{responseBytes.Length}"],
            ["X-Processed-By"] = ["fibonacci-batch"],
        });

        // Envoi en un seul chunk (ou découper si voulu)
        await req.Response.WriteAsync(responseBytes, 0, responseBytes.Length);
        await req.Response.CompleteAsync();
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
