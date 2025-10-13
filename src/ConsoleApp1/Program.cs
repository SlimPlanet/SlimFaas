// dotnet run --project Client  (par ex.)
// Framework: .NET 7/8/9 OK

using System.Net.Http;
using System.Text;
using System.Text.Json;

var baseAddress = "http://localhost:30021";
var fib1Path = "/async-function/fibonacci1/compute";
var fib2Path = "/async-function/fibonacci2/compute";

// nombre total de requêtes pour reproduire le bug
int total = 100;
// délai entre requêtes (optionnel)
TimeSpan? delay = TimeSpan.FromMilliseconds(50);

using var http = new HttpClient { BaseAddress = new Uri(baseAddress) };

for (int i = 0; i < total; i++)
{
    bool toFib1 = (i % 2 == 0);
    var endpoint = toFib1 ? fib1Path : fib2Path;

    // Bodies différents pour 1 et 2
    object payload = toFib1
        ? new { seq = i, type = "fib1", n = 30, hint = "alt:1" }
        : new { seq = i, type = "fib2", n = 31, hint = "alt:2" };

    var json = JsonSerializer.Serialize(payload);
    using var content = new StringContent(json, Encoding.UTF8, "application/json");

    Console.WriteLine($"-> POST {endpoint}  body={json}");
    var resp = await http.PostAsync(endpoint, content);
    var respText = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"<- {(int)resp.StatusCode} {resp.ReasonPhrase}  body={respText}");

    if (delay is not null)
        await Task.Delay(delay.Value);
}

Console.WriteLine("Done.");
