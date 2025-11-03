// dotnet run
// .NET 7/8/9

using System.Net.Http;
using System.Text;
using System.Text.Json;

var baseAddress = "http://localhost:30021";
var fib1 = "/async-function/fibonacci1/compute";
var fib2 = "/async-function/fibonacci2/compute";

// Combien de paires (2 requêtes envoyées en même temps : fib1 + fib2)
int pairs = 10000;

// (Optionnel) délai entre paires pour bien voir les logs côté serveur
TimeSpan? interPairDelay = TimeSpan.FromMilliseconds(10);

using var http = new HttpClient { BaseAddress = new Uri(baseAddress) };

for (int i = 0; i < pairs; i++)
{
    // Prépare 2 bodies différents
    var body1 = JsonSerializer.Serialize(new { seq = i, type = "fib1", n = 30, note = "pair-start" });
    var body2 = JsonSerializer.Serialize(new { seq = i, type = "fib2", n = 31, note = "pair-start" });

    // Crée les 2 requêtes
    using var req1 = new HttpRequestMessage(HttpMethod.Post, fib1)
    {
        Content = new StringContent(body1, Encoding.UTF8, "application/json")
    };
    using var req2 = new HttpRequestMessage(HttpMethod.Post, fib2)
    {
        Content = new StringContent(body2, Encoding.UTF8, "application/json")
    };

    Console.WriteLine($"[{i}] -> (parallel) POST {fib1} | {fib2}");

    // Lance vraiment en parallèle
    var send1 = http.SendAsync(req1);
    var send2 = http.SendAsync(req2);

    var responses = await Task.WhenAll(send1, send2);

    // (Optionnel) log des réponses
    for (int k = 0; k < responses.Length; k++)
    {
        var r = responses[k];
        var txt = await r.Content.ReadAsStringAsync();
        Console.WriteLine($"[{i}] <- {(k==0 ? "fib1" : "fib2")} {((int)r.StatusCode)} {r.ReasonPhrase} body={txt}");
    }

    if (interPairDelay is not null)
        await Task.Delay(interPairDelay.Value);
}

Console.WriteLine("Done.");
