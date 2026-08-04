using System.Net;
using MemoryPack;
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
        IDatabaseService db,
        Task<HttpResponseMessage>? responseTask = null,
        Mock<IMasterService>? masterService = null)
    {
        Mock<ISendClient> sendClientMock = new();
        var sendSetup = sendClientMock
            .Setup(s => s.SendHttpRequestAsync(
                It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationTokenSource?>(),
                It.IsAny<IProxy?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Stream?>()));
        if (responseTask is null)
        {
            sendSetup.ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK));
        }
        else
        {
            sendSetup.Returns(responseTask);
        }

        Mock<ISlimDataStatus> slimDataStatus = new();
        slimDataStatus.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);

        if (masterService is null)
        {
            masterService = new Mock<IMasterService>();
            masterService.Setup(s => s.IsMaster).Returns(true);
        }

        var workersOptions = Microsoft.Extensions.Options.Options.Create(new WorkersOptions
        {
            QueuesDelayMilliseconds = 10
        });

        var worker = new SlimQueuesWorker(
            queue,
            replicasService,
            new HistoryHttpMemoryService(),
            new Mock<ILogger<SlimQueuesWorker>>().Object,
            sendClientMock.Object,
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
    private static IReplicasService BuildReplicasService(
        string functionName = "my-func",
        string podIp = "192.168.99.1",
        int maximumParallelism = 1)
    {
        Mock<IReplicasService> mock = new();
        mock.Setup(r => r.Deployments).Returns(new DeploymentsInformations(
            Functions: new List<DeploymentInformation>
            {
                new(Replicas: 1,
                    Deployment: functionName,
                    Namespace: "default",
                    NumberParallelRequest: maximumParallelism,
                    ReplicasMin: 0,
                    ReplicasAtStart: 1,
                    TimeoutSecondBeforeSetReplicasMin: 300,
                    ReplicasStartAsSoonAsOneFunctionRetrieveARequest: true,
                    Configuration: new SlimFaasConfiguration(),
                    Pods: new List<PodInformation> { new("pod-1", true, true, podIp, functionName) },
                    EndpointReady: true,
                    NumberParallelRequestPerPod: maximumParallelism)
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
        queueMock
            .Setup(q => q.GetDispatchStateAsync(functionName))
            .ReturnsAsync(new QueueDispatchState(1, 0, 0, []));
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
            .Setup(d => d.GetAsync(DataFileKeys.MetaKey(fileId)))
            .ReturnsAsync(metaBytes);
        // --- fileSync mock : retourne un stream fictif ---
        var fakeFileStream = new TrackingMemoryStream(new byte[2 * 1024 * 1024]);
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
        dbMock.Verify(d => d.GetAsync(DataFileKeys.MetaKey(fileId)), Times.AtLeastOnce);

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
        Assert.True(fakeFileStream.Disposed);
        dbMock.Verify(d => d.DeleteAsync(DataFileKeys.MetaKey(fileId)), Times.Never);
        fileSyncMock.Verify(
            f => f.BroadcastFileDeleteAsync(fileId, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Retryable_response_keeps_offloaded_body_for_the_next_attempt()
    {
        const string functionName = "my-func";
        const string fileId = "retryable-file";
        const string sha = "retry-sha";
        var db = new Mock<IDatabaseService>();
        db.Setup(service => service.GetAsync(DataFileKeys.MetaKey(fileId)))
            .ReturnsAsync(MemoryPackSerializer.Serialize(
                new DataSetMetadata(sha, 10, "application/octet-stream", fileId)));
        var fileSync = new Mock<IClusterFileSync>();
        fileSync.Setup(service => service.PullFileIfMissingAsync(
                fileId, sha, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilePullResult(new TrackingMemoryStream(new byte[10])));
        var request = new CustomRequest(
            [],
            Body: null,
            FunctionName: functionName,
            Path: "/retry",
            Method: "POST",
            Query: "",
            OffloadedFileId: fileId);
        var queue = BuildQueueMock(
            functionName,
            new QueueData("retry-element", MemoryPackSerializer.Serialize(request), 0, false, 0, 0));
        var (worker, _) = BuildWorker(
            queue.Object,
            BuildReplicasService(functionName, "192.168.99.21"),
            fileSync.Object,
            db.Object,
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var stopping = new CancellationTokenSource();

        await worker.StartAsync(stopping.Token);
        await Task.Delay(250);
        await stopping.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        db.Verify(service => service.DeleteAsync(DataFileKeys.MetaKey(fileId)), Times.Never);
        fileSync.Verify(
            service => service.BroadcastFileDeleteAsync(fileId, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Slow_terminal_offload_cleanup_does_not_block_the_next_dispatch()
    {
        const string functionName = "my-func";
        string[] fileIds = ["cleanup-file-1", "cleanup-file-2"];
        var metadata = fileIds.ToDictionary(
            id => DataFileKeys.MetaKey(id),
            id => MemoryPackSerializer.Serialize(
                new DataSetMetadata($"sha-{id}", 10, "application/octet-stream", id)));
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var db = new Mock<IDatabaseService>();
        db.Setup(service => service.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => metadata[key]);
        var fileSync = new Mock<IClusterFileSync>();
        fileSync.Setup(service => service.PullFileIfMissingAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilePullResult(new TrackingMemoryStream(new byte[10])));
        fileSync.Setup(service => service.BroadcastFileDeleteAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
            });
        QueueData[] messages = fileIds.Select((fileId, index) =>
        {
            var request = new CustomRequest(
                [],
                Body: null,
                FunctionName: functionName,
                Path: $"/cleanup/{index}",
                Method: "POST",
                Query: "",
                OffloadedFileId: fileId);
            return new QueueData(
                $"cleanup-element-{index}",
                MemoryPackSerializer.Serialize(request),
                0,
                true,
                0,
                0);
        }).ToArray();
        var queue = new Mock<ISlimFaasQueue>();
        var dequeueIndex = 0;
        queue.Setup(service => service.GetDispatchStateAsync(functionName))
            .ReturnsAsync(() => new QueueDispatchState(dequeueIndex < messages.Length ? 1 : 0, 0, 0, []));
        queue.Setup(service => service.DequeueAsync(
                functionName,
                It.IsAny<int>(),
                It.IsAny<IList<string>?>()))
            .ReturnsAsync(() => dequeueIndex < messages.Length
                ? [messages[dequeueIndex++]]
                : []);
        queue.Setup(service => service.ListCallbackAsync(
                functionName, It.IsAny<ListQueueItemStatus>()))
            .Returns(Task.CompletedTask);
        var (worker, sendClient) = BuildWorker(
            queue.Object,
            BuildReplicasService(functionName, "192.168.99.22", maximumParallelism: 1),
            fileSync.Object,
            db.Object);
        var sendCount = 0;
        var bothSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sendClient.Reset();
        sendClient.Setup(service => service.SendHttpRequestAsync(
                It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationTokenSource?>(),
                It.IsAny<IProxy?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Stream?>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref sendCount) == messages.Length)
                    bothSent.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            });
        using var stopping = new CancellationTokenSource();

        await worker.StartAsync(stopping.Token);
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await bothSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseCleanup.TrySetResult();
        await stopping.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, sendCount);
    }

    [Fact]
    public async Task Offloaded_bodies_are_prepared_concurrently_but_dispatched_in_fifo_order()
    {
        const string functionName = "my-func";
        string[] fileIds = ["file-1", "file-2"];
        var metadata = fileIds.ToDictionary(
            id => DataFileKeys.MetaKey(id),
            id => MemoryPackSerializer.Serialize(
                new DataSetMetadata($"sha-{id}", 10, "application/octet-stream", id)));
        var db = new Mock<IDatabaseService>();
        db.Setup(service => service.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => metadata[key]);
        var bothPullsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePulls = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pullCount = 0;
        var fileSync = new Mock<IClusterFileSync>();
        fileSync.Setup(service => service.PullFileIfMissingAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .Returns(async (string id, string _, string? _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref pullCount) == fileIds.Length)
                    bothPullsStarted.TrySetResult();
                await releasePulls.Task.WaitAsync(token);
                return new FilePullResult(new TrackingMemoryStream(new byte[10]));
            });
        QueueData[] messages = fileIds.Select((fileId, index) =>
        {
            var request = new CustomRequest(
                [],
                Body: null,
                FunctionName: functionName,
                Path: $"/process/{index}",
                Method: "POST",
                Query: "",
                OffloadedFileId: fileId);
            return new QueueData(
                $"element-{index}",
                MemoryPackSerializer.Serialize(request),
                0,
                true,
                0,
                0);
        }).ToArray();
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(service => service.GetDispatchStateAsync(functionName))
            .ReturnsAsync(new QueueDispatchState(messages.Length, 0, 0, []));
        queue.SetupSequence(service => service.DequeueAsync(
                functionName,
                It.IsAny<int>(),
                It.IsAny<IList<string>?>()))
            .ReturnsAsync(messages)
            .ReturnsAsync([]);
        queue.Setup(service => service.ListCallbackAsync(
                It.IsAny<string>(), It.IsAny<ListQueueItemStatus>()))
            .Returns(Task.CompletedTask);
        var (worker, sendClient) = BuildWorker(
            queue.Object,
            BuildReplicasService(functionName, "192.168.99.20", maximumParallelism: 2),
            fileSync.Object,
            db.Object);
        var sentPaths = new List<string>();
        var bothSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sendClient.Reset();
        sendClient.Setup(service => service.SendHttpRequestAsync(
                It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationTokenSource?>(),
                It.IsAny<IProxy?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Stream?>()))
            .Returns((CustomRequest request, SlimFaasDefaultConfiguration _, string? _,
                CancellationTokenSource? _, IProxy? _, string? _, string? _, string? _, Stream? _) =>
            {
                lock (sentPaths)
                {
                    sentPaths.Add(request.Path);
                    if (sentPaths.Count == messages.Length)
                        bothSent.TrySetResult();
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            });
        using var stopping = new CancellationTokenSource();

        await worker.StartAsync(stopping.Token);
        await bothPullsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releasePulls.TrySetResult();
        await bothSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stopping.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(["/process/0", "/process/1"], sentPaths);
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
    /// le worker loggue un avertissement, marque l'essai avec un statut retryable et n'appelle pas la fonction.
    /// </summary>
    [Fact]
    public async Task WhenOffloadedFileMetadataNotFound_ShouldMarkRetryableFailureWithoutCallingFunction()
    {
        const string functionName = "my-func";
        const string fileId = "missing-file-id";

        // db retourne null → métadonnées introuvables
        Mock<IDatabaseService> dbMock = new();
        dbMock
            .Setup(d => d.GetAsync(DataFileKeys.MetaKey(fileId)))
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
            sendClientMock.Object,
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
        dbMock.Verify(d => d.GetAsync(DataFileKeys.MetaKey(fileId)), Times.AtLeastOnce);

        // Un warning doit avoir été loggué
        loggerMock.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.AtLeastOnce);

        // La fonction ne doit jamais être appelée avec un body vide.
        sendClientMock.Verify(s => s.SendHttpRequestAsync(
            It.IsAny<CustomRequest>(),
            It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationTokenSource?>(),
            It.IsAny<IProxy?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<Stream?>()), Times.Never);

        // Le code 500 appartient aux statuts retryables par défaut de la queue.
        queueMock.Verify(q => q.ListCallbackAsync(
            functionName,
            It.Is<ListQueueItemStatus>(status =>
                status.Items != null &&
                status.Items.Any(item => item.Id == "element-id-3" && item.HttpCode == 500))),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Faulted_outbound_request_disposes_offloaded_stream()
    {
        const string functionName = "my-func";
        const string fileId = "faulted-file";
        const string sha = "sha";
        var db = new Mock<IDatabaseService>();
        db.Setup(d => d.GetAsync(DataFileKeys.MetaKey(fileId)))
            .ReturnsAsync(MemoryPackSerializer.Serialize(
                new DataSetMetadata(sha, 10, "application/octet-stream", fileId)));
        var stream = new TrackingMemoryStream(new byte[10]);
        var fileSync = new Mock<IClusterFileSync>();
        fileSync.Setup(f => f.PullFileIfMissingAsync(fileId, sha, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilePullResult(stream));
        var request = new CustomRequest(
            [],
            Body: null,
            FunctionName: functionName,
            Path: "/process",
            Method: "POST",
            Query: "",
            OffloadedFileId: fileId);
        var queue = BuildQueueMock(
            functionName,
            new QueueData("faulted-element", MemoryPackSerializer.Serialize(request), 0, true, 0, 0));
        var (worker, _) = BuildWorker(
            queue.Object,
            BuildReplicasService(functionName, "192.168.99.10"),
            fileSync.Object,
            db.Object,
            Task.FromException<HttpResponseMessage>(new HttpRequestException("failed")));
        using var stopping = new CancellationTokenSource();

        await worker.StartAsync(stopping.Token);
        await Task.Delay(250);
        await stopping.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Leadership_loss_cancels_request_and_disposes_offloaded_stream()
    {
        const string functionName = "my-func";
        const string fileId = "leadership-file";
        const string sha = "sha";
        var db = new Mock<IDatabaseService>();
        db.Setup(d => d.GetAsync(DataFileKeys.MetaKey(fileId)))
            .ReturnsAsync(MemoryPackSerializer.Serialize(
                new DataSetMetadata(sha, 10, "application/octet-stream", fileId)));
        var stream = new TrackingMemoryStream(new byte[10]);
        var fileSync = new Mock<IClusterFileSync>();
        fileSync.Setup(f => f.PullFileIfMissingAsync(fileId, sha, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilePullResult(stream));
        var request = new CustomRequest(
            [],
            Body: null,
            FunctionName: functionName,
            Path: "/process",
            Method: "POST",
            Query: "",
            OffloadedFileId: fileId);
        var queue = BuildQueueMock(
            functionName,
            new QueueData("leadership-element", MemoryPackSerializer.Serialize(request), 0, true, 0, 0));
        var master = new Mock<IMasterService>();
        var leadershipChecks = 0;
        master.Setup(s => s.IsMaster).Returns(() => Interlocked.Increment(ref leadershipChecks) == 1);
        var (worker, sendClient) = BuildWorker(
            queue.Object,
            BuildReplicasService(functionName, "192.168.99.11"),
            fileSync.Object,
            db.Object,
            masterService: master);
        CancellationTokenSource? capturedCancellation = null;
        sendClient.Reset();
        sendClient.Setup(s => s.SendHttpRequestAsync(
                It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationTokenSource?>(),
                It.IsAny<IProxy?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Stream?>()))
            .Returns((
                CustomRequest _,
                SlimFaasDefaultConfiguration _,
                string? _,
                CancellationTokenSource? cancellation,
                IProxy? _,
                string? _,
                string? _,
                string? _,
                Stream? _) =>
            {
                capturedCancellation = cancellation;
                return WaitForCancellationAsync(cancellation!.Token);
            });
        using var stopping = new CancellationTokenSource();

        await worker.StartAsync(stopping.Token);
        await Task.Delay(250);
        await stopping.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(capturedCancellation);
        Assert.True(capturedCancellation.IsCancellationRequested);
        Assert.True(stream.Disposed);
    }

    private static async Task<HttpResponseMessage> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Cancellation was expected.");
    }

    private sealed class TrackingMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Disposed = true;
            base.Dispose(disposing);
        }
    }
}
