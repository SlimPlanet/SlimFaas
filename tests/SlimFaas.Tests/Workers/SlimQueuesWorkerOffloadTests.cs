using System.Net;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SlimData;
using SlimData.ClusterFiles;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests.Workers;

/// <summary>
/// Tests vérifiant le comportement de SlimQueuesWorker lorsque la CustomRequest
/// contient un OffloadedFileId (body offloadé en base / cluster).
/// Chaque test utilise une IP de pod unique pour éviter les collisions avec
/// le dictionnaire statique Proxy.ActiveRequestsPerPod.
/// </summary>
[Collection("SlimQueuesWorkerOffload")]
public class SlimQueuesWorkerOffloadTests
{
    /// <summary>Vide les états statiques du Proxy avant chaque test.</summary>
    public SlimQueuesWorkerOffloadTests()
    {
        Proxy.IpAddresses.Clear();
    }

    // -----------------------------------------------------------------------
    // Helper : crée le SlimQueuesWorker avec tous les mocks nécessaires
    // -----------------------------------------------------------------------
    private static (SlimQueuesWorker worker, Mock<ISendClient> sendClientMock) BuildWorker(
        ISlimFaasQueue queue,
        IReplicasService replicasService,
        IClusterFileSync fileSync,
        IDatabaseService db)
    {
        HttpResponseMessage responseMessage = new() { StatusCode = HttpStatusCode.OK };
        Mock<ISendClient> sendClientMock = new();
        sendClientMock
            .Setup(s => s.SendHttpRequestAsync(
                It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationTokenSource?>(),
                It.IsAny<IProxy?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Stream?>()))
            .ReturnsAsync(responseMessage);

        Mock<IServiceProvider> serviceProvider = new();
        serviceProvider.Setup(x => x.GetService(typeof(ISendClient))).Returns(sendClientMock.Object);

        Mock<IServiceScope> serviceScope = new();
        serviceScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> serviceScopeFactory = new();
        serviceScopeFactory.Setup(x => x.CreateScope()).Returns(serviceScope.Object);
        serviceProvider.Setup(x => x.GetService(typeof(IServiceScopeFactory))).Returns(serviceScopeFactory.Object);

        Mock<ISlimDataStatus> slimDataStatus = new();
        slimDataStatus.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);

        Mock<IMasterService> masterService = new();
        masterService.Setup(s => s.IsMaster).Returns(true);

        var workersOptions = Microsoft.Extensions.Options.Options.Create(new WorkersOptions
        {
            QueuesDelayMilliseconds = 10
        });

        var worker = new SlimQueuesWorker(
            queue,
            replicasService,
            new HistoryHttpMemoryService(),
            new Mock<ILogger<SlimQueuesWorker>>().Object,
            serviceProvider.Object,
            slimDataStatus.Object,
            masterService.Object,
            fileSync,
            db,
            workersOptions,
            new NetworkActivityTracker());

        return (worker, sendClientMock);
    }

    // -----------------------------------------------------------------------
    // Helper : crée un IReplicasService minimal avec une fonction "my-func"
    // -----------------------------------------------------------------------
    private static IReplicasService BuildReplicasService(string functionName = "my-func", string podIp = "192.168.99.1")
    {
        Mock<IReplicasService> mock = new();
        mock.Setup(r => r.Deployments).Returns(new DeploymentsInformations(
            Functions: new List<DeploymentInformation>
            {
                new(Replicas: 1,
                    Deployment: functionName,
                    Namespace: "default",
                    NumberParallelRequest: 1,
                    ReplicasMin: 0,
                    ReplicasAtStart: 1,
                    TimeoutSecondBeforeSetReplicasMin: 300,
                    ReplicasStartAsSoonAsOneFunctionRetrieveARequest: true,
                    Configuration: new SlimFaasConfiguration(),
                    Pods: new List<PodInformation> { new("pod-1", true, true, podIp, functionName) },
                    EndpointReady: true)
            },
            SlimFaas: new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
            Pods: new List<PodInformation>()));
        return mock.Object;
    }

    private static Mock<ISlimFaasQueue> BuildQueueMock(string functionName, QueueData queueData)
    {
        Mock<ISlimFaasQueue> queueMock = new();
        queueMock
            .Setup(q => q.DequeueAsync(functionName, It.IsAny<int>(), It.IsAny<IList<string>?>()))
            .ReturnsAsync(new List<QueueData> { queueData });
        queueMock
            .Setup(q => q.CountElementAsync(It.IsAny<string>(), It.IsAny<List<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(1L);
        queueMock
            .Setup(q => q.CountElementAsync(It.IsAny<string>(), It.IsAny<List<CountType>>()))
            .ReturnsAsync(1L);
        queueMock
            .Setup(q => q.ListCallbackAsync(It.IsAny<string>(), It.IsAny<ListQueueItemStatus>()))
            .Returns(Task.CompletedTask);
        queueMock
            .Setup(q => q.ListElementsAsync(It.IsAny<string>(), It.IsAny<IList<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(new List<QueueData>());
        return queueMock;
    }

    /// <summary>
    /// Vérifie que lorsque la CustomRequest déqueued a un OffloadedFileId défini :
    /// 1. db.GetAsync est appelé pour récupérer les métadonnées du fichier
    /// 2. fileSync.PullFileIfMissingAsync est appelé pour streamer le fichier depuis la base/cluster
    /// 3. Le stream obtenu est transmis à SendHttpRequestAsync
    /// </summary>
    [Fact]
    public async Task WhenOffloadedFileIdIsSet_ShouldPullStreamFromClusterAndPassItToSendClient()
    {
        const string functionName = "my-func";
        const string fileId = "abc123def456";
        const string sha256 = "deadbeef";

        // --- Préparer les métadonnées serialisées en MemoryPack ---
        var meta = new DataSetMetadata(sha256, 2 * 1024 * 1024, "application/octet-stream", fileId);
        byte[] metaBytes = MemoryPackSerializer.Serialize(meta);

        // --- db mock : retourne les métadonnées pour la clé correspondante ---
        Mock<IDatabaseService> dbMock = new();
        dbMock
            .Setup(d => d.GetAsync($"data:file:{fileId}:meta"))
            .ReturnsAsync(metaBytes);
        // DeleteAsync sera appelé après la complétion pour nettoyage
        dbMock
            .Setup(d => d.DeleteAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // --- fileSync mock : retourne un stream fictif ---
        var fakeFileStream = new MemoryStream(new byte[2 * 1024 * 1024]);
        Mock<IClusterFileSync> fileSyncMock = new();
        fileSyncMock
            .Setup(f => f.PullFileIfMissingAsync(fileId, sha256, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilePullResult(fakeFileStream));

        // --- Queue : contient UN message avec OffloadedFileId ---
        var customRequest = new CustomRequest(
            Headers: new List<CustomHeader>(),
            Body: null,
            FunctionName: functionName,
            Path: "/process",
            Method: "POST",
            Query: "",
            OffloadedFileId: fileId);

        var queueData = new QueueData("element-id-1", MemoryPackSerializer.Serialize(customRequest), 0, true, 0, 0);
        var queueMock = BuildQueueMock(functionName, queueData);

        var replicasService = BuildReplicasService(functionName);
        var (worker, sendClientMock) = BuildWorker(queueMock.Object, replicasService, fileSyncMock.Object, dbMock.Object);

        // --- Act ---
        using var cts = new CancellationTokenSource();
        Task task = worker.StartAsync(cts.Token);

        await Task.Delay(500);
        await cts.CancelAsync();
        await task;

        // --- Assert ---

        // 1. db.GetAsync doit avoir été appelé avec la clé de métadonnées
        dbMock.Verify(d => d.GetAsync($"data:file:{fileId}:meta"), Times.AtLeastOnce);

        // 2. PullFileIfMissingAsync doit avoir été appelé avec les bons paramètres
        fileSyncMock.Verify(f => f.PullFileIfMissingAsync(fileId, sha256, null, It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // 3. SendHttpRequestAsync doit avoir reçu un stream non nul (le stream offloadé)
        sendClientMock.Verify(s => s.SendHttpRequestAsync(
            It.IsAny<CustomRequest>(),
            It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationTokenSource?>(),
            It.IsAny<IProxy?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.Is<Stream?>(stream => stream != null)), Times.AtLeastOnce);
    }

    /// <summary>
    /// Vérifie que lorsque la CustomRequest n'a PAS d'OffloadedFileId (body inline),
    /// db.GetAsync et fileSync.PullFileIfMissingAsync ne sont PAS appelés,
    /// et SendHttpRequestAsync reçoit un stream null.
    /// </summary>
    [Fact]
    public async Task WhenOffloadedFileIdIsNotSet_ShouldNotPullFromCluster_AndPassNullStreamToSendClient()
    {
        const string functionName = "my-func";

        Mock<IDatabaseService> dbMock = new(MockBehavior.Strict);
        // MockBehavior.Strict : toute tentative d'appel à GetAsync échouerait

        Mock<IClusterFileSync> fileSyncMock = new(MockBehavior.Strict);
        // MockBehavior.Strict : toute tentative d'appel à PullFileIfMissingAsync échouerait

        // Requête avec body inline (pas d'offload)
        var customRequest = new CustomRequest(
            Headers: new List<CustomHeader>(),
            Body: new byte[] { 1, 2, 3, 4 },
            FunctionName: functionName,
            Path: "/process",
            Method: "POST",
            Query: "",
            OffloadedFileId: null);

        var queueData = new QueueData("element-id-2", MemoryPackSerializer.Serialize(customRequest), 0, true, 0, 0);
        var queueMock = BuildQueueMock(functionName, queueData);

        var replicasService = BuildReplicasService(functionName);
        var (worker, sendClientMock) = BuildWorker(queueMock.Object, replicasService, fileSyncMock.Object, dbMock.Object);

        using var cts = new CancellationTokenSource();
        Task task = worker.StartAsync(cts.Token);

        await Task.Delay(500);
        await cts.CancelAsync();
        await task;

        // SendHttpRequestAsync doit avoir été appelé avec stream = null
        sendClientMock.Verify(s => s.SendHttpRequestAsync(
            It.IsAny<CustomRequest>(),
            It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationTokenSource?>(),
            It.IsAny<IProxy?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.Is<Stream?>(stream => stream == null)), Times.AtLeastOnce);
    }

    /// <summary>
    /// Vérifie que si les métadonnées du fichier sont introuvables en base (db.GetAsync retourne null),
    /// le worker loggue un avertissement et envoie quand même la requête avec un stream null
    /// (comportement de fallback décrit dans le code source).
    /// </summary>
    [Fact]
    public async Task WhenOffloadedFileMetadataNotFound_ShouldLogWarningAndSendWithNullStream()
    {
        const string functionName = "my-func";
        const string fileId = "missing-file-id";

        // db retourne null → métadonnées introuvables
        Mock<IDatabaseService> dbMock = new();
        dbMock
            .Setup(d => d.GetAsync($"data:file:{fileId}:meta"))
            .ReturnsAsync((byte[]?)null);
        dbMock
            .Setup(d => d.DeleteAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        Mock<IClusterFileSync> fileSyncMock = new(MockBehavior.Strict);
        // PullFileIfMissingAsync ne doit pas être appelé si les métadonnées sont absentes

        var customRequest = new CustomRequest(
            Headers: new List<CustomHeader>(),
            Body: null,
            FunctionName: functionName,
            Path: "/process",
            Method: "POST",
            Query: "",
            OffloadedFileId: fileId);

        var queueData = new QueueData("element-id-3", MemoryPackSerializer.Serialize(customRequest), 0, true, 0, 0);
        var queueMock = BuildQueueMock(functionName, queueData);

        var replicasService = BuildReplicasService(functionName);

        // Construire manuellement pour pouvoir capturer les logs
        HttpResponseMessage responseMessage = new() { StatusCode = HttpStatusCode.OK };
        Mock<ISendClient> sendClientMock = new();
        sendClientMock
            .Setup(s => s.SendHttpRequestAsync(
                It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationTokenSource?>(),
                It.IsAny<IProxy?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Stream?>()))
            .ReturnsAsync(responseMessage);

        Mock<IServiceProvider> serviceProvider = new();
        serviceProvider.Setup(x => x.GetService(typeof(ISendClient))).Returns(sendClientMock.Object);

        Mock<IServiceScope> serviceScope = new();
        serviceScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> serviceScopeFactory = new();
        serviceScopeFactory.Setup(x => x.CreateScope()).Returns(serviceScope.Object);
        serviceProvider.Setup(x => x.GetService(typeof(IServiceScopeFactory))).Returns(serviceScopeFactory.Object);

        Mock<ISlimDataStatus> slimDataStatus = new();
        slimDataStatus.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);

        Mock<IMasterService> masterService = new();
        masterService.Setup(s => s.IsMaster).Returns(true);

        Mock<ILogger<SlimQueuesWorker>> loggerMock = new();

        var workersOptions = Microsoft.Extensions.Options.Options.Create(new WorkersOptions
        {
            QueuesDelayMilliseconds = 10
        });

        var worker = new SlimQueuesWorker(
            queueMock.Object,
            replicasService,
            new HistoryHttpMemoryService(),
            loggerMock.Object,
            serviceProvider.Object,
            slimDataStatus.Object,
            masterService.Object,
            fileSyncMock.Object,
            dbMock.Object,
            workersOptions,
            new NetworkActivityTracker());

        using var cts = new CancellationTokenSource();
        Task task = worker.StartAsync(cts.Token);

        await Task.Delay(500);
        await cts.CancelAsync();
        await task;

        // db.GetAsync doit avoir été appelé
        dbMock.Verify(d => d.GetAsync($"data:file:{fileId}:meta"), Times.AtLeastOnce);

        // Un warning doit avoir été loggué
        loggerMock.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.AtLeastOnce);

        // SendHttpRequestAsync doit être appelé avec stream = null (fallback)
        sendClientMock.Verify(s => s.SendHttpRequestAsync(
            It.IsAny<CustomRequest>(),
            It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationTokenSource?>(),
            It.IsAny<IProxy?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.Is<Stream?>(stream => stream == null)), Times.AtLeastOnce);
    }
}



