using System.Reflection;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Jobs;

public class SlimScheduleJobsWorkerTests
{
    // --------- mocks partagés ----------
    private readonly Mock<IJobService>              _jobSvc   = new();
    private readonly Mock<IJobConfiguration>        _config   = new();
    private readonly Mock<ILogger<SlimJobsWorker>>  _logger   = new();
    private readonly Mock<ISlimDataStatus>          _status   = new();
    private readonly Mock<IDatabaseService>         _db       = new();
    private readonly Mock<IMasterService>           _master   = new();

    private readonly SlimScheduleJobsWorker         _sut;          // System-Under-Test
    private readonly SlimFaasJobConfiguration       _faasConfig;   // Configuration réelle

    public SlimScheduleJobsWorkerTests()
    {
        // --- configuration par défaut (visibilité publique pour simplifier) ---
        var job = new SlimfaasJob(
            Image: "allowed:latest",
            ImagesWhitelist: new() { "allowed:latest" },
            Visibility: nameof(FunctionVisibility.Public));

        _faasConfig = new SlimFaasJobConfiguration(new()
        {
            { "func", job },
        });

        _config.SetupGet(c => c.Configuration).Returns(_faasConfig);

        // Ready immédiatement
        _status.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);

        // delay=0 pour des tests instantanés
        _sut = new SlimScheduleJobsWorker(
            _jobSvc.Object, _config.Object, _logger.Object, _status.Object,
            _db.Object, _master.Object, delay: 0);
    }

    // -------------   helpers refléxion   -------------
    private static Task InvokeDoOneCycleAsync(SlimScheduleJobsWorker worker, CancellationToken token)
    {
        var m = typeof(SlimScheduleJobsWorker)
                .GetMethod("DoOneCycle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)m.Invoke(worker, new object[] { token })!;
    }

    private static byte[] Serialize<T>(T obj) => MemoryPackSerializer.Serialize(obj);

    // -------------   tests   -------------

    [Fact(DisplayName = "Le worker ne fait rien quand le nœud n'est pas maître")]
    public async Task DoOneCycle_Should_DoNothing_When_Not_Master()
    {
        // Arrange
        _master.SetupGet(m => m.IsMaster).Returns(false);

        // Act
        await InvokeDoOneCycleAsync(_sut, CancellationToken.None);

        // Assert : aucune interaction
        _db.Verify(d => d.HashGetAllAsync(It.IsAny<string>()), Times.Never);
        _jobSvc.Verify(s => s.EnqueueJobAsync(
                It.IsAny<string>(), It.IsAny<CreateJob>(), true), Times.Never);
    }

    [Fact(DisplayName = "Premier passage : pose le timestamp, pas d'enqueue")]
    public async Task DoOneCycle_FirstRun_Should_SetTimestamp_Only()
    {
        // Arrange (node maître)
        _master.SetupGet(m => m.IsMaster).Returns(true);

        // Une entrée Schedule dans Redis
        var scheduleJob = new ScheduleCreateJob("* * * * *", new() { "arg" });
        var dict = new Dictionary<string, byte[]> { { "sid", Serialize(scheduleJob) } };
        _db.Setup(d => d.HashGetAllAsync("ScheduleJob:func")).ReturnsAsync(dict);

        // Pas de timestamp existant → GetAsync renvoie null
        _db.Setup(d => d.GetAsync("ScheduleJob:func:sid")).ReturnsAsync((byte[]?)null);

        // Act
        await InvokeDoOneCycleAsync(_sut, CancellationToken.None);

        // Assert
        _db.Verify(d => d.SetAsync("ScheduleJob:func:sid", It.IsAny<byte[]>()), Times.Once);
        _jobSvc.Verify(s => s.EnqueueJobAsync(It.IsAny<string>(), It.IsAny<CreateJob>(), true), Times.Never);
    }

    [Fact(DisplayName = "Job en retard : envoie dans la file et met à jour le timestamp")]
    public async Task DoOneCycle_LateJob_Should_Enqueue_And_Update_Timestamp()
    {
        // Arrange
        _master.SetupGet(m => m.IsMaster).Returns(true);

        var scheduleJob = new ScheduleCreateJob("* * * * *", new() { "arg" });
        var dict = new Dictionary<string, byte[]> { { "sid", Serialize(scheduleJob) } };
        _db.Setup(d => d.HashGetAllAsync("ScheduleJob:func")).ReturnsAsync(dict);

        // Force un timestamp ancien (0) pour déclencher l'exécution
        _db.Setup(d => d.GetAsync("ScheduleJob:func:sid")).ReturnsAsync(Serialize(0L));

        // Retour "succès" du JobService
        _jobSvc.Setup(s => s.EnqueueJobAsync("func", It.IsAny<CreateJob>(), true))
               .ReturnsAsync(new ResultWithError<EnqueueJobResult>( new EnqueueJobResult("job-id")));

        // Act
        await InvokeDoOneCycleAsync(_sut, CancellationToken.None);

        // Assert
        _jobSvc.Verify(s => s.EnqueueJobAsync("func", It.IsAny<CreateJob>(), true), Times.Once);
        _db.Verify(d => d.SetAsync("ScheduleJob:func:sid", It.IsAny<byte[]>()), Times.AtLeastOnce);
    }
}
