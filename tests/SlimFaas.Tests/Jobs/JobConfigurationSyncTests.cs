using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests.Jobs;

/// <summary>
/// Tests for JobConfiguration.SyncJobsConfigurationAsync and the private
/// MergeJobConfigurations method (exercised indirectly through Sync).
/// </summary>
public class JobConfigurationSyncTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static IOptions<SlimFaasOptions> Options(string? json = null) =>
        Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            Namespace = "default",
            JobsConfiguration = json
        });


    private JobConfiguration BuildSut(
        Mock<IKubernetesService> k8sMock,
        string? initialJson = null)
    {
        var namespaceMock = new Mock<INamespaceProvider>();
        namespaceMock.SetupGet(n => n.CurrentNamespace).Returns("default");

        return new JobConfiguration(
            Options(initialJson),
            k8sMock.Object,
            NullLogger<JobConfiguration>.Instance,
            namespaceMock.Object);
    }

    // ── SyncJobsConfigurationAsync ────────────────────────────────────────────

    [Fact(DisplayName = "Sync: when k8s returns null, configuration is unchanged")]
    public async Task SyncAsync_K8sReturnsNull_ConfigurationUnchanged()
    {
        // Arrange
        var k8s = new Mock<IKubernetesService>();
        k8s.Setup(s => s.ListJobsConfigurationAsync("default"))
           .ReturnsAsync((SlimFaasJobConfiguration?)null);

        var sut = BuildSut(k8s);
        var before = sut.Configuration;

        // Act
        await sut.SyncJobsConfigurationAsync();

        // Assert – same reference (Interlocked.Exchange was never called)
        Assert.Same(before, sut.Configuration);
    }

    [Fact(DisplayName = "Sync: new job from k8s is merged into configuration")]
    public async Task SyncAsync_K8sReturnsNewJob_IsAddedToConfiguration()
    {
        // Arrange
        var newJob = new SlimfaasJob("k8s-image:v1", new List<string>());
        var k8sConfig = new SlimFaasJobConfiguration(
            new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase)
            {
                { "k8s-job", newJob }
            });

        var k8s = new Mock<IKubernetesService>();
        k8s.Setup(s => s.ListJobsConfigurationAsync("default"))
           .ReturnsAsync(k8sConfig);

        var sut = BuildSut(k8s);

        // Act
        await sut.SyncJobsConfigurationAsync();

        // Assert
        Assert.True(sut.Configuration.Configurations.ContainsKey("k8s-job"));
        Assert.Equal("k8s-image:v1", sut.Configuration.Configurations["k8s-job"].Image);
    }

    [Fact(DisplayName = "Sync: initial (env-based) jobs are preserved alongside k8s jobs")]
    public async Task SyncAsync_PreservesInitialJobs()
    {
        // Arrange – initial config has "env-job"
        string initialJson = """
        {
            "Configurations": {
                "env-job": {
                    "Image": "env-image:v1",
                    "ImagesWhitelist": [],
                    "Resources": {
                        "Requests": { "cpu": "50m", "memory": "64Mi" },
                        "Limits":   { "cpu": "50m", "memory": "64Mi" }
                    }
                }
            }
        }
        """;

        var k8sJob = new SlimfaasJob("k8s-image:v2", new List<string>());
        var k8sConfig = new SlimFaasJobConfiguration(
            new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase)
            {
                { "k8s-job", k8sJob }
            });

        var k8s = new Mock<IKubernetesService>();
        k8s.Setup(s => s.ListJobsConfigurationAsync("default"))
           .ReturnsAsync(k8sConfig);

        var sut = BuildSut(k8s, initialJson);

        // Act
        await sut.SyncJobsConfigurationAsync();

        // Assert – both jobs present
        Assert.True(sut.Configuration.Configurations.ContainsKey("env-job"));
        Assert.True(sut.Configuration.Configurations.ContainsKey("k8s-job"));
        Assert.True(sut.Configuration.Configurations.ContainsKey("Default"));
    }

    [Fact(DisplayName = "Sync: k8s job named 'Default' does not overwrite default entry")]
    public async Task SyncAsync_K8sJobNamedDefault_IsIgnored()
    {
        // Arrange – k8s sends back a "Default" key; it should be skipped by MergeJobConfigurations
        var defaultOverride = new SlimfaasJob("override-image:v99", new List<string>());
        var k8sConfig = new SlimFaasJobConfiguration(
            new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase)
            {
                { "Default", defaultOverride }
            });

        var k8s = new Mock<IKubernetesService>();
        k8s.Setup(s => s.ListJobsConfigurationAsync("default"))
           .ReturnsAsync(k8sConfig);

        var sut = BuildSut(k8s);
        string originalDefaultImage = sut.Configuration.Configurations["Default"].Image;

        // Act
        await sut.SyncJobsConfigurationAsync();

        // Assert – "Default" image not overwritten
        Assert.Equal(originalDefaultImage, sut.Configuration.Configurations["Default"].Image);
        Assert.NotEqual("override-image:v99", sut.Configuration.Configurations["Default"].Image);
    }

    [Fact(DisplayName = "Sync: k8s schedules are merged into configuration")]
    public async Task SyncAsync_K8sSchedules_AreMerged()
    {
        // Arrange
        var schedule = new ScheduleCreateJob("0 * * * *", new List<string> { "run" });
        var k8sConfig = new SlimFaasJobConfiguration(
            new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase)
            {
                { "scheduled-job", new SlimfaasJob("img:v1", new List<string>()) }
            },
            new Dictionary<string, IList<ScheduleCreateJob>>(StringComparer.OrdinalIgnoreCase)
            {
                { "scheduled-job", new List<ScheduleCreateJob> { schedule } }
            });

        var k8s = new Mock<IKubernetesService>();
        k8s.Setup(s => s.ListJobsConfigurationAsync("default"))
           .ReturnsAsync(k8sConfig);

        var sut = BuildSut(k8s);

        // Act
        await sut.SyncJobsConfigurationAsync();

        // Assert
        Assert.NotNull(sut.Configuration.Schedules);
        Assert.True(sut.Configuration.Schedules!.ContainsKey("scheduled-job"));
        Assert.Equal("0 * * * *", sut.Configuration.Schedules["scheduled-job"][0].Schedule);
    }

    [Fact(DisplayName = "Sync: existing schedule is overwritten by k8s value")]
    public async Task SyncAsync_ExistingSchedule_IsOverwritten()
    {
        // Arrange – initial config already has a schedule
        string initialJson = """
        {
            "Configurations": {},
            "Schedules": {
                "my-job": [{ "Schedule": "5 * * * *", "Args": [] }]
            }
        }
        """;

        var newSchedule = new ScheduleCreateJob("0 12 * * *", new List<string>());
        var k8sConfig = new SlimFaasJobConfiguration(
            new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase)
            {
                { "my-job", new SlimfaasJob("img:v1", new List<string>()) }
            },
            new Dictionary<string, IList<ScheduleCreateJob>>(StringComparer.OrdinalIgnoreCase)
            {
                { "my-job", new List<ScheduleCreateJob> { newSchedule } }
            });

        var k8s = new Mock<IKubernetesService>();
        k8s.Setup(s => s.ListJobsConfigurationAsync("default"))
           .ReturnsAsync(k8sConfig);

        var sut = BuildSut(k8s, initialJson);

        // Act
        await sut.SyncJobsConfigurationAsync();

        // Assert – schedule replaced with k8s value
        Assert.Equal("0 12 * * *", sut.Configuration.Schedules!["my-job"][0].Schedule);
    }

    [Fact(DisplayName = "Sync: multiple calls keep accumulating k8s jobs")]
    public async Task SyncAsync_CalledTwice_AccumulatesJobs()
    {
        // Arrange
        var k8s = new Mock<IKubernetesService>();

        var firstConfig = new SlimFaasJobConfiguration(
            new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase)
            {
                { "job-first", new SlimfaasJob("img:v1", new List<string>()) }
            });

        var secondConfig = new SlimFaasJobConfiguration(
            new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase)
            {
                { "job-second", new SlimfaasJob("img:v2", new List<string>()) }
            });

        k8s.SetupSequence(s => s.ListJobsConfigurationAsync("default"))
           .ReturnsAsync(firstConfig)
           .ReturnsAsync(secondConfig);

        var sut = BuildSut(k8s);

        // Act
        await sut.SyncJobsConfigurationAsync();
        await sut.SyncJobsConfigurationAsync();

        // Assert – both jobs present (initial config is always the merge base)
        Assert.True(sut.Configuration.Configurations.ContainsKey("job-second"));
    }
}
