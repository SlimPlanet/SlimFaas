using MemoryPack;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests.Jobs;

public sealed class SlimJobsWorkerConcurrencyTests
{
    [Theory]
    [InlineData(JobStatus.Succeeded)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.ImagePullBackOff)]
    public async Task RetainedJobs_DoNotBlockFifthJob(JobStatus retainedStatus)
    {
        using var fixture = new Fixture();
        Job[] retained = Enumerable.Range(0, 4)
            .Select(index => MakeJob("batch", index.ToString(), retainedStatus))
            .ToArray();

        await fixture.Worker.DoJobOneCycle(retained);

        fixture.AssertDispatched("batch", 4);
        Assert.All(retained, job => Assert.Equal(retainedStatus, job.Status));
    }

    [Fact]
    public async Task MixedStates_OnlyPendingAndRunningConsumeSlots()
    {
        using var fixture = new Fixture();

        await fixture.Worker.DoJobOneCycle([
            MakeJob("batch", "running", JobStatus.Running),
            MakeJob("batch", "pending", JobStatus.Pending),
            MakeJob("batch", "success", JobStatus.Succeeded),
            MakeJob("batch", "failure", JobStatus.Failed),
            MakeJob("batch", "pull", JobStatus.ImagePullBackOff)
        ]);

        fixture.AssertDispatched("batch", 2);
    }

    [Theory]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Pending)]
    public async Task OccupiedSlots_DoNotDequeue(JobStatus status)
    {
        using var fixture = new Fixture();

        await fixture.Worker.DoJobOneCycle(Enumerable.Range(0, 4)
            .Select(index => MakeJob("batch", index.ToString(), status)).ToArray());

        fixture.Queue.Verify(queue => queue.DequeueAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        fixture.Service.Verify(service => service.CreateJobAsync(It.IsAny<string>(), It.IsAny<CreateJob>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task OccupiedConfiguration_DoesNotBlockAnotherConfiguration()
    {
        using var fixture = new Fixture("batch", "other");

        await fixture.Worker.DoJobOneCycle([
            .. Enumerable.Range(0, 4).Select(index => MakeJob("batch", index.ToString(), JobStatus.Running)),
            .. Enumerable.Range(0, 4).Select(index => MakeJob("other", index.ToString(), JobStatus.Succeeded))
        ]);

        fixture.Queue.Verify(queue => queue.DequeueAsync("batch", It.IsAny<int>()), Times.Never);
        fixture.AssertDispatched("other", 4);
    }

    [Theory]
    [InlineData(JobStatus.Pending, true)]
    [InlineData(JobStatus.Running, true)]
    [InlineData(JobStatus.Succeeded, false)]
    [InlineData(JobStatus.Failed, false)]
    [InlineData(JobStatus.ImagePullBackOff, false)]
    public async Task DependencyActivity_IsOnlyRefreshedForUnfinishedJobs(JobStatus status, bool refresh)
    {
        using var fixture = new Fixture();
        fixture.History.SetTickLastCall("dependency", 1);

        await fixture.Worker.DoJobOneCycle([MakeJob("batch", "existing", status)]);

        long tick = fixture.History.GetTicksLastCall("dependency");
        Assert.Equal(refresh, tick > 1);
    }

    [Fact]
    public async Task Scheduling_DoesNotRemoveFinishedJobsFromListings()
    {
        using var fixture = new Fixture();
        IList<Job> retained = [MakeJob("batch", "success", JobStatus.Succeeded), MakeJob("batch", "failure", JobStatus.Failed)];
        var kubernetes = new Mock<IKubernetesService>();
        kubernetes.Setup(service => service.ListJobsAsync(It.IsAny<string>())).ReturnsAsync(retained);
        var service = new JobService(kubernetes.Object, fixture.Configuration.Object,
            fixture.Queue.Object, Mock.Of<INamespaceProvider>(), NullLogger<JobService>.Instance);

        await fixture.Worker.DoJobOneCycle(await service.SyncJobsAsync());
        IList<JobListResult> listed = await service.ListJobAsync("batch");

        Assert.Same(retained, service.Jobs);
        Assert.Contains(listed, job => job.Id == "success" && job.Status == "Succeeded");
        Assert.Contains(listed, job => job.Id == "failure" && job.Status == "Failed");
        kubernetes.Verify(service => service.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static Job MakeJob(string name, string id, JobStatus status) =>
        new($"{name}{KubernetesService.SlimfaasJobKey}{id}", status, [], ["dependency"], id, 1, 2);

    private sealed class Fixture : IDisposable
    {
        public Mock<IJobQueue> Queue { get; } = new(MockBehavior.Strict);
        public Mock<IJobService> Service { get; } = new(MockBehavior.Strict);
        public Mock<IJobConfiguration> Configuration { get; } = new(MockBehavior.Strict);
        public HistoryHttpMemoryService History { get; } = new();
        public SlimJobsWorker Worker { get; }

        public Fixture(params string[] names)
        {
            if (names.Length == 0) names = ["batch"];
            var configurations = new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                configurations.Add(name, new SlimfaasJob("image", [], NumberParallelJob: 4,
                    TtlSecondsAfterFinished: 3600));
                var command = new JobInQueue(new CreateJob([], TtlSecondsAfterFinished: 3600),
                    $"{name}{KubernetesService.SlimfaasJobKey}fifth", 3);
                byte[] payload = MemoryPackSerializer.Serialize(command);
                // Enough waiting work to check the exact available capacity. Only one is returned
                // by the dequeue mock so creation and acknowledgement can be checked precisely.
                IList<QueueData> waiting = Enumerable.Range(0, 4).Select(index =>
                    new QueueData(index.ToString(), payload, 0, true, 1, TimeSpan.TicksPerMinute)).ToArray();
                Queue.Setup(queue => queue.CountElementAsync(name, It.IsAny<IList<CountType>>(), It.IsAny<int>()))
                    .ReturnsAsync(waiting);
                Queue.Setup(queue => queue.DequeueAsync(name, It.IsAny<int>()))
                    .ReturnsAsync([new QueueData("fifth", payload, 0, false, 1, TimeSpan.TicksPerMinute)]);
                Queue.Setup(queue => queue.ListCallbackAsync(name, It.IsAny<ListQueueItemStatus>()))
                    .Returns(Task.CompletedTask);
                Service.Setup(service => service.CreateJobAsync(name,
                        It.Is<CreateJob>(job => job.TtlSecondsAfterFinished == 3600), "fifth", command.JobFullName, 3))
                    .Returns(Task.CompletedTask);
            }
            Configuration.SetupGet(configuration => configuration.Configuration)
                .Returns(new SlimFaasJobConfiguration(configurations));
            var replicas = new Mock<IReplicasService>();
            replicas.SetupGet(service => service.Deployments)
                .Returns(new DeploymentsInformations([], new SlimFaasDeploymentInformation(1, []), []));
            Worker = new SlimJobsWorker(Queue.Object, Service.Object, Configuration.Object,
                NullLogger<SlimJobsWorker>.Instance, History, Mock.Of<ISlimDataStatus>(),
                Mock.Of<IMasterService>(), replicas.Object,
                Microsoft.Extensions.Options.Options.Create(new WorkersOptions()));
        }

        public void AssertDispatched(string name, int capacity)
        {
            Queue.Verify(queue => queue.DequeueAsync(name, capacity), Times.Once);
            Service.Verify(service => service.CreateJobAsync(name, It.IsAny<CreateJob>(), "fifth",
                $"{name}{KubernetesService.SlimfaasJobKey}fifth", 3), Times.Once);
            Queue.Verify(queue => queue.ListCallbackAsync(name, It.Is<ListQueueItemStatus>(callback =>
                callback.Items.Count == 1 && callback.Items[0].Id == "fifth" && callback.Items[0].HttpCode == 200)), Times.Once);
        }

        public void Dispose() => Worker.Dispose();
    }
}
