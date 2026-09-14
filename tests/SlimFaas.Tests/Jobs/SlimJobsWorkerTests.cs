using MemoryPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests.Jobs;

public class SlimJobsWorkerTests
{
    // Borne haute pour attendre le signal d'un mock : généreuse pour les runners
    // lents (instrumentation de couverture), jamais atteinte en fonctionnement normal.
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    // Comme vous l'aviez déjà dans votre code
    private readonly HistoryHttpMemoryService _historyHttpMemoryService;
    private readonly Mock<IJobConfiguration> _jobConfigurationMock;
    private readonly Mock<IJobQueue> _jobQueueMock;
    private readonly Mock<IJobService> _jobServiceMock;
    private readonly Mock<ILogger<SlimJobsWorker>> _loggerMock;
    private readonly Mock<IMasterService> _masterServiceMock;
    private readonly Mock<IReplicasService> _replicasServiceMock;
    private readonly Mock<ISlimDataStatus> _slimDataStatusMock;

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

    private SlimJobsWorker CreateWorker()
    {
        var workersOptions = Microsoft.Extensions.Options.Options.Create(new WorkersOptions
        {
            JobsDelayMilliseconds = 10
        });

        return new SlimJobsWorker(
            _jobQueueMock.Object,
            _jobServiceMock.Object,
            _jobConfigurationMock.Object,
            _loggerMock.Object,
            _historyHttpMemoryService,
            _slimDataStatusMock.Object,
            _masterServiceMock.Object,
            _replicasServiceMock.Object,
            workersOptions,
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions()),
            new SlimFaas.Kubernetes.Watch.KubernetesWatchSignals()
        );
    }

    /// <summary>
    ///     Démarre le worker, attend que le mock instrumenté signale que le cycle
    ///     observé a été atteint, puis arrête le worker. Aucun budget temps réel :
    ///     seul un blocage franc (au-delà de <see cref="SignalTimeout" />) fait échouer le test.
    /// </summary>
    private static async Task RunUntilSignalAsync(SlimJobsWorker worker, TaskCompletionSource signal)
    {
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await signal.Task.WaitAsync(SignalTimeout);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Cas : le worker n'est pas "master".
    ///     On vérifie qu'aucune synchro de jobs ni dequeue n'a lieu.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NotMaster_NoSyncNoDequeue()
    {
        // ARRANGE
        // Le cycle est considéré atteint dès que le worker a consulté IsMaster.
        TaskCompletionSource cycleReached = CreateSignal();
        _masterServiceMock
            .Setup(m => m.IsMaster)
            .Returns(false)
            .Callback(() => cycleReached.TrySetResult());

        // On mocke une configuration vide pour éviter toute exception
        SlimFaasJobConfiguration fakeSlimFaasJobConfig = new(new Dictionary<string, SlimfaasJob>());
        _jobConfigurationMock
            .Setup(c => c.Configuration)
            .Returns(fakeSlimFaasJobConfig);
        _jobServiceMock
            .Setup(s => s.SyncJobsAsync())
            .ReturnsAsync(new List<Job>());

        SlimJobsWorker worker = CreateWorker();

        // ACT
        await RunUntilSignalAsync(worker, cycleReached);

        // ASSERT
        _masterServiceMock.Verify(m => m.IsMaster, Times.AtLeastOnce);
        // Avec MockBehavior.Strict, tout appel non configuré lèvera une exception.
        // Ici on n'attendait aucune interaction supplémentaire.
        _jobQueueMock.VerifyNoOtherCalls();
    }

    /// <summary>
    ///     Cas : le worker est master, SyncJobsAsync retourne une liste vide,
    ///     et la queue n'a pas d'éléments (count = 0).
    ///     Résultat : aucun job créé, aucun dequeue.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Master_EmptyJobs_NoJobCreated()
    {
        // ARRANGE
        _masterServiceMock.Setup(m => m.IsMaster).Returns(true);

        // Configuration simple : 1 job "myJob"
        var config = new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase);
        config.Add("myJob", new SlimfaasJob("myImage", new List<string> { "myImage" }, NumberParallelJob: 2));
        SlimFaasJobConfiguration fakeSlimFaasJobConfig = new(config);

        _jobConfigurationMock
            .Setup(c => c.Configuration)
            .Returns(fakeSlimFaasJobConfig);

        // SyncJobsAsync renvoie 0 jobs
        _jobServiceMock
            .Setup(s => s.SyncJobsAsync())
            .ReturnsAsync(new List<Job>());

        // QueueCount => 0. C'est la dernière étape du cycle pour ce job : son appel
        // signale que le cycle complet a été exécuté.
        TaskCompletionSource countReached = CreateSignal();
        _jobQueueMock
            .Setup(q => q.CountElementAsync("myjob", It.IsAny<IList<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(new List<QueueData>())
            .Callback(() => countReached.TrySetResult());

        // On simulera des déploiements vides
        DeploymentsInformations emptyDeployments = new(
            new List<DeploymentInformation>(),
            new SlimFaasDeploymentInformation(0, new List<PodInformation>()),
            Array.Empty<PodInformation>()
        );
        _replicasServiceMock
            .Setup(r => r.Deployments)
            .Returns(emptyDeployments);

        SlimJobsWorker worker = CreateWorker();

        // ACT
        await RunUntilSignalAsync(worker, countReached);

        // ASSERT
        _jobServiceMock.Verify(s => s.SyncJobsAsync(), Times.AtLeastOnce);
        _jobQueueMock.Verify(q => q.CountElementAsync("myjob", It.IsAny<IList<CountType>>(), It.IsAny<int>()),
            Times.AtLeastOnce);
        // Pas de dequeue, pas de job créé
        _jobQueueMock.Verify(q => q.DequeueAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _jobServiceMock.Verify(
            s => s.CreateJobAsync(It.IsAny<string>(), It.IsAny<CreateJob>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>()), Times.Never);
    }

    /// <summary>
    ///     Cas : worker master, 1 élément en file d'attente,
    ///     mais dépendance "dependencyA" n'a pas de réplicas => on ne dépile pas.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Master_DependsOnNoReplica_SkipDequeue()
    {
        // ARRANGE
        _masterServiceMock.Setup(m => m.IsMaster).Returns(true);

        // Le worker indexe les configurations par nom en minuscules : le dictionnaire
        // doit être insensible à la casse pour que "myJob" soit retrouvé sous "myjob".
        var config = new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase);
        config.Add("myJob", new SlimfaasJob(
            "myImage",
            new List<string> { "myImage" },
            NumberParallelJob: 2,
            DependsOn: new List<string> { "dependencyA" }
        ));
        SlimFaasJobConfiguration fakeSlimFaasJobConfig = new(config);

        _jobConfigurationMock
            .Setup(c => c.Configuration)
            .Returns(fakeSlimFaasJobConfig);

        // 0 jobs en cours
        _jobServiceMock
            .Setup(s => s.SyncJobsAsync())
            .ReturnsAsync(new List<Job>());

        // CountElement => 1 élément dispo, dépendant de "dependencyA"
        CreateJob createJobObj = new(new List<string> { "arg1" }, DependsOn: ["dependencyA"]);
        JobInQueue createJobInQueue = new(createJobObj, "myjob1", 1);
        byte[] dataBytes = MemoryPackSerializer.Serialize(createJobInQueue);
        _jobQueueMock
            .Setup(q => q.CountElementAsync("myjob", It.IsAny<IList<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(new List<QueueData> { new("1", dataBytes, 0, true, DateTime.UtcNow.Ticks, 30L * TimeSpan.TicksPerSecond) });

        // "dependencyA" à 0 réplicas => skip. La lecture des déploiements est l'étape
        // de vérification des dépendances : elle signale que la décision a été prise.
        DeploymentsInformations deployments = new(
            new List<DeploymentInformation>
            {
                new(
                    "dependencyA",
                    "default",
                    new List<PodInformation>(),
                    new SlimFaasConfiguration(),
                    0
                )
            },
            new SlimFaasDeploymentInformation(0, new List<PodInformation>()),
            Array.Empty<PodInformation>()
        );
        TaskCompletionSource dependenciesChecked = CreateSignal();
        _replicasServiceMock
            .Setup(r => r.Deployments)
            .Returns(deployments)
            .Callback(() => dependenciesChecked.TrySetResult());

        SlimJobsWorker worker = CreateWorker();

        // ACT
        await RunUntilSignalAsync(worker, dependenciesChecked);

        // ASSERT
        _jobQueueMock.Verify(q => q.CountElementAsync("myjob", It.IsAny<IList<CountType>>(), It.IsAny<int>()),
            Times.AtLeastOnce);
        // Dequeue n'a pas lieu car la dépendance n'est pas prête
        _jobQueueMock.Verify(q => q.DequeueAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _jobServiceMock.Verify(
            s => s.CreateJobAsync(It.IsAny<string>(), It.IsAny<CreateJob>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>()), Times.Never);
    }

    /// <summary>
    ///     Cas : worker master, 1 élément en file, dépendance OK => on dépile et on crée le job.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Master_OneMessageAndReplicaOk_JobCreated()
    {
        // ARRANGE
        _masterServiceMock.Setup(m => m.IsMaster).Returns(true);

        // 1 job "myJob", dépendant de "dependencyA"
        var config = new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase);
        config.Add("myJob", new SlimfaasJob("myImage", new List<string> { "myImage" }, NumberParallelJob: 2, DependsOn: new List<string> { "dependencyA" }));
        SlimFaasJobConfiguration fakeSlimFaasJobConfig = new(config);

        _jobConfigurationMock
            .Setup(c => c.Configuration)
            .Returns(fakeSlimFaasJobConfig);

        // Pas de jobs en cours
        _jobServiceMock
            .Setup(s => s.SyncJobsAsync())
            .ReturnsAsync(new List<Job>());

        // Simule un dequeue qui retourne un seul élément
        CreateJob createJobObj = new(new List<string> { "arg1", "arg2" }, DependsOn: ["dependencyA"]);
        JobInQueue createJobInQueue = new(createJobObj, "myjob1", 1);
        byte[] dataBytes = MemoryPackSerializer.Serialize(createJobInQueue);

        // 1 élément dispo dans la queue
        _jobQueueMock
            .Setup(q => q.CountElementAsync("myjob", It.IsAny<IList<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(new List<QueueData> { new("fakeId", dataBytes, 0, true, DateTime.UtcNow.Ticks, 30L * TimeSpan.TicksPerSecond), new("fakeId", dataBytes, 0, true, DateTime.UtcNow.Ticks, 30L * TimeSpan.TicksPerSecond) });

        // "dependencyA" a 1 réplique => c'est prêt
        DeploymentsInformations deployments = new(
            new List<DeploymentInformation>
            {
                new(
                    "dependencyA",
                    "default",
                    new List<PodInformation>(),
                    new SlimFaasConfiguration(),
                    1
                )
            },
            new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
            []
        );
        _replicasServiceMock
            .Setup(r => r.Deployments)
            .Returns(deployments);

        List<QueueData> queueDataList = new() { new QueueData("fakeId", dataBytes, 0, false, DateTime.UtcNow.Ticks, 30L * TimeSpan.TicksPerSecond) };

        _jobQueueMock
            .Setup(q => q.DequeueAsync("myjob", It.IsAny<int>()))
            .ReturnsAsync(queueDataList);

        // On s'attend à ce que CreateJobAsync soit appelé
        _jobServiceMock
            .Setup(s => s.CreateJobAsync("myjob", It.IsAny<CreateJob>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        // On s'attend à un callback après la création : c'est la dernière étape du
        // cycle, son appel signale que dequeue + création ont bien eu lieu.
        TaskCompletionSource callbackReached = CreateSignal();
        _jobQueueMock
            .Setup(q => q.ListCallbackAsync("myjob", It.IsAny<ListQueueItemStatus>()))
            .Returns(Task.CompletedTask)
            .Callback(() => callbackReached.TrySetResult());

        SlimJobsWorker worker = CreateWorker();

        // ACT
        await RunUntilSignalAsync(worker, callbackReached);

        // ASSERT
        // numberParallelJob = 2 => on devrait tenter de dépiler 2 messages
        _jobQueueMock.Verify(q => q.DequeueAsync("myjob", 2), Times.AtLeastOnce);
        _jobServiceMock.Verify(
            s => s.CreateJobAsync("myjob", It.IsAny<CreateJob>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>()), Times.AtLeastOnce);

        // Contrôle du callback 200
        _jobQueueMock.Verify(q => q.ListCallbackAsync(
            "myjob",
            It.Is<ListQueueItemStatus>(list =>
                list.Items != null
                && list.Items.Count == 1
                && list.Items[0].Id == "fakeId"
                && list.Items[0].HttpCode == 200
            )
        ), Times.AtLeastOnce);
    }
}
