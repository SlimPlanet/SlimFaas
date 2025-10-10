using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimFaas;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Tests;

// === Services de réplicas dédiés aux scénarios de timing ===
internal class NeverReadyReplicasService : IReplicasService
{
    private readonly DeploymentInformation _function;
    private readonly DeploymentsInformations _deployments;

    public NeverReadyReplicasService(int httpTimeoutTenthsMs = 20) // 20 => ~2s
    {
        _function = new DeploymentInformation(
            Replicas: 1,
            Deployment: "fibonacci",
            Namespace: "default",
            Configuration: new SlimFaasConfiguration
            {
                DefaultSync = new SlimFaasDefaultConfiguration
                {
                    HttpTimeout = httpTimeoutTenthsMs
                }
            },
            Pods: new List<PodInformation>
            {
                // Pod non prêt, Endpoint non prêt
                new PodInformation("fibonacci-0", false, false, "0", "fibonacci", new List<int>{8080})
            },
            EndpointReady: false
        );

        _deployments = new DeploymentsInformations(
            new List<DeploymentInformation> { _function },
            new SlimFaasDeploymentInformation(1, new List<PodInformation> { new("", true, true, "", "", new List<int> { 5000 }) }),
            new List<PodInformation>()
        );
    }

    // On expose toujours la même instance (le middleware lit l'objet par référence)
    public DeploymentsInformations Deployments => _deployments;

    public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => throw new NotImplementedException();
    public Task CheckScaleAsync(string kubeNamespace) => throw new NotImplementedException();
    public Task SyncDeploymentsFromSlimData(DeploymentsInformations deploymentsInformations) => Task.CompletedTask;
}

internal class FlipReadyQuicklyReplicasService : IReplicasService
{
    private readonly DeploymentInformation _function;
    private readonly DeploymentsInformations _deployments;

    public FlipReadyQuicklyReplicasService(int httpTimeoutTenthsMs = 20, int flipDelayMs = 100)
    {
        _function = new DeploymentInformation(
            Replicas: 1,
            Deployment: "fibonacci",
            Namespace: "default",
            Configuration: new SlimFaasConfiguration
            {
                DefaultSync = new SlimFaasDefaultConfiguration { HttpTimeout = httpTimeoutTenthsMs }
            },
            Pods: new List<PodInformation>
            {
                // Démarre non prêt, puis deviendra prêt après flip
                new PodInformation("fibonacci-0", false, false, "0", "fibonacci", new List<int> { 8080 })
            },
            EndpointReady: false
        );

        _deployments = new DeploymentsInformations(
            new List<DeploymentInformation> { _function },
            new SlimFaasDeploymentInformation(1,
                new List<PodInformation> { new("", true, true, "", "", new List<int> { 5000 }) }),
            new List<PodInformation>()
        );

        // Flip asynchrone : met chaque pod en READY après flipDelayMs
        _ = Task.Run(async () =>
        {
            await Task.Delay(flipDelayMs);

            // On remplace les éléments par index (foreach interdit l'affectation)
            var funcs = _deployments.Functions;
            for (int i = 0; i < funcs.Count; i++)
            {
                if (funcs[i].Deployment != "fibonacci") continue;

                var f = funcs[i];
                var pods = f.Pods;

                for (int j = 0; j < pods.Count; j++)
                {
                    // pods[j] est un record => on crée une nouvelle instance avec Ready=true
                    pods[j] = pods[j] with { Ready = true };
                }

                // On replace le record function par un nouveau avec la même liste (déjà modifiée)
                funcs[i] = f with { Pods = pods };
                break;
            }
        });
    }

    public DeploymentsInformations Deployments { get; }
    public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => throw new NotImplementedException();

    public Task CheckScaleAsync(string kubeNamespace) => throw new NotImplementedException();

}

// === Client HTTP sync pilotable pour forcer un 504 si besoin ===
internal class SendClientGatewayTimeout : ISendClient
{
    public Task<HttpResponseMessage> SendHttpRequestAsync(CustomRequest customRequest, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, CancellationTokenSource? cancellationToken = null, Proxy proxy = null)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    public Task<HttpResponseMessage> SendHttpRequestSync(HttpContext httpContext, string functionName, string functionPath, string functionQuery, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, Proxy proxy = null)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
}

// === TEST 1 : Timeout ~2s quand aucun pod ne devient prêt ===
public class ProxyMiddlewareTimeoutReadyTests
{
    [Fact]
    public async Task Sync_TimesOut_When_No_Pod_Ready_After_2s()
    {
        // HttpTimeout = 20 -> ~ 2 secondes dans WaitForAnyPodStartedAsync
        var replicas = new NeverReadyReplicasService(httpTimeoutTenthsMs: 20);
        var sendClient = new SendClientGatewayTimeout();

        var wakeUpFunctionMock = new Mock<IWakeUpFunction>();
        var jobServiceMock = new Mock<IJobService>();
        jobServiceMock.Setup(j => j.SyncJobsAsync()).ReturnsAsync(new List<Job>());
        jobServiceMock.Setup(j => j.Jobs).Returns(new List<Job>());

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>();
                        s.AddSingleton<ISendClient>(sendClient);
                        s.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        s.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        s.AddSingleton<IReplicasService>(replicas);
                        s.AddSingleton<IWakeUpFunction>(sp => wakeUpFunctionMock.Object);
                        s.AddSingleton<IJobService>(sp => jobServiceMock.Object);
                    })
                    .Configure(app => app.UseMiddleware<SlimProxyMiddleware>());
            })
            .StartAsync();

        var client = host.GetTestClient();

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response = await client.GetAsync("http://localhost:5000/function/fibonacci/compute");
        sw.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        // marge de tolérance CI : 1.7s à 3s
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(1700), TimeSpan.FromMilliseconds(4000));
    }
}

// === TEST 2 : Pod devient prêt rapidement -> succès < 2s ===
public class ProxyMiddlewareFlipReadyTests
{
    [Fact]
    public async Task Sync_Succeeds_When_Pod_Becomes_Ready_Quickly()
    {
        // Timeout max 2s, mais on flip READY après ~100ms
        var replicas = new FlipReadyQuicklyReplicasService(httpTimeoutTenthsMs: 20, flipDelayMs: 100);
        var sendClientOk = new SendClientMock(); // déjà défini dans le fichier, retourne 200 OK

        var wakeUpFunctionMock = new Mock<IWakeUpFunction>();
        var jobServiceMock = new Mock<IJobService>();
        jobServiceMock.Setup(j => j.SyncJobsAsync()).ReturnsAsync(new List<Job>());
        jobServiceMock.Setup(j => j.Jobs).Returns(new List<Job>());

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(s =>
                    {
                        s.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>();
                        s.AddSingleton<ISendClient>(sendClientOk);
                        s.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        s.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        s.AddSingleton<IReplicasService>(replicas);
                        s.AddSingleton<IWakeUpFunction>(sp => wakeUpFunctionMock.Object);
                        s.AddSingleton<IJobService>(sp => jobServiceMock.Object);
                    })
                    .Configure(app => app.UseMiddleware<SlimProxyMiddleware>());
            })
            .StartAsync();

        var client = host.GetTestClient();

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response = await client.GetAsync("http://localhost:5000/function/fibonacci/compute");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // doit être nettement < 2s (large marge CI)
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1200), $"Elapsed too high: {sw.Elapsed.TotalMilliseconds} ms");
    }
}
