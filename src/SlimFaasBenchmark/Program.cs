using NBomber.CSharp;
using NBomber.Http;

// Pour le scope using var
using var httpClient = new HttpClient
{
    BaseAddress = new Uri("http://localhost:30021") // Adaptez à votre URL
};

var scenario = Scenario.Create("hello_scenario", async context =>
    {
        // On envoie une requête GET vers /hello/John
        var response = await httpClient.GetAsync("http://localhost:30021/function/fibonacci1/hello/CNCF");

        // On détermine OK/Fail en fonction du code HTTP
        return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
    })
    .WithoutWarmUp()
    .WithLoadSimulations(
        // 333 req/s durant 5 minutes (≈ 100 000 requêtes)
        Simulation.Inject(
            rate: 1,
            interval: TimeSpan.FromSeconds(1),
            during: TimeSpan.FromMinutes(5))
    );

// Optionnel : HttpMetricsPlugin() pour avoir plus de métriques HTTP
NBomberRunner
    .RegisterScenarios(scenario)
    .WithWorkerPlugins(new HttpMetricsPlugin())
    .Run();
