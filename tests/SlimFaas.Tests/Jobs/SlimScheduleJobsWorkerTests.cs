using System.Reflection;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Moq;
using NodaTime;
using SlimData.Commands;
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
        _db.Verify(d => d.SetAsync("ScheduleJob:func:sid", It.IsAny<byte[]>(), It.IsAny<long?>()), Times.Once);
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
        _db.Verify(d => d.SetAsync("ScheduleJob:func:sid", It.IsAny<byte[]>(), It.IsAny<long?>()), Times.AtLeastOnce);
    }

     // -------------------------------------------------------------------------
    // 1) Premier passage : Schedule dans la config → SetAsync seulement
    // -------------------------------------------------------------------------
    [Fact(DisplayName = "Config schedule – 1er passage : juste SetAsync, pas d'enqueue")]
    public async Task DoOneCycle_ConfigSchedule_FirstRun_Should_SetTimestampOnly()
    {
        // Arrange ----------------------------------------------------------------
        _master.SetupGet(m => m.IsMaster).Returns(true);

        // --- dictionnaire des jobs ---------------------------------------------
        var jobsDict = new Dictionary<string, SlimfaasJob>
        {
            ["func"] = new SlimfaasJob(
                Image: "allowed:latest",
                ImagesWhitelist: new() { "allowed:latest" },
                Visibility: nameof(FunctionVisibility.Public))
        };

        // --- dictionnaire des schedules ----------------------------------------
        const string cron = "*/5 * * * *";
        var schedule = new ScheduleCreateJob(cron, new List<string>{ "arg" });

        var schedulesDict = new Dictionary<string, IList<ScheduleCreateJob>>
        {
            ["func"] = new List<ScheduleCreateJob> { schedule }
        };

        // ➜ nouvel objet configuration intégrant le schedule
        var faasConfig = new SlimFaasJobConfiguration(jobsDict, schedulesDict);
        _config.SetupGet(c => c.Configuration).Returns(faasConfig);

        // Pas de timestamp encore stocké
        _db.Setup(d => d.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((byte[]?)null);

        _db.Setup(d => d.HashGetAllAsync(It.IsAny<string>()))
            .ReturnsAsync(new Dictionary<string, byte[]>());
        // Act -----------------------------------------------------------------
        await InvokeDoOneCycleAsync(_sut, CancellationToken.None);

        // Assert --------------------------------------------------------------
        // ➜ le worker initialise le timestamp mais n'appelle PAS EnqueueJobAsync
        _db.Verify(d => d.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<long?>()), Times.Once);
        _jobSvc.Verify(s => s.EnqueueJobAsync(It.IsAny<string>(), It.IsAny<CreateJob>(), true), Times.Never);
    }


    // -------------------------------------------------------------------------
    // 2) Horloge avancée : le job est “en retard” → Enqueue + mise à jour TS
    // -------------------------------------------------------------------------
    [Fact(DisplayName = "Config schedule – retard : Enqueue + mise à jour timestamp")]
    public async Task DoOneCycle_ConfigSchedule_Late_Should_Enqueue_And_UpdateTimestamp()
    {
        // Arrange ----------------------------------------------------------------
        _master.SetupGet(m => m.IsMaster).Returns(true);

        // --- dictionnaire des jobs ---------------------------------------------
        var jobsDict = new Dictionary<string, SlimfaasJob>
        {
            ["func"] = new SlimfaasJob(
                Image: "allowed:latest",
                ImagesWhitelist: new() { "allowed:latest" },
                Visibility: nameof(FunctionVisibility.Public))
        };

        // --- dictionnaire des schedules ----------------------------------------
        const string cron = "*/5 * * * *";
        var schedule = new ScheduleCreateJob(cron, new List<string>{ "arg" });

        var schedulesDict = new Dictionary<string, IList<ScheduleCreateJob>>
        {
            ["func"] = new List<ScheduleCreateJob> { schedule }
        };

        // ➜ nouvel objet configuration intégrant le schedule
        var faasConfig = new SlimFaasJobConfiguration(jobsDict, schedulesDict);
        _config.SetupGet(c => c.Configuration).Returns(faasConfig);

        // Timestamp ancien (0) => runJob = true
        _db.Setup(d => d.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(MemoryPackSerializer.Serialize(0L));
        // Dictionnaire Redis (première boucle) : vide
        _db.Setup(d => d.HashGetAllAsync(It.IsAny<string>()))
            .ReturnsAsync(new Dictionary<string, byte[]>());

        _jobSvc.Setup(s => s.EnqueueJobAsync("func",
                                             It.IsAny<CreateJob>(),
                                             true))
               .ReturnsAsync(new ResultWithError<EnqueueJobResult>(
                   new EnqueueJobResult("job-id")));

        // Act -----------------------------------------------------------------
        await InvokeDoOneCycleAsync(_sut, CancellationToken.None);

        // Assert --------------------------------------------------------------
        _jobSvc.Verify(s => s.EnqueueJobAsync("func",
                                              It.IsAny<CreateJob>(),
                                              true), Times.Once);

        _db.Verify(d => d.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<long?>()),
                   Times.AtLeastOnce);   // mise à jour du nouveau timestamp
    }

    [Fact(DisplayName = "Config schedule – le cron est évalué dans le fuseau horaire de la planification")]
    public async Task DoOneCycle_ConfigSchedule_Should_Use_Schedule_TimeZone()
    {
        // Arrange ----------------------------------------------------------------
        _master.SetupGet(m => m.IsMaster).Returns(true);

        var jobsDict = new Dictionary<string, SlimfaasJob>
        {
            ["func"] = new SlimfaasJob(
                Image: "allowed:latest",
                ImagesWhitelist: new() { "allowed:latest" },
                Visibility: nameof(FunctionVisibility.Public))
        };
        var schedule = new ScheduleCreateJob("0 8 * * *", new List<string> { "arg" }, TimeZone: "Europe/Paris");
        var schedulesDict = new Dictionary<string, IList<ScheduleCreateJob>>
        {
            ["func"] = new List<ScheduleCreateJob> { schedule }
        };
        _config.SetupGet(c => c.Configuration).Returns(new SlimFaasJobConfiguration(jobsDict, schedulesDict));

        _db.Setup(d => d.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(MemoryPackSerializer.Serialize(0L));
        _db.Setup(d => d.HashGetAllAsync(It.IsAny<string>()))
            .ReturnsAsync(new Dictionary<string, byte[]>());
        _jobSvc.Setup(s => s.EnqueueJobAsync("func", It.IsAny<CreateJob>(), true))
               .ReturnsAsync(new ResultWithError<EnqueueJobResult>(new EnqueueJobResult("job-id")));

        var storedTimestamps = new List<long>();
        _db.Setup(d => d.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<long?>()))
            .Callback<string, byte[]?, long?, KeyValueOperation, long, decimal>((_, value, _, _, _, _) =>
                storedTimestamps.Add(MemoryPackSerializer.Deserialize<long>(value)));

        long before = LatestEightOClockInParis();

        // Act -----------------------------------------------------------------
        await InvokeDoOneCycleAsync(_sut, CancellationToken.None);

        // Assert --------------------------------------------------------------
        long after = LatestEightOClockInParis();
        long stored = Assert.Single(storedTimestamps);
        Assert.True(stored == before || stored == after, $"Stored {stored}, expected {before} or {after}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task DoOneCycle_ConfigSchedule_Should_Reuse_Legacy_Watermark_Without_Replaying(string? timeZone)
    {
        // Keep the daily occurrence away from the test's execution time, including midnight.
        int hour = DateTimeOffset.UtcNow.AddHours(-2).Hour;
        string cron = FormattableString.Invariant($"0 {hour} * * *");
        var schedule = new ScheduleCreateJob(cron, ["arg"], TimeZone: timeZone);
        var legacy = new LegacyScheduleCreateJob(cron, ["arg"]);
        string executionKey = $"ScheduleJob:func:{IdGenerator.GetId32Hex(Serialize(legacy))}";
        var watermarks = new Dictionary<string, byte[]> { [executionKey] = Serialize(0L) };
        _master.SetupGet(m => m.IsMaster).Returns(true);
        _config.SetupGet(c => c.Configuration).Returns(_faasConfig with
        {
            Schedules = new Dictionary<string, IList<ScheduleCreateJob>> { ["func"] = [schedule] }
        });
        _db.Setup(d => d.HashGetAllAsync(It.IsAny<string>()))
            .ReturnsAsync(new Dictionary<string, byte[]>());
        _db.Setup(d => d.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => watermarks.GetValueOrDefault(key));
        _db.Setup(d => d.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<long?>()))
            .Callback<string, byte[]?, long?, KeyValueOperation, long, decimal>((key, value, _, _, _, _) =>
                watermarks[key] = value!);
        _jobSvc.Setup(s => s.EnqueueJobAsync("func", It.IsAny<CreateJob>(), true))
            .ReturnsAsync(new ResultWithError<EnqueueJobResult>(new EnqueueJobResult("job-id")));

        await InvokeDoOneCycleAsync(_sut, CancellationToken.None);
        await InvokeDoOneCycleAsync(_sut, CancellationToken.None);

        _jobSvc.Verify(s => s.EnqueueJobAsync("func", It.IsAny<CreateJob>(), true), Times.Once);
        _db.Verify(d => d.GetAsync(executionKey), Times.Exactly(2));
        _db.Verify(d => d.SetAsync(executionKey, It.IsAny<byte[]>(), It.IsAny<long?>()), Times.Once);
        Assert.Single(watermarks);
        long persisted = MemoryPackSerializer.Deserialize<long>(watermarks[executionKey]);
        Assert.True(persisted > 0);
    }

    private static long LatestEightOClockInParis()
    {
        var paris = DateTimeZoneProviders.Tzdb["Europe/Paris"];
        var now = SystemClock.Instance.GetCurrentInstant().InZone(paris).LocalDateTime;
        var date = now.Hour >= 8 ? now.Date : now.Date.PlusDays(-1);
        return date.At(new LocalTime(8, 0)).InZoneStrictly(paris).ToInstant().ToUnixTimeSeconds();
    }
}
