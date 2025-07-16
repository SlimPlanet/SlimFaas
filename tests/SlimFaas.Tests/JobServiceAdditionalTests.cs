using MemoryPack;
using Moq;
using SlimFaas.Kubernetes;
using SlimFaas.Jobs;
using SlimData;

namespace SlimFaas.Tests;

[Collection("ScaleWorker")]
public class JobServiceAdditionalTests
{
    private readonly Mock<IKubernetesService> _kube;
    private readonly Mock<IJobQueue> _queue;
    private readonly Mock<IJobConfiguration> _conf;
    private readonly JobService _svc;

    // ---------- petits alias/mocks utilitaires ----------
    private static readonly string Ns = "unit‑tests";

    private static Job FakeJob(string name, string id, JobStatus status = JobStatus.Running) =>
        new(name, status,  new List<string>(), new List<string>(), id, 0, 0);

    private static QueueData FakeQueueItem(string id)
    {
        JobInQueue createJobInQueue = new(new CreateJob(new List<string>()), "fullName", DateTime.UtcNow.Ticks);
        var createJobSerialized = MemoryPackSerializer.Serialize(createJobInQueue);
        return new QueueData(id, createJobSerialized);
    }

    public JobServiceAdditionalTests()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariables.Namespace, Ns);

        _kube  = new Mock<IKubernetesService>();
        _queue = new Mock<IJobQueue>();
        _conf  = new Mock<IJobConfiguration>();

        _conf.Setup(x => x.Configuration)
            .Returns(new SlimfaasJobConfiguration(new()
            {
                { "Public",
                    new SlimfaasJob(Image: "img",
                        ImagesWhitelist: new() { "img" },
                        Visibility: nameof(FunctionVisibility.Public)) },
                { "Private",
                    new SlimfaasJob(Image: "img",
                        ImagesWhitelist: new() { "img" },
                        Visibility: nameof(FunctionVisibility.Private)) },
            }));


        _svc = new JobService(_kube.Object, _conf.Object, _queue.Object);
    }

    // ---------------------------------------------------------------------
    // SyncJobsAsync
    // ---------------------------------------------------------------------
    [Fact]
    public async Task SyncJobsAsync_updates_cache_and_returns_list()
    {
        // Arrange
        var expected = new List<Job> { FakeJob("job‑a", "1"), FakeJob("job‑b", "2") };
        _kube.Setup(k => k.ListJobsAsync(Ns)).ReturnsAsync(expected);

        // Act
        var returned = await _svc.SyncJobsAsync();

        // Assert
        Assert.Equal(expected, returned);                      // valeur de retour
        Assert.Equal(expected, _svc.Jobs.ToList());            // cache interne
    }

    // ---------------------------------------------------------------------
    // ListJobAsync
    // ---------------------------------------------------------------------
    [Fact]
    public async Task ListJobAsync_merges_running_and_queued_jobs()
    {
        // Arrange – 1 job déjà lancé
        var running = FakeJob("Public", "run‑1");
        _kube.Setup(k => k.ListJobsAsync(Ns)).ReturnsAsync([running]);
        await _svc.SyncJobsAsync();            // remplit le cache

        // Arrange – éléments en attente
        var queued = new List<QueueData> { FakeQueueItem("q‑1"), FakeQueueItem("q‑2") };
        _queue.Setup(q => q.CountElementAsync(
                "Public",
                It.IsAny<List<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(queued);

        // Act
        var list = await _svc.ListJobAsync("Public");

        // Assert – 1er bloc : job actif
        var active = Assert.Single(list.Where(l => l.Id == "run‑1"));
        Assert.Equal(running.Name, active.Name);
        Assert.Equal(running.Status.ToString(), active.Status);
        Assert.Equal(-1, active.PositionInQueue);

        // Assert – file d’attente
        var q1 = list.Single(l => l.Id == "q‑1");
        var q2 = list.Single(l => l.Id == "q‑2");

        Assert.Equal(0,  q1.PositionInQueue);
        Assert.Equal(1,  q2.PositionInQueue);
        Assert.All(new[] { q1, q2 }, l => Assert.Equal(nameof(JobStatusResult.Queued), l.Status));
    }

    // ---------------------------------------------------------------------
    // DeleteJobAsync
    // ---------------------------------------------------------------------
    [Fact]
    public async Task DeleteJobAsync_returns_false_when_private_and_external()
    {
        // Act
        var ok = await _svc.DeleteJobAsync("Private", "whatever", isMessageComeFromNamespaceInternal: false);

        // Assert
        Assert.False(ok);
        _kube.Verify(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteJobAsync_returns_false_if_element_not_found()
    {
        // Arrange – le cache ne contient pas l’id demandé
        _kube.Setup(k => k.ListJobsAsync(Ns)).ReturnsAsync(new List<Job>());
        var queued = new List<QueueData> { FakeQueueItem("q‑1"), FakeQueueItem("q‑2") };
        _queue.Setup(q => q.CountElementAsync(
                "Public",
                It.IsAny<List<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(queued);
        await _svc.SyncJobsAsync();

        // Act
        var ok = await _svc.DeleteJobAsync("Public", "no‑id", true);

        // Assert
        Assert.False(ok);
        _kube.Verify(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteJobAsync_calls_kubernetes_and_returns_true()
    {
        // Arrange – place le job dans le cache
        var job = FakeJob("job‑del", "id‑123");
        _kube.Setup(k => k.ListJobsAsync(Ns)).ReturnsAsync([job]);
        var queued = new List<QueueData> { FakeQueueItem("q‑1"), FakeQueueItem("q‑2") };
        _queue.Setup(q => q.CountElementAsync(
                "Public",
                It.IsAny<List<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(queued);

        await _svc.SyncJobsAsync();

        // Act
        var ok = await _svc.DeleteJobAsync("Public", "id‑123", true);

        // Assert
        Assert.True(ok);
        _kube.Verify(k => k.DeleteJobAsync(Ns, job.Name), Times.Once);
    }

    // ---------------------------------------------------------------------
    // Helpers : filtrage d’image
    // ---------------------------------------------------------------------
    [Theory]
    [InlineData("pattern‑img:*", "pattern‑img:v1",  true)]
    [InlineData("pattern‑img:*", "other:v1",        false)]
    [InlineData("exact‑img",    "exact‑img",        true)]
    public void IsPatternMatch_behaves_as_expected(string whitelist, string candidate, bool expected)
    {
        var allowed = typeof(JobService)  // appel méthode privée via reflection
            .GetMethod("IsPatternMatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [whitelist, candidate]);

        Assert.Equal(expected, allowed);
    }
}
