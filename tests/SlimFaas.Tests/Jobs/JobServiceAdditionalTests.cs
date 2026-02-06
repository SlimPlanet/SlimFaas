﻿﻿﻿using System.Reflection;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SlimData;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests;

[Collection("ScaleWorker")]
public class JobServiceAdditionalTests
{
    // ---------- petits alias/mocks utilitaires ----------
    private static readonly string Ns = "unit-tests";
    private readonly Mock<IJobConfiguration> _conf;
    private readonly Mock<IKubernetesService> _kube;
    private readonly Mock<IJobQueue> _queue;
    private readonly JobService _svc;

    public JobServiceAdditionalTests()
    {
        _kube = new Mock<IKubernetesService>();
        _queue = new Mock<IJobQueue>();
        _conf = new Mock<IJobConfiguration>();

        _conf.Setup(x => x.Configuration)
            .Returns(new SlimFaasJobConfiguration(new Dictionary<string, SlimfaasJob>
            {
                {
                    "Public", new SlimfaasJob("img",
                        new List<string> { "img" },
                        Visibility: nameof(FunctionVisibility.Public))
                },
                {
                    "Private", new SlimfaasJob("img",
                        new List<string> { "img" },
                        Visibility: nameof(FunctionVisibility.Private))
                }
            }));

        var namespaceProviderMock = new Mock<INamespaceProvider>();
        namespaceProviderMock.SetupGet(n => n.CurrentNamespace).Returns(Ns);

        _svc = new JobService(_kube.Object, _conf.Object, _queue.Object,
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions { Namespace = Ns }),
            namespaceProviderMock.Object,
            NullLogger<JobService>.Instance);
    }

    private static Job FakeJob(string name, string id, JobStatus status = JobStatus.Running) =>
        new(name, status, new List<string>(), new List<string>(), id, 0, 0);

    private static QueueData FakeQueueItem(string id)
    {
        JobInQueue createJobInQueue = new(new CreateJob(new List<string>()), "fullName", DateTime.UtcNow.Ticks);
        byte[] createJobSerialized = MemoryPackSerializer.Serialize(createJobInQueue);
        return new QueueData(id, createJobSerialized);
    }

    // ---------------------------------------------------------------------
    // SyncJobsAsync
    // ---------------------------------------------------------------------
    [Fact]
    public async Task SyncJobsAsync_updates_cache_and_returns_list()
    {
        // Arrange
        List<Job> expected = new() { FakeJob("job-a", "1"), FakeJob("job-b", "2") };
        _kube.Setup(k => k.ListJobsAsync(Ns)).ReturnsAsync(expected);

        // Act
        IList<Job> returned = await _svc.SyncJobsAsync();

        // Assert
        Assert.Equal(expected, returned); // valeur de retour
        Assert.Equal(expected, _svc.Jobs.ToList()); // cache interne
    }

    // ---------------------------------------------------------------------
    // ListJobAsync
    // ---------------------------------------------------------------------
    [Fact]
    public async Task ListJobAsync_merges_running_and_queued_jobs()
    {
        // Arrange – 1 job déjà lancé
        Job running = FakeJob("Public", "run‑1");
        _kube.Setup(k => k.ListJobsAsync(Ns)).ReturnsAsync([running]);
        await _svc.SyncJobsAsync(); // remplit le cache

        // Arrange – éléments en attente
        List<QueueData> queued = new() { FakeQueueItem("q‑1"), FakeQueueItem("q‑2") };
        _queue.Setup(q => q.CountElementAsync(
                "Public",
                It.IsAny<List<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(queued);

        // Act
        IList<JobListResult> list = await _svc.ListJobAsync("Public");

        // Assert – 1er bloc : job actif
        JobListResult active = list.First(l => l.Id == "run‑1");
        Assert.Equal(running.Name, active.Name);
        Assert.Equal(running.Status.ToString(), active.Status);
        Assert.Equal(-1, active.PositionInQueue);

        // Assert – file d'attente
        JobListResult q1 = list.Single(l => l.Id == "q‑1");
        JobListResult q2 = list.Single(l => l.Id == "q‑2");

        Assert.Equal(0, q1.PositionInQueue);
        Assert.Equal(1, q2.PositionInQueue);
        Assert.All(new[] { q1, q2 }, l => Assert.Equal(nameof(JobStatusResult.Queued), l.Status));
    }

    // ---------------------------------------------------------------------
    // DeleteJobAsync
    // ---------------------------------------------------------------------
    [Fact]
    public async Task DeleteJobAsync_returns_false_when_private_and_external()
    {
        // Act
        bool ok = await _svc.DeleteJobAsync("Private", "whatever", false);

        // Assert
        Assert.False(ok);
        _kube.Verify(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteJobAsync_returns_false_if_element_not_found()
    {
        // Arrange – le cache ne contient pas l’id demandé
        _kube.Setup(k => k.ListJobsAsync(Ns)).ReturnsAsync(new List<Job>());
        List<QueueData> queued = new() { FakeQueueItem("q‑1"), FakeQueueItem("q‑2") };
        _queue.Setup(q => q.CountElementAsync(
                "Public",
                It.IsAny<List<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(queued);
        await _svc.SyncJobsAsync();

        // Act
        bool ok = await _svc.DeleteJobAsync("Public", "no‑id", true);

        // Assert
        Assert.False(ok);
        _kube.Verify(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteJobAsync_calls_kubernetes_and_returns_true()
    {
        // Arrange – place le job dans le cache
        Job job = FakeJob("job‑del", "id‑123");
        _kube.Setup(k => k.ListJobsAsync(Ns)).ReturnsAsync([job]);
        List<QueueData> queued = new() { FakeQueueItem("q‑1"), FakeQueueItem("q‑2") };
        _queue.Setup(q => q.CountElementAsync(
                "Public",
                It.IsAny<List<CountType>>(), It.IsAny<int>()))
            .ReturnsAsync(queued);

        await _svc.SyncJobsAsync();

        // Act
        bool ok = await _svc.DeleteJobAsync("Public", "id‑123", true);

        // Assert
        Assert.True(ok);
        _kube.Verify(k => k.DeleteJobAsync(Ns, job.Name), Times.Once);
    }

    // ---------------------------------------------------------------------
    // Helpers : filtrage d'image
    // ---------------------------------------------------------------------
    [Theory]
    [InlineData("pattern‑img:*", "pattern‑img:v1", true)]
    [InlineData("pattern‑img:*", "other:v1", false)]
    [InlineData("exact‑img", "exact‑img", true)]
    public void IsPatternMatch_behaves_as_expected(string whitelist, string candidate, bool expected)
    {
        // IsPatternMatch est maintenant une méthode d'instance privée (non-static)
        object? allowed = typeof(JobService)
            .GetMethod("IsPatternMatch", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_svc, [whitelist, candidate]);

        Assert.Equal(expected, allowed);
    }
}
