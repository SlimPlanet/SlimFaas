using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using NBomber.CSharp;
using NBomber.Http;

// Exemple de payload JSON
var payload = new
{
    n = 30,
    message = "Compute Fibonacci"
};

// Création du client HTTP (NB: tu peux aussi injecter le BaseAddress directement)
using var httpClient = new HttpClient
{
    BaseAddress = new Uri("http://localhost:30021")
};

// Définition du scénario
var scenario = Scenario.Create("fibonacci_scenario", async context =>
    {
        // Envoie du JSON dans le corps de la requête
        var response = await httpClient.PostAsJsonAsync(
            "async-function/fibonacci1/compute", // chemin relatif
            payload,                             // ton JSON
            new CancellationToken()
        );

        // Retourne le résultat à NBomber
        return response.IsSuccessStatusCode
            ? Response.Ok()
            : Response.Fail(statusCode: response.StatusCode.ToString());
    })
    .WithoutWarmUp()
    .WithLoadSimulations(
        Simulation.Inject(
            rate: 333,
            interval: TimeSpan.FromSeconds(1),
            during: TimeSpan.FromMinutes(5))
    );

// Exécution du scénario
NBomberRunner
    .RegisterScenarios(scenario)
    .WithWorkerPlugins(new HttpMetricsPlugin())
    .Run();
