using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Database;
using SlimData;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests;

public class SlimJobsWorkerTests
{
    private readonly Mock<IJobQueue> _jobQueueMock;
    private readonly Mock<IJobService> _jobServiceMock;
    private readonly Mock<IJobConfiguration> _jobConfigurationMock;
    private readonly Mock<ILogger<SlimJobsWorker>> _loggerMock;
    private readonly Mock<ISlimDataStatus> _slimDataStatusMock;
    private readonly Mock<IMasterService> _masterServiceMock;
    private readonly Mock<IReplicasService> _replicasServiceMock;

    // Comme vous l'aviez déjà dans votre code
    private readonly HistoryHttpMemoryService _historyHttpMemoryService;

    public SlimJobsWorkerTests()
    {
        // Mocks en mode Strict pour détecter tout appel imprévu
        _jobQueueMock = new Mock<IJobQueue>(MockBehavior.Strict);
        _jobServiceMock = new Mock<IJobService>(MockBehavior.Strict);
        _jobConfigurationMock = new Mock<IJobConfiguration>(MockBehavior.Strict);
        _loggerMock = new Mock<ILogger<SlimJobsWorker>>(MockBehavior.Loose);
        _slimDataStatusMock = new Mock<ISlimDataStatus>(MockBehavior.Strict);
        _masterServiceMock = new Mock<IMasterService>(MockBehavior.Strict);
        _replicasServiceMock = new Mock<IReplicasService>(MockBehavior.Strict);

        _historyHttpMemoryService = new HistoryHttpMemoryService();

        // Par défaut, WaitForReadyAsync ne fait rien (pas d'exception, ni de délai)
        _slimDataStatusMock
            .Setup(s => s.WaitForReadyAsync())
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Cas : le worker n'est pas "master".
    /// On vérifie qu'aucune synchro de jobs ni dequeue n'a lieu.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NotMaster_NoSyncNoDequeue()
    {
        // ARRANGE
        _masterServiceMock.Setup(m => m.IsMaster).Returns(false);

        // On mocke une configuration vide pour éviter toute exception
        var fakeSlimfaasJobConfig = new SlimfaasJobConfiguration(new Dictionary<string, SlimfaasJob>());
        _jobConfigurationMock
            .Setup(c => c.Configuration)
            .Returns(fakeSlimfaasJobConfig);
        _jobServiceMock
            .Setup(s => s.SyncJobsAsync())
            .ReturnsAsync(new List<Job>());

        var worker = new SlimJobsWorker(
            _jobQueueMock.Object,
            _jobServiceMock.Object,
            _jobConfigurationMock.Object,
            _loggerMock.Object,
            _historyHttpMemoryService,
            _slimDataStatusMock.Object,
            _masterServiceMock.Object,
            _replicasServiceMock.Object,
            delay: 10
        );

        using var cts = new CancellationTokenSource();
        // On annule vite le cycle principal du BackgroundService
        cts.CancelAfter(200);

        // ACT
        await worker.StartAsync(cts.Token);
        await Task.Delay(300);
        await worker.StopAsync(CancellationToken.None);

        // ASSERT
        _masterServiceMock.Verify(m => m.IsMaster, Times.AtLeastOnce);
        // Avec MockBehavior.Strict, tout appel non configuré lèvera une exception.
        // Ici on n'attendait aucune interaction supplémentaire.
        _jobQueueMock.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Cas : le worker est master, SyncJobsAsync retourne une liste vide,
    /// et la queue n'a pas d'éléments (count = 0).
    /// Résultat : aucun job créé, aucun dequeue.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Master_EmptyJobs_NoJobCreated()
    {
        // ARRANGE
        _masterServiceMock.Setup(m => m.IsMaster).Returns(true);

        // Configuration simple : 1 job "myJob"
        var fakeSlimfaasJobConfig = new SlimfaasJobConfiguration(
            new Dictionary<string, SlimfaasJob>
            {
                {
                    "myJob",
                    new SlimfaasJob(
                        Image: "myImage",
                        ImagesWhitelist: new List<string> { "myImage" },
                        NumberParallelJob: 2
                    )
                }
            }
        );

        _jobConfigurationMock
            .Setup(c => c.Configuration)
            .Returns(fakeSlimfaasJobConfig);

        // SyncJobsAsync renvoie 0 jobs
        _jobServiceMock
            .Setup(s => s.SyncJobsAsync())
            .ReturnsAsync(new List<Job>());

        // QueueCount => 0
        _jobQueueMock
            .Setup(q => q.CountElementAsync("myJob", It.IsAny<IList<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(new List<QueueData>());

        // On simulera des déploiements vides
        var emptyDeployments = new DeploymentsInformations(
            Functions: new List<DeploymentInformation>(),
            SlimFaas: new SlimFaasDeploymentInformation(0, new List<PodInformation>()),
            Pods: Array.Empty<PodInformation>()
        );
        _replicasServiceMock
            .Setup(r => r.Deployments)
            .Returns(emptyDeployments);

        var worker = new SlimJobsWorker(
            _jobQueueMock.Object,
            _jobServiceMock.Object,
            _jobConfigurationMock.Object,
            _loggerMock.Object,
            _historyHttpMemoryService,
            _slimDataStatusMock.Object,
            _masterServiceMock.Object,
            _replicasServiceMock.Object,
            delay: 10
        );

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        // ACT
        await worker.StartAsync(cts.Token);
        await Task.Delay(300);
        await worker.StopAsync(CancellationToken.None);

        // ASSERT
        _jobServiceMock.Verify(s => s.SyncJobsAsync(), Times.AtLeastOnce);
        _jobQueueMock.Verify(q => q.CountElementAsync("myJob", It.IsAny<IList<CountType>>(), It.IsAny<int>()), Times.AtLeastOnce);
        // Pas de dequeue, pas de job créé
        _jobQueueMock.Verify(q => q.DequeueAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _jobServiceMock.Verify(s => s.CreateJobAsync(It.IsAny<string>(), It.IsAny<CreateJob>()), Times.Never);
    }

    /// <summary>
    /// Cas : worker master, 1 élément en file d'attente,
    /// mais dépendance "dependencyA" n'a pas de réplicas => on ne dépile pas.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Master_DependsOnNoReplica_SkipDequeue()
    {
        // ARRANGE
        _masterServiceMock.Setup(m => m.IsMaster).Returns(true);

        var fakeSlimfaasJobConfig = new SlimfaasJobConfiguration(
            new Dictionary<string, SlimfaasJob>
            {
                {
                    "myJob",
                    new SlimfaasJob(
                        Image: "myImage",
                        ImagesWhitelist: new List<string> { "myImage" },
                        NumberParallelJob: 2,
                        DependsOn: new List<string> { "dependencyA" }
                    )
                }
            }
        );

        _jobConfigurationMock
            .Setup(c => c.Configuration)
            .Returns(fakeSlimfaasJobConfig);

        // 0 jobs en cours
        _jobServiceMock
            .Setup(s => s.SyncJobsAsync())
            .ReturnsAsync(new List<Job>());

        // CountElement => 1 élément dispo
        _jobQueueMock
            .Setup(q => q.CountElementAsync("myJob", It.IsAny<IList<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(new List<QueueData>(){ new("1", [byte.MinValue]) });

        // "dependencyA" à 0 réplicas => skip
        var deployments = new DeploymentsInformations(
            Functions: new List<DeploymentInformation>
            {
                new DeploymentInformation(
                    Deployment: "dependencyA",
                    Namespace: "default",
                    Pods: new List<PodInformation>(),
                    Configuration: new SlimFaasConfiguration(),
                    Replicas: 0
                )
            },
            SlimFaas: new SlimFaasDeploymentInformation(0, new List<PodInformation>()),
            Pods: Array.Empty<PodInformation>()
        );
        _replicasServiceMock
            .Setup(r => r.Deployments)
            .Returns(deployments);

        var worker = new SlimJobsWorker(
            _jobQueueMock.Object,
            _jobServiceMock.Object,
            _jobConfigurationMock.Object,
            _loggerMock.Object,
            _historyHttpMemoryService,
            _slimDataStatusMock.Object,
            _masterServiceMock.Object,
            _replicasServiceMock.Object,
            delay: 10
        );

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        // ACT
        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        // ASSERT
        // Dequeue n'a pas lieu car la dépendance n'est pas prête
        _jobQueueMock.Verify(q => q.DequeueAsync("myJob", It.IsAny<int>()), Times.Never);
        _jobServiceMock.Verify(s => s.CreateJobAsync(It.IsAny<string>(), It.IsAny<CreateJob>()), Times.Never);
    }

    /// <summary>
    /// Cas : worker master, 1 élément en file, dépendance OK => on dépile et on crée le job.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Master_OneMessageAndReplicaOk_JobCreated()
    {
        // ARRANGE
        _masterServiceMock.Setup(m => m.IsMaster).Returns(true);

        // 1 job "myJob", dépendant de "dependencyA"
        var fakeSlimfaasJobConfig = new SlimfaasJobConfiguration(
            new Dictionary<string, SlimfaasJob>
            {
                {
                    "myJob",
                    new SlimfaasJob(
                        Image: "myImage",
                        ImagesWhitelist: new List<string> { "myImage" },
                        NumberParallelJob: 2,
                        DependsOn: new List<string> { "dependencyA" }
                    )
                }
            }
        );
        _jobConfigurationMock
            .Setup(c => c.Configuration)
            .Returns(fakeSlimfaasJobConfig);

        // Pas de jobs en cours
        _jobServiceMock
            .Setup(s => s.SyncJobsAsync())
            .ReturnsAsync(new List<Job>());

        // Simule un dequeue qui retourne un seul élément
        var createJobObj = new CreateJob(new List<string>() {"arg1", "arg2"}, DependsOn: ["dependencyA"]);
        var dataBytes = MemoryPack.MemoryPackSerializer.Serialize(createJobObj);

        // 1 élément dispo dans la queue
        _jobQueueMock
            .Setup(q => q.CountElementAsync("myJob", It.IsAny<IList<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(new List<QueueData>(){ new("fakeId", dataBytes), new("fakeId", dataBytes) });

        // "dependencyA" a 1 réplique => c'est prêt
        var deployments = new DeploymentsInformations(
            Functions: new List<DeploymentInformation>
            {
                new(
                    Deployment: "dependencyA",
                    Namespace: "default",
                    Pods: new List<PodInformation>(),
                    Configuration: new SlimFaasConfiguration(),
                    Replicas: 1
                )
            },
            SlimFaas: new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
            Pods: []
        );
        _replicasServiceMock
            .Setup(r => r.Deployments)
            .Returns(deployments);

        var queueDataList = new List<QueueData>
        {
            new("fakeId", dataBytes)
        };

        _jobQueueMock
            .Setup(q => q.DequeueAsync("myJob", It.IsAny<int>()))
            .ReturnsAsync(queueDataList);

        // On s'attend à un callback après la création
        _jobQueueMock
            .Setup(q => q.ListCallbackAsync("myJob", It.IsAny<ListQueueItemStatus>()))
            .Returns(Task.CompletedTask);

        // On s'attend à ce que CreateJobAsync soit appelé
        _jobServiceMock
            .Setup(s => s.CreateJobAsync("myJob", It.IsAny<CreateJob>()))
            .Returns(Task.CompletedTask);

        var worker = new SlimJobsWorker(
            _jobQueueMock.Object,
            _jobServiceMock.Object,
            _jobConfigurationMock.Object,
            _loggerMock.Object,
            _historyHttpMemoryService,
            _slimDataStatusMock.Object,
            _masterServiceMock.Object,
            _replicasServiceMock.Object,
            delay: 10
        );

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        // ACT
        await worker.StartAsync(cts.Token);
        await Task.Delay(300);
        await worker.StopAsync(CancellationToken.None);

        // ASSERT
        // numberParallelJob = 2 => on devrait tenter de dépiler 2 messages
        _jobQueueMock.Verify(q => q.DequeueAsync("myJob", 2), Times.AtLeastOnce);
        _jobServiceMock.Verify(s => s.CreateJobAsync("myJob", It.IsAny<CreateJob>()), Times.AtLeastOnce);

        // Contrôle du callback 200
        _jobQueueMock.Verify(q => q.ListCallbackAsync(
            "myJob",
            It.Is<ListQueueItemStatus>(list =>
                list.Items.Count == 1
                && list.Items[0].Id == "fakeId"
                && list.Items[0].HttpCode == 200
            )
        ), Times.AtLeastOnce);
    }
}
