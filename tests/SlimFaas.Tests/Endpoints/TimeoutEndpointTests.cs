using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Security;
using SlimFaas.WebSocket;
using KubernetesJob = SlimFaas.Kubernetes.Job;

namespace SlimFaas.Tests.Endpoints;

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
                DefaultSync = new SlimFaasSyncConfiguration
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

// Readiness is released by the test after observing the wait event.
internal class ControlledReadyReplicasService : IReplicasService
{
    private DeploymentsInformations _deployments;
    private readonly DeploymentInformation _function;

    public ControlledReadyReplicasService(int httpTimeoutSeconds = 10)
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
                DefaultSync = new SlimFaasSyncConfiguration
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

    }

    public void SetReady()
    {
        var ready = _function with { Pods = _function.Pods.Select(pod => pod with { Ready = true }).ToList() };
        Volatile.Write(ref _deployments, _deployments with { Functions = [ready] });
    }

    public DeploymentsInformations Deployments => Volatile.Read(ref _deployments);

    public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => throw new NotImplementedException();
    public Task CheckScaleAsync(string kubeNamespace) => throw new NotImplementedException();
    public Task SyncDeploymentsFromSlimData(DeploymentsInformations deploymentsInformations) => Task.CompletedTask;
}


// === Client HTTP sync pilotable pour forcer un 504 si besoin ===
internal class SendClientGatewayTimeout : ISendClient
{
    public Task<HttpResponseMessage> SendHttpRequestAsync(CustomRequest customRequest, SlimFaasDefaultConfiguration slimFaasDefaultConfiguration, string? baseUrl = null, CancellationTokenSource? cancellationToken = null, IProxy? proxy = null, string? reservedPodIp = null, string? activitySource = null, string? activitySourcePod = null, Stream? bodyOverrideStream = null, string? activityQueueName = null, string? activityCorrelationId = null)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    public Task<HttpResponseMessage> SendHttpRequestSync(HttpContext httpContext, string functionName, string functionPath, string functionQuery, SlimFaasSyncConfiguration slimFaasSyncConfiguration, string? baseUrl = null, IProxy? proxy = null, string? activitySource = null, string? activitySourcePod = null, string? activityCorrelationId = null)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
}

// === TEST 1 : Timeout ~2s quand aucun pod ne devient prêt ===
public class TimeoutReadyEndpointTests
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
                        s.AddSingleton<IWakeUpFunction>(_ => wakeUpFunctionMock.Object);
                        s.AddSingleton<IJobService>(_ => jobServiceMock.Object);
                        s.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        s.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        s.AddSingleton<IWebSocketSendClient, WebSocketSendClientMock>();
                        s.AddMemoryCache();
                        s.AddSingleton<FunctionStatusCache>();
                        s.AddSingleton<WakeUpGate>();
                        s.AddSingleton<NetworkActivityTracker>();
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
        // marge de tolérance CI : 1.7s à 4s
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(1700), TimeSpan.FromMilliseconds(4000));
    }
}

// A request waits for a known pod, then dispatches using its ready snapshot.
public class FlipReadyEndpointTests
{
    [Fact]
    public async Task Sync_Dispatches_When_Known_Pod_Becomes_Ready()
    {
        var replicas = new ControlledReadyReplicasService();
        var tracker = new NetworkActivityTracker();
        var (reader, channel) = tracker.Subscribe();
        var sendClient = new Mock<ISendClient>();
        sendClient.Setup(client => client.SendHttpRequestSync(It.IsAny<HttpContext>(), "fibonacci", "compute", "",
                It.IsAny<SlimFaasSyncConfiguration>(), null, It.IsAny<IProxy?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns<HttpContext, string, string, string, SlimFaasSyncConfiguration, string?, IProxy?, string?, string?, string?>(
                (_, _, _, _, _, _, proxy, _, _, _) =>
                {
                    Assert.Equal("10.0.0.42", proxy!.GetNextIP());
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                });

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
                        s.AddSingleton<ISendClient>(sendClient.Object);
                        s.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        s.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        s.AddSingleton<IReplicasService>(replicas);
                        s.AddSingleton<IWakeUpFunction>(_ => wakeUpFunctionMock.Object);
                        s.AddSingleton<IJobService>(_ => jobServiceMock.Object);
                        s.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        s.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        s.AddSingleton<IWebSocketSendClient, WebSocketSendClientMock>();
                        s.AddMemoryCache();
                        s.AddSingleton<FunctionStatusCache>();
                        s.AddSingleton<WakeUpGate>();
                        s.AddSingleton(tracker);
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

        try
        {
            Task<HttpResponseMessage> pending = client.GetAsync("http://localhost:5000/function/fibonacci/compute");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            NetworkActivityEvent waiting;
            do { waiting = await reader.ReadAsync(deadline.Token); }
            while (waiting.Type != NetworkActivityTracker.EventTypes.RequestWaiting);
            Assert.False(pending.IsCompleted);
            sendClient.VerifyNoOtherCalls();

            replicas.SetReady();
            using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var started = Assert.Single(tracker.GetRecent(), e => e.Type == NetworkActivityTracker.EventTypes.RequestStarted);
            Assert.Equal(waiting.CorrelationId, started.CorrelationId);
            sendClient.VerifyAll();
        }
        finally { tracker.Unsubscribe(channel); }
    }
}
