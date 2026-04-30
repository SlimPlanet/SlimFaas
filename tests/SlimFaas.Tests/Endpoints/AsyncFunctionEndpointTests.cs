using System.Net;
using System.Text;
using MemoryPack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimData;
using SlimData.ClusterFiles;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Endpoints;

public class AsyncFunctionEndpointTests
{
    [Theory]
    [InlineData("/async-function/fibonacci/download", HttpStatusCode.Accepted)]
    [InlineData("/async-function/wrong/download", HttpStatusCode.NotFound)]
    public async Task CallFunctionInAsyncSyncModeAndReturnOk(string path, HttpStatusCode expected)
    {
        Mock<IWakeUpFunction> wakeUpFunctionMock = new();
        Mock<IJobService> jobServiceMock = new();

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>();
                        services.AddSingleton<ISendClient, SendClientMock>();
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<IWakeUpFunction>(_ => wakeUpFunctionMock.Object);
                        services.AddSingleton<IJobService>(_ => jobServiceMock.Object);
                        services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton<IClusterFileSync>(_ => new Mock<IClusterFileSync>().Object);
                        services.AddSingleton<IDatabaseService>(_ => new Mock<IDatabaseService>().Object);
                        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
                        {
                            Namespace = "default",
                            BaseFunctionUrl = "http://{pod_ip}:{pod_port}"
                        }));
                        var namespaceProviderMock = new Mock<INamespaceProvider>();
                        namespaceProviderMock.SetupGet(n => n.CurrentNamespace).Returns("default");
                        services.AddSingleton<INamespaceProvider>(namespaceProviderMock.Object);
                        services.AddMemoryCache();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<WakeUpGate>();
                        services.AddSingleton<NetworkActivityTracker>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapSlimFaasEndpoints());
                    });
            })
            .StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// Vérifie que lorsque le body d'une requête async dépasse 1 Mo :
    /// - le payload est offloadé via BroadcastFilePutAsync (broadcast cluster)
    /// - les métadonnées sont persistées en base via db.SetAsync
    /// - la CustomRequest mise en queue contient un OffloadedFileId non nul et un Body null
    /// </summary>
    [Fact]
    public async Task WhenBodyExceedsOneMegabyte_ShouldOffloadPayloadToClusterAndEnqueueFileId()
    {
        // ---------- mocks pour la capture ----------
        Mock<IClusterFileSync> fileSyncMock = new(MockBehavior.Strict);
        Mock<IDatabaseService> dbMock = new(MockBehavior.Strict);
        Mock<ISlimFaasQueue> queueMock = new();

        // BroadcastFilePutAsync doit être appelé exactement une fois
        string? capturedFileId = null;
        fileSyncMock
            .Setup(s => s.BroadcastFilePutAsync(
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                false,
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Stream, string, long, bool, long?, CancellationToken>(
                (id, _, _, _, _, _, _) => capturedFileId = id)
            .ReturnsAsync(new FilePutResult("abc123sha", "application/octet-stream", 2 * 1024 * 1024));

        // db.SetAsync doit être appelé pour stocker les métadonnées
        string? capturedMetaKey = null;
        byte[]? capturedMetaBytes = null;
        dbMock
            .Setup(d => d.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<long?>()))
            .Callback<string, byte[], long?>((k, v, _) =>
            {
                capturedMetaKey = k;
                capturedMetaBytes = v;
            })
            .Returns(Task.CompletedTask);

        // La queue capture le message enqueué pour pouvoir l'inspecter
        byte[]? enqueuedPayload = null;
        queueMock
            .Setup(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<RetryInformation>()))
            .Callback<string, byte[], RetryInformation>((_, data, _) => enqueuedPayload = data)
            .ReturnsAsync(Guid.NewGuid().ToString());

        // ReplicasService configuré avec AsyncBodyOffloadThresholdBytes = 1 Mo
        var fibonacciConfig = new SlimFaasConfiguration
        {
            DefaultAsync = new SlimFaasDefaultConfiguration
            {
                AsyncBodyOffloadThresholdBytes = 1 * 1024L * 1024L
            }
        };

        Mock<IReplicasService> replicasServiceMock = new();
        replicasServiceMock.Setup(r => r.Deployments).Returns(new DeploymentsInformations(
            Functions: new List<DeploymentInformation>
            {
                new(Replicas: 1, Deployment: "fibonacci", Namespace: "default",
                    Configuration: fibonacciConfig,
                    Pods: new List<PodInformation> { new("pod1", true, true, "1", "fibonacci", new List<int> { 8080 }) },
                    EndpointReady: true)
            },
            SlimFaas: new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
            Pods: new List<PodInformation>()));

        // Body de plus de 1 Mo
        byte[] largeBody = new byte[2 * 1024 * 1024]; // 2 Mo
        new Random(42).NextBytes(largeBody);

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>();
                        services.AddSingleton<ISendClient, SendClientMock>();
                        services.AddSingleton<ISlimFaasQueue>(_ => queueMock.Object);
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IReplicasService>(_ => replicasServiceMock.Object);
                        services.AddSingleton<IWakeUpFunction>(_ => new Mock<IWakeUpFunction>().Object);
                        services.AddSingleton<IJobService>(_ => new Mock<IJobService>().Object);
                        services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton<IClusterFileSync>(_ => fileSyncMock.Object);
                        services.AddSingleton<IDatabaseService>(_ => dbMock.Object);
                        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
                        {
                            Namespace = "default",
                            BaseFunctionUrl = "http://{pod_ip}:{pod_port}"
                        }));
                        var namespaceProviderMock = new Mock<INamespaceProvider>();
                        namespaceProviderMock.SetupGet(n => n.CurrentNamespace).Returns("default");
                        services.AddSingleton<INamespaceProvider>(namespaceProviderMock.Object);
                        services.AddMemoryCache();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<WakeUpGate>();
                        services.AddSingleton<NetworkActivityTracker>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapSlimFaasEndpoints());
                    });
            })
            .StartAsync();

        // Envoi d'un POST avec un body > 1 Mo
        using var content = new ByteArrayContent(largeBody);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage response = await host.GetTestClient()
            .PostAsync("http://localhost:5000/async-function/fibonacci/process", content);

        // -------- Assertions --------

        // L'endpoint doit retourner 202 Accepted
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // BroadcastFilePutAsync doit avoir été appelé
        fileSyncMock.Verify(s => s.BroadcastFilePutAsync(
            It.IsAny<string>(),
            It.IsAny<Stream>(),
            It.IsAny<string>(),
            It.IsAny<long>(),
            false,
            It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // db.SetAsync doit avoir été appelé avec la clé de métadonnées
        Assert.NotNull(capturedMetaKey);
        Assert.NotNull(capturedFileId);
        Assert.Equal($"data:file:{capturedFileId}:meta", capturedMetaKey);

        // Les métadonnées doivent être désérialisables et cohérentes
        Assert.NotNull(capturedMetaBytes);
        var meta = MemoryPackSerializer.Deserialize<DataSetMetadata>(capturedMetaBytes!);
        Assert.Equal("abc123sha", meta!.Sha256Hex);

        // La CustomRequest enqueued doit avoir un OffloadedFileId non nul et Body null
        Assert.NotNull(enqueuedPayload);
        var enqueuedRequest = MemoryPackSerializer.Deserialize<CustomRequest>(enqueuedPayload!);
        Assert.NotNull(enqueuedRequest.OffloadedFileId);
        Assert.Equal(capturedFileId, enqueuedRequest.OffloadedFileId);
        Assert.Null(enqueuedRequest.Body);
    }

    /// <summary>
    /// Vérifie que lorsque le body est inférieur au seuil (inférieur à 1 Mo),
    /// le payload est directement sérialisé dans la CustomRequest (Body non null, OffloadedFileId null).
    /// </summary>
    [Fact]
    public async Task WhenBodyBelowThreshold_ShouldSerializeBodyDirectlyAndNotOffload()
    {
        Mock<IClusterFileSync> fileSyncMock = new(MockBehavior.Strict);
        // Strict : BroadcastFilePutAsync ne doit jamais être appelé

        Mock<IDatabaseService> dbMock = new(MockBehavior.Strict);
        // SetAsync ne doit jamais être appelé pour une métadonnée de fichier

        Mock<ISlimFaasQueue> queueMock = new();
        byte[]? enqueuedPayload = null;
        queueMock
            .Setup(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<RetryInformation>()))
            .Callback<string, byte[], RetryInformation>((_, data, _) => enqueuedPayload = data)
            .ReturnsAsync(Guid.NewGuid().ToString());

        var fibonacciConfig = new SlimFaasConfiguration
        {
            DefaultAsync = new SlimFaasDefaultConfiguration
            {
                AsyncBodyOffloadThresholdBytes = 1 * 1024L * 1024L  // seuil 1 Mo
            }
        };

        Mock<IReplicasService> replicasServiceMock = new();
        replicasServiceMock.Setup(r => r.Deployments).Returns(new DeploymentsInformations(
            Functions: new List<DeploymentInformation>
            {
                new(Replicas: 1, Deployment: "fibonacci", Namespace: "default",
                    Configuration: fibonacciConfig,
                    Pods: new List<PodInformation> { new("pod1", true, true, "1", "fibonacci", new List<int> { 8080 }) },
                    EndpointReady: true)
            },
            SlimFaas: new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
            Pods: new List<PodInformation>()));

        // Body de 512 Ko (inférieur au seuil de 1 Mo)
        byte[] smallBody = Encoding.UTF8.GetBytes("hello small body");

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>();
                        services.AddSingleton<ISendClient, SendClientMock>();
                        services.AddSingleton<ISlimFaasQueue>(_ => queueMock.Object);
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IReplicasService>(_ => replicasServiceMock.Object);
                        services.AddSingleton<IWakeUpFunction>(_ => new Mock<IWakeUpFunction>().Object);
                        services.AddSingleton<IJobService>(_ => new Mock<IJobService>().Object);
                        services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton<IClusterFileSync>(_ => fileSyncMock.Object);
                        services.AddSingleton<IDatabaseService>(_ => dbMock.Object);
                        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
                        {
                            Namespace = "default",
                            BaseFunctionUrl = "http://{pod_ip}:{pod_port}"
                        }));
                        var namespaceProviderMock = new Mock<INamespaceProvider>();
                        namespaceProviderMock.SetupGet(n => n.CurrentNamespace).Returns("default");
                        services.AddSingleton<INamespaceProvider>(namespaceProviderMock.Object);
                        services.AddMemoryCache();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<WakeUpGate>();
                        services.AddSingleton<NetworkActivityTracker>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapSlimFaasEndpoints());
                    });
            })
            .StartAsync();

        using var content = new ByteArrayContent(smallBody);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage response = await host.GetTestClient()
            .PostAsync("http://localhost:5000/async-function/fibonacci/process", content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // BroadcastFilePutAsync ne doit jamais avoir été appelé
        fileSyncMock.Verify(s => s.BroadcastFilePutAsync(
            It.IsAny<string>(),
            It.IsAny<Stream>(),
            It.IsAny<string>(),
            It.IsAny<long>(),
            It.IsAny<bool>(),
            It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // La CustomRequest enqueued doit avoir Body non null et OffloadedFileId null
        Assert.NotNull(enqueuedPayload);
        var enqueuedRequest = MemoryPackSerializer.Deserialize<CustomRequest>(enqueuedPayload!);
        Assert.Null(enqueuedRequest.OffloadedFileId);
        Assert.NotNull(enqueuedRequest.Body);
        Assert.Equal(smallBody, enqueuedRequest.Body);
    }
}
