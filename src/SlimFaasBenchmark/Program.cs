using NBomber.CSharp;
using NBomber.Http;

// Pour le scope using var
using var httpClient = new HttpClient
{
    BaseAddress = new Uri("http://localhost:30021")
};

var scenario = Scenario.Create("hello_scenario", async context =>
    {
       var response = await httpClient.PostAsync("http://localhost:30021/async-function/fibonacci1/compute", new StringContent(""));
        return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
    })
    .WithoutWarmUp()
    .WithLoadSimulations(
        // 333 req/s durant 5 minutes (≈ 100 000 requêtes)
        Simulation.Inject(
            rate: 33,
            interval: TimeSpan.FromSeconds(1),
            during: TimeSpan.FromMinutes(5))
    );

// Optionnel : HttpMetricsPlugin() pour avoir plus de métriques HTTP
NBomberRunner
    .RegisterScenarios(scenario)
    .WithWorkerPlugins(new HttpMetricsPlugin())
    .Run();
