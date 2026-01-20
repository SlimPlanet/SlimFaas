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
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Security;
using KubernetesJob = SlimFaas.Kubernetes.Job;

namespace SlimFaas.Tests;

// === Services de réplicas dédiés aux scénarios de timing ===
internal class NeverReadyReplicasService : IReplicasService
{
    private readonly DeploymentInformation _function;
    private readonly DeploymentsInformations _deployments;

    public NeverReadyReplicasService(int httpTimeoutTenthsSeconds = 2)
    {
        _function = new DeploymentInformation(
            Replicas: 1,
            Deployment: "fibonacci",
            Namespace: "default",
            Configuration: new SlimFaasConfiguration
            {
                DefaultSync = new SlimFaasDefaultConfiguration
                {
                    // HttpTimeout est en secondes
                    HttpTimeout = httpTimeoutTenthsSeconds
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

// Flip Ready en place (sans recréer les records)
internal class FlipReadyQuicklyReplicasService : IReplicasService
{
    private readonly DeploymentsInformations _deployments;
    private readonly DeploymentInformation _function; // référence gardée pour modifier ses pods

    public FlipReadyQuicklyReplicasService(int httpTimeoutSeconds = 2, int flipDelayMs = 100)
    {
        // Fonction "fibonacci" : EndpointReady = true dès le départ
        _function = new DeploymentInformation(
            Replicas: 1,
            Deployment: "fibonacci",
            SubscribeEvents: new List<SubscribeEvent>(),
            PathsStartWithVisibility: new List<PathVisibility>(),
            Namespace: "default",
            Configuration: new SlimFaasConfiguration
            {
                DefaultSync = new SlimFaasDefaultConfiguration
                {
                    // HttpTimeout en secondes
                    HttpTimeout = httpTimeoutSeconds
                }
            },
            Pods: new List<PodInformation>
            {
                // Pod initialement non prêt
                new PodInformation("fibonacci-0", false, false, "10.0.0.42", "fibonacci", new List<int>{8080})
            },
            EndpointReady: true // ✅ on ne la modifie plus ensuite
        );

        _deployments = new DeploymentsInformations(
            new List<DeploymentInformation> { _function },
            new SlimFaasDeploymentInformation(1, new List<PodInformation> { new("", true, true, "", "", new List<int> { 5000 }) }),
            new List<PodInformation>()
        );

        // Après un court délai, on bascule le/les pods en Ready=true (modif en place)
        _ = Task.Run(async () =>
        {
            await Task.Delay(flipDelayMs).ConfigureAwait(false);

            // On modifie la LISTE pods existante (même référence) :
            // - on ne recrée NI la fonction NI Deployments
            var pods = _function.Pods;
            for (int j = 0; j < pods.Count; j++)
            {
                // PodInformation est un record => on remplace l'élément par une copie Ready=true
                pods[j] = pods[j] with { Ready = true };
            }
        });
    }

    public DeploymentsInformations Deployments => _deployments;

    public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => throw new NotImplementedException();
    public Task CheckScaleAsync(string kubeNamespace) => throw new NotImplementedException();
    public Task SyncDeploymentsFromSlimData(DeploymentsInformations deploymentsInformations) => Task.CompletedTask;
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
        // HttpTimeout = 2 -> 2 secondes de timeout
        var replicas = new NeverReadyReplicasService(httpTimeoutTenthsSeconds: 2);
        var sendClient = new SendClientGatewayTimeout();

        var wakeUpFunctionMock = new Mock<IWakeUpFunction>();
        var jobServiceMock = new Mock<IJobService>();
        jobServiceMock.Setup(j => j.SyncJobsAsync()).ReturnsAsync(new List<KubernetesJob>());
        jobServiceMock.Setup(j => j.Jobs).Returns(new List<KubernetesJob>());

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
                        s.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        s.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapSlimFaasEndpoints());
                    });
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
        var replicas = new FlipReadyQuicklyReplicasService(httpTimeoutSeconds: 2, flipDelayMs: 100);
        var sendClientOk = new SendClientMock(); // déjà défini dans le fichier, retourne 200 OK

        var wakeUpFunctionMock = new Mock<IWakeUpFunction>();
        var jobServiceMock = new Mock<IJobService>();
        jobServiceMock.Setup(j => j.SyncJobsAsync()).ReturnsAsync(new List<KubernetesJob>());
        jobServiceMock.Setup(j => j.Jobs).Returns(new List<KubernetesJob>());

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
                        s.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        s.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapSlimFaasEndpoints());
                    });
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
